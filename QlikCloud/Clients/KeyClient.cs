using IdentityModel.Client;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;

namespace QlikCloud.Clients
{
    public interface IKeyClient
    {
        // Add any Key-specific methods here if needed
    }
    public class KeyClient : RestClient<KeyClient>, IKeyClient
    {
        public string ApiKey { get; }

        public KeyClient(string tenant, string apiKey, ILogger logger = default) : base(tenant, logger)
        {
            _logger?.LogTrace("KeyClient: Constructor");
            ApiKey = apiKey;
            Initialize();
        }

        public override bool Authenticate()
        {
            _logger?.LogTrace("KeyClient: Authenticate");
            try
            {
                Config.httpClient.SetBearerToken(ApiKey);
                base.Initialize();
            }
            catch (Exception ex)
            {
                _logger?.LogError("Error authenticating client: {@Error}", ex);
                return false;
            }
            return true;
        }
        protected override void Initialize()
        {
            _logger?.LogTrace("KeyClient: Initialize");
            Authenticate();
        }
    }
}
