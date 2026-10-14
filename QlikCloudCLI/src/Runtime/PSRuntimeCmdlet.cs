using System;
using System.Management.Automation;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using QlikCloud.Common;

namespace QlikCloudCLI.Runtime
{
    public abstract class PSRuntimeCmdlet : PSCmdlet, IDisposable
    {
        private readonly PowerShellLogger queue = new PowerShellLogger();
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly object cancellationGate = new object();
        private readonly CancellationToken operationToken;
        private bool disposed;
        private ILogger externalLogger;

        protected PSRuntimeCmdlet() => operationToken = cancellation.Token;

        [Parameter]
        public ILogger Logger { get => queue; set => externalLogger = value; }

        protected CancellationToken CancellationToken => operationToken;

        protected T AwaitOperation<T>(Task<T> task)
        {
            if (task == null) throw new InvalidOperationException("The Qlik Cloud operation did not start.");
            try
            {
                while (!task.IsCompleted)
                {
                    Task.WaitAny(new Task[] { task }, 100);
                    FlushLogs();
                }
                var result = task.GetAwaiter().GetResult();
                if (CancellationToken.IsCancellationRequested)
                {
                    // A completed download whose output is suppressed still owns its response stream.
                    (result as IDisposable)?.Dispose();
                    CancellationToken.ThrowIfCancellationRequested();
                }
                return result;
            }
            finally { FlushLogs(); }
        }

        protected virtual void FlushLogs() => FlushLogger(queue);

        protected void FlushLogger(PowerShellLogger logger)
        {
            if (logger == null) return;
            while (logger.TryRead(out var message))
            {
                externalLogger?.Log(message.Level, new EventId(0), message.Text, null, (text, error) => text);
                switch (message.Level)
                {
                    case LogLevel.Trace: WriteVerbose(message.Text); break;
                    case LogLevel.Debug: WriteDebug(message.Text); break;
                    case LogLevel.Information: WriteInformation(message.Text, new[] { "QlikCloud" }); break;
                    case LogLevel.Warning: WriteWarning(message.Text); break;
                    // Operations surface a single structured terminating error. Diagnostic entries
                    // must not turn successful output into a second, unrelated PowerShell error.
                    case LogLevel.Error:
                    case LogLevel.Critical: WriteVerbose(message.Text); break;
                }
            }
        }

        protected void TerminateOperation(Exception error, string errorId, string operation, object target = null)
        {
            while (error.InnerException != null && ((error is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
                || error is TargetInvocationException))
                error = error.InnerException;
            if (error is PipelineStoppedException) throw error;
            if (error is ActionPreferenceStopException) throw error;

            Exception safeError;
            ErrorCategory category;
            if (error is QlikCloud.RestClientBase.ApiException api)
            {
                safeError = new QlikCloudApiException(operation + " failed with HTTP status " + api.StatusCode + ".", api.StatusCode);
                errorId = "QlikCloudHttpError";
                category = api.StatusCode == 401 ? ErrorCategory.AuthenticationError
                    : api.StatusCode == 403 ? ErrorCategory.PermissionDenied
                    : api.StatusCode == 404 ? ErrorCategory.ObjectNotFound
                    : api.StatusCode == 429 ? ErrorCategory.LimitsExceeded
                    : api.StatusCode == 400 ? ErrorCategory.InvalidArgument : ErrorCategory.InvalidOperation;
            }
            else if (error is OperationCanceledException)
            {
                safeError = new OperationCanceledException("The Qlik Cloud operation was cancelled.");
                category = ErrorCategory.OperationStopped;
            }
            else if (error is HttpRequestException)
            {
                safeError = new HttpRequestException("The Qlik Cloud HTTP request failed.");
                category = ErrorCategory.ConnectionError;
            }
            else
            {
                // Exception messages and inner exceptions from transport/JSON libraries can
                // contain request payloads, bearer credentials, or complete response bodies.
                safeError = new InvalidOperationException(operation + " failed (" + error.GetType().Name + ").");
                category = ErrorCategory.InvalidOperation;
            }
            ThrowTerminatingError(new ErrorRecord(safeError, errorId, category, target));
        }

        protected override void StopProcessing()
        {
            lock (cancellationGate) if (!disposed) cancellation.Cancel();
        }
        protected override void EndProcessing() { FlushLogs(); base.EndProcessing(); }
        public void Dispose()
        {
            lock (cancellationGate)
            {
                if (disposed) return;
                disposed = true;
                cancellation.Dispose();
            }
        }
    }

    public sealed class QlikCloudApiException : Exception
    {
        public int StatusCode { get; }
        public QlikCloudApiException(string message, int statusCode) : base(message) => StatusCode = statusCode;
    }
}

namespace QlikCloud.Common
{
    public abstract class PSConnectionBase : QlikCloudCLI.Runtime.PSRuntimeCmdlet
    {
        private Connection requestedConnection;
        private Connection activeConnection;

        [Parameter]
        public Connection Connection
        {
            get => activeConnection ?? requestedConnection;
            set { requestedConnection = value; activeConnection = null; }
        }

        protected Connection RequireConnection()
        {
            // Resolve a default for every record without turning it into an explicit connection.
            activeConnection = requestedConnection ?? ConnectionManager.GetConnection();
            if (Connection == null || Connection.IsDisposed || Connection.QCSClient.Config?.httpClient == null)
                ThrowTerminatingError(new ErrorRecord(new InvalidOperationException(
                    "No active Qlik Cloud connection. Run Connect-QlikCloud or supply -Connection."),
                    "QlikCloudConnectionRequired", ErrorCategory.ConnectionError, null));
            return Connection;
        }

        protected override void FlushLogs()
        {
            base.FlushLogs();
            var connectionLogger = Connection?.Logger as PowerShellLogger;
            if (!ReferenceEquals(connectionLogger, Logger)) FlushLogger(connectionLogger);
        }
    }
}
