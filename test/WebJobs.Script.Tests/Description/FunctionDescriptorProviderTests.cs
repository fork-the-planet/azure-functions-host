﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Script.Binding;
using Microsoft.Azure.WebJobs.Script.Description;
using Microsoft.Azure.WebJobs.Script.Extensibility;
using Microsoft.Azure.WebJobs.Script.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.WebJobs.Script.Tests;
using Moq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Microsoft.Azure.WebJobs.Script.Tests
{
    public class FunctionDescriptorProviderTests : IDisposable
    {
        private readonly FunctionDescriptorProvider _provider;
        private readonly ScriptHost _scriptHost;
        private readonly IHost _host;

        public FunctionDescriptorProviderTests()
        {
            string rootPath = Path.Combine(Environment.CurrentDirectory, @"TestScripts\Node");

            _host = new HostBuilder()
                .ConfigureDefaultTestWebScriptHost(webJobsBuilder =>
                {
                    webJobsBuilder.AddAzureStorage();
                },
                o =>
                {
                    o.ScriptPath = rootPath;
                    o.LogPath = TestHelpers.GetHostLogFileDirectory().Parent.FullName;
                })
                .ConfigureServices((context, services) =>
                {
                    services.AddMetrics();
                    services.AddSingleton<IEnvironment>(new TestEnvironment());
                    services.AddSingleton<IHostMetrics, HostMetrics>();
                })
                .Build();

            _scriptHost = _host.GetScriptHost();
            _scriptHost.InitializeAsync().GetAwaiter().GetResult();
            var serviceBindingProviders = _host.Services.GetService<IEnumerable<IScriptBindingProvider>>().ToArray();
            _provider = new TestDescriptorProvider(_scriptHost, _host.Services.GetService<IOptions<ScriptJobHostOptions>>().Value, serviceBindingProviders);
        }

        [Fact]
        public void ValidateFunction_DuplicateBindingNames_Throws()
        {
            FunctionMetadata functionMetadata = new FunctionMetadata();
            functionMetadata.Bindings.Add(new BindingMetadata
            {
                Name = "test",
                Type = "BlobTrigger"
            });
            functionMetadata.Bindings.Add(new BindingMetadata
            {
                Name = "dupe",
                Type = "Blob"
            });
            functionMetadata.Bindings.Add(new BindingMetadata
            {
                Name = "dupe",
                Type = "Blob"
            });

            var ex = Assert.Throws<InvalidOperationException>(() =>
            {
                _provider.ValidateFunction(functionMetadata);
            });

            Assert.Equal($"{nameof(FunctionDescriptorProvider)}: Multiple bindings with name 'dupe' discovered. Binding names must be unique.", ex.Message);
        }

        [Fact]
        public void ValidateFunction_NoBindingType_Throws()
        {
            FunctionMetadata functionMetadata = new FunctionMetadata();
            functionMetadata.Bindings.Add(new BindingMetadata
            {
                Name = "a",
                Type = "BlobTrigger"
            });
            functionMetadata.Bindings.Add(new BindingMetadata
            {
                Name = "b"
            });

            var ex = Assert.Throws<ArgumentException>(() =>
            {
                _provider.ValidateFunction(functionMetadata);
            });

            Assert.Equal("Binding 'b' is invalid. Bindings must specify a Type.", ex.Message);
        }

        [Fact]
        public void ValidateFunction_MultipleTriggerBindings_Throws()
        {
            FunctionMetadata functionMetadata = new FunctionMetadata();
            functionMetadata.Bindings.Add(new BindingMetadata
            {
                Name = "a",
                Type = "BlobTrigger"
            });
            functionMetadata.Bindings.Add(new BindingMetadata
            {
                Name = "b",
                Type = "QueueTrigger"
            });

            var ex = Assert.Throws<InvalidOperationException>(() =>
            {
                _provider.ValidateFunction(functionMetadata);
            });

            Assert.Equal("Multiple trigger bindings defined. A function can only have a single trigger binding.", ex.Message);
        }

        [Fact]
        public void ValidateFunction_NoTriggerBinding_Throws()
        {
            FunctionMetadata functionMetadata = new FunctionMetadata();
            functionMetadata.Bindings.Add(new BindingMetadata
            {
                Name = "test",
                Type = "Blob"
            });

            var ex = Assert.Throws<InvalidOperationException>(() =>
            {
                _provider.ValidateFunction(functionMetadata);
            });

            Assert.Equal("No trigger binding specified. A function must have a trigger input binding.", ex.Message);
        }

        [Fact]
        public async Task VerifyResolvedBindings_WithNoBindingMatch_ThrowsExpectedException()
        {
            FunctionMetadata functionMetadata = new FunctionMetadata();
            BindingMetadata triggerMetadata = BindingMetadata.Create(JObject.Parse("{\"type\": \"blobTrigger\",\"name\": \"req\",\"direction\": \"in\", \"blobPath\": \"test\"}"));
            BindingMetadata bindingMetadata = BindingMetadata.Create(JObject.Parse("{\"type\": \"unknownbinding\",\"name\": \"blob\",\"direction\": \"in\"}"));

            functionMetadata.Bindings.Add(triggerMetadata);
            functionMetadata.Bindings.Add(bindingMetadata);

            var ex = await Assert.ThrowsAsync<FunctionConfigurationException>(async () =>
            {
                var (created, descriptor) = await _provider.TryCreate(functionMetadata);
            });

            Assert.Contains("The binding type(s) 'unknownbinding' are not registered", ex.Message);
        }

        [Fact]
        public async Task VerifyResolvedBindings_WithValidBindingMatch_DoesNotThrow()
        {
            FunctionMetadata functionMetadata = new FunctionMetadata();
            BindingMetadata triggerMetadata = BindingMetadata.Create(JObject.Parse("{\"type\": \"httpTrigger\",\"name\": \"req\",\"direction\": \"in\"}"));
            BindingMetadata bindingMetadata = BindingMetadata.Create(JObject.Parse("{\"type\": \"http\",\"name\": \"$return\",\"direction\": \"out\"}"));

            functionMetadata.Bindings.Add(triggerMetadata);
            functionMetadata.Bindings.Add(bindingMetadata);
            try
            {
                var (created, descriptor) = await _provider.TryCreate(functionMetadata);
                Assert.True(true, "No exception thrown");
            }
            catch (Exception ex)
            {
                Assert.True(false, "Exception not expected:" + ex.Message);
                throw;
            }
        }

        [Fact]
        public async Task CreateTriggerParameter_WithNoBindingMatch_ThrowsExpectedException()
        {
            FunctionMetadata functionMetadata = new FunctionMetadata();
            BindingMetadata metadata = BindingMetadata.Create(JObject.Parse("{\"type\": \"someInvalidTrigger\",\"name\": \"req\",\"direction\": \"in\"}"));

            functionMetadata.Bindings.Add(metadata);

            var ex = await Assert.ThrowsAsync<FunctionConfigurationException>(async () =>
            {
                var (created, descriptor) = await _provider.TryCreate(functionMetadata);
            });

            Assert.Contains("someInvalidTrigger", ex.Message);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("binding-test")]
        [InlineData("binding name")]
        public void ValidateBinding_InvalidName_Throws(string bindingName)
        {
            BindingMetadata bindingMetadata = new BindingMetadata
            {
                Name = bindingName
            };

            var ex = Assert.Throws<ArgumentException>(() =>
            {
                _provider.ValidateBinding(bindingMetadata);
            });

            Assert.Equal($"The binding name {bindingName} is invalid. Please assign a valid name to the binding. See https://aka.ms/azure-functions-binding-name-rules for more details.", ex.Message);
        }

        [Theory]
        [InlineData("__")]
        [InlineData("__binding")]
        [InlineData("binding__")]
        [InlineData("bind__ing")]
        [InlineData("__binding__")]
        [InlineData("_binding")]
        [InlineData("binding_")]
        [InlineData("_binding_")]
        [InlineData("_another_binding_test_")]
        [InlineData("long_binding_name_that_is_valid")]
        [InlineData("binding_name")]
        [InlineData("_")]
        [InlineData("bindingName")]
        [InlineData("binding1")]
        [InlineData(ScriptConstants.SystemReturnParameterBindingName)]
        public void ValidateBinding_ValidName_DoesNotThrow(string bindingName)
        {
            BindingMetadata bindingMetadata = new BindingMetadata
            {
                Name = bindingName,
                Type = "Blob"
            };

            if (bindingMetadata.IsReturn)
            {
                bindingMetadata.Direction = BindingDirection.Out;
            }

            try
            {
                _provider.ValidateBinding(bindingMetadata);
            }
            catch (ArgumentException)
            {
                Assert.True(false, $"Valid binding name '{bindingName}' failed validation.");
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _host?.Dispose();
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }

        private class TestDescriptorProvider : FunctionDescriptorProvider
        {
            public TestDescriptorProvider(ScriptHost host, ScriptJobHostOptions config, ICollection<IScriptBindingProvider> bindingProviders)
                : base(host, config, bindingProviders)
            {
            }

            protected override IFunctionInvoker CreateFunctionInvoker(string scriptFilePath, BindingMetadata triggerMetadata, FunctionMetadata functionMetadata, Collection<FunctionBinding> inputBindings, Collection<FunctionBinding> outputBindings)
            {
                return new Mock<IFunctionInvoker>().Object;
            }
        }
    }
}
