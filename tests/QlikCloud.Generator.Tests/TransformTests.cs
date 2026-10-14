using Newtonsoft.Json.Linq;
using QlikCloud.Generator;
using Xunit;

namespace QlikCloud.Generator.Tests;

public class TransformTests
{
	[Fact]
	public void AppReloadIdentifierIsAPathParameterAndInputRemainsUnmodified()
	{
		var original = JObject.Parse("""
		{"paths":{"/api/v1/apps/{appId}/reloads/metadata/{reloadId}":{"get":{"parameters":[
		  {"name":"appId","in":"path","required":true,"schema":{"type":"string"}},
		  {"name":"reloadId","in":"query","schema":{"type":"string"}}
		]}}}}
		""");
		const string route = "/api/v1/apps/{appId}/reloads/metadata/{reloadId}";
		var fixedDocument = SchemaTransforms.Apply("apps", original);
		JObject ReloadId(JObject document) => ((JArray)document["paths"]![route]!["get"]!["parameters"]!).OfType<JObject>().Single(p => (string?)p["name"] == "reloadId");
		Assert.Equal("query", (string?)ReloadId(original)["in"]);
		Assert.Equal("path", (string?)ReloadId(fixedDocument)["in"]);
		Assert.True((bool)ReloadId(fixedDocument)["required"]!);
		Assert.True(JToken.DeepEquals(fixedDocument, SchemaTransforms.Apply("apps", fixedDocument)));
	}

	[Fact]
	public void SharedSchemaFixesPreserveOriginalAndOptionalAuthorization()
	{
		var original = JObject.Parse("""
		{
		  "paths": { "/v1/example": { "get": { "operationId": "example#v1#get",
		    "parameters": [
		      { "name": "Authorization", "in": "header", "required": true },
		      { "name": "Authorization", "in": "header", "required": false },
		      { "name": "Authorization", "in": "query", "required": true }
		    ] } } },
		  "components": { "schemas": {
		    "ConnectionGet": { "required": ["qSeparateCredentials", "privileges", "created", "updated", "user", "id"] },
		    "ConnectionsResponse": { "required": ["connectStatement", "id"] },
		    "itemResultResponseBody": { "required": ["resourceCustomAttributes", "id"] }
		  } }
		}
		""");
		var before = original.ToString();
		var fixedDocument = SchemaTransforms.Apply("data-connections", original);
		Assert.Equal(before, original.ToString());
		Assert.Equal("get", (string?)fixedDocument.SelectToken("paths['/v1/example'].get.operationId"));
		Assert.Equal(2, ((JArray)fixedDocument.SelectToken("paths['/v1/example'].get.parameters")!).Count);
		foreach (var name in new[] { "ConnectionGet", "ConnectionsResponse", "itemResultResponseBody" })
			Assert.Equal("[\"id\"]", fixedDocument["components"]!["schemas"]![name]!["required"]!.ToString(Newtonsoft.Json.Formatting.None));
	}

