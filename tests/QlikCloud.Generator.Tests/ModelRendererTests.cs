using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QlikCloud.Generator;
using Xunit;

namespace QlikCloud.Generator.Tests;

public sealed class ModelRendererTests
{
    [Fact]
    public void OAuth_user_responses_without_subject_deserialize_without_weakening_creation_or_other_required_fields()
    {
        // Fabricated contract/payload: no private tenant response data is retained.
        var original = JObject.Parse("""
        {
          "openapi":"3.0.0",
          "components":{"schemas":{
            "User":{"type":"object","required":["id","subject","name","tenantId"],"properties":{
              "id":{"type":"string"},"subject":{"type":"string","description":"Identity-provider subject."},
              "name":{"type":"string"},"tenantId":{"type":"string"}
            }},
            "Users":{"type":"object","properties":{"data":{"type":"array","items":{"$ref":"#/components/schemas/User"}}}},
            "UserPostSchema":{"type":"object","required":["subject"],"properties":{"subject":{"type":"string"},"name":{"type":"string"}}},
            "OtherIdentity":{"type":"object","required":["subject"],"properties":{"subject":{"type":"string"}}}
          }}
        }
        """);
        var before = original.ToString();
        var normalized = SchemaTransforms.Apply("users", original);
        Assert.Equal(before, original.ToString());
        Assert.True(JToken.DeepEquals(normalized, SchemaTransforms.Apply("users", normalized)));
        Assert.Contains("subject", SchemaTransforms.Apply("other-api", original)["components"]!["schemas"]!["User"]!["required"]!.Values<string>());
        var renderer = new ModelRenderer("users", normalized);
        var assembly = Compile(renderer);
        var usersType = assembly.GetType("QlikCloud.Users.Users", true)!;
        var userType = assembly.GetType("QlikCloud.Users.User", true)!;
        const string response = """{"data":[{"id":"fixture-machine-user","name":"Fixture machine user","tenantId":"fixture-tenant"}]}""";
        var originalUsersType = Compile(new ModelRenderer("users", original)).GetType("QlikCloud.Users.Users", true)!;
        Assert.Throws<JsonSerializationException>(() => JsonConvert.DeserializeObject(response, originalUsersType));
        var users = JsonConvert.DeserializeObject(response, usersType)!;
        Assert.True(JToken.DeepEquals(JToken.Parse(response), JToken.Parse(JsonConvert.SerializeObject(users))));
        var entry = ((System.Collections.IEnumerable)usersType.GetProperty("Data")!.GetValue(users)!).Cast<object>().Single();
        Assert.Null(userType.GetProperty("Subject")!.GetValue(entry));

        foreach (var stillRequired in new[] { "id", "name", "tenantId" })
        {
            var invalidUser = (JObject)JObject.Parse(response)["data"]![0]!.DeepClone();
            invalidUser.Remove(stillRequired);
            Assert.Throws<JsonSerializationException>(() => JsonConvert.DeserializeObject(invalidUser.ToString(), userType));
        }
        Assert.Throws<JsonSerializationException>(() => JsonConvert.DeserializeObject("{}", assembly.GetType("QlikCloud.Users.UserPostSchema", true)!));
        Assert.Throws<JsonSerializationException>(() => JsonConvert.DeserializeObject("{}", assembly.GetType("QlikCloud.Users.OtherIdentity", true)!));
        Assert.Contains("May be omitted in OAuth user-list responses.", renderer.RenderModels());
    }

    [Fact]
    public void Recursive_models_round_trip_wire_names_enums_dates_and_extension_data()
    {
        var renderer = Renderer("""
        {
          "Node": {
            "type":"object", "required":["id", "nullableCount"],
            "properties": {
              "id":{"type":"string"},
              "nullableCount":{"type":"integer", "nullable":true},
              "status":{"type":"string","enum":["in-progress","complete", ""]},
              "createdAt":{"type":"string","format":"date-time"},
              "next":{"$ref":"#/components/schemas/Node"},
              "item-count":{"type":"integer","format":"int64"}
            }
          }
        }
        """);
        var assembly = Compile(renderer);
        var node = assembly.GetType("QlikCloud.Users.Node", true)!;
        const string json = """{"id":"first","nullableCount":null,"status":"in-progress","createdAt":"2025-12-04T00:15:00+11:00","next":{"id":"second","nullableCount":2},"item-count":9000000000,"future":{"flag":true}}""";
        var instance = JsonConvert.DeserializeObject(json, node)!;
        var actual = JObject.Parse(JsonConvert.SerializeObject(instance));
        Assert.Equal("in-progress", actual.Value<string>("status"));
        Assert.Equal(9000000000L, actual.Value<long>("item-count"));
        Assert.True(actual["future"]!.Value<bool>("flag"));
        Assert.Equal("second", actual["next"]!.Value<string>("id"));
        Assert.Equal(JTokenType.Null, actual["nullableCount"]!.Type);
        Assert.Equal(typeof(long?), node.GetProperty("ItemCount")!.PropertyType);
        Assert.Equal(typeof(DateTimeOffset?), node.GetProperty("CreatedAt")!.PropertyType);
        Assert.Throws<JsonSerializationException>(() => JsonConvert.DeserializeObject("{}", node));
        Assert.Equal(renderer.RenderModels(), renderer.RenderModels());
    }

