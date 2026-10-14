using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using QlikCloud.Generator;
using Xunit;

namespace QlikCloud.Generator.Tests;

/// <summary>Exercises the offline CLI boundary and complete output transaction.</summary>
public sealed class PipelineTests
{
	[Fact]
	public async Task OfflineGenerateAndCheckAreByteStableAndCheckDoesNotRewriteFiles()
	{
		using var workspace = new PipelineWorkspace();
		workspace.WriteSchema("widgets");
		Assert.Equal(0, await Program.Main(workspace.GenerateArguments()));
		var before = Snapshot(workspace.Output);
		var stamps = Directory.GetFiles(workspace.Output, "*", SearchOption.AllDirectories)
			.ToDictionary(path => path, File.GetLastWriteTimeUtc, StringComparer.Ordinal);
		var manifest = JObject.Parse(File.ReadAllText(Path.Combine(workspace.Output, "manifest.json")));
		Assert.Equal("generate", (string?)manifest["command"]);
		Assert.Equal("widgets", (string?)manifest["apis"]![0]!["api"]);
		Assert.Contains("src/Widgets.cs", before.Keys);
		Assert.Contains("Clients/RestClientAPIClients.cs", before.Keys);

		Assert.Equal(0, await Program.Main(workspace.GenerateArguments("--check")));
		AssertSnapshot(before, workspace.Output);
		foreach (var (path, timestamp) in stamps) Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
		Assert.Equal(0, await Program.Main(workspace.GenerateArguments()));
		AssertSnapshot(before, workspace.Output);
	}

	[Fact]
	public async Task CheckReportsDriftWithoutRepairingOrCreatingOutput()
	{
		using var workspace = new PipelineWorkspace();
		workspace.WriteSchema("widgets");
		Assert.Equal(1, await Program.Main(workspace.GenerateArguments("--check")));
		Assert.False(Directory.Exists(workspace.Output));
		Assert.Equal(0, await Program.Main(workspace.GenerateArguments()));
		File.AppendAllText(Path.Combine(workspace.Output, "src", "Widgets.cs"), "// external edit\n");
		var modified = Snapshot(workspace.Output);
		Assert.Equal(1, await Program.Main(workspace.GenerateArguments("--check")));
		AssertSnapshot(modified, workspace.Output);
	}

	[Theory]
	[InlineData("not-json")]
	[InlineData("{\"openapi\":\"3.0.0\",\"paths\":{\"/api/v1/broken\":{\"get\":{\"responses\":{\"200\":{\"description\":\"OK\",\"content\":{\"application/json\":{\"schema\":{\"$ref\":\"#/components/schemas/Missing\"}}}}}}}}}")]
	public async Task FailedInputOrRenderingLeavesPreviousCompleteOutputIntact(string invalidSchema)
	{
		using var workspace = new PipelineWorkspace();
		workspace.WriteSchema("widgets");
		Assert.Equal(0, await Program.Main(workspace.GenerateArguments()));
		var before = Snapshot(workspace.Output);
		// Sort after the valid API to exercise failure after an earlier client renders.
		File.WriteAllText(Path.Combine(workspace.Schemas, "zzz-broken.json"), invalidSchema);
		Assert.Equal(1, await Program.Main(workspace.GenerateArguments()));
		AssertSnapshot(before, workspace.Output);
		Assert.Empty(Directory.GetDirectories(workspace.Root, "*.tmp"));
		Assert.Empty(Directory.GetDirectories(workspace.Root, "*.bak"));
	}

	[Fact]
	public async Task AddingAndRemovingSchemasUpdatesFacadeAndRemovesStaleGeneratedFiles()
	{
		using var workspace = new PipelineWorkspace();
		workspace.WriteSchema("widgets");
		Assert.Equal(0, await Program.Main(workspace.GenerateArguments()));
		workspace.WriteSchema("gadgets");
		Assert.Equal(0, await Program.Main(workspace.GenerateArguments()));
		var facadePath = Path.Combine(workspace.Output, "Clients", "RestClientAPIClients.cs");
		var facade = File.ReadAllText(facadePath);
		Assert.Contains("QlikCloud.Widgets.Client Widgets", facade);
		Assert.Contains("QlikCloud.Gadgets.Client Gadgets", facade);
		Assert.True(File.Exists(Path.Combine(workspace.Output, "src", "Gadgets.cs")));

		File.Delete(Path.Combine(workspace.Schemas, "widgets.json"));
		Assert.Equal(0, await Program.Main(workspace.GenerateArguments()));
		facade = File.ReadAllText(facadePath);
		Assert.DoesNotContain("Widgets", facade);
		Assert.Contains("QlikCloud.Gadgets.Client Gadgets", facade);
		Assert.False(File.Exists(Path.Combine(workspace.Output, "src", "Widgets.cs")));
		Assert.False(File.Exists(Path.Combine(workspace.Output, "schemas", "widgets.json")));
		Assert.Equal(0, await Program.Main(workspace.GenerateArguments("--check")));
	}

	[Fact]
	public async Task SelectedApisAreTheCompleteReviewedOutputAndUnknownSelectionDoesNotReplaceIt()
	{
		using var workspace = new PipelineWorkspace();
		workspace.WriteSchema("widgets");
		workspace.WriteSchema("gadgets");
		Assert.Equal(0, await Program.Main(workspace.GenerateArguments("--api", "WIDGETS")));
		Assert.True(File.Exists(Path.Combine(workspace.Output, "src", "Widgets.cs")));
		Assert.False(File.Exists(Path.Combine(workspace.Output, "src", "Gadgets.cs")));
		var before = Snapshot(workspace.Output);
		Assert.Equal(1, await Program.Main(workspace.GenerateArguments("--api", "missing")));
		AssertSnapshot(before, workspace.Output);
	}

