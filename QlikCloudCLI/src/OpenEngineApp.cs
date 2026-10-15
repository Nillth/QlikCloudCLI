using System;
using System.Management.Automation;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Qlik.Engine.PowerShell;
using QlikCloud.Clients;
using QlikCloud.Common;
using QlikCloud.Oauth;

namespace QlikCloudCLI
{
    [Cmdlet(VerbsCommon.Open, "QSEApp")]
    [OutputType("Qlik.Engine.IApp")]
    public sealed class OpenQseApp : EngineOpenAppCmdletBase
    {
        protected override object ResolveConnection(object connection) => CloudEngineConnection.Resolve(connection);
        protected override Task<object> OpenAppAsync(object connection, string appId, bool noData) =>
            CloudEngineConnection.OpenAsync((Connection)connection, appId, noData, TrackOpeningResource);
    }

    // Retain the original spelling and Boolean parameters for existing scripts.
    [Cmdlet(VerbsLifecycle.Invoke, "OpenApp")]
    [OutputType("Qlik.Engine.IApp")]
    public sealed class EngineApp : EngineCmdletBase
    {
        [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true, ValueFromPipelineByPropertyName = true)]
        [ValidateNotNullOrEmpty]
        public string AppId { get; set; }
        [Parameter]
        public Connection Connection { get; set; }
        [Parameter(Position = 1, ValueFromPipelineByPropertyName = true)]
        public bool NoData { get; set; }
        [Parameter(Position = 2, ValueFromPipelineByPropertyName = true)]
        public bool LogWS { get; set; }
        [Parameter]
        public ILogger Logger { get; set; }

        protected override void ProcessRecord()
        {
            Connection connection;
            try { connection = CloudEngineConnection.Resolve(Connection); }
            catch (Exception exception) { Fail(exception, "QSEConnectionFailed"); return; }
            if (LogWS) WriteVerbose("Opening an isolated Engine session. Credentials and WebSocket messages are not logged.");
            WriteObject(OpenOwnedApp(connection, AppId, NoData,
                () => CloudEngineConnection.OpenAsync(connection, AppId, NoData, TrackOpeningResource)));
        }
    }

    internal static class CloudEngineConnection
    {
        internal static Connection Resolve(object value)
        {
            if (value is PSObject wrapped) value = wrapped.BaseObject;
            if (value != null && !(value is Connection))
                throw new EngineOperationException("Use the connection returned by Connect-QlikCloud with this module's Open-QSEApp.");
            var connection = (Connection)value ?? ConnectionManager.GetConnection();
            if (connection == null || connection.IsDisposed)
                throw new EngineOperationException("No active Qlik Cloud connection. Run Connect-QlikCloud or supply -Connection.");
            return connection;
        }

        internal static Task<object> OpenAsync(Connection connection, string appId, bool noData, Action<IDisposable> trackOpeningResource)
        {
            EngineRuntime.EnsureLoaded();
            return Task.Run(() => Open(connection, appId, noData, trackOpeningResource));
        }

        // SDK types stay inside a lazily invoked method: importing cmdlet metadata
        // does not require resolving the Engine dependency.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static object Open(Connection connection, string appId, bool noData, Action<IDisposable> trackOpeningResource)
        {
            if (connection.IsDisposed) throw new ObjectDisposedException(nameof(Connection));
            var authority = connection.QCSClient.Config.httpClient.BaseAddress.GetLeftPart(UriPartial.Authority);
            var location = Qlik.Engine.QcsLocation.FromUri(authority);
            if (connection.QCSClient is OAuthClient oauth)
            {
                var token = oauth.TokenExchange(OauthTokenExchangePurpose.Websocket);
                if (token == null || token.IsError || string.IsNullOrWhiteSpace(token.AccessToken))
                    throw new EngineOperationException("Could not obtain an OAuth token for the Engine connection.");
                location.CustomUserUrlParameters.Add("accessToken", token.AccessToken);
                location.AsManualConfiguration();
            }
            else if (connection.QCSClient is KeyClient key)
                location.AsApiKey(key.ApiKey);
            else
                throw new EngineOperationException("Engine access requires an API-key or OAuth Cloud connection.");

            Qlik.Engine.Communication.IQlikConnection partialSession = null;
            try
            {
                // A unique token isolates selection state from other opens and browsers.
                return location.AppAsync(new Qlik.Engine.AsyncHandle(), appId,
                    hub => { partialSession = hub.Session; trackOpeningResource(partialSession); },
                    Qlik.Engine.SessionToken.Unique(), noData)
                    .GetAwaiter().GetResult();
            }
            catch
            {
                partialSession?.Dispose();
                throw;
            }
        }
    }
}
