using Qlik.Engine;
using QlikCloud.Oauth;
using System.Collections.Generic;
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Management.Automation;
using Qlik.Engine.Communication;
using Qlik.Sense.JsonRpc;
using QlikCloud.Clients;
using QlikCloud.Common;
using QcsLocation = Qlik.Engine.QcsLocation;

namespace QlikCloudCLI
{
    [Cmdlet(VerbsLifecycle.Invoke, "OpenApp")]
    [OutputType(typeof(IApp))]
    public class EngineApp : PSConnectionBase
    {
        [Parameter(
            Mandatory = true,
            Position = 0,
            ValueFromPipeline = true,
            ValueFromPipelineByPropertyName = true)]
        public string AppId { get; set; }
        [Parameter(
            Mandatory = false,
            Position = 1,
            ValueFromPipeline = true,
            ValueFromPipelineByPropertyName = true)]
        public bool NoData { get; set; }
        [Parameter(
            Mandatory = false,
            Position = 2,
            ValueFromPipeline = true,
            ValueFromPipelineByPropertyName = true)]
        public bool LogWS { get; set; }

        public async Task<IApp> OpenQlikApp(string Id, bool logging = false)
        {
            var baseAddress = Connection.QCSClient.Config.httpClient.BaseAddress;
            var authority = $"{baseAddress.Scheme}://{baseAddress.Authority}";
            var location = QcsLocation.FromUri(authority);

            // Determine the type of QCSClient and handle accordingly
            if (Connection.QCSClient is OAuthClient)
            {
                var oAuthTokenExchange = ((OAuthClient)Connection.QCSClient).TokenExchange(OauthTokenExchangePurpose.Websocket);
                if (oAuthTokenExchange == null || oAuthTokenExchange.IsError || string.IsNullOrWhiteSpace(oAuthTokenExchange.AccessToken))
                    throw new InvalidOperationException("Could not obtain an OAuth token for the engine connection.");
                location.CustomUserUrlParameters.Add("accessToken", oAuthTokenExchange.AccessToken);
                location.AsManualConfiguration();
            }
            else if (this.Connection.QCSClient is JWTClient)
            {
                location.AsApiKey(((JWTClient)Connection.QCSClient).JwtToken);
            }
            else if (Connection.QCSClient is KeyClient)
            {
                location.AsApiKey(((KeyClient)Connection.QCSClient).ApiKey);
            }
            else
            {
                throw new InvalidOperationException("Unsupported QCSClient type.");
            }
            Logger.LogInformation("Opening engine app {AppId}", Id);
            IApp qcsuploadedApp;
            try
            {
                qcsuploadedApp = await location.AppAsync(Id, null, NoData).ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    qcsuploadedApp = await location.AppAsync(Id, null, NoData).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    Logger.LogError("Failed to open engine app {AppId}", Id);
                    throw;
                }
            }

            if (logging)
            {
                var sessionlog = new QlikSessionHandler(qcsuploadedApp.Session, Connection.Logger);
            }
            return qcsuploadedApp;
        }

        protected override void BeginProcessing()
        {
            base.BeginProcessing();
        }

        // This method will be called for each input received from the pipeline to this cmdlet; if no input is received, this method is not called
        protected override void ProcessRecord()
        {
            try
            {
                RequireConnection();
                var app = AwaitOperation(OpenQlikApp(AppId, LogWS));
                WriteObject(app);
            }catch(Exception ex)
            {
                TerminateOperation(ex, "OpenAppError", "Opening the engine app", AppId);
            }
        }

        // This method will be called once at the end of pipeline execution; if no input is received, this method is not called
        protected override void EndProcessing()
        {
            base.EndProcessing();
        }

    }
    public class QlikSessionHandler : IDisposable
    {
        private readonly IQlikConnection _session;
        private readonly ILogger _logger;
        private bool _disposed;

        public QlikSessionHandler(IQlikConnection session, ILogger logger)
        {
            _session = session;
            _logger = logger ?? NullLogger.Instance;
            _logger.LogDebug("Initializing QlikSessionHandler.");
            // Attach event handlers

            _session.ConnectionClosingEvent += OnConnectionClosing;
            _session.CommunicationErrorEvent += OnCommunicationError;
            _session.ObjectChangedMessageEvent += OnObjectChanged;
            _session.ObjectClosedMessageEvent += OnObjectClosed;
            _session.PushMessageEvent += OnPushMessage;
            _session.PushMethodReceivedEvent += OnPushMethodReceived;
            _session.ResponseEvent += OnResponseReceived;
        }

        private void OnConnectionClosing(object sender, EventArgs e)
        {

            _logger.LogDebug("Connection is closing.");
            Dispose();
        }

        private void OnCommunicationError(object sender, CommunicationErrorEventArgs e)
        {
            _logger.LogDebug("Engine communication error.");
        }

        private void OnObjectChanged(object sender, IEnumerable<int> e)
        {
            _logger.LogDebug("Engine objects changed: {ObjectIds}", string.Join(",", e));
        }

        private void OnObjectClosed(object sender, IEnumerable<int> e)
        {
            _logger.LogDebug("Engine objects closed: {ObjectIds}", string.Join(",", e));
        }

        private void OnPushMessage(object sender, PushMessageEventArgs e)
        {
            _logger.LogDebug("Engine push message received.");
        }

        private void OnPushMethodReceived(object sender, PushMethodReceivedEventArgs e)
        {
            _logger.LogDebug("Engine push method received: {Method}", e.Method);
        }

        private void OnResponseReceived(object sender, Response e)
        {
            _logger.LogDebug("Engine response received.");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _session.ConnectionClosingEvent -= OnConnectionClosing;
            _session.CommunicationErrorEvent -= OnCommunicationError;
            _session.ObjectChangedMessageEvent -= OnObjectChanged;
            _session.ObjectClosedMessageEvent -= OnObjectClosed;
            _session.PushMessageEvent -= OnPushMessage;
            _session.PushMethodReceivedEvent -= OnPushMethodReceived;
            _session.ResponseEvent -= OnResponseReceived;
        }
    }
}
