using System;
using System.Management.Automation;
using System.Threading.Tasks;
using IdentityModel.Client;
using QlikCloud.Clients;
using QlikCloud.Common;

namespace QlikCloudCLI.Runtime
{
    [Cmdlet(VerbsCommunications.Connect, "QlikCloud", DefaultParameterSetName = "OAuth")]
    [OutputType(typeof(Connection))]
    public sealed class ConnectQlikCloud : PSRuntimeCmdlet
    {
        [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
        [ValidateNotNullOrEmpty]
        public string Tenant { get; set; }

        [Parameter(Mandatory = true, ParameterSetName = "APIKey")]
        [ValidateNotNullOrEmpty]
        public string APIKey { get; set; }

        [Parameter(Mandatory = true, ParameterSetName = "JWTToken")]
        [ValidateNotNullOrEmpty]
        public string JWTToken { get; set; }

        [Parameter(ParameterSetName = "OAuth")]
        public string Scope { get; set; } = "user_default identity.name:read identity.email:read identity.subject:read identity.picture:read";

        [Parameter(ParameterSetName = "OAuth")]
        public string ClientID { get; set; }

        [Parameter(ParameterSetName = "OAuth")]
        public string ClientSecret { get; set; }

        [Parameter(ParameterSetName = "OAuth")]
        public SwitchParameter ClientCredentials { get; set; }

        [Parameter]
        public SwitchParameter PassThru { get; set; }

        protected override void ProcessRecord()
        {
            if (ParameterSetName == "JWTToken")
                ThrowTerminatingError(new ErrorRecord(new NotSupportedException(
                    "JWT authentication is not implemented by QlikCloud.dll. Use -APIKey or OAuth."),
                    "QlikCloudJwtNotSupported", ErrorCategory.NotImplemented, null));
            if (ClientCredentials.IsPresent && (string.IsNullOrWhiteSpace(ClientID) || string.IsNullOrWhiteSpace(ClientSecret)))
                ThrowTerminatingError(new ErrorRecord(new ArgumentException(
                    "-ClientCredentials requires both -ClientID and -ClientSecret."),
                    "QlikCloudClientCredentialsRequired", ErrorCategory.InvalidArgument, null));

            if (!Uri.TryCreate(Tenant.Contains("://") ? Tenant : "https://" + Tenant, UriKind.Absolute, out var tenant)
                || (tenant.Scheme != "https" && tenant.Scheme != "http") || string.IsNullOrEmpty(tenant.Host)
                || !string.IsNullOrEmpty(tenant.UserInfo) || tenant.AbsolutePath != "/"
                || !string.IsNullOrEmpty(tenant.Query) || !string.IsNullOrEmpty(tenant.Fragment))
                ThrowTerminatingError(new ErrorRecord(new ArgumentException("Tenant must be a tenant hostname or base HTTP(S) URL."),
                    "QlikCloudInvalidTenant", ErrorCategory.InvalidArgument, null));

            IRestClient client = null;
            try
            {
                var authority = tenant.GetLeftPart(UriPartial.Authority) + "/";
                if (ParameterSetName == "APIKey") client = new KeyClient(authority, APIKey, Logger);
                else
                {
                    var oauth = new OAuthClient(authority, ClientID, ClientSecret, Logger);
                    client = oauth;
                    var scope = Scope;
                    var credentials = ClientCredentials.IsPresent;
                    var succeeded = AwaitOperation(Task.Run(() =>
                    {
                        if (credentials) { oauth.ClientCredentials(scope); return true; }
                        return oauth.AuthorizationCodePKCE(scope);
                    }));
                    if (!succeeded) throw new InvalidOperationException("OAuth authentication failed.");
                }
                if (string.IsNullOrWhiteSpace(client.Config?.httpClient?.DefaultRequestHeaders.Authorization?.Parameter))
                    throw new InvalidOperationException("Authentication did not produce an access token.");
                var connection = new Connection(client, Logger);
                if (PassThru.IsPresent) WriteObject(connection);
                else ConnectionManager.SetConnection(connection);
                client = null; // The returned/default connection now owns the client transport.
            }
            catch (Exception error) { TerminateOperation(error, "QlikCloudConnectFailed", "Connecting to Qlik Cloud"); }
            finally { client?.Config?.httpClient?.Dispose(); FlushLogs(); }
        }
    }

    [Cmdlet(VerbsCommon.Get, "QCConnection")]
    [OutputType(typeof(Connection))]
    public sealed class GetQlikCloudConnection : PSConnectionBase
    {
        protected override void ProcessRecord() => WriteObject(RequireConnection());
    }

    [Cmdlet(VerbsCommunications.Disconnect, "QlikCloud", SupportsShouldProcess = true)]
    public sealed class DisconnectQlikCloud : PSConnectionBase
    {
        protected override void ProcessRecord()
        {
            var connection = Connection ?? ConnectionManager.GetConnection();
            if (connection == null || !ShouldProcess(connection.ToString(), "Disconnect Qlik Cloud")) return;
            ConnectionManager.ClearConnection(connection);
            connection.Dispose();
        }
    }

    [Cmdlet(VerbsCommunications.Receive, "QCToken")]
    [OutputType(typeof(TokenResponse))]
    public sealed class ReceiveQlikCloudToken : PSConnectionBase
    {
        [Parameter]
        public QlikCloud.Oauth.OauthTokenExchangePurpose TokenPurpose { get; set; } = QlikCloud.Oauth.OauthTokenExchangePurpose.Websocket;

        protected override void ProcessRecord()
        {
            var connection = RequireConnection();
            if (!(connection.QCSClient is OAuthClient oauth))
                ThrowTerminatingError(new ErrorRecord(new InvalidOperationException("Token exchange requires an OAuth connection."),
                    "QlikCloudOAuthConnectionRequired", ErrorCategory.InvalidOperation, null));
            else
                try
                {
                    var purpose = TokenPurpose;
                    var result = AwaitOperation(Task.Run(() => oauth.TokenExchange(purpose)));
                    if (result == null || result.IsError || string.IsNullOrWhiteSpace(result.AccessToken))
                        throw new InvalidOperationException("OAuth token exchange failed.");
                    WriteObject(result);
                }
                catch (Exception error) { TerminateOperation(error, "QlikCloudTokenExchangeFailed", "Exchanging the OAuth token"); }
        }
    }

    [Cmdlet(VerbsCommon.Get, "QlikUserAgent")]
    [OutputType(typeof(string))]
    public sealed class GetQlikUserAgent : PSCmdlet
    {
        protected override void ProcessRecord() => WriteObject(QlikCloud.Utilities.Info.GetUserAgent("QlikCloudCLI"));
    }
}
