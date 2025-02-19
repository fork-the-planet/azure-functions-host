﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Azure.WebJobs.Script.Description;
using Microsoft.Azure.WebJobs.Script.Diagnostics;
using Microsoft.Azure.WebJobs.Script.Eventing;
using Microsoft.Azure.WebJobs.Script.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using FunctionMetadata = Microsoft.Azure.WebJobs.Script.Description.FunctionMetadata;

namespace Microsoft.Azure.WebJobs.Script.Workers
{
    internal class HttpFunctionInvocationDispatcher : IFunctionInvocationDispatcher
    {
        private readonly IMetricsLogger _metricsLogger;
        private readonly ILogger _logger;
        private readonly IHttpWorkerChannelFactory _httpWorkerChannelFactory;
        private readonly IApplicationLifetime _applicationLifetime;
        private readonly TimeSpan thresholdBetweenRestarts = TimeSpan.FromMinutes(WorkerConstants.WorkerRestartErrorIntervalThresholdInMinutes);

        private IScriptEventManager _eventManager;
        private IDisposable _workerErrorSubscription;
        private IDisposable _workerRestartSubscription;
        private ScriptJobHostOptions _scriptOptions;
        private bool _disposed = false;
        private bool _disposing = false;
        private ConcurrentStack<HttpWorkerErrorEvent> _invokerErrors = new ConcurrentStack<HttpWorkerErrorEvent>();
        private IHttpWorkerChannel _httpWorkerChannel;

        public HttpFunctionInvocationDispatcher(IOptions<ScriptJobHostOptions> scriptHostOptions,
            IMetricsLogger metricsLogger,
            IApplicationLifetime applicationLifetime,
            IScriptEventManager eventManager,
            ILoggerFactory loggerFactory,
            IHttpWorkerChannelFactory httpWorkerChannelFactory)
        {
            _metricsLogger = metricsLogger;
            _scriptOptions = scriptHostOptions.Value;
            _applicationLifetime = applicationLifetime;
            _eventManager = eventManager;
            _logger = loggerFactory.CreateLogger<HttpFunctionInvocationDispatcher>();
            _httpWorkerChannelFactory = httpWorkerChannelFactory ?? throw new ArgumentNullException(nameof(httpWorkerChannelFactory));

            State = FunctionInvocationDispatcherState.Default;
            ErrorEventsThreshold = 3;

            _workerErrorSubscription = _eventManager.OfType<HttpWorkerErrorEvent>()
               .Subscribe(WorkerError);
            _workerRestartSubscription = _eventManager.OfType<HttpWorkerRestartEvent>()
               .Subscribe(WorkerRestart);
        }

        // For tests
        internal HttpFunctionInvocationDispatcher()
        {
        }

        public FunctionInvocationDispatcherState State { get; private set; }

        public int ErrorEventsThreshold { get; private set; }

        internal async Task InitializeHttpWorkerChannelAsync(int attemptCount, CancellationToken cancellationToken = default)
        {
            _httpWorkerChannel = _httpWorkerChannelFactory.Create(_scriptOptions.RootScriptPath, _metricsLogger, attemptCount);
            await _httpWorkerChannel.StartWorkerProcessAsync(cancellationToken);
            _logger.LogDebug("Adding http worker channel. workerId:{id}", _httpWorkerChannel.Id);
            SetFunctionDispatcherStateToInitializedAndLog();
        }

        private void SetFunctionDispatcherStateToInitializedAndLog()
        {
            State = FunctionInvocationDispatcherState.Initialized;
            _logger.LogInformation("Worker process started and initialized.");
        }

        public Task InitializeAsync(IEnumerable<FunctionMetadata> functions, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (functions == null || !functions.Any())
            {
                // do not initialize function dispachter if there are no functions
                return Task.CompletedTask;
            }

            State = FunctionInvocationDispatcherState.Initializing;
            InitializeHttpWorkerChannelAsync(0, cancellationToken).Forget();
            return Task.CompletedTask;
        }

