using System.Text.Json;
using System.Text.Json.Nodes;
using QlikCloudCLI.Generator;
using Xunit;

namespace QlikCloudCLI.Generator.Tests;

public sealed class GeneratorTests
{
    [Fact]
    public void FullInventoryBindsOverloadsCustomMethodsAndTransitiveModels()
    {
        using var workspace = new Workspace();
        CommandInventory inventory = workspace.Generate();
        Assert.Equal(4, inventory.Operations.Count);
        Assert.Equal(2, inventory.Constructors.Count);
        Assert.All(inventory.Operations, command => Assert.Matches("^[A-Za-z]+-QC[A-Za-z0-9_]+$", command.CommandName));
        Assert.All(inventory.Constructors, command => Assert.StartsWith("New-QC", command.CommandName));
        Assert.Equal(2, inventory.FormatVersion);
        Assert.Equal(3, inventory.CommandCount);
        EndpointCommandGroup grouped = Assert.Single(inventory.Commands, command => command.CommandName == "Get-QCWidgets");
        Assert.Equal("List", grouped.DefaultParameterSetName);
        Assert.Equal(new[] { "List", "ByWidgetId" }, grouped.ParameterSetNames);
        EndpointCommand item = Assert.Single(inventory.Operations, c => c.ParameterSetName == "ByWidgetId");
        Assert.Equal("Get-QCWidgets", item.CommandName);
        CommandParameter id = Assert.Single(item.Parameters);
        Assert.True(id.Mandatory);
        Assert.True(id.PipelineByValue);
        Assert.True(id.PipelineByPropertyName);
        Assert.Equal(new[] { "Id" }, id.Aliases);
        Assert.Equal("custom", Assert.Single(inventory.Operations, c => c.CommandName == "Get-QCWidgetsMe").Implementation);
        EndpointCommand create = Assert.Single(inventory.Operations, c => c.CommandName == "New-QCWidgets");
        Assert.True(create.SupportsShouldProcess);
        Assert.False(item.SupportsShouldProcess);
        Assert.Contains(inventory.Constructors, c => c.ModelType.EndsWith("NestedModel", StringComparison.Ordinal));
        ModelCommand constructor = Assert.Single(inventory.Constructors, c => c.ModelType.EndsWith("WidgetRequest", StringComparison.Ordinal));
        Assert.Equal("New-QCWidgetsWidgetRequestObject", constructor.CommandName);
        Assert.Equal("New-QCWidgetsNestedModelObject", Assert.Single(inventory.Constructors, c => c.ModelType.EndsWith("NestedModel", StringComparison.Ordinal)).CommandName);
        Assert.Contains(constructor.Parameters, p => p.Name == "HostParameter");
        Assert.Contains(constructor.Parameters, p => p.Name == "Children" && p.Type.EndsWith("NestedModel[]", StringComparison.Ordinal));
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(workspace.Output, "commands.json")));
        Assert.Equal(4, document.RootElement.GetProperty("operations").GetArrayLength());
        Assert.Equal(2, document.RootElement.GetProperty("constructors").GetArrayLength());
        Assert.Equal(3, document.RootElement.GetProperty("commandCount").GetInt32());
        Assert.Equal(3, document.RootElement.GetProperty("commands").GetArrayLength());
        Assert.False(File.Exists(Path.Combine(workspace.Output, "src", "Get-QCWidgetsByWidgetId.cs")));
    }

    [Fact]
    public void NestedResourceKeepsItsOwnCommandAndPerSetBindings()
    {
        using var workspace = new Workspace();
        JsonObject manifest = workspace.ReadManifest();
        JsonArray operations = manifest["apis"]![0]!["operations"]!.AsArray();
        operations.Add(Operation("/api/v1/widgets/{widgetId}/parts", "V1WidgetsPartsGet"));
        operations.Add(Operation("/api/v1/widgets/{widgetId}/parts/{id}", "V1WidgetsPartsGet"));
        workspace.WriteManifest(manifest);
        CommandInventory inventory = workspace.Generate();
        EndpointCommandGroup nested = Assert.Single(inventory.Commands, command => command.CommandName == "Get-QCWidgetsParts");
        Assert.Equal(new[] { "List", "ById" }, nested.ParameterSetNames);
        EndpointCommand list = Assert.Single(nested.Operations, operation => operation.ParameterSetName == "List");
        EndpointCommand detail = Assert.Single(nested.Operations, operation => operation.ParameterSetName == "ById");
        CommandParameter listParent = Assert.Single(list.Parameters, parameter => parameter.Name == "WidgetId");
        CommandParameter detailParent = Assert.Single(detail.Parameters, parameter => parameter.Name == "WidgetId");
        Assert.True(listParent.Mandatory);
        Assert.True(detailParent.Mandatory);
        Assert.True(listParent.PipelineByValue);
        Assert.False(detailParent.PipelineByValue);
        Assert.Empty(listParent.Aliases); // The item's real Id property makes this alias invalid command-wide.
        Assert.Empty(detailParent.Aliases);
        Assert.Contains(inventory.Commands, command => command.CommandName == "Get-QCWidgets");
        Assert.Equal(6, inventory.Operations.Count);
        Assert.Equal(4, inventory.CommandCount);

        string generated = File.ReadAllText(Path.Combine(workspace.Output, "src", "Get-QCWidgetsParts.cs"));
        Assert.Contains("DefaultParameterSetName = \"List\"", generated);
        Assert.Contains("[Parameter(ParameterSetName = \"List\", Mandatory = false)]\n        public global::System.Int32? Limit", generated);
        Assert.Contains("[Parameter(ParameterSetName = \"List\", Mandatory = false)]\n        public global::System.Boolean? Enabled", generated);
        Assert.Contains("client.V1WidgetsPartsGetAsync(WidgetId, Limit, Enabled, cancellationToken)", generated);
        Assert.Contains("client.V1WidgetsPartsGetAsync(WidgetId, Id, cancellationToken)", generated);
        Assert.Contains("switch (ParameterSetName)", generated);
        Assert.DoesNotContain("BeginProcessing", generated);
        Assert.DoesNotContain("if (Enabled", generated);
        Assert.DoesNotContain("if (Limit", generated);
        Assert.Contains("ParameterSetName = new[] { \"List\" }", generated);
        Assert.Contains("ParameterSetName = new[] { \"ById\" }", generated);
    }

    [Fact]
    public void QueryIdAndPathIdRequireExplicitParameterPolicy()
    {
        using var workspace = new Workspace();
        workspace.ReplaceOperations(Operation("/api/v1/widgets", "V1WidgetsFiltered"), Operation("/api/v1/widgets/{id}", "V1WidgetsAudit"));
        Assert.Contains("also a list parameter", Assert.Throws<InvalidDataException>(() => workspace.Generate()).Message);
        Assert.False(Directory.Exists(workspace.Output));
        File.WriteAllText(workspace.Rules, """
            {"formatVersion":1,"parameterNames":[{"operation":"widgets GET /api/v1/widgets/{id}","parameter":"id","name":"WidgetId","reason":"Separate path identifier from list filter."}]}
            """);
        CommandInventory inventory = workspace.Generate();
        EndpointCommandGroup group = Assert.Single(inventory.Commands);
        Assert.Equal(new[] { "List", "ByWidgetId" }, group.ParameterSetNames);
        CommandParameter item = Assert.Single(group.Operations[1].Parameters);
        Assert.Equal("id", item.ClrName);
        Assert.Equal("WidgetId", item.Name);
        Assert.Equal("Separate path identifier from list filter.", item.NamingReason);
        Assert.Empty(item.Aliases);
        Assert.Equal("Id", Assert.Single(group.Operations[0].Parameters).Name);
        string generated = File.ReadAllText(Path.Combine(workspace.Output, "src", "Get-QCWidgets.cs"));
        Assert.Contains("client.V1WidgetsFilteredAsync(Id, cancellationToken)", generated);
        Assert.Contains("client.V1WidgetsAuditAsync(WidgetId, cancellationToken)", generated);
        Assert.Contains("target.Replace(\"{id}\", global::System.Convert.ToString(WidgetId", generated);
    }

    [Fact]
    public void IncompatibleSharedTypeRequiresExplicitSeparateCommand()
    {
        using var workspace = new Workspace();
        workspace.ReplaceOperations(Operation("/api/v1/widgets", "V1WidgetsTypedList"), Operation("/api/v1/widgets/{widgetId}", "V1WidgetsTypedItem"));
        Assert.Contains("incompatible types for parameter Limit", Assert.Throws<InvalidDataException>(() => workspace.Generate()).Message);
        Assert.False(Directory.Exists(workspace.Output));
        File.WriteAllText(workspace.Rules, """
            {"formatVersion":1,"commandNames":[{"operation":"widgets GET /api/v1/widgets/{widgetId}","commandName":"Get-QCWidgetsByWidgetId","reason":"Preserve distinct Limit types."}]}
            """);
        CommandInventory inventory = workspace.Generate();
        Assert.Equal(2, inventory.CommandCount);
        Assert.All(inventory.Operations, operation => Assert.Equal("Default", operation.ParameterSetName));
        Assert.Equal("global::System.Int32?", inventory.Operations[0].Parameters.Single().Type);
        Assert.Equal("global::System.String", inventory.Operations[1].Parameters.Single(parameter => parameter.Name == "Limit").Type);
    }

    [Theory]
    [InlineData("unused-operation")]
    [InlineData("unused-parameter")]
    [InlineData("missing-reason")]
    [InlineData("invalid-name")]
    [InlineData("reserved-name")]
    [InlineData("duplicate")]
    public void InvalidOrStaleParameterNamePolicyFailsClosed(string kind)
    {
        using var workspace = new Workspace();
        var rule = new JsonObject
        {
            ["operation"] = kind == "unused-operation" ? "widgets GET /api/v1/missing" : "widgets GET /api/v1/widgets/{widgetId}",
            ["parameter"] = kind == "unused-parameter" ? "missing" : "widgetId",
            ["name"] = kind == "invalid-name" ? "Bad-Name" : kind == "reserved-name" ? "Connection" : "RecordId",
            ["reason"] = kind == "missing-reason" ? " " : "Fixture parameter policy."
        };
        var policies = new JsonArray(rule);
        if (kind == "duplicate") policies.Add(rule.DeepClone());
        File.WriteAllText(workspace.Rules, new JsonObject { ["formatVersion"] = 1, ["parameterNames"] = policies }.ToJsonString());
        Assert.ThrowsAny<Exception>(() => workspace.Generate());
        Assert.False(Directory.Exists(workspace.Output));
    }

    [Fact]
    public void RemovingDetailRouteReplacesGroupedClassWithSingleSet()
    {
        using var workspace = new Workspace();
        workspace.Generate();
        JsonObject manifest = workspace.ReadManifest();
        manifest["apis"]![0]!["operations"]!.AsArray().RemoveAt(1);
        workspace.WriteManifest(manifest);
        // The fixture assembly still has the retired route's same-named overload.
        // Bind the surviving SDK signature explicitly, as a real policy must do.
        File.WriteAllText(workspace.Rules, new JsonObject
        {
            ["formatVersion"] = 1,
            ["methodBindings"] = new JsonArray(new JsonObject
            {
                ["operation"] = "widgets GET /api/v1/widgets",
                ["parameterNames"] = new JsonArray("filter", "limit", "enabled", "cancellationToken"),
                ["reason"] = "Fixture assembly still contains the retired detail overload."
            })
        }.ToJsonString());
        CommandInventory inventory = workspace.Generate();
        EndpointCommandGroup group = Assert.Single(inventory.Commands, command => command.CommandName == "Get-QCWidgets");
        Assert.Equal("Default", group.DefaultParameterSetName);
        Assert.Equal(new[] { "Default" }, group.ParameterSetNames);
        string generated = File.ReadAllText(Path.Combine(workspace.Output, "src", "Get-QCWidgets.cs"));
        Assert.DoesNotContain("ByWidgetId", generated);
        Assert.DoesNotContain("public global::System.String WidgetId", generated);
    }

    [Fact]
    public void IdenticalInputsProduceIdenticalBytesAndCheckDoesNotReplaceOutput()
    {
        using var workspace = new Workspace();
        workspace.Generate();
        var first = Snapshot(workspace.Output);
        string sentinel = Path.Combine(workspace.Output, "commands.json");
        DateTime written = File.GetLastWriteTimeUtc(sentinel);
        workspace.Generate(check: true);
        Assert.Equal(written, File.GetLastWriteTimeUtc(sentinel));
        workspace.Generate();
        EqualSnapshots(first, Snapshot(workspace.Output));
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("missing")]
    [InlineData("stale")]
    public void CheckFailsForChangedMissingAndStaleOutputWithoutRepair(string kind)
    {
        using var workspace = new Workspace();
        workspace.Generate();
        string path = Path.Combine(workspace.Output, kind == "stale" ? "stale.cs" : "commands.json");
        if (kind == "missing") File.Delete(path);
        else File.AppendAllText(path, "changed\n");
        var changed = Snapshot(workspace.Output);
        Assert.Throws<InvalidDataException>(() => workspace.Generate(check: true));
        EqualSnapshots(changed, Snapshot(workspace.Output));
    }

    [Fact]
    public void GenerationReconcilesRemovedOperationsAndModels()
    {
        using var workspace = new Workspace();
        workspace.Generate();
        JsonObject manifest = workspace.ReadManifest();
        JsonArray operations = manifest["apis"]![0]!["operations"]!.AsArray();
        operations.RemoveAt(2); // Remove the only request-body operation and its transitive model roots.
        workspace.WriteManifest(manifest);
        CommandInventory inventory = workspace.Generate();
        Assert.Equal(3, inventory.Operations.Count);
        Assert.Empty(inventory.Constructors);
        Assert.False(File.Exists(Path.Combine(workspace.Output, "src", "New-QCWidgets.cs")));
        Assert.False(File.Exists(Path.Combine(workspace.Output, "src", "New-QCWidgetsWidgetRequestObject.cs")));
    }

    [Fact]
    public void MissingManifestMethodFailsWithoutReplacingGoodOutput()
    {
        using var workspace = new Workspace();
        workspace.Generate();
        var previous = Snapshot(workspace.Output);
        JsonObject manifest = workspace.ReadManifest();
        manifest["apis"]![0]!["operations"]![0]!["name"] = "V1Missing";
        workspace.WriteManifest(manifest);
        var exception = Assert.Throws<InvalidDataException>(() => workspace.Generate());
        Assert.Contains("Expected one SDK method", exception.Message);
        EqualSnapshots(previous, Snapshot(workspace.Output));
    }

    [Fact]
    public void DuplicateManifestOperationsAreRejected()
    {
        using var workspace = new Workspace();
        JsonObject manifest = workspace.ReadManifest();
        JsonArray operations = manifest["apis"]![0]!["operations"]!.AsArray();
        operations.Add(operations[0]!.DeepClone());
        workspace.WriteManifest(manifest);
        Assert.Contains("Duplicate operation", Assert.Throws<InvalidDataException>(() => workspace.Generate()).Message);
        Assert.False(Directory.Exists(workspace.Output));
    }

    [Theory]
    [InlineData("unused")]
    [InlineData("missing-reason")]
    [InlineData("bad-verb")]
    [InlineData("legacy-prefix")]
    [InlineData("missing-model")]
    [InlineData("wrong-signature")]
    public void InvalidOrUnusedPolicyFailsClosed(string kind)
    {
        using var workspace = new Workspace();
        JsonObject rules = JsonNode.Parse("""{"formatVersion":1,"commandNames":[],"methodBindings":[],"modelRoots":[]}""")!.AsObject();
        if (kind == "missing-model") rules["modelRoots"]!.AsArray().Add(new JsonObject { ["type"] = "QlikCloud.Widgets.Missing", ["reason"] = "Fixture" });
        else if (kind == "wrong-signature") rules["methodBindings"]!.AsArray().Add(new JsonObject
        {
            ["operation"] = "widgets GET /api/v1/widgets", ["parameterNames"] = new JsonArray("missing"), ["reason"] = "Fixture"
        });
        else rules["commandNames"]!.AsArray().Add(new JsonObject
        {
            ["operation"] = kind == "unused" ? "widgets DELETE /api/v1/widgets" : "widgets GET /api/v1/widgets",
            ["commandName"] = kind == "bad-verb" ? "Fetch-QCWidgets" : kind == "legacy-prefix" ? "Get-QlikCloudWidgets" : "Get-QCWidgetList",
            ["reason"] = kind == "missing-reason" ? "" : "Fixture"
        });
        File.WriteAllText(workspace.Rules, rules.ToJsonString());
        Assert.ThrowsAny<Exception>(() => workspace.Generate());
        Assert.False(Directory.Exists(workspace.Output));
    }

    [Fact]
    public void ExplicitNamingRuleIsRecordedAndCollisionIsRejected()
    {
        using var workspace = new Workspace();
        File.WriteAllText(workspace.Rules, """
            {"formatVersion":1,"commandNames":[{"operation":"widgets GET /api/v1/widgets","commandName":"Get-QCWidgetList","reason":"Explicit test naming policy"}]}
            """);
        CommandInventory inventory = workspace.Generate();
        Assert.Equal("Explicit test naming policy", Assert.Single(inventory.Operations, c => c.CommandName == "Get-QCWidgetList").NamingReason);
        File.WriteAllText(workspace.Rules, """
            {"formatVersion":1,"commandNames":[{"operation":"widgets GET /api/v1/widgets","commandName":"Get-QCWidgetsMe","reason":"Intentional collision"}]}
            """);
        Assert.Contains("collision", Assert.Throws<InvalidDataException>(() => workspace.Generate()).Message);
    }

    [Fact]
    public void UnownedOutputIsNeverAdoptedOrReplaced()
    {
        using var workspace = new Workspace();
        Directory.CreateDirectory(workspace.Output);
        string sentinel = Path.Combine(workspace.Output, "manual.txt");
        File.WriteAllText(sentinel, "keep");
        Assert.Contains("unowned", Assert.Throws<InvalidDataException>(() => workspace.Generate()).Message);
        Assert.Equal("keep", File.ReadAllText(sentinel));
        Assert.Single(Directory.GetFiles(workspace.Output));
    }

    private static Dictionary<string, byte[]> Snapshot(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(directory, path), File.ReadAllBytes, StringComparer.Ordinal);
    private static void EqualSnapshots(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys.OrderBy(k => k, StringComparer.Ordinal), actual.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var entry in expected) Assert.Equal(entry.Value, actual[entry.Key]);
    }

    private static JsonObject Operation(string path, string methodName, string verb = "get") =>
        new() { ["path"] = path, ["method"] = verb, ["name"] = methodName, ["implementation"] = "generated" };

    private sealed class Workspace : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "qlikcloudcli-generator-test-" + Guid.NewGuid().ToString("N"));
        public string Output => Path.Combine(root, "generated");
        public string Rules => Path.Combine(root, "rules.json");
        private string Manifest => Path.Combine(root, "client-manifest.json");
        public Workspace()
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Rules, """{"formatVersion":1,"commandNames":[],"methodBindings":[],"modelRoots":[]}""");
            File.WriteAllText(Manifest, """
                {"generator":"QlikCloud.Generator","formatVersion":1,"command":"generate","apis":[
                  {"api":"widgets","operations":[
                    {"path":"/api/v1/widgets","method":"get","name":"V1WidgetsGet","implementation":"generated"},
                    {"path":"/api/v1/widgets/{widgetId}","method":"get","name":"V1WidgetsGet","implementation":"generated"},
                    {"path":"/api/v1/widgets","method":"post","name":"V1WidgetsPost","implementation":"generated"},
                    {"path":"/api/v1/widgets/me","method":"get","name":"V1WidgetsMe","implementation":"custom"}]},
                  {"api":"ignored","excluded":"Explicit fixture exclusion"}]}
                """);
        }
        public CommandInventory Generate(bool check = false) => new CmdletGenerator().Generate(
            Path.Combine(AppContext.BaseDirectory, "Fixture", "QlikCloud.dll"), Manifest, Output, Rules, check);
        public JsonObject ReadManifest() => JsonNode.Parse(File.ReadAllText(Manifest))!.AsObject();
        public void WriteManifest(JsonObject manifest) => File.WriteAllText(Manifest, manifest.ToJsonString());
        public void ReplaceOperations(params JsonObject[] operations)
        {
            JsonObject manifest = ReadManifest();
            manifest["apis"]![0]!["operations"] = new JsonArray(operations.Cast<JsonNode>().ToArray());
            WriteManifest(manifest);
        }
        public void Dispose()
        {
            string full = Path.GetFullPath(root);
            if (Path.GetDirectoryName(full) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))
                || !Path.GetFileName(full).StartsWith("qlikcloudcli-generator-test-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe test cleanup target: " + full);
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }
}
