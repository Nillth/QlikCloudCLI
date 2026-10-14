using System.Net.Http;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace QlikCloud.Generator;

/// <summary>Loads a complete selected schema set, without silently skipping failed inputs.</summary>
public static class SchemaDiscovery
{
    // These sitemap entries are conceptual guides, not API specification documents.
    private static readonly HashSet<string> DocumentationOnlyPages = new(StringComparer.OrdinalIgnoreCase)
    {
        "pagination-sorting-filtering",
        "rate-limiting"
    };

    public static async Task<IReadOnlyDictionary<string, string>> LoadAsync(
        HttpClient client,
        string? schemaDirectory,
        Uri sitemapUri,
        IReadOnlyCollection<string> selectedApis,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(sitemapUri);
        ArgumentNullException.ThrowIfNull(selectedApis);
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in selectedApis)
        {
            ValidateApiName(name);
            selected.Add(name);
        }

        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (schemaDirectory is not null)
        {
            string directory = Path.GetFullPath(schemaDirectory);
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException($"Schema directory not found: {directory}");

            var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                         .Where(path => string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = Path.GetFileNameWithoutExtension(path);
                if (string.Equals(name, "manifest", StringComparison.OrdinalIgnoreCase)) continue;
                ValidateApiName(name);
                if (!inputs.TryAdd(name, path))
                    throw new InvalidDataException($"Duplicate API schema name: {name}");
            }

            ValidateSelection(inputs.Keys, selected);
            foreach (var entry in inputs.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                if (selected.Count != 0 && !selected.Contains(entry.Key)) continue;
                string json = await File.ReadAllTextAsync(entry.Value, cancellationToken).ConfigureAwait(false);
                ValidateSchema(json, entry.Value);
                result.Add(entry.Key, json);
            }
        }
        else
        {
            var apis = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            await VisitSitemapAsync(client, sitemapUri, visited, apis, cancellationToken).ConfigureAwait(false);
            ValidateSelection(apis, selected);
            foreach (string name in apis.OrderBy(name => name, StringComparer.Ordinal))
            {
                if (selected.Count != 0 && !selected.Contains(name)) continue;
                var schemaUri = new Uri("https://qlik.dev/specs/rest/" + Uri.EscapeDataString(name) + ".json");
                string json = await DownloadAsync(client, schemaUri, cancellationToken).ConfigureAwait(false);
                ValidateSchema(json, schemaUri.AbsoluteUri);
                result.Add(name, json);
            }
        }

        if (result.Count == 0)
            throw new InvalidDataException("No REST API schemas were found in the supplied source.");
        return result;
    }

    private static async Task VisitSitemapAsync(
        HttpClient client,
        Uri uri,
        HashSet<string> visited,
        HashSet<string> apis,
        CancellationToken cancellationToken)
    {
        ValidateHttpUri(uri);
        if (!visited.Add(uri.AbsoluteUri)) return;
        string xml = await DownloadAsync(client, uri, cancellationToken).ConfigureAwait(false);
        using var input = new StringReader(xml);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        });
        XDocument document = XDocument.Load(reader);
        XElement root = document.Root ?? throw new InvalidDataException($"Empty sitemap: {uri}");
        if (root.Name.LocalName is not ("sitemapindex" or "urlset"))
            throw new InvalidDataException($"Expected sitemapindex or urlset at {uri}.");
        string childName = root.Name.LocalName == "sitemapindex" ? "sitemap" : "url";
        foreach (XElement child in root.Elements().Where(element => element.Name.LocalName == childName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? location = child.Elements().FirstOrDefault(element => element.Name.LocalName == "loc")?.Value.Trim();
            if (string.IsNullOrEmpty(location) || !Uri.TryCreate(uri, location, out Uri? target))
                throw new InvalidDataException($"Missing or invalid sitemap location at {uri}.");
            ValidateHttpUri(target);
            if (root.Name.LocalName == "sitemapindex")
            {
                if (!string.Equals(uri.Host, target.Host, StringComparison.OrdinalIgnoreCase)
                    || uri.Port != target.Port || uri.Scheme != target.Scheme)
                    throw new InvalidDataException($"Cross-origin sitemap location is not allowed: {target}");
                await VisitSitemapAsync(client, target, visited, apis, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                const string prefix = "/apis/rest/";
                string path = target.AbsolutePath;
                if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;
                string name = Uri.UnescapeDataString(path[prefix.Length..].TrimEnd('/'));
                // API detail/reference subpages are not separate schema documents.
                if (name.Length == 0 || name.Contains('/')) continue;
                ValidateApiName(name);
                if (DocumentationOnlyPages.Contains(name)) continue;
                apis.Add(name);
            }
        }
    }

    private static async Task<string> DownloadAsync(HttpClient client, Uri uri, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Downloading {uri} failed: {(int)response.StatusCode} {response.ReasonPhrase}.", null, response.StatusCode);
        string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidDataException($"Downloaded document is empty: {uri}");
        return text;
    }

    private static void ValidateSelection(IEnumerable<string> availableNames, HashSet<string> selected)
    {
        var available = new HashSet<string>(availableNames, StringComparer.OrdinalIgnoreCase);
        string[] missing = selected.Where(name => !available.Contains(name)).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        if (missing.Length != 0)
            throw new InvalidDataException("Selected APIs were not found: " + string.Join(", ", missing));
    }

    private static void ValidateApiName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".."
            || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
            throw new InvalidDataException($"Invalid API schema name: {name}");
    }

    private static void ValidateHttpUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length != 0)
            throw new InvalidDataException($"Expected an HTTP(S) URL without credentials: {uri}");
    }

    private static void ValidateSchema(string json, string source)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !(root.TryGetProperty("openapi", out JsonElement version) || root.TryGetProperty("swagger", out version))
                || version.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("paths", out JsonElement paths) || paths.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Expected an OpenAPI schema with a version and paths object: {source}");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Invalid JSON schema: {source}", exception);
        }
    }
}