    [Fact]
    public void AllOf_collections_and_dictionaries_preserve_values_and_named_contracts()
    {
        var renderer = Renderer("""
        {
          "Base":{"type":"object","required":["id"],"properties":{"id":{"type":"string"}}},
          "Child":{"allOf":[{"$ref":"#/components/schemas/Base"},{"type":"object","required":["active"],"properties":{"active":{"type":"boolean"}}}]},
          "Children":{"type":"array","items":{"$ref":"#/components/schemas/Child"}},
          "Counts":{"type":"object","additionalProperties":{"type":"integer","format":"int64"}},
          "Container":{"type":"object","properties":{"children":{"$ref":"#/components/schemas/Children"},"counts":{"$ref":"#/components/schemas/Counts"}}}
        }
        """);
        var assembly = Compile(renderer);
        var type = assembly.GetType("QlikCloud.Users.Container", true)!;
        const string json = """{"children":[{"id":"one","active":true}],"counts":{"one":5000000000}}""";
        var value = JsonConvert.DeserializeObject(json, type)!;
        Assert.True(JToken.DeepEquals(JToken.Parse(json), JToken.Parse(JsonConvert.SerializeObject(value))));
        Assert.NotNull(assembly.GetType("QlikCloud.Users.Children"));
        Assert.Equal("Counts", renderer.ResolveType(JObject.Parse("""{"$ref":"#/components/schemas/Counts"}"""), "ignored"));
        Assert.Throws<JsonSerializationException>(() => JsonConvert.DeserializeObject("{\"id\":\"one\"}", assembly.GetType("QlikCloud.Users.Child", true)!));
    }

    [Fact]
    public void AllOf_refinements_intersect_enums_and_preserve_conflicting_wire_values()
    {
        var renderer = Renderer("""
        {
          "Base":{"type":"object","required":["code"],"properties":{"code":{"type":"integer"},"status":{"type":"string","enum":["active","disabled","pending"]}}},
          "Refined":{"allOf":[{"$ref":"#/components/schemas/Base"},{"type":"object","properties":{"code":{"type":"string"},"status":{"type":"string","enum":["pending","unknown"]}}}]}
        }
        """);
        var assembly = Compile(renderer);
        var type = assembly.GetType("QlikCloud.Users.Refined", true)!;
        Assert.Equal(typeof(object), type.GetProperty("Code")!.PropertyType);
        var status = Nullable.GetUnderlyingType(type.GetProperty("Status")!.PropertyType)!;
        Assert.Equal(["Pending"], Enum.GetNames(status));
        foreach (var json in new[] { "{\"code\":1001,\"status\":\"pending\"}", "{\"code\":\"1001\",\"status\":\"pending\"}" })
            Assert.True(JToken.DeepEquals(JToken.Parse(json), JToken.Parse(JsonConvert.SerializeObject(JsonConvert.DeserializeObject(json, type)))));
        Assert.Contains(renderer.Warnings, warning => warning.Contains("Conflicting allOf property types", StringComparison.Ordinal));
        Assert.Equal(renderer.RenderModels(), renderer.RenderModels());
    }

    [Fact]
    public void Unions_retain_arrays_scalars_and_objects_and_announce_validation_limitation()
    {
        var renderer = Renderer("""
        {
          "Flexible":{"type":"object","properties":{"value":{"oneOf":[{"type":"string"},{"type":"object"},{"type":"array","items":{"type":"integer"}}]}}},
          "MaybeCount":{"oneOf":[{"type":"integer"},{"type":"null"}]}
        }
        """);
        var assembly = Compile(renderer);
        var type = assembly.GetType("QlikCloud.Users.Flexible", true)!;
        foreach (var json in new[] { "{\"value\":\"text\"}", "{\"value\":{\"nested\":[1,2]}}", "{\"value\":[1,2]}" })
            Assert.True(JToken.DeepEquals(JToken.Parse(json), JToken.Parse(JsonConvert.SerializeObject(JsonConvert.DeserializeObject(json, type)))));
        Assert.Contains(renderer.Warnings, warning => warning.Contains("Union retained as object", StringComparison.Ordinal));
        Assert.Contains("oneOf", renderer.RenderModels());
        Assert.Equal("int?", renderer.ResolveType(JObject.Parse("""{"$ref":"#/components/schemas/MaybeCount"}"""), "Count"));
    }

