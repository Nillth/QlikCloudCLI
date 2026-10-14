using System.Net;
using System.Text;
using QlikCloud.Generator;
using Xunit;

namespace QlikCloud.Generator.Tests;

public sealed class InfrastructureTests
{
    private const string Schema = "{\"openapi\":\"3.0.0\",\"info\":{\"title\":\"Fixture\",\"version\":\"1\"},\"paths\":{}}";
    private const string Manifest = "{\"generator\":\"QlikCloud.Generator\"}\n";

    [Fact]
    public async Task OfflineDiscoveryUsesTopLevelSchemasAndIgnoresManifest()
    {
        using var temporary = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temporary.Path, "users.json"), Schema);
        File.WriteAllText(Path.Combine(temporary.Path, ".well-known.json"), Schema);
        File.WriteAllText(Path.Combine(temporary.Path, "manifest.json"), Manifest);
        Directory.CreateDirectory(Path.Combine(temporary.Path, "nested"));
        File.WriteAllText(Path.Combine(temporary.Path, "nested", "ignored.json"), "not a schema");
        using var handler = new FixtureHandler(new Dictionary<string, string>());
        using var client = new HttpClient(handler);

        var result = await SchemaDiscovery.LoadAsync(client, temporary.Path, new Uri("https://qlik.dev/sitemap-index.xml"), [], default);

        Assert.Equal(new[] { ".well-known", "users" }, result.Keys);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task OfflineDiscoveryRejectsMissingSelectionsAndMalformedSchemas()
    {
        using var temporary = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temporary.Path, "users.json"), Schema);
        using var client = new HttpClient(new FixtureHandler(new Dictionary<string, string>()));
        var sitemap = new Uri("https://qlik.dev/sitemap-index.xml");

        await Assert.ThrowsAsync<InvalidDataException>(() => SchemaDiscovery.LoadAsync(client, temporary.Path, sitemap, ["missing"], default));
        File.WriteAllText(Path.Combine(temporary.Path, "spaces.json"), "{}");
        await Assert.ThrowsAsync<InvalidDataException>(() => SchemaDiscovery.LoadAsync(client, temporary.Path, sitemap, [], default));

        var selected = await SchemaDiscovery.LoadAsync(client, temporary.Path, sitemap, ["USERS"], default);
        Assert.Single(selected);
        Assert.True(selected.ContainsKey("users"));
    }

    [Fact]
    public async Task OnlineDiscoveryRecursesDeduplicatesAndIncludesWellKnown()
    {
        var documents = new Dictionary<string, string>
        {
            ["https://qlik.dev/sitemap-index.xml"] = Index("/nested.xml", "/nested.xml"),
            ["https://qlik.dev/nested.xml"] = Index("/rest.xml", "/sitemap-index.xml"),
            ["https://qlik.dev/rest.xml"] = Urls("/apis/rest/", "/apis/rest/users/", "/apis/rest/.well-known/", "/apis/rest/users/details/", "/apis/rest/rate-limiting/", "/apis/rest/pagination-sorting-filtering/", "/unrelated/"),
            ["https://qlik.dev/specs/rest/users.json"] = Schema,
            ["https://qlik.dev/specs/rest/.well-known.json"] = Schema
        };
        using var handler = new FixtureHandler(documents);
        using var client = new HttpClient(handler);

        var result = await SchemaDiscovery.LoadAsync(client, null, new Uri("https://qlik.dev/sitemap-index.xml"), [], default);

        Assert.Equal(new[] { ".well-known", "users" }, result.Keys);
        Assert.Equal(5, handler.Requests.Count);
        Assert.Equal(handler.Requests.Count, handler.Requests.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task OnlineDiscoverySelectionMustMatchBeforeAnySchemaDownload()
    {
        using var handler = new FixtureHandler(new Dictionary<string, string>
        {
            ["https://qlik.dev/sitemap.xml"] = Urls("/apis/rest/users/")
        });
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => SchemaDiscovery.LoadAsync(client, null, new Uri("https://qlik.dev/sitemap.xml"), ["missing"], default));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task OnlineDiscoveryFailsEntireBatchOnFailedOrEmptySchemaDownload()
    {
        var documents = new Dictionary<string, string>
        {
            ["https://qlik.dev/sitemap.xml"] = Urls("/apis/rest/users/", "/apis/rest/spaces/"),
            ["https://qlik.dev/specs/rest/spaces.json"] = Schema
        };
        using var handler = new FixtureHandler(documents);
        using var client = new HttpClient(handler);
        var sitemap = new Uri("https://qlik.dev/sitemap.xml");

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(() => SchemaDiscovery.LoadAsync(client, null, sitemap, [], default));
        Assert.Contains("https://qlik.dev/specs/rest/users.json", exception.Message);
        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        documents["https://qlik.dev/specs/rest/users.json"] = " ";
        await Assert.ThrowsAsync<InvalidDataException>(() => SchemaDiscovery.LoadAsync(client, null, sitemap, [], default));
    }

    [Fact]
    public void OutputIsDeterministicUtf8WithoutBomAndCheckDetectsDrift()
    {
        using var temporary = new TemporaryDirectory();
        string output = Path.Combine(temporary.Path, "generated");
        var files = Files(("Models/Test.cs", "// test\r\nclass Test {}\r"));

        SafeOutput.Write(output, files);
        SafeOutput.Write(output, files, check: true);
        byte[] expected = Encoding.UTF8.GetBytes("// test\nclass Test {}\n");
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(output, "Models", "Test.cs")));
        SafeOutput.Write(output, files);
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(output, "Models", "Test.cs")));

        File.AppendAllText(Path.Combine(output, "Models", "Test.cs"), "// edited");
        Assert.Throws<InvalidDataException>(() => SafeOutput.Write(output, files, check: true));
        Assert.EndsWith("// edited", File.ReadAllText(Path.Combine(output, "Models", "Test.cs")));
        SafeOutput.Write(output, files);
        File.WriteAllText(Path.Combine(output, "stale.cs"), "stale");
        Assert.Throws<InvalidDataException>(() => SafeOutput.Write(output, files, check: true));
        SafeOutput.Write(output, files);
        Assert.False(File.Exists(Path.Combine(output, "stale.cs")));
    }

    [Theory]
    [InlineData("../escape.cs")]
    [InlineData("nested/../../escape.cs")]
    [InlineData("nested\\..\\escape.cs")]
    [InlineData("/root.cs")]
    [InlineData("nested/file.cs:stream")]
    [InlineData("nested/trailing. ")]
    [InlineData("NUL.cs")]
    public void InvalidOutputPathPreservesPreviousOutput(string invalidPath)
    {
        using var temporary = new TemporaryDirectory();
        string output = Path.Combine(temporary.Path, "generated");
        var original = Files(("keep.cs", "original\n"));
        SafeOutput.Write(output, original);

        Assert.Throws<InvalidDataException>(() => SafeOutput.Write(output, Files((invalidPath, "bad"))));

        SafeOutput.Write(output, original, check: true);
        Assert.False(File.Exists(Path.Combine(temporary.Path, "escape.cs")));
    }

    [Fact]
    public void OutputRejectsUnownedDirectoriesDuplicateNamesAndMissingMarkers()
    {
        using var temporary = new TemporaryDirectory();
        string output = Path.Combine(temporary.Path, "generated");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "keep.txt"), "original");
        Assert.Throws<InvalidDataException>(() => SafeOutput.Write(output, Files(("new.cs", "new"))));
        Assert.Throws<InvalidDataException>(() => SafeOutput.Write(output, Files(("new.cs", "new")), check: true));
        File.WriteAllText(Path.Combine(output, "manifest.json"), "{\"generator\":\"Other.Tool\"}");
        Assert.Throws<InvalidDataException>(() => SafeOutput.Write(output, Files(("new.cs", "new"))));
        Assert.Equal("original", File.ReadAllText(Path.Combine(output, "keep.txt")));

        string fresh = Path.Combine(temporary.Path, "fresh");
        Assert.Throws<InvalidDataException>(() => SafeOutput.Write(fresh, new Dictionary<string, string> { ["file.cs"] = "no manifest" }));
        Assert.Throws<InvalidDataException>(() => SafeOutput.Write(fresh, Files(("Test.cs", "one"), ("test.cs", "two"))));
        Assert.Throws<InvalidDataException>(() => SafeOutput.Write(fresh, Files(("nested", "file"), ("nested/file.cs", "nested file"))));
        Assert.False(Directory.Exists(fresh));
    }

    [Fact]
    public async Task OutputLeaseSpansAsyncContinuationAndNestedWrite()
    {
        using var temporary = new TemporaryDirectory();
        string output = Path.Combine(temporary.Path, "generated");
        using (SafeOutput.AcquireLock(output))
        {
            await Task.Yield();
            SafeOutput.Write(output, Files(("test.cs", "test")));
            SafeOutput.Write(output, Files(("test.cs", "test")), check: true);
        }
    }

    private static Dictionary<string, string> Files(params (string Path, string Text)[] files)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal) { ["manifest.json"] = Manifest };
        foreach (var file in files) result.Add(file.Path, file.Text);
        return result;
    }

    private static string Index(params string[] paths) => "<sitemapindex xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">"
        + string.Concat(paths.Select(path => $"<sitemap><loc>https://qlik.dev{path}</loc></sitemap>")) + "</sitemapindex>";

    private static string Urls(params string[] paths) => "<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">"
        + string.Concat(paths.Select(path => $"<url><loc>https://qlik.dev{path}</loc></url>")) + "</urlset>";

    private sealed class FixtureHandler(Dictionary<string, string> documents) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string uri = request.RequestUri!.AbsoluteUri;
            Requests.Add(uri);
            return Task.FromResult(documents.TryGetValue(uri, out string? content)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "QlikCloud.Generator.Tests." + Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            string full = System.IO.Path.GetFullPath(Path);
            string parent = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar);
            if (System.IO.Path.GetDirectoryName(full) != parent
                || !System.IO.Path.GetFileName(full).StartsWith("QlikCloud.Generator.Tests.", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected test cleanup path.");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }
}