	[Fact]
	public void ApiSpecificInputFixesKeepCustomPartialTypesCompatible()
	{
		var users = JObject.Parse("""{"components":{"schemas":{"JSONPatch":{"properties":{"path":{"enum":["/name"]},"value":{"type":"string"}}}}}}""");
		var fixedUsers = SchemaTransforms.Apply("users", users);
		Assert.Equal("#/components/schemas/JSONPatchValue", (string?)fixedUsers.SelectToken("components.schemas.JSONPatch.properties.value['$ref']"));
		Assert.Equal("object", (string?)fixedUsers.SelectToken("components.schemas.JSONPatchValue.type"));
		Assert.Contains("/email", fixedUsers.SelectToken("components.schemas.JSONPatch.properties.path.enum")!.Values<string>());
		Assert.True(JToken.DeepEquals(fixedUsers, SchemaTransforms.Apply("Users", fixedUsers)));

		var apps = SchemaTransforms.Apply("Apps", JObject.Parse("""{"components":{"schemas":{"NxAttributes":{"properties":{}}}}}"""));
		Assert.Equal("string", (string?)apps.SelectToken("components.schemas.NxAttributes.properties.spaceId.type"));
		var roles = SchemaTransforms.Apply("Roles", JObject.Parse("""{"components":{"schemas":{"Role":{"required":["tenantId","description","name"]}}}}"""));
		Assert.Equal("[\"name\"]", roles.SelectToken("components.schemas.Role.required")!.ToString(Newtonsoft.Json.Formatting.None));
		var licenses = SchemaTransforms.Apply("Licenses", JObject.Parse("""{"components":{"schemas":{"licenseOverview":{"properties":{"allotments":{"items":{"properties":{"name":{"type":"string","enum":["old"]}}}}}}}}}"""));
		Assert.Null(licenses.SelectToken("components.schemas.licenseOverview.properties.allotments.items.properties.name.enum"));
		var ml = SchemaTransforms.Apply("ML", JObject.Parse("""{"components":{"schemas":{"ExperimentVersion":{"properties":{"status":{"enum":["ready"]}}}}}}"""));
		Assert.Contains("pending", ml.SelectToken("components.schemas.ExperimentVersion.properties.status.enum")!.Values<string>());
	}

	[Fact]
	public void DeprecatedSchemaReferencesAreRemovedWithoutConfusingPropertyNames()
	{
		var document = JObject.Parse("""
		{
		  "components": { "schemas": {
		    "old-schema": { "type": "object", "x-qlik-deprecated": true },
		    "OldAlias": { "$ref": "#/components/schemas/old-schema" },
		    "legacyField": { "type": "string" },
		    "Container": { "type": "object", "required": ["gone", "legacyField", "keep", "inherited"], "properties": {
		      "gone": { "$ref": "#/components/schemas/old-schema" },
		      "legacyField": { "type": "string", "x-qlik-deprecated": true },
		      "keep": { "$ref": "#/components/schemas/legacyField" }
		    } },
		    "KeepEmpty": {},
		    "PrunedEmpty": { "properties": { "old": { "x-qlik-deprecated": true } } },
		    "RefToPrunedEmpty": { "$ref": "#/components/schemas/PrunedEmpty" },
		    "AliasConsumer": { "type": "object", "properties": { "gone": { "$ref": "#/components/schemas/OldAlias" } } },
		    "OnlyOld": { "type": "object", "required": ["gone"], "properties": { "gone": { "$ref": "#/components/schemas/old-schema" } } },
		    "Combined": { "allOf": [ { "$ref": "#/components/schemas/old-schema" }, { "type": "object" } ] }
		  } },
		  "paths": { "/old": { "get": { "x-qlik-deprecated": true } }, "/live": { "get": { "responses": { "200": { "description": "OK" } } } } }
		}
		""");
		var actual = SchemaTransforms.Apply("Example", document);
		Assert.Null(actual.SelectToken("components.schemas['old-schema']"));
		Assert.Null(actual.SelectToken("components.schemas.OldAlias"));
		Assert.Null(actual.SelectToken("components.schemas.AliasConsumer.properties"));
		Assert.NotNull(actual.SelectToken("components.schemas.legacyField"));
		Assert.NotNull(actual.SelectToken("components.schemas.KeepEmpty"));
		Assert.NotNull(actual.SelectToken("components.schemas.PrunedEmpty"));
		Assert.NotNull(actual.SelectToken("components.schemas.RefToPrunedEmpty"));
		Assert.Null(actual.SelectToken("components.schemas.Container.properties.gone"));
		Assert.Null(actual.SelectToken("components.schemas.Container.properties.legacyField"));
		Assert.Equal("#/components/schemas/legacyField", (string?)actual.SelectToken("components.schemas.Container.properties.keep['$ref']"));
		Assert.Equal("[\"keep\",\"inherited\"]", actual.SelectToken("components.schemas.Container.required")!.ToString(Newtonsoft.Json.Formatting.None));
		Assert.Null(actual.SelectToken("components.schemas.OnlyOld.required"));
		Assert.Single((JArray)actual.SelectToken("components.schemas.Combined.allOf")!);
		Assert.Null(actual.SelectToken("paths['/old']"));
		Assert.NotNull(actual.SelectToken("paths['/live']"));
		Assert.True(JToken.DeepEquals(actual, SchemaTransforms.Apply("Example", actual)));
	}

