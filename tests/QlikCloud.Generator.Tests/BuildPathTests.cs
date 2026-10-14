using System.Diagnostics;
using System.Security;
using System.Text.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace QlikCloud.Generator.Tests;

/// <summary>Builds an isolated SDK project with the production generation targets and real generator.</summary>
public sealed class BuildPathTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EvaluationExcludesLegacySourcesAndPreservesCustomSources(bool designTime)
    {
        using var workspace = new BuildWorkspace();
        var result = await workspace.Run("msbuild", workspace.Project, "-nologo", "-getItem:Compile",
            "-p:DesignTimeBuild=" + designTime.ToString().ToLowerInvariant());
        result.AssertSuccess();
        using var document = JsonDocument.Parse(result.Output);
        var sources = document.RootElement.GetProperty("Items").GetProperty("Compile")
            .EnumerateArray().Select(item => item.GetProperty("Identity").GetString()!.Replace('\\', '/')).ToArray();
        Assert.Contains("src/Custom/Keep.cs", sources);
        Assert.DoesNotContain("src/Legacy.cs", sources);
        Assert.DoesNotContain("Clients/RestClientAPIClients.cs", sources);
        Assert.False(Directory.Exists(workspace.DefaultOutput));
    }

    [Fact]
    public async Task DefaultBuildGeneratesClientsAndReconcilesChangedSchemaInventory()
    {
        using var workspace = new BuildWorkspace();
        (await workspace.Build()).AssertSuccess();
        Assert.True(File.Exists(workspace.Assembly));
        Assert.True(File.Exists(Path.Combine(workspace.DefaultOutput, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(workspace.DefaultOutput, "src", "Widgets.cs")));
        Assert.Contains("QlikCloud.Widgets.Client Widgets", workspace.Facade());
        workspace.AssertSelectedSources(workspace.DefaultOutput, "Widgets");

        // An ordinary incremental build must still reconcile additions and removals.
        File.Move(Path.Combine(workspace.Schemas, "widgets.json"), Path.Combine(workspace.Root, "previous-widgets.json"));
        workspace.WriteSchema("gadgets");
        (await workspace.Build()).AssertSuccess();
        Assert.False(File.Exists(Path.Combine(workspace.DefaultOutput, "src", "Widgets.cs")));
        Assert.True(File.Exists(Path.Combine(workspace.DefaultOutput, "src", "Gadgets.cs")));
        Assert.DoesNotContain("Widgets", workspace.Facade());
        Assert.Contains("QlikCloud.Gadgets.Client Gadgets", workspace.Facade());
        workspace.AssertSelectedSources(workspace.DefaultOutput, "Gadgets");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrInvalidSchemaFailsEvenWithPreviouslyBuiltGeneratedSources(bool missing)
    {
        using var workspace = new BuildWorkspace();
        (await workspace.Build()).AssertSuccess();
        var manifestPath = Path.Combine(workspace.DefaultOutput, "manifest.json");
        var previousManifest = File.ReadAllBytes(manifestPath);
        var previousAssembly = File.ReadAllBytes(workspace.Assembly);
        if (missing)
            Directory.Move(workspace.Schemas, Path.Combine(workspace.Root, "previous-schemas"));
        else
            File.WriteAllText(Path.Combine(workspace.Schemas, "widgets.json"), "not-json");

        var result = await workspace.Build();
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(missing ? "schema directory not found" : "MSB3073", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LEGACY_SOURCE_MUST_NEVER_COMPILE", result.Output);
        Assert.Equal(previousManifest, File.ReadAllBytes(manifestPath));
        Assert.Equal(previousAssembly, File.ReadAllBytes(workspace.Assembly));
    }

    [Fact]
    public async Task ArtifactsOutputBuildRunsGeneratorFromResolvedDllOnSelectedSdk()
    {
        using var workspace = new BuildWorkspace();
        var artifacts = Path.Combine(workspace.Root, "custom artifacts");
        (await workspace.Build("--artifacts-path", artifacts)).AssertSuccess();

        var generated = Path.Combine(artifacts, "obj", "BuildFixture", "Generated", "Debug", "net8.0");
        Assert.True(File.Exists(Path.Combine(generated, "src", "Widgets.cs")));
        Assert.True(File.Exists(Path.Combine(artifacts, "bin", "BuildFixture", "debug", "BuildFixture.dll")));
        Assert.True(File.Exists(Path.Combine(artifacts, "bin", "QlikCloud.Generator", "release", "QlikCloud.Generator.dll")));
        Assert.False(Directory.Exists(Path.Combine(workspace.Root, "tools", "QlikCloud.Generator", "bin")));
    }

    [Fact]
    public async Task GeneratedRootOverrideAloneStillRunsGeneratorWithDefaultSchemas()
    {
        using var workspace = new BuildWorkspace();
        var output = Path.Combine(workspace.ProjectDirectory, "obj", "custom generated output");
        (await workspace.Build("-p:QlikCloudGeneratedRoot=" + output)).AssertSuccess();
        Assert.True(File.Exists(Path.Combine(output, "src", "Widgets.cs")));
        Assert.Contains("QlikCloud.Widgets.Client Widgets", workspace.Facade(output));
        Assert.False(Directory.Exists(workspace.DefaultOutput));
        workspace.AssertSelectedSources(output, "Widgets");

        workspace.WriteSchema("gadgets");
        (await workspace.Build("-p:QlikCloudGeneratedRoot=" + output)).AssertSuccess();
        Assert.True(File.Exists(Path.Combine(output, "src", "Gadgets.cs")));
        Assert.Contains("QlikCloud.Gadgets.Client Gadgets", workspace.Facade(output));
    }

    [Fact]
    public async Task ExplicitEmptyGlobalPropertiesStillUseDefaultsAndGenerate()
    {
        using var workspace = new BuildWorkspace();
        (await workspace.Build("-p:QlikCloudSchemaDirectory=", "-p:QlikCloudGeneratedRoot=")).AssertSuccess();
        Assert.True(File.Exists(Path.Combine(workspace.DefaultOutput, "src", "Widgets.cs")));
        workspace.AssertSelectedSources(workspace.DefaultOutput, "Widgets");
    }

    [Fact]
    public async Task GeneratedOutputOutsideIntermediateDirectoryIsRejected()
    {
        using var workspace = new BuildWorkspace();
        var output = Path.Combine(workspace.ProjectDirectory, "generated");
        var result = await workspace.Build("-p:QlikCloudGeneratedRoot=" + output);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("QlikCloudGeneratedRoot must be a child of the intermediate directory", result.Output);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task DesignTimeGenerationTargetDoesNotRunGeneratorOrRequireSchemas()
    {
        using var workspace = new BuildWorkspace();
        Directory.Move(workspace.Schemas, Path.Combine(workspace.Root, "previous-schemas"));
        var result = await workspace.Run("msbuild", workspace.Project, "-nologo", "-t:GenerateQlikCloudSources",
            "-p:DesignTimeBuild=true", "-p:QlikCloudGeneratorProject=" + Path.Combine(workspace.Root, "missing.csproj"));
        result.AssertSuccess();
        Assert.False(Directory.Exists(workspace.DefaultOutput));
    }

    private sealed record BuildResult(int ExitCode, string Output)
    {
        public void AssertSuccess() => Assert.True(ExitCode == 0, Output);
    }

    private sealed class BuildWorkspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "qlikcloud-build-" + Guid.NewGuid().ToString("N"));
        public string ProjectDirectory => Path.Combine(Root, "QlikCloud");
        public string Project => Path.Combine(ProjectDirectory, "BuildFixture.csproj");
        public string Schemas => Path.Combine(Root, "schemas", "qlik-cloud");
        public string DefaultOutput => Path.Combine(ProjectDirectory, "obj", "Generated", "Debug", "net8.0");
        public string Assembly => Path.Combine(ProjectDirectory, "bin", "Debug", "net8.0", "BuildFixture.dll");
        private string SelectedSources => Path.Combine(ProjectDirectory, "obj", "selected-sources.txt");

        public BuildWorkspace()
        {
            var repositoryRoot = TestPaths.RepositoryRoot;
            Directory.CreateDirectory(Schemas);
            Directory.CreateDirectory(Path.Combine(ProjectDirectory, "BuildFiles"));
            Directory.CreateDirectory(Path.Combine(ProjectDirectory, "src", "Custom"));
            Directory.CreateDirectory(Path.Combine(ProjectDirectory, "Clients"));
            // Nested builds must exercise the SDK selected by the source checkout,
            // rather than silently falling forward to another installed SDK.
            File.Copy(Path.Combine(repositoryRoot, "global.json"), Path.Combine(Root, "global.json"));
            // Preserve the production relative layout to exercise the targets' schema default.
            File.Copy(Path.Combine(repositoryRoot, "QlikCloud", "BuildFiles", "Generation.targets"),
                Path.Combine(ProjectDirectory, "BuildFiles", "Generation.targets"));
            File.WriteAllText(Path.Combine(Root, "NuGet.Config"), "<configuration><packageSources><clear /></packageSources></configuration>");
            File.WriteAllText(Path.Combine(ProjectDirectory, "src", "Legacy.cs"), "#error LEGACY_SOURCE_MUST_NEVER_COMPILE\n");
            File.WriteAllText(Path.Combine(ProjectDirectory, "Clients", "RestClientAPIClients.cs"), "#error LEGACY_FACADE_MUST_NEVER_COMPILE\n");
            File.WriteAllText(Path.Combine(ProjectDirectory, "src", "Custom", "Keep.cs"), "public sealed class CustomKeep {}\n");
            File.WriteAllText(Path.Combine(ProjectDirectory, "Runtime.cs"), RuntimeStubs);
            TestPaths.CopyGeneratorSources(Path.Combine(Root, "tools", "QlikCloud.Generator"));
            var jsonAssembly = SecurityElement.Escape(typeof(JObject).Assembly.Location);
            File.WriteAllText(Project, $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <Reference Include="Newtonsoft.Json"><HintPath>{{jsonAssembly}}</HintPath></Reference>
                  </ItemGroup>
                  <Import Project="BuildFiles/Generation.targets" />
                  <Target Name="RecordSelectedSources" AfterTargets="CoreCompile">
                    <WriteLinesToFile File="$(BaseIntermediateOutputPath)selected-sources.txt"
                                      Lines="@(Compile->'%(FullPath)')" Overwrite="true" />
                  </Target>
                </Project>
                """);
            WriteSchema("widgets");
        }

        public void WriteSchema(string api) => File.WriteAllText(Path.Combine(Schemas, api + ".json"),
            """{"openapi":"3.0.0","info":{"title":"Build fixture","version":"1"},"paths":{}}""");

        public string Facade(string? output = null) => File.ReadAllText(Path.Combine(output ?? DefaultOutput, "Clients", "RestClientAPIClients.cs"));

        public void AssertSelectedSources(string output, string api)
        {
            var selected = File.ReadAllLines(SelectedSources).Select(Path.GetFullPath).ToArray();
            Assert.Contains(Path.Combine(ProjectDirectory, "src", "Custom", "Keep.cs"), selected);
            Assert.Contains(Path.Combine(output, "src", api + ".cs"), selected);
            Assert.Contains(Path.Combine(output, "Clients", "RestClientAPIClients.cs"), selected);
            Assert.DoesNotContain(Path.Combine(ProjectDirectory, "src", "Legacy.cs"), selected);
            Assert.DoesNotContain(Path.Combine(ProjectDirectory, "Clients", "RestClientAPIClients.cs"), selected);
        }

        public Task<BuildResult> Build(params string[] properties) => Run(
            new[] { "build", Project, "--nologo", "--verbosity", "quiet" }.Concat(properties).ToArray());

        public async Task<BuildResult> Run(params string[] arguments)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            start.Environment["DOTNET_NOLOGO"] = "1";
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw new TimeoutException("dotnet did not finish within two minutes.\n" + await stdout + await stderr);
            }
            return new BuildResult(process.ExitCode, await stdout + await stderr);
        }

        public void Dispose()
        {
            var absolute = Path.GetFullPath(Root);
            var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (!string.Equals(Path.GetDirectoryName(absolute), parent, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(absolute).StartsWith("qlikcloud-build-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to remove a directory outside the test workspace.");
            if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
        }

        private const string RuntimeStubs = """
            namespace QlikCloud.Clients
            {
                public interface IRestClient { }
                public sealed class RestClientConfig { }
                public abstract partial class RestClient<T>
                {
                    public RestClientConfig Config { get; } = new RestClientConfig();
                }
            }
            namespace QlikCloud
            {
                public abstract class RestClientBase
                {
                    protected RestClientBase(Clients.RestClientConfig config) { }
                    public class ApiException : System.Exception
                    {
                        public ApiException(string message, int status, string body, object headers,
                            System.Exception inner) : base(message, inner) { }
                    }
                }
            }
            """;
    }
}
