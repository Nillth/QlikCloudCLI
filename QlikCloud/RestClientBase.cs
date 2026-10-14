using QlikCloud.Webhooks;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json;
using QlikCloud.Spaces;
using System.Net.Http;
using System.Linq;
using QlikCloud.Clients;

namespace QlikCloud
{
    public abstract partial class RestClientBase
    {
        protected readonly HttpClient client_;
        private const int MaxRetries = 5;
        private const int InitialDelayMs = 500; // Start with a 500ms delay
        public partial class ApiException : System.Exception
        {
            public int StatusCode { get; private set; }

            public string Response { get; private set; }

            public System.Collections.Generic.IReadOnlyDictionary<string, System.Collections.Generic.IEnumerable<string>> Headers { get; private set; }

            public ApiException(string message, int statusCode, string response, System.Collections.Generic.IReadOnlyDictionary<string, System.Collections.Generic.IEnumerable<string>> headers, System.Exception innerException)
                : base(message + "\n\nStatus: " + statusCode + "\nResponse: \n" + ((response == null) ? "(null)" : response.Substring(0, response.Length >= 512 ? 512 : response.Length)), innerException)
            {
                StatusCode = statusCode;
                Response = response;
                Headers = headers;
            }

            public override string ToString()
            {
                return string.Format("HTTP Response: \n\n{0}\n\n{1}", Response, base.ToString());
            }
        }

        public partial class ApiException<TResult> : ApiException
        {
            public TResult Result { get; private set; }

            public ApiException(string message, int statusCode, string response, System.Collections.Generic.IReadOnlyDictionary<string, System.Collections.Generic.IEnumerable<string>> headers, TResult result, System.Exception innerException)
                : base(message, statusCode, response, headers, innerException)
            {
                Result = result;
            }
        }
        // internal ILogger _logger = new LoggerFactory().CreateLogger("QlikCloud.ClientBase");
        internal readonly ILogger _logger = Global.LoggingInstance<RestClientBase>();
        static partial void UpdateJsonSerializerSettings(Newtonsoft.Json.JsonSerializerSettings settings);

        protected RestClientBase(RestClientConfig config)
        {

            client_ = config.httpClient ?? throw new ArgumentNullException(nameof(config.httpClient));

        }

        internal async Task PrepareRequestAsync(System.Net.Http.HttpClient client, System.Net.Http.HttpRequestMessage request, string url, CancellationToken cancellationToken)
        {
            try
            {
                //Console.WriteLine($"PrepareRequestAsync: {url}");
                _logger?.Verbose("PrepareRequestAsync: {url}", url);
            }
            catch { }
            await Task.Run(() => { });
            return;
        }
        internal async Task PrepareRequestAsync(System.Net.Http.HttpClient client, System.Net.Http.HttpRequestMessage request, System.Text.StringBuilder urlBuilder, CancellationToken cancellationToken)
        {
            try
            {
                //Console.WriteLine($"PrepareRequestAsync: {urlBuilder.ToString()}");
                _logger?.Verbose("PrepareRequestAsync: {url}", urlBuilder.ToString());
            }
            catch { }
            await Task.Run(() => { });
            return;
        }
        internal async Task ProcessResponseAsync(System.Net.Http.HttpClient client, System.Net.Http.HttpResponseMessage response, CancellationToken cancellationToken)
        {
            try
            {
                var responseText = response.Content.ReadAsStringAsync().Result;
                _logger?.Verbose("response: {@Content}", responseText);
                //Console.WriteLine($"response: {responseText}");
            }
            catch { }
            await Task.Run(() => { });
            return;
        }
        public async Task<HttpResponseMessage> SendRequestAsync(HttpRequestMessage request, System.Net.Http.HttpCompletionOption httpCompletionOption ,CancellationToken cancellationToken = default)
        {
            int retryCount = 0;
            int delay = InitialDelayMs;

            while (true)
            {
                try
                {
                    var response = await client_.SendAsync(request, httpCompletionOption, cancellationToken);
                    if ((int)response.StatusCode == 429) // Explicit comparison
                    {
                        if (retryCount >= MaxRetries)
                        {
                            _logger?.Warning("Max retry attempts reached for {Url}.", request.RequestUri);
                            return response;
                        }

                        retryCount++;
                        int retryAfterSeconds = GetRetryAfterSeconds(response);
                        _logger?.Warning($"Rate limit hit. Retrying {retryCount}/{MaxRetries} after {delay}ms for {request.RequestUri}",
                            retryCount, MaxRetries, retryAfterSeconds * 1000, request.RequestUri);
                        await Task.Delay(retryAfterSeconds * 1000, cancellationToken); // Convert seconds to milliseconds
                    }
                    else
                    {
                        return response; // Success
                    }
                }
                catch (HttpRequestException ex) when (retryCount < MaxRetries)
                {
                    _logger?.Error(ex, $"Request failed. Retrying {retryCount}/{MaxRetries} after {delay}ms for {request.RequestUri}",
                        retryCount, MaxRetries, delay, request.RequestUri);
                    await Task.Delay(delay, cancellationToken);
                    delay *= 2; // Exponential backoff
                    retryCount++;
                }
            }
        }

