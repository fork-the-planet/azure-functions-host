﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Script.Binding;
using Microsoft.Azure.WebJobs.Script.Extensibility;
using Microsoft.Azure.WebJobs.Script.Extensions;
using Microsoft.Extensions.Logging;

namespace Microsoft.Azure.WebJobs.Script.Description
{
    public abstract class FunctionDescriptorProvider
    {
        protected FunctionDescriptorProvider(ScriptHost host, ScriptJobHostOptions config, ICollection<IScriptBindingProvider> bindingProviders)
        {
            Host = host;
            Config = config;
            BindingProviders = bindingProviders;
        }

        protected ScriptHost Host { get; private set; }

        protected ScriptJobHostOptions Config { get; private set; }

        protected ICollection<IScriptBindingProvider> BindingProviders { get; private set; }

        public virtual async Task<(bool Success, FunctionDescriptor Descriptor)> TryCreate(FunctionMetadata functionMetadata)
        {
            if (functionMetadata == null)
            {
                throw new InvalidOperationException("functionMetadata");
            }

            ValidateFunction(functionMetadata);

            // parse the bindings
            Collection<FunctionBinding> inputBindings = FunctionBinding.GetBindings(Config, BindingProviders, functionMetadata.InputBindings, FileAccess.Read);
            Collection<FunctionBinding> outputBindings = FunctionBinding.GetBindings(Config, BindingProviders, functionMetadata.OutputBindings, FileAccess.Write);
            VerifyResolvedBindings(functionMetadata, inputBindings, outputBindings);

            BindingMetadata triggerMetadata = functionMetadata.InputBindings.FirstOrDefault(p => p.IsTrigger);
            string scriptFilePath = Path.Combine(Config.RootScriptPath, functionMetadata.ScriptFile ?? string.Empty);
            IFunctionInvoker invoker = null;

            try
            {
                invoker = CreateFunctionInvoker(scriptFilePath, triggerMetadata, functionMetadata, inputBindings, outputBindings);

                Collection<CustomAttributeBuilder> methodAttributes = new Collection<CustomAttributeBuilder>();
                Collection<ParameterDescriptor> parameters = await GetFunctionParametersAsync(invoker, functionMetadata, triggerMetadata, methodAttributes, inputBindings, outputBindings);

                var functionDescriptor = new FunctionDescriptor(functionMetadata.Name, invoker, functionMetadata, parameters, methodAttributes, inputBindings, outputBindings);

                return (true, functionDescriptor);
            }
            catch (Exception ex)
            {
                Host.Logger.LogDebug(ex, $"Creating function descriptor for function {functionMetadata.Name} failed");
                IDisposable disposableInvoker = invoker as IDisposable;
                if (disposableInvoker != null)
                {
                    disposableInvoker.Dispose();
                }

                throw;
            }
        }

        public void VerifyResolvedBindings(FunctionMetadata functionMetadata, IEnumerable<FunctionBinding> inputBindings, IEnumerable<FunctionBinding> outputBindings)
        {
            IEnumerable<string> bindingsFromMetadata = functionMetadata.InputBindings.Union(functionMetadata.OutputBindings).Select(f => f.Type);
            IEnumerable<string> resolvedBindings = inputBindings.Union(outputBindings).Select(b => b.Metadata.Type);
            IEnumerable<string> unresolvedBindings = bindingsFromMetadata.Except(resolvedBindings);

            if (unresolvedBindings.Any())
            {
                string allUnresolvedBindings = string.Join(", ", unresolvedBindings);
                string errorMessage = CreateBindingError(allUnresolvedBindings);
                throw new FunctionConfigurationException(errorMessage);
            }
        }

        protected virtual Task<Collection<ParameterDescriptor>> GetFunctionParametersAsync(IFunctionInvoker functionInvoker, FunctionMetadata functionMetadata,
            BindingMetadata triggerMetadata, Collection<CustomAttributeBuilder> methodAttributes, Collection<FunctionBinding> inputBindings, Collection<FunctionBinding> outputBindings)
        {
            if (functionInvoker == null)
            {
                throw new ArgumentNullException(nameof(functionInvoker));
            }
            if (functionMetadata == null)
            {
                throw new ArgumentNullException(nameof(functionMetadata));
            }
            if (triggerMetadata == null)
            {
                throw new ArgumentNullException(nameof(triggerMetadata));
            }
            if (methodAttributes == null)
            {
                throw new ArgumentNullException(nameof(methodAttributes));
            }

            ApplyMethodLevelAttributes(functionMetadata, triggerMetadata, methodAttributes);

            Collection<ParameterDescriptor> parameters = new Collection<ParameterDescriptor>();
            ParameterDescriptor triggerParameter = CreateTriggerParameter(triggerMetadata);
            parameters.Add(triggerParameter);

            // Add an IBinder to support the binding programming model
            parameters.Add(new ParameterDescriptor(ScriptConstants.SystemBinderParameterName, typeof(IBinder)));

            // Add ExecutionContext to provide access to InvocationId, etc.
            parameters.Add(new ParameterDescriptor(ScriptConstants.SystemExecutionContextParameterName, typeof(ExecutionContext)));

            parameters.Add(new ParameterDescriptor(ScriptConstants.SystemLoggerParameterName, typeof(ILogger)));

            return Task.FromResult(parameters);
        }

