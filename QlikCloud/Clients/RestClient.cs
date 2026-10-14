using Microsoft.Extensions.Logging;
using QlikCloud.Utilities;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;

namespace QlikCloud.Clients
{
    public abstract partial class RestClient<T> : IRestClient
    {
        public RestClientConfig Config { get; } = new();
        protected readonly ILogger _logger;
        public ILogger Logger { get { return _logger; } }

        protected Uri TenantUri { get; }
        protected Uri ApiUri => new Uri($"{TenantUri.AbsoluteUri}api/");

        protected RestClient(string tenant, ILogger logger = default)
        {
            _logger = logger ?? Global.MELoggingInstance<T>();
            _logger.LogTrace("RestClient: Constructor");
            TenantUri = FormatURI(tenant);
            Config.httpClient = GetClient(ApiUri);
        }
        public abstract bool Authenticate();

        protected virtual void Initialize()
        {
            _logger?.LogTrace("RestClient: Initialize");
            // Base initialization logic, if any
            ClientInitialize();
        }

        private Uri FormatURI(string uri)
        {
            _logger?.LogTrace("RestClient: FormatURI");
            Uri tempuri;
            if (Uri.IsWellFormedUriString(uri, UriKind.Absolute))
                tempuri = new Uri(uri);
            else
                tempuri = new Uri($"https://{uri}");
            return tempuri;
        }
        protected HttpClient GetClient(Uri baseaddress)
        {
            Info._client = typeof(T).Name;
            _logger?.LogTrace("RestClient: GetClient");
            return new HttpClient(new ExtensionLoggingHandler(_logger, new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer() }))
            {
                BaseAddress = baseaddress,
                DefaultRequestHeaders = { { "User-Agent", Info.GetUserAgent(typeof(T).Name) } },
                //Timeout = System.Threading.Timeout.InfiniteTimeSpan
                Timeout = TimeSpan.FromMinutes(120)
            };
        }
    }
}
