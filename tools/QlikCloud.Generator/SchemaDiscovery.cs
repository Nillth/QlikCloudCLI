using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace QlikCloud.Generator;

/// <summary>Loads a complete selected schema set, without silently skipping failed inputs.</summary>
public static class SchemaDiscovery
{
    public const string DefaultArchiveUrl = "https://qlik.dev/specs/rest/qlik-cloud-rest-openapi-specs.zip";
    private const string ArchiveRoot = "qlik-cloud-rest-openapi-specs/";
    private const int MaximumArchiveBytes = 64 * 1024 * 1024;
    private const int MaximumDocumentBytes = 16 * 1024 * 1024;
    private const long MaximumExpandedBytes = 128 * 1024 * 1024;
    private const int MaximumEntries = 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static async Task<IReadOnlyDictionary<string, string>> LoadAsync(
        HttpClient client,
        string? schemaDirectory,
        Uri archiveUri,
        IReadOnlyCollection<string> selectedApis,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(archiveUri);
        ArgumentNullException.ThrowIfNull(selectedApis);
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in selectedApis)
        {
            ValidateApiName(name);
            selected.Add(CanonicalApiName(name));
        }

        var result = schemaDirectory is not null
            ? await LoadDirectoryAsync(schemaDirectory, selected, cancellationToken).ConfigureAwait(false)
            : await LoadArchiveAsync(client, archiveUri, selected, cancellationToken).ConfigureAwait(false);
        if (result.Count == 0)
            throw new InvalidDataException("No REST API schemas were found in the supplied source.");
        return result;
    }

    private static async Task<SortedDictionary<string, string>> LoadDirectoryAsync(
        string schemaDirectory, HashSet<string> selected, CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(schemaDirectory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Schema directory not found: {root}");
        for (DirectoryInfo? parent = new(root); parent != null; parent = parent.Parent) RejectLink(parent.FullName);
        var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pathCasing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.Count != 0)
        {
            string directory = directories.Pop();
            foreach (string path in Directory.EnumerateFileSystemEntries(directory).OrderBy(path => path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes = RejectLink(path);
                string relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                ValidatePathCasing(relative, pathCasing);
                if ((attributes & FileAttributes.Directory) != 0) { directories.Push(path); continue; }
                if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(relative, "manifest.json", StringComparison.OrdinalIgnoreCase)) continue;
                string name = CanonicalApiName(relative[..^5]);
                ValidateApiName(name);
                if (!inputs.TryAdd(name, path)) throw new InvalidDataException($"Duplicate API schema name: {name}");
            }
        }
        ValidateSelection(inputs.Keys, selected);
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in inputs.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (selected.Count != 0 && !selected.Contains(entry.Key)) continue;
            RejectLink(entry.Value);
            using Stream stream = File.OpenRead(entry.Value);
            string json = DecodeJson(await ReadBoundedAsync(stream, MaximumDocumentBytes, cancellationToken).ConfigureAwait(false));
            ValidateSchema(json, entry.Value);
            result.Add(entry.Key, json);
        }
        return result;
    }

    private static async Task<SortedDictionary<string, string>> LoadArchiveAsync(
        HttpClient client, Uri uri, HashSet<string> selected, CancellationToken cancellationToken)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Expected an HTTP(S) archive URL without credentials or a fragment.");
        using HttpResponseMessage response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Downloading the schema archive failed: {(int)response.StatusCode}.", null, response.StatusCode);
        if (response.Content.Headers.ContentLength is long length && (length <= 0 || length > MaximumArchiveBytes))
            throw new InvalidDataException("Schema archive has an invalid download size.");
        using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] bytes = await ReadBoundedAsync(body, MaximumArchiveBytes, cancellationToken).ConfigureAwait(false);
        using var input = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        if (archive.Entries.Count == 0 || archive.Entries.Count > MaximumEntries)
            throw new InvalidDataException("Schema archive has an invalid entry count.");

        var paths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var pathCasing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inputs = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long expandedBytes = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = entry.FullName;
            bool directory = path.EndsWith('/');
            if (!path.StartsWith(ArchiveRoot, StringComparison.Ordinal) || path.Contains('\\'))
                throw new InvalidDataException($"Unexpected schema archive path: {path}");
            string relative = path[ArchiveRoot.Length..];
            string checkedPath = directory && relative.Length > 0 ? relative[..^1] : relative;
            if (checkedPath.Length == 0 && path != ArchiveRoot)
                throw new InvalidDataException("Invalid schema archive directory.");
            if (checkedPath.Length != 0) ValidateApiName(checkedPath);
            ValidatePathCasing(checkedPath, pathCasing);
            if (!paths.TryAdd(path.TrimEnd('/'), directory))
                throw new InvalidDataException($"Duplicate schema archive path: {path}");
            int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0
                || (unixType != 0 && unixType != (directory ? 0x4000 : 0x8000)))
                throw new InvalidDataException($"Linked or special archive entries are not supported: {path}");
            if (entry.Length < 0 || entry.Length > MaximumDocumentBytes || (directory && entry.Length != 0))
                throw new InvalidDataException($"Invalid expanded schema archive entry size: {path}");
            expandedBytes += entry.Length;
            if (expandedBytes > MaximumExpandedBytes) throw new InvalidDataException("Schema archive exceeds the expanded size limit.");
            if (directory || relative == "00-README.md") continue;
            if (!relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Unexpected schema archive file: {path}");
            string name = CanonicalApiName(relative[..^5]);
            ValidateApiName(name);
            if (name.Equals("manifest", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Schema archive cannot replace the snapshot ownership manifest.");
            if (!inputs.TryAdd(name, entry)) throw new InvalidDataException($"Duplicate API schema name: {name}");
        }
        // A file cannot also serve as a parent directory, regardless of entry order.
        foreach (string path in paths.Keys)
            for (int slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
                if (paths.TryGetValue(path[..slash], out bool directory) && !directory)
                    throw new InvalidDataException($"Conflicting schema archive file and directory: {path}");

        ValidateSelection(inputs.Keys, selected);
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in inputs.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (selected.Count != 0 && !selected.Contains(entry.Key)) continue;
            using Stream stream = entry.Value.Open();
            byte[] document = await ReadBoundedAsync(stream, MaximumDocumentBytes, cancellationToken).ConfigureAwait(false);
            if (document.LongLength != entry.Value.Length) throw new InvalidDataException($"Incomplete schema archive entry: {entry.Value.FullName}");
            string json = DecodeJson(document);
            ValidateSchema(json, entry.Value.FullName);
            result.Add(entry.Key, json);
        }
        return result;
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read > maximumBytes) throw new InvalidDataException("Schema input exceeds its size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static string DecodeJson(byte[] bytes)
    {
        try { return Utf8.GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException exception) { throw new InvalidDataException("Schema document is not valid UTF-8.", exception); }
    }

    private static FileAttributes RejectLink(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Linked schema paths are not supported: {path}");
        return attributes;
    }

    private static string CanonicalApiName(string name) => name.Equals("well-known", StringComparison.OrdinalIgnoreCase) ? ".well-known" : name;

    private static void ValidatePathCasing(string path, Dictionary<string, string> canonicalPaths)
    {
        if (path.Length == 0) return;
        string prefix = "";
        foreach (string segment in path.Split('/'))
        {
            prefix = prefix.Length == 0 ? segment : prefix + "/" + segment;
            if (canonicalPaths.TryGetValue(prefix, out string? existing) && existing != prefix)
                throw new InvalidDataException($"Schema paths use inconsistent casing: {prefix}");
            canonicalPaths.TryAdd(prefix, prefix);
        }
    }

    private static void ValidateSelection(IEnumerable<string> availableNames, HashSet<string> selected)
    {
        var available = new HashSet<string>(availableNames, StringComparer.OrdinalIgnoreCase);
        string[] missing = selected.Where(name => !available.Contains(name)).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        if (missing.Length != 0) throw new InvalidDataException("Selected APIs were not found: " + string.Join(", ", missing));
    }

    private static void ValidateApiName(string name)
    {
        string[] segments = name.Split('/');
        if (name.Length > 512 || segments.Length > 16 || segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment) || segment is "." or ".." || segment.EndsWith('.')
                || segment.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.'))))
            throw new InvalidDataException($"Invalid API schema name: {name}");
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
        catch (JsonException exception) { throw new InvalidDataException($"Invalid JSON schema: {source}", exception); }
    }
}