    [Fact]
    public void Forbidden_additional_properties_are_rejected_instead_of_discarded()
    {
        var renderer = Renderer("""
        {"Strict":{"type":"object","additionalProperties":false,"properties":{"id":{"type":"string"}}}}
        """);
        var type = Compile(renderer).GetType("QlikCloud.Users.Strict", true)!;
        var error = Assert.ThrowsAny<Exception>(() => JsonConvert.DeserializeObject("{\"id\":\"one\",\"unknown\":true}", type));
        Assert.IsType<JsonSerializationException>(error.GetBaseException());
        Assert.NotNull(JsonConvert.DeserializeObject("{\"id\":\"one\"}", type));
    }

    [Fact]
    public void Swagger_references_json_pointer_escaping_and_aliases_work()
    {
        var renderer = new ModelRenderer("api-keys", JObject.Parse("""
        {"swagger":"2.0","definitions":{
          "a/b~c":{"type":"object","properties":{"class":{"type":"string"},"123-name":{"type":"boolean"}}},
          "Alias":{"$ref":"#/definitions/a~1b~0c"},
          "Numeric":{"type":"integer","enum":[-1,0,4294967296]}
        }}
        """));
        Assert.Equal("QlikCloud.ApiKeys", renderer.Namespace);
        Assert.Equal("ABC", renderer.ResolveType(JObject.Parse("""{"$ref":"#/definitions/Alias"}"""), "Alias"));
        var assembly = Compile(renderer);
        var numeric = assembly.GetType("QlikCloud.ApiKeys.Numeric", true)!;
        Assert.Equal(typeof(long), Enum.GetUnderlyingType(numeric));
        Assert.Equal("4294967296", JsonConvert.SerializeObject(Enum.ToObject(numeric, 4294967296L)));
    }

    [Fact]
    public void Broken_external_and_cyclic_alias_references_fail_explicitly()
    {
        Assert.Throws<InvalidDataException>(() => Renderer("""{"Broken":{"type":"object","properties":{"value":{"$ref":"#/components/schemas/Missing"}}}}"""));
        Assert.Throws<InvalidDataException>(() => Renderer("""{"Broken":{"$ref":"other.json#/Value"}}"""));
        var renderer = Renderer("""{"A":{"$ref":"#/components/schemas/B"},"B":{"$ref":"#/components/schemas/A"}}""");
        Assert.Throws<InvalidDataException>(() => renderer.ResolveType(JObject.Parse("""{"$ref":"#/components/schemas/A"}"""), "A"));
    }

    [Fact]
    public void Inline_name_collisions_emit_distinct_compilable_members_and_types()
    {
        var renderer = Renderer("""
        {"Object":{"type":"object","properties":{
          "object":{"type":"string"},"toJson":{"type":"string"},
          "a-b":{"type":"object","properties":{"one":{"type":"boolean"}}},
          "a_b":{"type":"object","properties":{"two":{"type":"boolean"}}},
          "mode":{"type":"string","enum":["a-b","a_b", "1", ""]}
        }}}
        """);
        var type = Compile(renderer).GetType("QlikCloud.Users.Object", true)!;
        Assert.NotNull(type.GetProperty("Object2"));
        Assert.NotNull(type.GetProperty("ToJson2"));
        Assert.NotNull(type.GetProperty("AB"));
        Assert.NotNull(type.GetProperty("AB2"));
        var instance = JsonConvert.DeserializeObject("{\"a-b\":{\"one\":true},\"a_b\":{\"two\":true},\"mode\":\"a_b\"}", type)!;
        Assert.Equal("a_b", JObject.Parse(JsonConvert.SerializeObject(instance)).Value<string>("mode"));
    }

    private static ModelRenderer Renderer(string schemas)
        => new("users", new JObject { ["openapi"] = "3.0.0", ["components"] = new JObject { ["schemas"] = JObject.Parse(schemas) } });

    private static Assembly Compile(ModelRenderer renderer)
    {
        var source = "namespace " + renderer.Namespace + " {\n" + renderer.RenderModels() + "\n}";
        var frameworkReferences = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(JsonConvert).Assembly.Location).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("ModelTests_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source)], frameworkReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var output = new MemoryStream();
        var result = compilation.Emit(output);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return Assembly.Load(output.ToArray());
    }
}
