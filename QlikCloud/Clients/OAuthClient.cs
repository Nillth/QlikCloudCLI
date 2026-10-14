using IdentityModel;
using IdentityModel.Client;
using IdentityModel.OidcClient;
using IdentityModel.OidcClient.Browser;
using Microsoft.Extensions.Logging;
using QlikCloud.Oauth;
using QlikCloud.Tenants;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QlikCloud.Clients
{
    public interface IOAuthClient: IRestClient
    {
        IdentityModel.Client.TokenResponse TokenExchangeAsync(Oauth.OauthTokenExchangePurpose Purpose);
        IOAuthClient Impersonation(string identifier);
        void RefreshCode();
    }

    public class OAuthClient : RestClient<OAuthClient>, IOAuthClient
    {
        private string ClientId { get; }
        private readonly Browser _browser;
        private OidcClient _oidcClient;
        private readonly int _port;
        private string _refreshToken;
        public bool Connected = false;
        public LoginResult LoginResult;
        internal OAuthCredentials oauthCredentials;
        internal bool Authenticated => !LoginResult.IsError;
        internal string _accessToken;
        protected string _scope =
    "user_default offline_access identity.name:read identity.email:read identity.subject:read identity.picture:read admin.users admin.spaces";

        public OAuthClient(string tenant, string clientId = default, string clientSecret = default, ILogger logger = default) : base(tenant, logger)
        {
            if (string.IsNullOrWhiteSpace(clientId))
                throw new ArgumentException("Supply the client ID of your own Qlik OAuth registration. No default OAuth registration is configured.", nameof(clientId));

            oauthCredentials = OAuthCredentials.CreateInstance(clientId, clientSecret);
            _port = Global.GetRandomUnusedPort();
            _browser = new Browser(_port);
        }
        private readonly Timer _refreshTimer;
        public OAuthClient(string tenant, string accessToken, OAuthCredentials _oauthCredentials, ILogger logger = default) : base(tenant, logger)
        {
            _accessToken = accessToken;
            oauthCredentials = _oauthCredentials;
            Authenticate();
            _refreshTimer = new Timer(async _ => await AutoRefreshToken(), null, TimeSpan.FromMinutes(300), TimeSpan.FromMinutes(300));
        }

        private async Task AutoRefreshToken()
        {
            if (!string.IsNullOrEmpty(_refreshToken))
            {
                await RefreshCodeAsync();
            }
        }


        internal void AuthorizationCode(string scope)
        {
            AuthorizationCodeAsync(scope).Wait();
        }
        public bool AuthorizationCodePKCE(string scope)
        {
            Task.Run(async () => await AuthorizationCodeAsyncPKCE(scope).ConfigureAwait(false)).GetAwaiter().GetResult();
            return true;
        }
        internal async Task AuthorizationCodeAsyncPKCE(string scope)
        {
            var options = new OidcClientOptions
            {
                Authority = TenantUri.AbsoluteUri,
                ClientId = oauthCredentials.clientId,
                Scope = scope,
                RedirectUri = $"http://127.0.0.1:{_port}/callback/",
                Browser = _browser,
                LoadProfile = false,
                LoggerFactory = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance
            };

            if (!string.IsNullOrWhiteSpace(oauthCredentials.clientSecret))
            {
                options.ClientSecret = oauthCredentials.clientSecret;
            }
            _oidcClient = new OidcClient(options);

            var result = await _oidcClient.LoginAsync(new LoginRequest());
            LoginResult = result;
            if (result.IsError == false)
            {
                _accessToken = result.AccessToken;
                _refreshToken = result.RefreshToken;
                Authenticate();
                _logger?.LogTrace("{@result}", result);
            }
            else
            {
                _logger?.LogError("Error: {@error}", result.Error);
                throw new Exception(result.Error);
            }

        }
        internal async Task AuthorizationCodeAsync(string scope) //working
        {
            var options = new OidcClientOptions
            {
                Authority = TenantUri.AbsoluteUri,
                ClientId = oauthCredentials.clientId,
                Scope = scope,
                ClientSecret = oauthCredentials.clientSecret,
                RedirectUri = $"http://127.0.0.1:{_port}/callback/",
                Browser = _browser,
                LoadProfile = false
            };
            _oidcClient = new OidcClient(options);
            // Use await instead of .Result to asynchronously wait for the operation to complete

            var result = await _oidcClient.LoginAsync(new LoginRequest());
            _accessToken = result.AccessToken;
            _refreshToken = result.RefreshToken;
            Authenticate();
            _logger?.LogTrace("{@result}", result);
        }

        public async Task RefreshCodeAsync()
        {
            if (_refreshToken != default && _oidcClient != default)
            {
                var result = await _oidcClient.RefreshTokenAsync(_refreshToken);
                _accessToken = result.AccessToken;
                _refreshToken = result.RefreshToken;
                Config.httpClient.SetBearerToken(_accessToken);
            }
        }

        public void ClientCredentials(string scope)
        {
            ClientCredentialsAsync(scope).Wait();
        }

        private async Task ClientCredentialsAsync(string scope)
        {
            _scope = scope;
            var options = new ClientCredentialsTokenRequest
            {
                ClientId = oauthCredentials.clientId,
                Scope = scope,
                ClientSecret = oauthCredentials.clientSecret,
                GrantType = OidcConstants.GrantTypes.ClientCredentials,
                Address = TenantUri.AbsoluteUri + "oauth/token",
                ClientCredentialStyle = ClientCredentialStyle.AuthorizationHeader
            };
            try
            {
                _logger?.LogDebug("Options: {@options}", options);
                var tr = await Config.httpClient.RequestClientCredentialsTokenAsync(options);
                _accessToken = tr.AccessToken;
                _refreshToken = tr.RefreshToken;
                Authenticate();
            }
            catch (Exception ex)
            {
                _logger?.LogError("Error requesting client credentials: {@Error}", ex);
            }
        }

        public IdentityModel.Client.TokenResponse TokenExchange(QlikCloud.Oauth.OauthTokenExchangePurpose Purpose)
        {
            return TokenExchangeAsync(Purpose).Result;
        }
        protected async Task<IdentityModel.Client.TokenResponse> TokenExchangeAsync(QlikCloud.Oauth.OauthTokenExchangePurpose Purpose)
        {
            IdentityModel.Client.TokenResponse tokenResponse = default;
            try
            {
                using (var httpClient = GetClient(TenantUri))
                {
                    var tokenClientOptions = new TokenClientOptions();
                    tokenClientOptions.ClientId = oauthCredentials.clientId;
                    tokenClientOptions.Address = "oauth/token";
                    var tokenClient = new TokenClient(httpClient, tokenClientOptions);
                    string _purpose = Purpose.ToString();
                    if (Purpose == QlikCloud.Oauth.OauthTokenExchangePurpose.Webresource)
                    {
                        _purpose = "webresource";
                    }
                    else
                    {
                        _purpose = "websocket";
                    }

                    tokenResponse = await tokenClient.RequestTokenAsync("urn:ietf:params:oauth:grant-type:token-exchange", new Parameters { { "purpose", _purpose }, { "subject_token", _accessToken }, { "subject_token_type", "urn:ietf:params:oauth:token-type:access_token" } });
                    if (tokenResponse.IsError)
                    {
                        _logger?.LogError("Error requesting client credentials: {@Error}", tokenResponse.Error);
                    }
                    else
                    {
                        //return new APIClient(new OAuthClient(Tenant.AbsoluteUri, tokenResponse.AccessToken));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError("Error requesting client credentials: {@Error}", ex);
            }
            return tokenResponse;
        }

        public OAuthClient Impersonation(string identifier)
        {
            try
            {
                var httpClient = GetClient(TenantUri);
                var tokenClientOptions = new TokenClientOptions();
                tokenClientOptions.ClientId = oauthCredentials.clientId;
                tokenClientOptions.ClientSecret = oauthCredentials.clientSecret;
                tokenClientOptions.Address = "oauth/token";
                var tokenClient = new TokenClient(httpClient, tokenClientOptions);
                var tokenResponse = tokenClient.RequestTokenAsync("urn:qlik:oauth:user-impersonation", new Parameters { { "user_lookup.field", "userId" }, { "user_lookup.value", identifier }, { "scope", "user_default" } }).Result;
                if (tokenResponse.IsError)
                {
                    _logger?.LogError("Error requesting client credentials: {@Error}", tokenResponse.Error);
                }
                else
                {
                    return new OAuthClient(TenantUri.AbsoluteUri, tokenResponse.AccessToken, oauthCredentials);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError("Error requesting client credentials: {@Error}", ex);
            }
            return null;
        }

        public override bool Authenticate()
        {
            try
            {
                Config.httpClient.SetBearerToken(_accessToken);
                base.Initialize();
            }
            catch (Exception ex)
            {
                _logger?.LogError("Error authenticating client: {@Error}", ex);
                return false;
            }
            return true;
        }

        TokenResponse IOAuthClient.TokenExchangeAsync(OauthTokenExchangePurpose Purpose)
        {
            return TokenExchangeAsync(Purpose).Result;
        }

        IOAuthClient IOAuthClient.Impersonation(string identifier)
        {
            return Impersonation(identifier);
        }

        public void RefreshCode()
        {
            throw new NotImplementedException();
        }
    }

}
