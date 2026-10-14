using System;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;


namespace QlikCloud
{
    public class Response
    {
        protected readonly HttpResponseMessage _response;
        public HttpStatusCode StatusCode { get; }
        public bool IsError { get; }
        public Exception Error { get; }

        public Response(HttpResponseMessage response)
        {
            _response = response;
            StatusCode = response.StatusCode;
            IsError = !response.IsSuccessStatusCode;
            Error = null;
        }

        public Response(Exception error)
        {
            _response = null;
            StatusCode = HttpStatusCode.InternalServerError;
            IsError = true;
            Error = error;
        }
        private HttpContent content => _response?.Content;
        public string Content => IsError ? Error?.Message : content.ReadAsStringAsync().Result;
        public byte[] GetBytes() { return IsError ? null : content.ReadAsByteArrayAsync().Result; }
        public System.IO.Stream GetStream() { return IsError ? null : content.ReadAsStreamAsync().Result; }
        public HttpResponseMessage OriginalResponse => _response;
    }


    public class Response<T> : Response
    {
        private readonly Type _type;

        public Response(HttpResponseMessage response, Type type) : base(response)
        {
            _type = type;
        }

        public Response(Exception error) : base(error)
        {
            _type = null;
        }
        public object Object
        {
            get
            {
                // If there's an error or _type is null, return null
                if (IsError || _type == null)
                    return null;

                if (string.IsNullOrWhiteSpace(Content))
                    return null;

                try
                {
                    var methodInfo = _type.GetMethod("FromJson", BindingFlags.Public | BindingFlags.Static);
                    if (methodInfo != null)
                    {
                        var deserializedObject = methodInfo.Invoke(null, new object[] { Content });
                        return deserializedObject;
                    }
                    else { return null; }
                }
                catch (JsonException ex)
                {
                    // Log or handle the error appropriately.
                    Console.WriteLine($"Deserialization failed: {ex.Message}");
                    return null;
                }
            }
        }
    }

}