	[Fact]
	public void SchemaEnumPolicyPreservesExistingNumericValuesAndAddsNewUpstreamMembers()
	{
		var spaces = SchemaTransforms.Apply("spaces", JObject.Parse("""
		{"components":{"schemas":{
		  "ActionName":{"type":"string","enum":["change_owner","create","link_environment","restrict"]},
		  "RoleType":{"type":"string","enum":["consumer","datapreview","future_role"]}
		}}}
		"""));
		var actions = spaces.SelectToken("components.schemas.ActionName.enum")!.Values<string>().ToArray();
		Assert.Equal("change_owner", actions[5]);
		Assert.Equal(new[] { "link_environment", "restrict" }, actions[6..]);
		var roles = spaces.SelectToken("components.schemas.RoleType.enum")!.Values<string>().ToArray();
		Assert.Equal("datapreview", roles[9]);
		Assert.Equal("future_role", roles[10]);
		var renderer = new ModelRenderer("spaces", spaces);
		var source = renderer.RenderModels();
		Assert.Contains("ChangeOwner = 5", source);
		Assert.Contains("Datapreview = 9", source);
		Assert.Contains("LinkEnvironment = 6", source);
		Assert.True(JToken.DeepEquals(spaces, SchemaTransforms.Apply("spaces", spaces)));

		var users = SchemaTransforms.Apply("users", JObject.Parse("""
		{"components":{"schemas":{"User":{"type":"object","properties":{"roles":{"type":"array","items":{"type":"string","enum":["TenantAdmin","NewRole"]}}}}}}}
		"""));
		Assert.Equal("#/components/schemas/Roles", (string?)users.SelectToken("components.schemas.User.properties.roles.items['$ref']"));
		Assert.Equal("PrivateAnalyticsCreator", (string?)users.SelectToken("components.schemas.Roles.enum[16]"));
		Assert.Equal("NewRole", (string?)users.SelectToken("components.schemas.Roles.enum[17]"));
		Assert.True(JToken.DeepEquals(users, SchemaTransforms.Apply("users", users)));
	}

	[Fact]
	public void IdentityProviderSchemaRetainsNamedUnionAndTypedDataCollection()
	{
		var input = JObject.Parse("""
		{"components":{"schemas":{
		  "IDP":{"oneOf":[{"$ref":"#/components/schemas/IDPOIDC"},{"$ref":"#/components/schemas/IDPJWTAuth"}],"discriminator":{"propertyName":"protocol"}},
		  "IDPOIDC":{"type":"object","properties":{"issuer":{"type":"string"}}},
		  "IDPJWTAuth":{"type":"object","properties":{"keys":{"type":"string"}}},
		  "IDPArray":{"type":"object","properties":{"data":{"type":"array","items":{"type":"object"}}}}
		}}}
		""");
		var actual = SchemaTransforms.Apply("identity-providers", input);
		Assert.True((bool)actual.SelectToken("components.schemas.IDP['x-generator-preserve-named-object']")!);
		Assert.NotNull(actual.SelectToken("components.schemas.IDP.oneOf"));
		Assert.Equal("#/components/schemas/IDP", (string?)actual.SelectToken("components.schemas.IDPArray.properties.data.items['$ref']"));
		var renderer = new ModelRenderer("identity-providers", actual);
		var source = renderer.RenderModels();
		Assert.Contains("public partial class IDP", source);
		Assert.Contains("ICollection<IDP> Data", source);
		Assert.Contains("JsonExtensionData", source);
	}
}