	[Fact]
	public async Task InvalidArgumentsAndNamespaceCollisionsCannotChangeExistingOutput()
	{
		using var workspace = new PipelineWorkspace();
		workspace.WriteSchema("widgets");
		Assert.Equal(0, await Program.Main(workspace.GenerateArguments()));
		var before = Snapshot(workspace.Output);
		Assert.Equal(1, await Program.Main(["unknown"]));
		Assert.Equal(1, await Program.Main(["generate"]));
		Assert.Equal(1, await Program.Main(workspace.GenerateArguments("--unknown", "value")));
		Assert.Equal(1, await Program.Main(workspace.GenerateArguments("--api")));
		Assert.Equal(1, await Program.Main(workspace.GenerateArguments("--output", workspace.Output)));
		Assert.Equal(1, await Program.Main(workspace.GenerateArguments("--api", "../widgets")));
		AssertSnapshot(before, workspace.Output);

		workspace.WriteSchema("duplicate-name");
		workspace.WriteSchema("duplicate_name");
		Assert.Equal(1, await Program.Main(workspace.GenerateArguments()));
		AssertSnapshot(before, workspace.Output);
	}

	[Fact]
	public async Task UnownedOutputIsNeverReplaced()
	{
		using var workspace = new PipelineWorkspace();
		workspace.WriteSchema("widgets");
		Directory.CreateDirectory(workspace.Output);
		File.WriteAllText(Path.Combine(workspace.Output, "user-work.txt"), "preserve this work");
		var before = Snapshot(workspace.Output);
		Assert.Equal(1, await Program.Main(workspace.GenerateArguments()));
		AssertSnapshot(before, workspace.Output);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task OverlappingPathsAreRejectedEvenWhenParentHasTrailingSeparator(bool outputIsParent)
	{
		using var workspace = new PipelineWorkspace();
		var parent = Path.Combine(workspace.Root, "parent");
		var child = Path.Combine(parent, "child");
		var schemas = outputIsParent ? child : parent;
		var output = outputIsParent ? parent : child;
		Directory.CreateDirectory(schemas);
		File.WriteAllText(Path.Combine(schemas, "widgets.json"), PipelineWorkspace.Schema("widgets"));
		if (outputIsParent)
			File.WriteAllText(Path.Combine(output, "manifest.json"), "{\"generator\":\"QlikCloud.Generator\"}");
		var before = Snapshot(parent);
		var arguments = workspace.GenerateArguments();
		arguments[Array.IndexOf(arguments, "--schemas") + 1] = schemas + (outputIsParent ? "" : Path.DirectorySeparatorChar);
		arguments[Array.IndexOf(arguments, "--output") + 1] = output + (outputIsParent ? Path.DirectorySeparatorChar : "");
		Assert.Equal(1, await Program.Main(arguments));
		AssertSnapshot(before, parent);
	}

	private static SortedDictionary<string, string> Snapshot(string directory) => new(
		Directory.GetFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(
			path => Path.GetRelativePath(directory, path).Replace('\\', '/'),
			path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal), StringComparer.Ordinal);

	private static void AssertSnapshot(SortedDictionary<string, string> expected, string directory)
	{
		var actual = Snapshot(directory);
		Assert.Equal(expected.Keys, actual.Keys);
		foreach (var (path, hash) in expected) Assert.Equal(hash, actual[path]);
	}

	private sealed class PipelineWorkspace : IDisposable
	{
		public string Root { get; } = Path.Combine(Path.GetTempPath(), "qlikcloud-pipeline-" + Guid.NewGuid().ToString("N"));
		public string Schemas => Path.Combine(Root, "schemas");
		public string Output => Path.Combine(Root, "generated");

		public PipelineWorkspace()
		{
			Directory.CreateDirectory(Schemas);
		}

		public string[] GenerateArguments(params string[] extra) =>
			new[] { "generate", "--schemas", Schemas, "--output", Output, "--templates", TestPaths.Templates, "--rules", TestPaths.Rules }.Concat(extra).ToArray();
		public void WriteSchema(string api) => File.WriteAllText(Path.Combine(Schemas, api + ".json"), Schema(api));
		public static string Schema(string api) => """
		{"openapi":"3.0.0","info":{"title":"Fixture","version":"1"},"paths":{
		  "/api/v1/__API__":{"get":{"responses":{"200":{"description":"OK","content":{"application/json":{"schema":{"$ref":"#/components/schemas/Entry"}}}}}}}
		},"components":{"schemas":{"Entry":{"type":"object","properties":{"id":{"type":"string"}}}}}}
		""".Replace("__API__", api, StringComparison.Ordinal);

		public void Dispose()
		{
			var absolute = Path.GetFullPath(Root);
			if (!absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
				!Path.GetFileName(absolute).StartsWith("qlikcloud-pipeline-", StringComparison.Ordinal))
				throw new InvalidOperationException("Refusing to remove a directory outside the test workspace.");
			if (Directory.Exists(absolute)) Directory.Delete(absolute, true);
		}
	}
}
