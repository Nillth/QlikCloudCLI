using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace QlikCloud.Generator;

/// <summary>Replaces only explicitly owned generated directories, with rollback and drift checking.</summary>
public static class SafeOutput
{
    public const string GeneratorName = "QlikCloud.Generator";
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly AsyncLocal<Dictionary<string, LockState>?> HeldLocks = new();

    /// <summary>
    /// Holds a cross-process, crash-released lock. The returned lease may cross async continuations.
    /// Callers may hold this around generation; nested Write calls reuse the lease.
    /// </summary>
    public static IDisposable AcquireLock(string outputDirectory)
    {
        string output = ValidateOutputPath(outputDirectory);
        Dictionary<string, LockState> held = HeldLocks.Value ??= new Dictionary<string, LockState>(StringComparer.OrdinalIgnoreCase);
        lock (held)
        {
            if (held.TryGetValue(output, out LockState? existing))
            {
                existing.Depth++;
                return new LockLease(held, output, existing);
            }
        }
        string parent = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(parent);
        RejectLinks(parent);
        string lockPath = Path.Combine(parent, "." + Path.GetFileName(output) + ".qlikcloud-generator.lock");
        RejectLinks(lockPath);
        var timer = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var state = new LockState(new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
                lock (held) held.Add(output, state);
                return new LockLease(held, output, state);
            }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 11 or 32 or 33)
            {
                // A FileShare.None lease serializes processes without a thread-affine mutex.
                if (timer.Elapsed >= TimeSpan.FromSeconds(120))
                    throw new TimeoutException($"Timed out waiting for generated output lock: {output}", exception);
                Thread.Sleep(50);
            }
        }
    }

    public static void Write(string outputDirectory, IReadOnlyDictionary<string, string> files, bool check = false)
    {
        ArgumentNullException.ThrowIfNull(files);
        string output = ValidateOutputPath(outputDirectory);
        SortedDictionary<string, string> prepared = PrepareFiles(files);
        using var lease = (LockLease)AcquireLock(output);
        lock (lease.State.SyncRoot) WriteLocked(output, prepared, check);
    }

    private static void WriteLocked(string output, SortedDictionary<string, string> prepared, bool check)
    {
        ValidateOwnedOutput(output);

        if (check)
        {
            CheckOutput(output, prepared);
            return;
        }

        string parent = Path.GetDirectoryName(output)!;
        string suffix = Guid.NewGuid().ToString("N");
        string stage = Path.Combine(parent, "." + Path.GetFileName(output) + "." + suffix + ".tmp");
        string backup = Path.Combine(parent, "." + Path.GetFileName(output) + "." + suffix + ".bak");
        bool oldMoved = false;
        bool newMoved = false;
        Directory.CreateDirectory(stage);
        try
        {
            foreach (var file in prepared)
            {
                string target = Path.Combine(stage, file.Key.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, file.Value, Utf8);
            }

            ValidateOwnedOutput(output);
            if (Directory.Exists(output))
            {
                Directory.Move(output, backup);
                oldMoved = true;
            }
            Directory.Move(stage, output);
            newMoved = true;
        }
        catch
        {
            if (oldMoved && !Directory.Exists(output) && Directory.Exists(backup))
                Directory.Move(backup, output);
            throw;
        }
        finally
        {
            if (Directory.Exists(stage)) DeleteUnlinkedTree(stage);
        }

        // The new complete output is committed. A backup cleanup failure leaves recoverable data.
        if (newMoved && oldMoved) DeleteUnlinkedTree(backup);
    }

    private static SortedDictionary<string, string> PrepareFiles(IReadOnlyDictionary<string, string> files)
    {
        if (files.Count == 0) throw new InvalidDataException("Refusing to generate an empty output directory.");
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in files)
        {
            string key = entry.Key.Replace('\\', '/');
            if (Path.IsPathRooted(key) || key.Split('/').Any(InvalidSegment))
                throw new InvalidDataException($"Unsafe generated output path: {entry.Key}");
            if (!names.Add(key)) throw new InvalidDataException($"Duplicate generated output path: {entry.Key}");
            if (entry.Value is null) throw new InvalidDataException($"Generated output content is null: {entry.Key}");
            result.Add(key, entry.Value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
        }
        foreach (string name in names)
        {
            int separator = name.IndexOf('/');
            while (separator >= 0)
            {
                if (names.Contains(name[..separator]))
                    throw new InvalidDataException($"Generated output uses a file as a directory: {name}");
                separator = name.IndexOf('/', separator + 1);
            }
        }
        if (!result.TryGetValue("manifest.json", out string? manifest))
            throw new InvalidDataException("Generated output requires a root manifest.json ownership marker.");
        ValidateManifest(manifest);
        return result;
    }

    private static bool InvalidSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment) || segment is "." or ".."
            || segment.TrimEnd(' ', '.') != segment || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || segment.Contains(':')) return true;
        string stem = segment.Split('.')[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (stem.Length == 4 && stem[3] is >= '1' and <= '9'
                && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)));
    }

    private static string ValidateOutputPath(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        string output = Path.GetFullPath(outputDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(Path.GetFileName(output)) || string.IsNullOrEmpty(Path.GetDirectoryName(output)))
            throw new InvalidDataException("Generated output must be a non-root directory.");
        RejectLinks(output);
        if (File.Exists(output)) throw new InvalidDataException($"Generated output is a file: {output}");
        return output;
    }

    private static void ValidateOwnedOutput(string output)
    {
        RejectLinks(output);
        if (File.Exists(output)) throw new InvalidDataException($"Generated output is a file: {output}");
        if (!Directory.Exists(output)) return;
        RejectTreeLinks(output);
        string manifest = Path.Combine(output, "manifest.json");
        if (!File.Exists(manifest))
            throw new InvalidDataException($"Refusing to replace an unowned directory without manifest.json: {output}");
        ValidateManifest(File.ReadAllText(manifest));
    }

    private static void ValidateManifest(string text)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.EnumerateObject().Any(property => property.Name.Equals("generator", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() == GeneratorName)) return;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Invalid generated output ownership manifest.", exception);
        }
        throw new InvalidDataException($"Output manifest must identify generator '{GeneratorName}'.");
    }

    private static void CheckOutput(string output, SortedDictionary<string, string> files)
    {
        if (!Directory.Exists(output)) throw new InvalidDataException($"Generated output is missing: {output}");
        var actual = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(output, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(files.Keys, StringComparer.Ordinal))
            throw new InvalidDataException("Generated output file inventory has drifted.");
        foreach (var file in files)
        {
            byte[] expected = Utf8.GetBytes(file.Value);
            if (!File.ReadAllBytes(Path.Combine(output, file.Key.Replace('/', Path.DirectorySeparatorChar))).AsSpan().SequenceEqual(expected))
                throw new InvalidDataException($"Generated output has drifted: {file.Key}");
        }
    }

    private static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Linked output paths are not allowed: {current}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void RejectTreeLinks(string directory)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Linked generated output entries are not allowed: {entry}");
            if ((attributes & FileAttributes.Directory) != 0) RejectTreeLinks(entry);
        }
    }

    private static void DeleteUnlinkedTree(string directory)
    {
        RejectLinks(directory);
        RejectTreeLinks(directory);
        Directory.Delete(directory, recursive: true);
    }

    private sealed class LockState(FileStream stream)
    {
        public FileStream Stream { get; } = stream;
        public object SyncRoot { get; } = new();
        public int Depth { get; set; } = 1;
    }

    private sealed class LockLease(Dictionary<string, LockState> held, string output, LockState state) : IDisposable
    {
        private bool disposed;
        public LockState State { get; } = state;

        public void Dispose()
        {
            lock (held)
            {
                if (disposed) return;
                disposed = true;
                if (--State.Depth == 0)
                {
                    held.Remove(output);
                    State.Stream.Dispose();
                }
            }
        }
    }
}
