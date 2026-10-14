using Microsoft.Extensions.Logging;
using QlikCloud.Webhooks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace QlikCloud.Clients
{
    public abstract partial class RestClient<T> : IRestClient
    {
        public Response Get(string endpoint) => SendRequest(HttpMethod.Get, endpoint);
        public Response Get(string endpoint, Type type) => SendRequest(HttpMethod.Get, endpoint, type);

        public Response Post(string endpoint, string data) => SendRequest(HttpMethod.Post, endpoint, data: data);
        public Response Post(string endpoint, string data, Type type) => SendRequest(HttpMethod.Post, endpoint, type, data);
        public Response Post(string endpoint, FileInfo file) => SendRequest(HttpMethod.Post, endpoint, file: file);
        public Response Post(string endpoint, FileInfo file, Type type) => SendRequest(HttpMethod.Post, endpoint, type, file:file);

        public Response Put(string endpoint, string data) => SendRequest(HttpMethod.Put, endpoint, data: data);
        public Response Put(string endpoint, string data, Type type) => SendRequest(HttpMethod.Put, endpoint, type, data);

        public Response Patch(string endpoint, string data) => SendRequest(new HttpMethod("PATCH"), endpoint, data: data);
        public Response Patch(string endpoint, string data, Type type) => SendRequest(new HttpMethod("PATCH"), endpoint, type, data);

        public Response Delete(string endpoint) => SendRequest(HttpMethod.Delete, endpoint);
        public Response Delete(string endpoint, Type type) => SendRequest(HttpMethod.Delete, endpoint, type);

        protected Response SendRequest(HttpMethod method, string endpoint, Type type = null, string data = null,FileInfo file = null,string mimeType = null)
        {
            try
            {
                var request = new HttpRequestMessage(method, endpoint);

                if (data != null)
                {
                    request.Content = new StringContent(data, Encoding.UTF8, "application/json");
                }
                if (file != null)
                {
                    if (mimeType == null) { mimeType = QlikCloud.MimeTypeMap.GetMimeType(file.Name); }
                    request.Content = new StreamContent(file.OpenRead());
                    request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mimeType);
                }
                HttpResponseMessage response = Config.httpClient.SendAsync(request).Result;

                return type == null ? new Response(response) : new Response<object>(response, type);
            }
            catch (Exception ex)
            {
                _logger?.LogError("Error in {method} request: {@error}", method, ex);
                return type == null ? new Response(ex) : new Response<object>(ex);
            }
        }
    }
}