        private int GetRetryAfterSeconds(HttpResponseMessage response)
        {
            if (response.Headers.TryGetValues("Retry-After", out var values) && int.TryParse(values.FirstOrDefault(), out int retryAfter))
            {
                return retryAfter; // Use Retry-After header if provided
            }

            return 2; // Default retry delay in seconds if no header is found
        }


    }
    public partial class APIResponse
    {
        public int StatusCode { get; private set; }

        public System.Collections.Generic.IReadOnlyDictionary<string, System.Collections.Generic.IEnumerable<string>> Headers { get; private set; }

        public string RawContent { get; private set; }
        public APIResponse(int statusCode, System.Collections.Generic.IReadOnlyDictionary<string, System.Collections.Generic.IEnumerable<string>> headers)
        {
            StatusCode = statusCode;
            Headers = headers;
        }
        public APIResponse(int statusCode, System.Collections.Generic.IReadOnlyDictionary<string, System.Collections.Generic.IEnumerable<string>> headers, string rawContent)
        {
            StatusCode = statusCode;
            Headers = headers;
            RawContent = rawContent;
        }
    }

    public partial class APIResponse<TResult> : APIResponse
    {
        public TResult Object { get; private set; }

        public APIResponse(int statusCode, System.Collections.Generic.IReadOnlyDictionary<string, System.Collections.Generic.IEnumerable<string>> headers, string rawContent, TResult result)
            : base(statusCode, headers, rawContent)
        {
            Object = result;
        }
        public APIResponse(int statusCode, System.Collections.Generic.IReadOnlyDictionary<string, System.Collections.Generic.IEnumerable<string>> headers, TResult result)
            : base(statusCode, headers)
        {
            Object = result;
        }
    }

    public class SafeEnumConverter : StringEnumConverter
    {
        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            // Check if the target type is an enum
            if (reader.TokenType == JsonToken.String && objectType.IsEnum)
            {
                string enumText = reader.Value.ToString();


                // Correct usage of Enum.TryParse with 3 arguments
                try
                {
                    // Attempt to parse the enum value using the correct TryParse overload
                    var result = Enum.Parse(objectType, enumText, true);

                    // Check if the result is a valid, defined value in the enum
                    if (Enum.IsDefined(objectType, result))
                    {
                        return result;
                    }
                }
                catch (ArgumentException)
                {
                    // Handling invalid parsing if TryParse fails
                }

                // Return the default value if parsing fails
                return Activator.CreateInstance(objectType);
            }

            // Fallback to default behavior if the type isn't an enum
            return base.ReadJson(reader, objectType, existingValue, serializer);
        }
    }

}