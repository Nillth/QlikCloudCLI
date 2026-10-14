using System.Runtime.CompilerServices;

namespace QlikCloud.Generator.Tests;

/// <summary>Locate source inputs independently of SDK output/artifacts directories.</summary>
internal static class TestPaths
{
    public static string RepositoryRoot { get; } = FindRepositoryRoot();
    public static string GeneratorSources => Path.Combine(RepositoryRoot, "tools", "QlikCloud.Generator");

    // These content files arrive through the ProjectReference alongside the generator
    // assembly under test. Never pick up a possibly stale repository bin directory.
    public static string GeneratorOutput => Path.GetDirectoryName(typeof(Program).Assembly.Location)!;
    public static string Templates => Path.Combine(GeneratorOutput, "Templates");
    public static string Rules => Path.Combine(GeneratorOutput, "generation-rules.json");

    private static string FindRepositoryRoot([CallerFilePath] string sourcePath = "")
    {
        // CI preserves deterministic /_/ source paths in CallerFilePath. Locate
        // its real checkout explicitly, including when test output is elsewhere.
        var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(workspace) && Path.IsPathFullyQualified(workspace)
            && File.Exists(Path.Combine(workspace, "tools", "QlikCloud.Generator", "QlikCloud.Generator.csproj"))
            && File.Exists(Path.Combine(workspace, "QlikCloud", "BuildFiles", "Generation.targets"))
            && File.Exists(Path.Combine(workspace, "global.json")))
            return Path.GetFullPath(workspace);

        var directory = new DirectoryInfo(Path.GetDirectoryName(sourcePath)!);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "tools", "QlikCloud.Generator", "QlikCloud.Generator.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the QlikCloud source checkout from test source: " + sourcePath);
    }

    public static void CopyGeneratorSources(string destination)
    {
        // Integration builds execute the real project in their disposable temp tree.
        // This avoids writing generator bin/obj output into the checkout or racing
        // another build that uses a different --artifacts-path.
        foreach (var path in Directory.EnumerateFiles(GeneratorSources, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(GeneratorSources, path);
            var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(segment => segment is "bin" or "obj")) continue;
            var extension = Path.GetExtension(path);
            if (extension is not (".cs" or ".csproj" or ".template") && relative != "generation-rules.json") continue;
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target);
        }
    }
}
