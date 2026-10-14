using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Logging;
using QlikCloud.Webhooks;

namespace QlikCloud.Clients
{
    internal class ExtensionLoggingHandler : DelegatingHandler
    {
        private readonly ILogger _logger;
        public ExtensionLoggingHandler(ILogger logger, HttpMessageHandler innerHandler) : base(innerHandler)
        {
            _logger = logger;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = null;
            HttpResponseMessage finalResponse = default;
            do
            {
                response?.Dispose(); // Dispose the previous response
                _logger.LogTrace("Request Headers: {@Headers}", request.Headers);
                _logger.LogDebug("Request: {Method}: {request}", request.Method, request.RequestUri.AbsoluteUri);
                response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                _logger.LogDebug("Response: {response}", response.StatusCode);
                if (IsRedirect(response.StatusCode))
                {
                    var uri = response.Headers.Location;
                    request.RequestUri = uri; // Update the request URI with the redirect URI
                    finalResponse = response; // Save the last response
                }
                else
                {
                    finalResponse = response; // Save the last response if it's not a redirect
                }
            } while (IsRedirect(response.StatusCode)); // Loop until no more redirection
            return finalResponse; // Return the final response
        }
        private static bool IsRedirect(System.Net.HttpStatusCode statusCode)
        {
            return statusCode == System.Net.HttpStatusCode.MovedPermanently ||
                   statusCode == System.Net.HttpStatusCode.Found ||
                   statusCode == System.Net.HttpStatusCode.SeeOther ||
                   statusCode == System.Net.HttpStatusCode.TemporaryRedirect;
        }
    }
}
