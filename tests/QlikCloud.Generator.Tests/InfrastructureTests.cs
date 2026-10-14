using System.IO.Compression;
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
    public async Task OfflineDiscoveryPreservesRecursiveIdentitiesAndIgnoresRootManifest()
    {
        using var temporary = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temporary.Path, "users.json"), Schema);
        File.WriteAllText(Path.Combine(temporary.Path, ".well-known.json"), Schema);
        File.WriteAllText(Path.Combine(temporary.Path, "manifest.json"), Manifest);
        Directory.CreateDirectory(Path.Combine(temporary.Path, "analytics", "discovery-agent"));
        File.WriteAllText(Path.Combine(temporary.Path, "apps.json"), Schema);
        File.WriteAllText(Path.Combine(temporary.Path, "analytics", "apps.json"), Schema);
        File.WriteAllText(Path.Combine(temporary.Path, "analytics", "discovery-agent", "adaptive-cards.json"), Schema);
        using var handler = new FixtureHandler(new Dictionary<string, byte[]>());
        using var client = new HttpClient(handler);

        var result = await SchemaDiscovery.LoadAsync(client, temporary.Path, ArchiveUri, [], default);

        Assert.Equal(new[] { ".well-known", "analytics/apps", "analytics/discovery-agent/adaptive-cards", "apps", "users" }, result.Keys);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task OfflineDiscoveryRejectsMissingSelectionsAndMalformedSchemas()
    {
        using var temporary = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temporary.Path, "users.json"), Schema);
        using var client = new HttpClient(new FixtureHandler(new Dictionary<string, byte[]>()));

        await Assert.ThrowsAsync<InvalidDataException>(() => SchemaDiscovery.LoadAsync(client, temporary.Path, ArchiveUri, ["missing"], default));
        File.WriteAllText(Path.Combine(temporary.Path, "spaces.json"), "{}");
        await Assert.ThrowsAsync<InvalidDataException>(() => SchemaDiscovery.LoadAsync(client, temporary.Path, ArchiveUri, [], default));

        var selected = await SchemaDiscovery.LoadAsync(client, temporary.Path, ArchiveUri, ["USERS"], default);
        Assert.Single(selected);
        Assert.True(selected.ContainsKey("users"));
    }

    [Fact]
    public async Task ArchiveDiscoveryPreservesNamespacesAndLegacyWellKnownIdentity()
    {
        byte[] zip = Archive([
            ("users.json", Schema), ("well-known.json", Schema), ("apps.json", Schema),
            ("analytics/apps.json", Schema), ("analytics/discovery-agent/adaptive-cards.json", Schema),
            ("00-README.md", "Archive documentation, not a schema.")]);
        using var handler = new FixtureHandler(new() { [ArchiveUri.AbsoluteUri] = zip });
        using var client = new HttpClient(handler);

        var result = await SchemaDiscovery.LoadAsync(client, null, ArchiveUri, [], default);

        Assert.Equal(new[] { ".well-known", "analytics/apps", "analytics/discovery-agent/adaptive-cards", "apps", "users" }, result.Keys);
        Assert.Equal(new[] { ArchiveUri.AbsoluteUri }, handler.Requests);
        using var temporary = new TemporaryDirectory();
        foreach (var entry in result)
        {
            string path = Path.Combine(temporary.Path, entry.Key + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, entry.Value);
        }
        var offline = await SchemaDiscovery.LoadAsync(client, temporary.Path, ArchiveUri, [], default);
        Assert.Equal(result.ToArray(), offline.ToArray());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ArchiveDiscoverySelectionUsesFullNamespaceIdentity()
    {
        using var handler = new FixtureHandler(new() { [ArchiveUri.AbsoluteUri] = Archive([("apps.json", Schema), ("analytics/apps.json", Schema)]) });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => SchemaDiscovery.LoadAsync(client, null, ArchiveUri, ["missing"], default));
        Assert.Single(handler.Requests);
        var selected = await SchemaDiscovery.LoadAsync(client, null, ArchiveUri, ["ANALYTICS/APPS"], default);
        Assert.Equal(new[] { "analytics/apps" }, selected.Keys);
    }

    [Fact]
    public async Task ArchiveDiscoveryFailsEntireBatchOnFailedMalformedOrEmptyDownload()
    {
        var documents = new Dictionary<string, byte[]>();
        using var handler = new FixtureHandler(documents);
        using var client = new HttpClient(handler);
        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(() => SchemaDiscovery.LoadAsync(client, null, ArchiveUri, [], default));
        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        foreach (byte[] content in new[] { Array.Empty<byte>(), Encoding.UTF8.GetBytes("not a ZIP"), Archive([]), Archive([("users.json", Schema)])[..^10], Archive([("users.json", Schema), ("spaces.json", "{}")]) })
        {
            documents[ArchiveUri.AbsoluteUri] = content;
            await Assert.ThrowsAsync<InvalidDataException>(() => SchemaDiscovery.LoadAsync(client, null, ArchiveUri, [], default));
        }
    }

    [Theory]
    [InlineData("../escape.json")]
    [InlineData("nested/../../escape.json")]
    [InlineData("nested\\escape.json")]
    [InlineData("/root.json")]
    [InlineData("nested/file.json:stream")]
    [InlineData("nested//file.json")]
    [InlineData("nested//")]
    [InlineData("unexpected.txt")]
    [InlineData("manifest.json")]
    public async Task ArchiveRejectsUnsafeOrUnexpectedPaths(string path)
    {
        using var client = new HttpClient(new FixtureHandler(new() { [ArchiveUri.AbsoluteUri] = Archive([("users.json", Schema), (path, Schema)]) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => SchemaDiscovery.LoadAsync(client, null, ArchiveUri, [], default));
    }

    [Theory]
    [InlineData("users.json", "users.json")]
    [InlineData("users.json", "USERS.json")]
    [InlineData("well-known.json", ".well-known.json")]
    [InlineData("users.json", "users.json/nested.json")]
    [InlineData("analytics/apps.json", "ANALYTICS/users.json")]
    [InlineData("analytics/discovery-agent/apps.json", "analytics/Discovery-Agent/users.json")]
    public async Task ArchiveRejectsDuplicateIdentitiesAndFileDirectoryCollisions(string first, string second)
    {
        using var client = new HttpClient(new FixtureHandler(new() { [ArchiveUri.AbsoluteUri] = Archive([(first, Schema), (second, Schema)]) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => SchemaDiscovery.LoadAsync(client, null, ArchiveUri, [], default));
    }

    [Fact]
    public async Task RepackagingArchiveDoesNotChangeDiscoveredSchemas()
    {
        var entries = new[] { ("users.json", Schema), ("analytics/apps.json", Schema), ("well-known.json", Schema) };
        byte[] first = Archive(entries, CompressionLevel.Fastest, new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
        byte[] second = Archive(entries.Reverse(), CompressionLevel.NoCompression, new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.NotEqual(first, second);
        var documents = new Dictionary<string, byte[]> { [ArchiveUri.AbsoluteUri] = first };
        using var client = new HttpClient(new FixtureHandler(documents));
        var original = await SchemaDiscovery.LoadAsync(client, null, ArchiveUri, [], default);
        documents[ArchiveUri.AbsoluteUri] = second;
        var repackaged = await SchemaDiscovery.LoadAsync(client, null, ArchiveUri, [], default);
        Assert.Equal(original.ToArray(), repackaged.ToArray());
    }

    [Fact]
    public async Task ArchiveRejectsOversizedEntriesAndExcessiveEntryCounts()
    {
        var documents = new Dictionary<string, byte[]> { [ArchiveUri.AbsoluteUri] = Archive([("users.json", new string(' ', 16 * 1024 * 1024 + 1))]) };
        using var client = new HttpClient(new FixtureHandler(documents));
        await Assert.ThrowsAsync<InvalidDataException>(() => SchemaDiscovery.LoadAsync(client, null, ArchiveUri, [], default));
        documents[ArchiveUri.AbsoluteUri] = Archive(Enumerable.Range(0, 1025).Select(index => ($"api{index}.json", Schema)));
        await Assert.ThrowsAsync<InvalidDataException>(() => SchemaDiscovery.LoadAsync(client, null, ArchiveUri, [], default));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OfflineDiscoveryRejectsLinkedDirectoriesAndFiles(bool directoryLink)
    {
        using var temporary = new TemporaryDirectory();
        string input = Path.Combine(temporary.Path, "input");
        string outside = Path.Combine(temporary.Path, "outside");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(outside);
        string outsideFile = Path.Combine(outside, "users.json");
        File.WriteAllText(outsideFile, Schema);
        string link = Path.Combine(input, directoryLink ? "linked" : "linked.json");
        if (directoryLink) Directory.CreateSymbolicLink(link, outside);
        else File.CreateSymbolicLink(link, outsideFile);
        try
        {
            using var client = new HttpClient(new FixtureHandler(new()));
            await Assert.ThrowsAsync<InvalidDataException>(() => SchemaDiscovery.LoadAsync(client, input, ArchiveUri, [], default));
        }
        finally
        {
            if (directoryLink) Directory.Delete(link);
            else File.Delete(link);
        }
        Assert.Equal(Schema, File.ReadAllText(outsideFile));
    }

    [Fact]
    public async Task OfflineDiscoveryRejectsWellKnownAliasCollision()
    {
        using var temporary = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temporary.Path, "well-known.json"), Schema);
        File.WriteAllText(Path.Combine(temporary.Path, ".well-known.json"), Schema);
        using var client = new HttpClient(new FixtureHandler(new()));
        await Assert.ThrowsAsync<InvalidDataException>(() => SchemaDiscovery.LoadAsync(client, temporary.Path, ArchiveUri, [], default));
    }

    [Fact]
    public async Task CliRejectsRetiredSitemapOption()
    {
        using var temporary = new TemporaryDirectory();
        Assert.Equal(1, await Program.Main(["refresh", "--output", Path.Combine(temporary.Path, "schemas"), "--sitemap", "https://example.invalid/sitemap.xml"]));
        Assert.False(Directory.Exists(Path.Combine(temporary.Path, "schemas")));
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

    private static readonly Uri ArchiveUri = new(SchemaDiscovery.DefaultArchiveUrl);

    private static byte[] Archive(IEnumerable<(string Path, string Text)> entries, CompressionLevel compression = CompressionLevel.Fastest, DateTimeOffset? timestamp = null)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var entry in entries)
            {
                ZipArchiveEntry item = archive.CreateEntry("qlik-cloud-rest-openapi-specs/" + entry.Path, compression);
                item.LastWriteTime = timestamp ?? new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var writer = new StreamWriter(item.Open(), new UTF8Encoding(false));
                writer.Write(entry.Text);
            }
        return stream.ToArray();
    }

    private sealed class FixtureHandler(Dictionary<string, byte[]> documents) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string uri = request.RequestUri!.AbsoluteUri;
            Requests.Add(uri);
            return Task.FromResult(documents.TryGetValue(uri, out byte[]? content)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }
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
