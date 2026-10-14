using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace QlikCloud.Clients
{
    public interface IJWTClient
    {
        // Add any JWT-specific methods here if needed
    }
    public class JWTClient : RestClient<JWTClient>, IJWTClient
    {
        public string JwtToken { get; }
        private X509Certificate Certificate { get; }

        public JWTClient(string tenant, string jwtToken = "", X509Certificate certificate = null, ILogger logger = default) : base(tenant, logger)
        {
            JwtToken = jwtToken;
            Certificate = certificate;
        }

        public override bool Authenticate()
        {
            throw new NotImplementedException();
        }

        // Implement any JWT-specific methods
    }
}
