using System.Net;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json.Linq;
using QlikCloud.Generator;
using Xunit;

namespace QlikCloud.Generator.Tests;

public class ClientRendererTests
{
    private const string Schema = """
    {"openapi":"3.0.0","info":{"title":"Fixture","version":"1"},"paths":{
      "/api/v1/widgets/{widgetId}":{"get":{"parameters":[
        {"name":"widgetId","in":"path","required":true,"schema":{"type":"string"}},
        {"name":"enabled","in":"query","schema":{"type":"boolean"}},
        {"name":"tags","in":"query","schema":{"type":"array","items":{"type":"string"}}},
        {"name":"x-test","in":"header","schema":{"type":"string"}}
      ],"responses":{"200":{"description":"OK","content":{"application/json":{"schema":{"$ref":"#/components/schemas/Widget"}}}},"400":{"description":"Bad request","content":{"application/json":{"schema":{"$ref":"#/components/schemas/Error"}}}}}}},
      "/api/v1/widgets":{"post":{"requestBody":{"required":true,"content":{"application/json":{"schema":{"$ref":"#/components/schemas/Widget"}}}},"responses":{"201":{"description":"Created","content":{"application/json":{"schema":{"$ref":"#/components/schemas/Widget"}}}}}}},
      "/api/v1/upload":{"put":{"requestBody":{"required":true,"content":{"application/octet-stream":{"schema":{"type":"string","format":"binary"}}}},"responses":{"204":{"description":"Done"}}}},
      "/api/v1/download":{"get":{"responses":{"200":{"description":"File","content":{"application/octet-stream":{"schema":{"type":"string","format":"binary"}}}}}}},
      }, "components":{"schemas":{"Widget":{"type":"object","required":["id"],"properties":{"id":{"type":"string"}}},"Error":{"type":"object","properties":{"message":{"type":"string"}}}}}}
    """;

