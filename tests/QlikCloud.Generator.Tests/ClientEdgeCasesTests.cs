using System.Net;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json.Linq;
using QlikCloud.Generator;
using Xunit;

namespace QlikCloud.Generator.Tests;

public sealed class ClientEdgeCasesTests
{
    [Fact]
    public async Task CorrectedReloadIdentifierIsEscapedIntoPathAndFalseIsPreserved()
    {
        var original = JObject.Parse("""
        {"openapi":"3.0.0","info":{"title":"Fixture","version":"1"},"paths":{
          "/api/v1/apps/{appId}/reloads/metadata/{reloadId}":{"get":{"parameters":[
            {"name":"appId","in":"path","required":true,"schema":{"type":"string"}},
            {"name":"reloadId","in":"query","schema":{"type":"string"}},
            {"name":"includeSkipStoreReloads","in":"query","schema":{"type":"boolean"}}
          ],"responses":{"204":{"description":"Done"}}}}
        }}
        """);
        Assert.Throws<InvalidDataException>(() => new ClientRenderer("apps", original).Render(TestPaths.Templates));
        var corrected = SchemaTransforms.Apply("apps", original);
        Assembly assembly = Compile(corrected.ToString());
        bool called = false;
        object client = CreateClient(assembly, request =>
        {
            called = true;
            Assert.Equal("https://tenant.example/api/v1/apps/app%2Fid/reloads/metadata/reload%2Fid?includeSkipStoreReloads=false", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        await Invoke(client, "V1AppsReloadsMetadataAsync", new Dictionary<string, object?>
        {
            ["appId"] = "app/id", ["reloadId"] = "reload/id", ["includeSkipStoreReloads"] = false
        });
        Assert.True(called);

        corrected["paths"]!["/api/v1/apps/{appId}/reloads/metadata/{reloadId}"]!["get"]!["parameters"]![0]!["name"] = "wrongId";
        Assert.Throws<InvalidDataException>(() => new ClientRenderer("apps", corrected).Render(TestPaths.Templates));
    }

    [Fact]
    public async Task DateOnlyQueryUsesCalendarDateWithoutTimeOrOffset()
    {
        const string schema = """
        {"openapi":"3.0.0","info":{"title":"Fixture","version":"1"},"paths":{
          "/api/v1/archive":{"get":{"parameters":[{"name":"date","in":"query","required":true,"schema":{"type":"string","format":"date"}},{"name":"before","in":"query","schema":{"type":"string","format":"date"}}],"responses":{"204":{"description":"Done"}}}}
        }}
        """;
        Assembly assembly = Compile(schema);
        bool called = false;
        object client = CreateClient(assembly, request =>
        {
            called = true;
            Assert.Equal("https://tenant.example/api/v1/archive?date=2020-02-20", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        Assert.Equal(typeof(DateTimeOffset?), client.GetType().GetMethod("V1ArchiveAsync")!.GetParameters()
            .Single(parameter => parameter.Name == "before").ParameterType);

        await Invoke(client, "V1ArchiveAsync", new Dictionary<string, object?>
        {
            ["date"] = new DateTimeOffset(2020, 2, 20, 15, 40, 30, TimeSpan.FromHours(11))
        });

        Assert.True(called);
    }

    [Fact]
    public async Task MultipartObjectUsesJsonEncodingInsteadOfClrTypeName()
    {
        // This is the Json part shape used by POST/PUT /api/v1/data-files.
        const string schema = """
        {"openapi":"3.0.0","info":{"title":"Fixture","version":"1"},"paths":{
          "/api/v1/upload":{"post":{"requestBody":{"required":true,"content":{"multipart/form-data":{
            "schema":{"type":"object","required":["Json"],"properties":{"Json":{"type":"object","required":["name"],"properties":{"name":{"type":"string"},"folder":{"type":"boolean"}}}}},
            "encoding":{"Json":{"contentType":"application/json","explode":false}}
          }}},"responses":{"204":{"description":"Done"}}}}
        }}
        """;
        Assembly assembly = Compile(schema);
        bool called = false;
        object client = CreateClient(assembly, async request =>
        {
            called = true;
            var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
            HttpContent part = Assert.Single(multipart);
            Assert.Equal("Json", part.Headers.ContentDisposition!.Name!.Trim('"'));
            Assert.Equal("application/json", part.Headers.ContentType!.MediaType);
            JObject json = JObject.Parse(await part.ReadAsStringAsync());
            Assert.Equal("reports/source.csv", (string?)json["name"]);
            Assert.Equal(false, (bool?)json["folder"]);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        MethodInfo method = client.GetType().GetMethod("V1UploadAsync")!;
        Type jsonType = method.GetParameters().Single(parameter => parameter.Name == "json").ParameterType;
        object metadata = Activator.CreateInstance(jsonType)!;
        jsonType.GetProperty("Name")!.SetValue(metadata, "reports/source.csv");
        jsonType.GetProperty("Folder")!.SetValue(metadata, false);

        await Invoke(client, method.Name, new Dictionary<string, object?> { ["json"] = metadata });

        Assert.True(called);
    }

    [Fact]
    public async Task MultipartBinaryUsesSchemaContentTypeUnlessCallerOverridesIt()
    {
        // Brand styles specifies application/json even though it is supplied as a binary file.
        const string schema = """
        {"openapi":"3.0.0","info":{"title":"Fixture","version":"1"},"paths":{
          "/api/v1/brand":{"post":{"requestBody":{"required":true,"content":{"multipart/form-data":{
            "schema":{"type":"object","required":["styles"],"properties":{"styles":{"type":"string","format":"binary"}}},
            "encoding":{"styles":{"contentType":"application/json"}}
          }}},"responses":{"204":{"description":"Done"}}}}
        }}
        """;
        Assembly assembly = Compile(schema);
        string expectedMediaType = "application/json";
        object client = CreateClient(assembly, async request =>
        {
            HttpContent part = Assert.Single(Assert.IsType<MultipartFormDataContent>(request.Content));
            Assert.Equal(expectedMediaType, part.Headers.ContentType?.MediaType);
            Assert.Equal("{}", await part.ReadAsStringAsync());
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        Type fileType = assembly.GetType("QlikCloud.Edge.FileParameter")!;
        object inferred = Activator.CreateInstance(fileType, new MemoryStream("{}"u8.ToArray()), "styles.json", null)!;
        await Invoke(client, "V1BrandAsync", new Dictionary<string, object?> { ["styles"] = inferred });
        expectedMediaType = "application/vnd.fixture+json";
        object overridden = Activator.CreateInstance(fileType, new MemoryStream("{}"u8.ToArray()), "styles.json", expectedMediaType)!;
        await Invoke(client, "V1BrandAsync", new Dictionary<string, object?> { ["styles"] = overridden });
    }

    [Fact]
    public async Task WildcardDownloadReturnsOpaqueBytesAndRetainsPartialStatus()
    {
        // Current temp-contents schemas describe arbitrary files as */* with type object.
        const string schema = """
        {"openapi":"3.0.0","info":{"title":"Fixture","version":"1"},"paths":{
          "/api/v1/content":{"get":{"responses":{
            "200":{"description":"Downloaded","content":{"*/*":{"schema":{"type":"object"}}}},
            "206":{"description":"Partial file","content":{"*/*":{"schema":{"type":"object"}}}}
          }}}
        }}
        """;
        Assembly assembly = Compile(schema);
        byte[] expected = [0, 255, 128, 65, 10];
        object client = CreateClient(assembly, _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(expected)
        }));

        object result = (await Invoke(client, "V1ContentAsync", new Dictionary<string, object?>()))!;

        using var lease = Assert.IsAssignableFrom<IDisposable>(result);
        Assert.Equal(true, result.GetType().GetProperty("IsPartial")!.GetValue(result));
        var stream = (Stream)result.GetType().GetProperty("Stream")!.GetValue(result)!;
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        Assert.Equal(expected, copy.ToArray());
    }

    [Fact]
    public async Task NegotiatedTextResponsePreservesStringContractAndCallerAccept()
    {
        // CSP generate-header offers text/plain and JSON, with an explicit Accept parameter.
        const string schema = """
        {"openapi":"3.0.0","info":{"title":"Fixture","version":"1"},"paths":{
          "/api/v1/header":{"get":{"parameters":[{"name":"Accept","in":"header","schema":{"type":"string","enum":["application/json","text/plain"]}}],"responses":{
            "200":{"description":"Header","content":{
              "text/plain":{"schema":{"type":"string"}},
              "application/json":{"schema":{"type":"object","properties":{"header":{"type":"string"}}}}
            }}
          }}}
        }}
        """;
        Assembly assembly = Compile(schema);
        string expectedAccept = "text/plain";
        string responseText = "default-src 'self'; img-src https://images.example";
        object client = CreateClient(assembly, request =>
        {
            Assert.Equal(expectedAccept, Assert.Single(request.Headers.Accept).MediaType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseText, System.Text.Encoding.UTF8, expectedAccept)
            });
        });
        MethodInfo method = client.GetType().GetMethod("V1HeaderAsync")!;
        Type optionalAccept = method.GetParameters().Single(parameter => parameter.Name == "accept").ParameterType;
        Type acceptType = Nullable.GetUnderlyingType(optionalAccept) ?? optionalAccept;
        object Accept(string media) => acceptType.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Single(field => field.GetCustomAttribute<System.Runtime.Serialization.EnumMemberAttribute>()?.Value == media).GetValue(null)!;

        object result = (await Invoke(client, method.Name, new Dictionary<string, object?> { ["accept"] = Accept(expectedAccept) }))!;
        Assert.Equal(responseText, Assert.IsType<string>(result.GetType().GetProperty("Object")!.GetValue(result)));

        expectedAccept = "application/json";
        responseText = "{\"header\":\"default-src 'self'\"}";
        result = (await Invoke(client, method.Name, new Dictionary<string, object?> { ["accept"] = Accept(expectedAccept) }))!;
        Assert.Equal(responseText, Assert.IsType<string>(result.GetType().GetProperty("Object")!.GetValue(result)));
    }

    private static async Task<object?> Invoke(object client, string methodName, IReadOnlyDictionary<string, object?> supplied)
    {
        MethodInfo method = client.GetType().GetMethod(methodName)!;
        object?[] arguments = method.GetParameters().Select(parameter => parameter.ParameterType == typeof(CancellationToken)
            ? (object)CancellationToken.None
            : supplied.GetValueOrDefault(parameter.Name!)).ToArray();
        var task = (Task)method.Invoke(client, arguments)!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task);
    }

    private static object CreateClient(Assembly assembly, Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        var http = new HttpClient(new Handler(respond)) { BaseAddress = new Uri("https://tenant.example/api/") };
        object config = Activator.CreateInstance(assembly.GetType("QlikCloud.Clients.RestClientConfig")!)!;
        config.GetType().GetField("httpClient")!.SetValue(config, http);
        return Activator.CreateInstance(assembly.GetType("QlikCloud.Edge.Client")!, config)!;
    }

    private static Assembly Compile(string json)
    {
        string source = new ClientRenderer("edge", JObject.Parse(json)).Render(TestPaths.Templates);
        string[] paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(JObject).Assembly.Location).Distinct().ToArray();
        var compilation = CSharpCompilation.Create("Generated_Edge_" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(source), CSharpSyntaxTree.ParseText(Stubs) },
            paths.Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
        stream.Position = 0;
        return AssemblyLoadContext.Default.LoadFromStream(stream);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

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
        protected System.Threading.Tasks.Task PrepareRequestAsync(System.Net.Http.HttpClient c,System.Net.Http.HttpRequestMessage r,object u,System.Threading.CancellationToken t) {return System.Threading.Tasks.Task.CompletedTask;}
        protected System.Threading.Tasks.Task ProcessResponseAsync(System.Net.Http.HttpClient c,System.Net.Http.HttpResponseMessage r,System.Threading.CancellationToken t) {return System.Threading.Tasks.Task.CompletedTask;}
        protected System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendRequestAsync(System.Net.Http.HttpRequestMessage r,System.Net.Http.HttpCompletionOption o,System.Threading.CancellationToken t) {return client_.SendAsync(r,o,t);}
        public class ApiException : System.Exception {public int StatusCode {get;} public ApiException(string message,int status,string text,object headers,System.Exception inner) : base(message,inner) {StatusCode=status;} }
        public class ApiException<T> : ApiException {public T Result {get;} public ApiException(string message,int status,string text,object headers,T result,System.Exception inner) : base(message,status,text,headers,inner) {Result=result;} }
      }
    }
    """;
}