        public Task InvokeAsync(ScriptInvocationContext invocationContext)
        {
            return _httpWorkerChannel.InvokeAsync(invocationContext);
        }

        public void WorkerError(HttpWorkerErrorEvent workerError)
        {
            if (!_disposing)
            {
                _logger.LogDebug("Handling WorkerErrorEvent for workerId:{workerId}. Failed with: {exception}", workerError.WorkerId, workerError.Exception);
                AddOrUpdateErrorBucket(workerError);
                DisposeAndRestartWorkerChannel(workerError.WorkerId);
            }
        }

        public void WorkerRestart(HttpWorkerRestartEvent workerRestart)
        {
            if (!_disposing)
            {
                _logger.LogDebug("Handling WorkerRestartEvent for workerId:{workerId}", workerRestart.WorkerId);
                DisposeAndRestartWorkerChannel(workerRestart.WorkerId);
            }
        }

        public Task StartWorkerChannel()
        {
            // currently only one worker
            return Task.CompletedTask;
        }

        private void DisposeAndRestartWorkerChannel(string workerId)
        {
            // Since we only have one HTTP worker process, as soon as we dispose it, InvokeAsync will fail. Set state to
            // indicate we are not ready to receive new requests.
            State = FunctionInvocationDispatcherState.WorkerProcessRestarting;
            _logger.LogDebug("Disposing channel for workerId: {channelId}", workerId);
            if (_httpWorkerChannel != null)
            {
                (_httpWorkerChannel as IDisposable)?.Dispose();
            }

            RestartWorkerChannel(workerId);
        }

        private void RestartWorkerChannel(string workerId)
        {
            if (_invokerErrors.Count < ErrorEventsThreshold)
            {
                _logger.LogDebug("Restarting http invoker channel");
                InitializeHttpWorkerChannelAsync(_invokerErrors.Count).Forget();
            }
            else
            {
                _logger.LogError("Exceeded http worker restart retry count. Shutting down Functions Host");
                _applicationLifetime.StopApplication();
            }
        }

        private void AddOrUpdateErrorBucket(HttpWorkerErrorEvent currentErrorEvent)
        {
            if (_invokerErrors.TryPeek(out HttpWorkerErrorEvent top))
            {
                if ((currentErrorEvent.CreatedAt - top.CreatedAt) > thresholdBetweenRestarts)
                {
                    while (!_invokerErrors.IsEmpty)
                    {
                        _invokerErrors.TryPop(out HttpWorkerErrorEvent popped);
                        _logger.LogDebug($"Popping out errorEvent createdAt:{popped.CreatedAt} workerId:{popped.WorkerId}");
                    }
                }
            }
            _invokerErrors.Push(currentErrorEvent);
        }

        public async Task<IDictionary<string, WorkerStatus>> GetWorkerStatusesAsync()
        {
            var workerStatus = await _httpWorkerChannel.GetWorkerStatusAsync();
            return new Dictionary<string, WorkerStatus>
            {
                { _httpWorkerChannel.Id, workerStatus }
            };
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _logger.LogDebug($"Disposing {nameof(HttpFunctionInvocationDispatcher)}");
                _workerErrorSubscription.Dispose();
                _workerRestartSubscription.Dispose();
                (_httpWorkerChannel as IDisposable)?.Dispose();
                State = FunctionInvocationDispatcherState.Disposed;
                _disposed = true;
            }
        }

        public void Dispose()
        {
            _disposing = true;
            State = FunctionInvocationDispatcherState.Disposing;
            Dispose(true);
        }

        public Task ShutdownAsync()
        {
            return Task.CompletedTask;
        }

        public Task<bool> RestartWorkerWithInvocationIdAsync(string invocationId)
        {
            // Since there's only one channel for httpworker
            DisposeAndRestartWorkerChannel(_httpWorkerChannel.Id);
            return Task.FromResult(true);
        }

        public void PreShutdown()
        {
        }
    }
}