    [Fact]
    public async Task GeneratedClientUsesTenantHttpClientAndPreservesWireValues()
    {
        var assembly = Compile(Schema);
        string? uri = null;
        var client = CreateClient(assembly, async request =>
        {
            uri = request.RequestUri!.AbsoluteUri;
            Assert.Equal("header", Assert.Single(request.Headers.GetValues("x-test")));
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"abc\"}") };
        });
        var method = client.GetType().GetMethod("V1WidgetsGetAsync")!;
        var response = await Invoke(method, client, "a/b", true, new[] { "one two", "a&b" }, "header", CancellationToken.None);
        Assert.Equal("https://tenant.example/api/v1/widgets/a%2Fb?enabled=true&tags=one%20two&tags=a%26b", uri);
        var value = response!.GetType().GetProperty("Object")!.GetValue(response)!;
        Assert.Equal("abc", value.GetType().GetProperty("Id")!.GetValue(value));
    }

    [Fact]
    public async Task UploadSendsRawBytesAndDownloadOwnsResponseStream()
    {
        var assembly = Compile(Schema);
        byte[] bytes = [0, 127, 255, 20];
        var client = CreateClient(assembly, async request =>
        {
            if (request.Method == HttpMethod.Put)
            {
                Assert.Equal("application/octet-stream", request.Content!.Headers.ContentType!.MediaType);
                Assert.Equal(bytes, await request.Content.ReadAsByteArrayAsync());
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        var file = Activator.CreateInstance(assembly.GetType("QlikCloud.Widgets.FileParameter")!, new MemoryStream(bytes));
        await Invoke(client.GetType().GetMethod("V1UploadAsync")!, client, file, CancellationToken.None);
        var download = await Invoke(client.GetType().GetMethod("V1DownloadAsync")!, client, CancellationToken.None);
        using var result = (IDisposable)download!;
        var stream = (Stream)download!.GetType().GetProperty("Stream")!.GetValue(download)!;
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        Assert.Equal(bytes, copy.ToArray());
    }

    [Fact]
    public async Task ErrorsKeepTypedBodyAndMalformedSuccessFails()
    {
        var assembly = Compile(Schema);
        var client = CreateClient(assembly, _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"message\":\"wrong\"}") }));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Invoke(client.GetType().GetMethod("V1WidgetsGetAsync")!, client, "id", null, null, null, CancellationToken.None));
        Assert.Contains("ApiException", error.GetType().Name);
        Assert.Equal(400, error.GetType().GetProperty("StatusCode")!.GetValue(error));
        Assert.NotNull(error.GetType().GetProperty("Result")!.GetValue(error));
        client = CreateClient(assembly, _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("invalid json") }));
        await Assert.ThrowsAnyAsync<Exception>(() => Invoke(client.GetType().GetMethod("V1WidgetsGetAsync")!, client, "id", null, null, null, CancellationToken.None));
    }

    [Fact]
    public void SchemaChangesDriveMethodsAndBrokenReferencesFail()
    {
        var document = JObject.Parse(Schema);
        var first = new ClientRenderer("widgets", document).Render(Templates());
        document["paths"]!["/api/v1/download"]!.Parent!.Remove();
        var second = new ClientRenderer("widgets", document).Render(Templates());
        Assert.Contains(" V1Download(", first);
        Assert.DoesNotContain(" V1Download(", second);
        Assert.Equal(second, new ClientRenderer("widgets", document).Render(Templates()));
        document["components"]!["schemas"]!["Widget"]!["properties"]!["bad"] = new JObject { ["$ref"] = "#/components/schemas/Missing" };
        Assert.Throws<InvalidDataException>(() => new ClientRenderer("widgets", document).Render(Templates()));
    }

    [Fact]
    public void ExplicitCompatibilityNamesPreserveUnusualLegacySpelling()
    {
        var names = new Dictionary<string, string> { ["widgets GET /api/v1/download"] = "_legacyDownload_name" };
        var source = new ClientRenderer("widgets", JObject.Parse(Schema), methodNames: names).Render(Templates());
        Assert.Contains(" _legacyDownload_name(", source);
        Assert.Contains(" _legacyDownload_nameAsync(", source);
        names["widgets GET /api/v1/download"] = "bad; name";
        Assert.Throws<InvalidDataException>(() => new ClientRenderer("widgets", JObject.Parse(Schema), methodNames: names).Render(Templates()));
    }

    private static string Templates() => TestPaths.Templates;
    private static Assembly Compile(string json)
    {
        string source = new ClientRenderer("widgets", JObject.Parse(json)).Render(Templates());
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Append(typeof(JObject).Assembly.Location).Distinct();
        var compilation = CSharpCompilation.Create("Generated_" + Guid.NewGuid().ToString("N"), new[] { CSharpSyntaxTree.ParseText(source), CSharpSyntaxTree.ParseText(Stubs) }, paths.Select(p => MetadataReference.CreateFromFile(p)), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        stream.Position = 0;
        return AssemblyLoadContext.Default.LoadFromStream(stream);
    }
    private static object CreateClient(Assembly assembly, Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        var http = new HttpClient(new Handler(respond)) { BaseAddress = new Uri("https://tenant.example/api/") };
        var config = Activator.CreateInstance(assembly.GetType("QlikCloud.Clients.RestClientConfig")!)!;
        config.GetType().GetField("httpClient")!.SetValue(config, http);
        return Activator.CreateInstance(assembly.GetType("QlikCloud.Widgets.Client")!, config)!;
    }
    private static async Task<object?> Invoke(MethodInfo method, object client, params object?[] arguments)
    {
        var task = (Task)method.Invoke(client, arguments)!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task);
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request); }
    private const string Stubs = """
    namespace QlikCloud.Clients { public class RestClientConfig { public System.Net.Http.HttpClient httpClient; } }
    namespace QlikCloud {
      public class APIResponse {
        public int StatusCode {get;} public object Headers {get;} public string RawContent {get;}
        public APIResponse(int status, object headers) {StatusCode=status;Headers=headers;}
        public APIResponse(int status, object headers,string raw) : this(status,headers) {RawContent=raw;}
      }
      public class APIResponse<T> : APIResponse { public T Object {get;}
        public APIResponse(int status,object headers,T value) : base(status,headers) {Object=value;}
        public APIResponse(int status,object headers,string raw,T value) : base(status,headers,raw) {Object=value;}
      }
      public abstract class RestClientBase {
        protected readonly System.Net.Http.HttpClient client_;
        protected RestClientBase(Clients.RestClientConfig config) {client_=config.httpClient;}
        protected System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendRequestAsync(System.Net.Http.HttpRequestMessage r,System.Net.Http.HttpCompletionOption o,System.Threading.CancellationToken t) {return client_.SendAsync(r,o,t);}
        protected System.Threading.Tasks.Task PrepareRequestAsync(System.Net.Http.HttpClient c,System.Net.Http.HttpRequestMessage r,object u,System.Threading.CancellationToken t) {return System.Threading.Tasks.Task.CompletedTask;}
        protected System.Threading.Tasks.Task ProcessResponseAsync(System.Net.Http.HttpClient c,System.Net.Http.HttpResponseMessage r,System.Threading.CancellationToken t) {return System.Threading.Tasks.Task.CompletedTask;}
        public class ApiException : System.Exception {public int StatusCode {get;} public ApiException(string message,int status,string text,object headers,System.Exception inner) : base(message,inner) {StatusCode=status;} }
        public class ApiException<T> : ApiException {public T Result {get;} public ApiException(string message,int status,string text,object headers,T result,System.Exception inner) : base(message,status,text,headers,inner) {Result=result;} }
      }
    }
    """;
}
