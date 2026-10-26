using Newtonsoft.Json.Linq;
using QlikCloud.Generator;
using Xunit;

namespace QlikCloud.Generator.Tests;

public sealed class DeprecationTests
{
    [Theory]
    [InlineData("{\"deprecated\":true}", true)]
    [InlineData("{\"x-qlik-deprecated\":true}", true)]
    [InlineData("{\"deprecated\":true,\"x-qlik-deprecated\":false}", true)]
    [InlineData("{\"deprecated\":false,\"x-qlik-deprecated\":true}", true)]
    [InlineData("{\"deprecated\":\"true\",\"x-qlik-deprecated\":1}", false)]
    [InlineData("{\"deprecated\":null,\"x-qlik-deprecated\":false}", false)]
    [InlineData("{\"Deprecated\":true,\"X-QLIK-DEPRECATED\":true}", false)]
    [InlineData("{}", false)]
    public void OnlyExactBooleanFlagsMarkDeprecation(string json, bool expected)
    {
        var node = JObject.Parse(json);
        Assert.Equal(expected, DeprecationPolicy.IsDeprecated(node));
        Assert.Equal(expected, DeprecationPolicy.Message("fixture", node) != null);
    }

    [Theory]
    [InlineData("document")]
    [InlineData("info")]
    [InlineData("paths")]
    [InlineData("path")]
    [InlineData("operation")]
    public void InheritedDeprecationRetainsOperationsAndReportsMetadata(string scope)
    {
        var document = Schema();
        JObject target = scope switch
        {
            "document" => document,
            "info" => (JObject)document["info"]!,
            "paths" => (JObject)document["paths"]!,
            "path" => (JObject)document["paths"]!["/widgets"]!,
            _ => (JObject)document["paths"]!["/widgets"]!["get"]!
        };
        target["deprecated"] = true;
        var renderer = new ClientRenderer("widgets", SchemaTransforms.Apply("widgets", document));
        string source = renderer.Render(TestPaths.Templates);
        var operations = JArray.FromObject(renderer.Operations);
        Assert.Single(operations);
        Assert.True((bool)operations[0]!["deprecated"]!);
        Assert.Equal("widgets GET /widgets is deprecated.", (string?)operations[0]!["deprecationMessage"]);
        Assert.Contains("[global::System.Obsolete(\"widgets GET /widgets is deprecated.\")]", source);
        Assert.Contains("public virtual async System.Threading.Tasks.Task<APIResponse>", source);
    }

    [Fact]
    public void FalseChildFlagDoesNotClearParentAndCustomOperationsReceiveMetadata()
    {
        var document = Schema();
        document["x-qlik-deprecated"] = true;
        document["paths"]!["/widgets"]!["get"]!["deprecated"] = false;
        var renderer = new ClientRenderer("widgets", document, ["widgets GET /widgets"]);
        renderer.Render(TestPaths.Templates);
        var operation = JObject.FromObject(Assert.Single(renderer.Operations));
        Assert.True((bool)operation["deprecated"]!);
        Assert.Equal("custom", (string?)operation["implementation"]);
    }

    [Fact]
    public void MixedOperationsHaveIndependentStatusAndReplacementGuidance()
    {
        var document = Schema();
        var path = (JObject)document["paths"]!["/widgets"]!;
        path["post"] = path["get"]!.DeepClone();
        path["post"]!["x-qlik-deprecated"] = true;
        path["post"]!["x-qlik-deprecation-message"] = "Use the namespaced API.";
        path["post"]!["x-qlik-replaced-by"] = JArray.Parse("""[{"namespace":"next","uniqueIdentifier":"POST:/next/widgets"}]""");
        var renderer = new ClientRenderer("widgets", document);
        renderer.Render(TestPaths.Templates);
        var operations = JArray.FromObject(renderer.Operations);
        Assert.False((bool)operations[0]!["deprecated"]!);
        Assert.Equal(JTokenType.Null, operations[0]!["deprecationMessage"]!.Type);
        Assert.True((bool)operations[1]!["deprecated"]!);
        Assert.Contains("Use the namespaced API.", (string?)operations[1]!["deprecationMessage"]);
        Assert.Contains("POST:/next/widgets", (string?)operations[1]!["deprecationMessage"]);
    }

    private static JObject Schema() => JObject.Parse("""
        {"openapi":"3.0.0","info":{},"paths":{"/widgets":{"get":{"responses":{"200":{"description":"OK"}}}}}}
        """);
}
