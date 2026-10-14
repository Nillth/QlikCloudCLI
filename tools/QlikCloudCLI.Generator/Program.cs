using System.Text.Json;

namespace QlikCloudCLI.Generator;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "--help" or "-h")
            {
                Console.WriteLine("QlikCloudCLI.Generator generate --assembly <QlikCloud.dll> --client-manifest <manifest.json> --output <owned-directory> [--rules <command-rules.json>] [--check]");
                return 0;
            }
            if (args[0] != "generate") throw new ArgumentException("Only the generate command is supported.");
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            bool check = false;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--check") { if (check) throw new ArgumentException("Duplicate --check option."); check = true; continue; }
                if (args[i] is not ("--assembly" or "--client-manifest" or "--output" or "--rules") || i + 1 == args.Length)
                    throw new ArgumentException("Unknown or incomplete option: " + args[i]);
                string key = args[i++];
                if (!options.TryAdd(key, args[i])) throw new ArgumentException("Duplicate option: " + key);
            }
            string Required(string key) => options.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
                ? Path.GetFullPath(value) : throw new ArgumentException("Missing required option: " + key);
            var report = new CmdletGenerator().Generate(Required("--assembly"), Required("--client-manifest"), Required("--output"),
                options.TryGetValue("--rules", out string? rules) ? Path.GetFullPath(rules) : Path.Combine(AppContext.BaseDirectory, "command-rules.json"), check);
            Console.WriteLine($"{(check ? "Verified" : "Generated")} {report.CommandCount} endpoint commands for {report.Operations.Count} operations and {report.Constructors.Count} model constructors.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}
