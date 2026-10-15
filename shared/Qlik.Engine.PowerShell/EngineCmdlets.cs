using System;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Qlik.Engine.PowerShell
{
    public abstract class EngineCmdletBase : PSCmdlet
    {
        private readonly CancellationTokenSource stopped = new CancellationTokenSource();
        private EngineOpeningScope openingScope;

        [Parameter]
        [ValidateRange(1, 86400)]
        public int TimeoutSeconds { get; set; } = 120;

        protected Guid CurrentRunspaceId
        {
            get
            {
                if (Runspace.DefaultRunspace == null) throw new EngineOperationException("A PowerShell runspace is required.");
                return Runspace.DefaultRunspace.InstanceId;
            }
        }

        protected override void StopProcessing() { stopped.Cancel(); }

        protected void TrackOpeningResource(IDisposable resource)
        {
            EngineOpeningScope scope = openingScope;
            if (scope == null) throw new EngineOperationException("An Engine open operation must be active before tracking its transport.");
            scope.Track(resource);
        }

        protected object OpenOwnedApp(object owner, string appId, bool noData, Func<Task<object>> factory)
        {
            Task<object> opening = null;
            IDisposable pending = null;
            var scope = new EngineOpeningScope();
            openingScope = scope;
            try
            {
                if (owner == null) throw new EngineOperationException("A connection is required. Connect first or supply -Connection.");
                Guid runspace = CurrentRunspaceId;
                pending = EngineSessionRegistry.BeginOpen(owner, runspace, scope.CancelAsync);
                // Authentication and SDK setup can also block before returning a task.
                opening = Task.Run(() =>
                {
                    stopped.Token.ThrowIfCancellationRequested();
                    scope.ThrowIfCancelled();
                    return factory();
                });
                object app = Await(opening, () => EngineSessionRegistry.Observe(scope.CancelAsync()));
                try { EngineSessionRegistry.Register(app, owner, runspace); }
                catch { EngineSessionRegistry.DisposeUnregistered(app); throw; }
                if (stopped.IsCancellationRequested || !scope.Transfer())
                {
                    EngineSessionRegistry.Abandon(app, runspace);
                    throw new OperationCanceledException();
                }
                return app;
            }
            catch (Exception exception)
            {
                EngineSessionRegistry.Observe(scope.CancelAsync());
                if (opening != null && (exception is TimeoutException || exception is OperationCanceledException))
                    opening.ContinueWith(completed =>
                    {
                        if (completed.Status == TaskStatus.RanToCompletion) EngineSessionRegistry.DisposeUnregistered(completed.Result);
                        else { var ignored = completed.Exception; }
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                Fail(exception, "QSEOpenAppFailed");
                return null;
            }
            finally { if (pending != null) pending.Dispose(); }
        }

        protected T Await<T>(Task<T> operation, Action abandon)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
                Task signal = Task.Delay(Timeout.Infinite, deadline.Token);
                Task completed = Task.WhenAny(operation, signal).GetAwaiter().GetResult();
                if (!ReferenceEquals(completed, operation) || stopped.IsCancellationRequested)
                {
                    if (abandon != null) abandon();
                    EngineSessionRegistry.Observe(operation);
                    if (stopped.IsCancellationRequested) throw new OperationCanceledException();
                    throw new TimeoutException();
                }
                deadline.Cancel();
                return operation.GetAwaiter().GetResult();
            }
        }

        protected void Fail(Exception exception, string id, string operation = null)
        {
            if (exception is PipelineStoppedException || exception is ActionPreferenceStopException)
                ExceptionDispatchInfo.Capture(exception).Throw();
            string message = exception is EngineRuntimeException || exception is EngineOperationException ? exception.Message : exception is TimeoutException
                ? "The Engine operation timed out. Its session has been closed; open the app again explicitly."
                : exception is OperationCanceledException
                    ? "The Engine operation was cancelled. Its session has been closed."
                    : exception is ObjectDisposedException
                        ? "The Engine app or its owning connection has been closed. Open the app again explicitly."
                        : FailureMessage(exception, operation);
            ThrowTerminatingError(new ErrorRecord(new InvalidOperationException(message), id,
                exception is TimeoutException ? ErrorCategory.OperationTimeout : ErrorCategory.InvalidOperation, null));
        }

        private static string FailureMessage(Exception exception, string operation)
        {
            string[] known = { "AppLayout", "Script", "TestScript", "Field", "Table", "TestExpression", "Expression", "SheetList", "BookmarkList", "DimensionList", "MeasureList", "MasterObjectList", "FieldList", "VariableList" };
            string context = Array.IndexOf(known, operation) >= 0 ? "Engine " + operation + " operation" : "Engine operation";
            // Retain only the numeric SDK error code, never a server message,
            // expression, request payload, endpoint, or arbitrary inner exception.
            for (int depth = 0; exception != null && depth < 8; depth++, exception = exception.InnerException)
            {
                string type = exception.GetType().FullName;
                if (type != "Qlik.Engine.MethodInvocationException" && type != "Qlik.Sense.JsonRpc.MethodInvocationException") continue;
                try
                {
                    object error = ReadProperty(exception, "InvocationError");
                    object value = ReadProperty(error, "Code");
                    if (value is int || value is long || value is short || (value != null && value.GetType().IsEnum))
                        return context + " failed (Engine error " + Convert.ToInt64(value).ToString(System.Globalization.CultureInfo.InvariantCulture) + "). Check the requested object's existence, expression syntax, and Engine permissions.";
                }
                catch { }
            }
            return context + " failed. Check the connection, requested object, and Engine permissions. No request was retried.";
        }

        private static object ReadProperty(object value, string name)
        {
            if (value == null) return null;
            // Qlik.Engine hides the JSON-RPC properties with strongly typed
            // versions; lookup by name alone can throw AmbiguousMatchException.
            for (Type type = value.GetType(); type != null; type = type.BaseType)
            {
                var property = type.GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
                if (property != null)
                {
                    try { return property.GetValue(value, null); }
                    catch { /* Older SDK paths can store a base JSON-RPC error. */ }
                }
            }
            return null;
        }
    }

    public abstract class EngineOpenAppCmdletBase : EngineCmdletBase
    {
        [Parameter]
        [Alias("QlikClient")]
        public object Connection { get; set; }

        [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
        [ValidateNotNullOrEmpty]
        public string AppId { get; set; }

        [Parameter]
        public SwitchParameter NoData { get; set; }

        protected abstract object ResolveConnection(object connection);
        protected abstract Task<object> OpenAppAsync(object connection, string appId, bool noData);

        protected override void ProcessRecord()
        {
            object owner;
            try { owner = ResolveConnection(Connection); }
            catch (Exception exception) { Fail(exception, "QSEConnectionFailed"); return; }
            WriteObject(OpenOwnedApp(owner, AppId, NoData.IsPresent, () => OpenAppAsync(owner, AppId, NoData.IsPresent)), false);
        }
    }

    public abstract class EngineAppCmdletBase : EngineCmdletBase
    {
        [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
        [ValidateNotNull]
        public object App { get; set; }

        protected object NativeApp { get { return App is PSObject ? ((PSObject)App).BaseObject : App; } }

        protected void Read(EngineReadRequest request)
        {
            try
            {
                object app = NativeApp;
                Guid runspace = CurrentRunspaceId;
                EngineReadResult result = Await(EngineSessionRegistry.ReadAsync(app, runspace, request), () => EngineSessionRegistry.Abandon(app, runspace));
                WriteObject(result.Value, result.Enumerate);
            }
            catch (Exception exception) { Fail(exception, "QSEReadFailed", request.Operation); }
        }
    }

    [Cmdlet(VerbsCommon.Close, "QSEApp")]
    public sealed class CloseQSEApp : EngineAppCmdletBase
    {
        protected override void ProcessRecord()
        {
            try { Await(Complete(EngineSessionRegistry.CloseAsync(NativeApp, CurrentRunspaceId)), null); }
            catch (Exception exception) { Fail(exception, "QSECloseFailed"); }
        }
        private static async Task<bool> Complete(Task task) { await task.ConfigureAwait(false); return true; }
    }

    [Cmdlet(VerbsCommon.Get, "QSEAppLayout")]
    public sealed class GetQSEAppLayout : EngineAppCmdletBase
    { protected override void ProcessRecord() { Read(new EngineReadRequest { Operation = "AppLayout" }); } }

    [Cmdlet(VerbsCommon.Get, "QSEScript")]
    public sealed class GetQSEScript : EngineAppCmdletBase
    { protected override void ProcessRecord() { Read(new EngineReadRequest { Operation = "Script" }); } }

    [Cmdlet(VerbsDiagnostic.Test, "QSEScript")]
    public sealed class TestQSEScript : EngineAppCmdletBase
    { protected override void ProcessRecord() { Read(new EngineReadRequest { Operation = "TestScript" }); } }

    [Cmdlet(VerbsCommon.Get, "QSEField")]
    public sealed class GetQSEField : EngineAppCmdletBase
    {
        [Parameter(Position = 1)] [ValidateNotNullOrEmpty] public string Name { get; set; }
        [Parameter] public string State { get; set; } = "$";
        protected override void ProcessRecord() { Read(new EngineReadRequest { Operation = "Field", Name = Name, State = State }); }
    }

    [Cmdlet(VerbsCommon.Get, "QSETable")]
    public sealed class GetQSETable : EngineAppCmdletBase
    {
        [Parameter(Position = 1)] [ValidateNotNullOrEmpty] public string Name { get; set; }
        protected override void ProcessRecord() { Read(new EngineReadRequest { Operation = "Table", Name = Name }); }
    }

    public abstract class EngineExpressionCmdletBase : EngineAppCmdletBase
    {
        [Parameter(Mandatory = true, Position = 1)] [ValidateNotNullOrEmpty]
        public string Expression { get; set; }
    }

    [Cmdlet(VerbsDiagnostic.Test, "QSEExpression")]
    public sealed class TestQSEExpression : EngineExpressionCmdletBase
    { protected override void ProcessRecord() { Read(new EngineReadRequest { Operation = "TestExpression", Expression = Expression }); } }

    [Cmdlet(VerbsLifecycle.Invoke, "QSEExpression")]
    public sealed class InvokeQSEExpression : EngineExpressionCmdletBase
    { protected override void ProcessRecord() { Read(new EngineReadRequest { Operation = "Expression", Expression = Expression }); } }

    public abstract class EngineListCmdletBase : EngineAppCmdletBase
    {
        [Parameter] public SwitchParameter AsSdkObject { get; set; }
        protected abstract string ListKind { get; }
        protected override void ProcessRecord() { Read(new EngineReadRequest { Operation = ListKind + "List", AsSdkObject = AsSdkObject.IsPresent }); }
    }
    [Cmdlet(VerbsCommon.Get, "QSESheetList")]
    public sealed class GetQSESheetList : EngineListCmdletBase { protected override string ListKind { get { return "Sheet"; } } }
    [Cmdlet(VerbsCommon.Get, "QSEBookmarkList")]
    public sealed class GetQSEBookmarkList : EngineListCmdletBase { protected override string ListKind { get { return "Bookmark"; } } }
    [Cmdlet(VerbsCommon.Get, "QSEDimensionList")]
    public sealed class GetQSEDimensionList : EngineListCmdletBase { protected override string ListKind { get { return "Dimension"; } } }
    [Cmdlet(VerbsCommon.Get, "QSEMeasureList")]
    public sealed class GetQSEMeasureList : EngineListCmdletBase { protected override string ListKind { get { return "Measure"; } } }
    [Cmdlet(VerbsCommon.Get, "QSEMasterObjectList")]
    public sealed class GetQSEMasterObjectList : EngineListCmdletBase { protected override string ListKind { get { return "MasterObject"; } } }
    [Cmdlet(VerbsCommon.Get, "QSEFieldList")]
    public sealed class GetQSEFieldList : EngineListCmdletBase { protected override string ListKind { get { return "Field"; } } }
    [Cmdlet(VerbsCommon.Get, "QSEVariableList")]
    public sealed class GetQSEVariableList : EngineListCmdletBase { protected override string ListKind { get { return "Variable"; } } }
}