        protected virtual ParameterDescriptor CreateTriggerParameter(BindingMetadata triggerMetadata, Type parameterType = null)
        {
            if (TryParseTriggerParameter(triggerMetadata, out ParameterDescriptor triggerParameter, parameterType))
            {
                triggerParameter.IsTrigger = true;
            }
            else
            {
                string errorMessage = CreateBindingError(triggerMetadata.Type);
                throw new FunctionConfigurationException(errorMessage);
            }

            return triggerParameter;
        }

        private bool TryParseTriggerParameter(BindingMetadata metadata, out ParameterDescriptor parameterDescriptor, Type parameterType = null)
        {
            parameterDescriptor = null;

            ScriptBindingContext bindingContext = new ScriptBindingContext(metadata.Raw);

            if (bindingContext.SupportsDeferredBinding && metadata.SkipDeferredBinding())
            {
                bindingContext.SupportsDeferredBinding = false;
            }

            ScriptBinding binding = null;
            foreach (var provider in BindingProviders)
            {
                if (provider.TryCreate(bindingContext, out binding))
                {
                    break;
                }
            }

            if (binding != null)
            {
                // Construct the Attribute builders for the binding
                var attributes = binding.GetAttributes();
                Collection<CustomAttributeBuilder> attributeBuilders = new Collection<CustomAttributeBuilder>();
                foreach (var attribute in attributes)
                {
                    var attributeBuilder = ExtensionBinding.GetAttributeBuilder(attribute);
                    attributeBuilders.Add(attributeBuilder);
                }

                Type triggerParameterType = parameterType ?? binding.DefaultType;
                parameterDescriptor = new ParameterDescriptor(bindingContext.Name, triggerParameterType, attributeBuilders);

                return true;
            }

            return false;
        }

        protected internal virtual void ValidateFunction(FunctionMetadata functionMetadata)
        {
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            foreach (var binding in functionMetadata.Bindings)
            {
                ValidateBinding(binding);

                // Ensure no duplicate binding names
                if (names.Contains(binding.Name))
                {
                    throw new InvalidOperationException($"{nameof(FunctionDescriptorProvider)}: Multiple bindings with name '{binding.Name}' discovered. Binding names must be unique.");
                }

                names.Add(binding.Name);
            }

            // Verify there aren't multiple triggers defined
            var triggers = functionMetadata.InputBindings.Where(p => p.IsTrigger).ToArray();
            if (triggers.Length > 1)
            {
                throw new InvalidOperationException($"Multiple trigger bindings defined. A function can only have a single trigger binding.");
            }

            // Functions must have a trigger binding
            var triggerMetadata = triggers.FirstOrDefault(p => p.IsTrigger);
            if (triggerMetadata == null)
            {
                throw new InvalidOperationException("No trigger binding specified. A function must have a trigger input binding.");
            }
        }

        protected internal virtual void ValidateBinding(BindingMetadata bindingMetadata)
        {
            Utility.ValidateBinding(bindingMetadata);

            if (bindingMetadata.Type == null)
            {
                throw new ArgumentException($"Binding '{bindingMetadata.Name}' is invalid. Bindings must specify a Type.");
            }
        }

        protected static void ApplyMethodLevelAttributes(FunctionMetadata functionMetadata, BindingMetadata triggerMetadata, Collection<CustomAttributeBuilder> methodAttributes)
        {
            if (Utility.IsHttporManualTrigger(triggerMetadata.Type))
            {
                // the function can be run manually, but there will be no automatic
                // triggering
                ConstructorInfo ctorInfo = typeof(NoAutomaticTriggerAttribute).GetConstructor(new Type[0]);
                CustomAttributeBuilder attributeBuilder = new CustomAttributeBuilder(ctorInfo, new object[0]);
                methodAttributes.Add(attributeBuilder);
            }

            // apply the retry settings from function.json
            if (functionMetadata.Retry != null)
            {
                CustomAttributeBuilder retryCustomAttributeBuilder = CustomAttributeBuilderUtility.GetRetryCustomAttributeBuilder(functionMetadata.Retry);
                if (retryCustomAttributeBuilder != null)
                {
                    methodAttributes.Add(retryCustomAttributeBuilder);
                }
            }
        }

        protected abstract IFunctionInvoker CreateFunctionInvoker(string scriptFilePath, BindingMetadata triggerMetadata, FunctionMetadata functionMetadata, Collection<FunctionBinding> inputBindings, Collection<FunctionBinding> outputBindings);

        protected ParameterDescriptor ParseManualTrigger(BindingMetadata trigger, Type triggerParameterType = null)
        {
            if (trigger == null)
            {
                throw new ArgumentNullException(nameof(trigger));
            }

            if (triggerParameterType == null)
            {
                triggerParameterType = typeof(string);
            }

            return new ParameterDescriptor(trigger.Name, triggerParameterType);
        }

        private string CreateBindingError(string unresolvedBindings)
        {
            return (Host.ExtensionBundleManager?.IsExtensionBundleConfigured() ?? false)
                    ? $"The binding type(s) '{unresolvedBindings}' were not found in the configured extension bundle. Please ensure the type is correct and the correct version of extension bundle is configured"
                    : $"The binding type(s) '{unresolvedBindings}' are not registered. Please ensure the type is correct and the binding extension is installed.";
        }
    }
}
