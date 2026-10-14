using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace QlikCloud.Generator;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        ConsoleCancelEventHandler? cancelHandler = null;
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help")
            {
                Console.WriteLine("QlikCloud.Generator (custom C# renderer; no NSwag)\n  refresh --output <schema-cache> [--api users,apps] [--sitemap <uri>]\n  generate --schemas <schema-cache> --output <generated-directory> [--api users,apps] [--check] [--templates <directory>] [--rules <file>]\nRefresh downloads public specifications only. Generate is offline. Existing output must be owned by this generator.");
                return 0;
            }
            string command = args[0];
            if (command is not ("refresh" or "generate")) throw new ArgumentException("Unknown command: " + command);
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 1; i < args.Length; i++)
            {
                string key = args[i];
                if (key == "--check") { if (!options.TryAdd(key, "true")) throw new ArgumentException("Duplicate option " + key); continue; }
                if (key is not ("--output" or "--schemas" or "--api" or "--sitemap" or "--templates" or "--rules") || ++i == args.Length) throw new ArgumentException("Unknown option or missing value: " + key);
                if (!options.TryAdd(key, args[i])) throw new ArgumentException("Duplicate option " + key);
            }
            string output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Required(options, "--output")));
            string[] selected = options.GetValueOrDefault("--api", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string? schemas = command == "generate" ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(Required(options, "--schemas"))) : null;
            if (schemas != null && (output.Equals(schemas, StringComparison.OrdinalIgnoreCase) || output.StartsWith(schemas + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || schemas.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Schema and generated output directories must be separate.");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("QlikCloud.Generator/1.0");
            using var cancellation = new CancellationTokenSource();
            cancelHandler = (_, e) => { e.Cancel = true; try { cancellation.Cancel(); } catch (ObjectDisposedException) { } };
            Console.CancelKeyPress += cancelHandler;
            using var generationLock = SafeOutput.AcquireLock(output);
            var source = await SchemaDiscovery.LoadAsync(client, schemas, new Uri(options.GetValueOrDefault("--sitemap", "https://qlik.dev/sitemap-index.xml")), selected, cancellation.Token);
            var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var report = new JObject { ["generator"] = "QlikCloud.Generator", ["formatVersion"] = 1, ["command"] = command };
            var inputs = new JArray();
            if (command == "refresh")
            {
                foreach (var (api, json) in source.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    string normalized = Normalize(JObject.Parse(json).ToString(Formatting.Indented)) + "\n";
                    files.Add(api + ".json", normalized);
                    inputs.Add(new JObject { ["api"] = api, ["sha256"] = Hash(normalized) });
                }
            }
            else
            {
                string templates = Path.GetFullPath(options.GetValueOrDefault("--templates", Path.Combine(AppContext.BaseDirectory, "Templates")));
                string rulesPath = Path.GetFullPath(options.GetValueOrDefault("--rules", Path.Combine(AppContext.BaseDirectory, "generation-rules.json")));
                var rules = JObject.Parse(File.ReadAllText(rulesPath));
                report["rulesSha256"] = Hash(File.ReadAllText(rulesPath));
                report["rendererSha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Program).Assembly.Location))).ToLowerInvariant();
                report["templates"] = new JArray(Directory.GetFiles(templates, "*.template").OrderBy(Path.GetFileName, StringComparer.Ordinal).Select(path => new JObject { ["path"] = Path.GetFileName(path), ["sha256"] = Hash(File.ReadAllText(path)) }));
                var clients = new List<string>();
                foreach (var (api, json) in source.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (rules["excludedApis"]?[api] != null) { inputs.Add(new JObject { ["api"] = api, ["excluded"] = rules["excludedApis"]![api]!.DeepClone() }); continue; }
                    Console.WriteLine("Generating " + api);
                    var normalized = SchemaTransforms.Apply(api, JObject.Parse(json));
                    var renderer = new ClientRenderer(api, normalized, ((JObject?)rules["customOperations"])?.Properties().Select(p => p.Name), ((JObject?)rules["methodNames"])?.Properties().ToDictionary(p => p.Name, p => p.Value.Value<string>() ?? throw new InvalidDataException("Invalid method-name policy."), StringComparer.Ordinal));
                    string name = ModelRenderer.Identifier(api.TrimStart('.'));
                    string generated = Normalize(renderer.Render(templates));
                    if (clients.Contains(name, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("API namespace collision: " + name);
                    clients.Add(name);
                    files.Add("src/" + name + ".cs", generated);
                    files.Add("schemas/" + api + ".json", Normalize(normalized.ToString(Formatting.Indented)) + "\n");
                    inputs.Add(new JObject { ["api"] = api, ["sha256"] = Hash(json), ["normalizedSha256"] = Hash(normalized.ToString(Formatting.None)), ["operations"] = JArray.FromObject(renderer.Operations), ["warnings"] = JArray.FromObject(renderer.Warnings) });
                }
                if (clients.Count == 0) throw new InvalidDataException("No APIs remain after exclusions.");
                files.Add("Clients/RestClientAPIClients.cs", RenderFacade(clients));
            }
            report["apis"] = inputs;
            report["files"] = new JArray(files.Select(p => new JObject { ["path"] = p.Key, ["sha256"] = Hash(p.Value) }));
            files.Add("manifest.json", Normalize(report.ToString(Formatting.Indented)) + "\n");
            SafeOutput.Write(output, files, options.ContainsKey("--check"));
            Console.WriteLine($"{command}: {source.Count} specifications; {(options.ContainsKey("--check") ? "verified" : "wrote")} {files.Count} files in {output}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
        finally
        {
            if (cancelHandler != null) Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static string Required(Dictionary<string, string> options, string key) => options.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException("Required option: " + key);
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
    private static string RenderFacade(IEnumerable<string> clients)
    {
        var builder = new StringBuilder("// <auto-generated by QlikCloud.Generator; do not edit />\nnamespace QlikCloud.Clients\n{\n    public abstract partial class RestClient<T> : IRestClient\n    {\n");
        foreach (string name in clients) builder.Append($"        public QlikCloud.{name}.Client {name} {{ get; private set; }}\n");
        builder.Append("        private void ClientInitialize()\n        {\n");
        foreach (string name in clients) builder.Append($"            {name} = new QlikCloud.{name}.Client(Config);\n");
        return builder.Append("        }\n    }\n}\n").ToString();
    }
}
