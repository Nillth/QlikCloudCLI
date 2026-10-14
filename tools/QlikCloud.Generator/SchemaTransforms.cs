using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace QlikCloud.Generator;

/// <summary>Explicit schema corrections and stable public model policies.</summary>
public static class SchemaTransforms
{
	private static readonly string[] UserRoles =
	[
		"AnalyticsAdmin", "DataAdmin", "SharedSpaceCreator", "ManagedSpaceCreator", "DataSpaceCreator", "TenantAdmin", "Developer", "AuditAdmin",
		"DataServicesContributor", "Steward", "PrivateAnalyticsContentCreator", "CollaborationPlatformUser", "AutomationCreator", "AutomlExperimentContributor",
		"AutomlDeploymentContributor", "EmbeddedAnalyticsUser", "PrivateAnalyticsCreator"
	];

	/// <summary>Returns a corrected copy; the downloaded document is never modified.</summary>
	public static JObject Apply(string apiName, JObject document)
	{
		ArgumentNullException.ThrowIfNull(apiName);
		ArgumentNullException.ThrowIfNull(document);
		var result = (JObject)document.DeepClone();
		var api = apiName.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();

		// Work on JSON values instead of the old formatting-dependent regex.
		foreach (var property in result.Descendants().OfType<JProperty>().Where(p => p.Name == "operationId"))
		{
			if (property.Value.Type != JTokenType.String) continue;
			var value = (string)property.Value!;
			var separator = value.LastIndexOf('#');
			if (separator >= 0) property.Value = value[(separator + 1)..];
		}

		RemoveRequired(result, "ConnectionsResponse", "connectStatement");
		RemoveRequired(result, "ConnectionGet", "qSeparateCredentials", "privileges", "created", "updated", "user");
		RemoveRequired(result, "itemResultResponseBody", "resourceCustomAttributes");
		foreach (var parameters in result.Descendants().OfType<JProperty>()
			.Where(p => p.Name == "parameters").Select(p => p.Value).OfType<JArray>().ToArray())
		{
			foreach (var parameter in parameters.OfType<JObject>().Where(p =>
				(string?)p["name"] == "Authorization" && (string?)p["in"] == "header" &&
				p["required"]?.Type == JTokenType.Boolean && (bool)p["required"]!).ToArray())
			{
				parameter.Remove();
			}
		}

		switch (api)
		{
			case "apps":
				// The published schema declares this URL placeholder as an optional
				// query parameter. It must be substituted into the path, not appended
				// to the query while leaving a literal {reloadId} in the request URL.
				if (result["paths"]?["/api/v1/apps/{appId}/reloads/metadata/{reloadId}"]?["get"]?["parameters"] is JArray reloadParameters)
				{
					foreach (var parameter in reloadParameters.OfType<JObject>().Where(p => (string?)p["name"] == "reloadId"))
					{
						parameter["in"] = "path";
						parameter["required"] = true;
					}
				}
				if (result.SelectToken("components.schemas.NxAttributes.properties") is JObject attributes && attributes["spaceId"] == null)
				{
					attributes["spaceId"] = new JObject
					{
						["type"] = "string",
						["description"] = "The space ID where the app is published."
					};
				}
				break;
			case "roles":
				RemoveRequired(result, "Role", "tenantId", "description");
				break;
			case "licenses":
				(result.SelectToken("components.schemas.licenseOverview.properties.allotments.items.properties.name") as JObject)?.Remove("enum");
				break;
			case "users":
				// Live OAuth user-list responses can omit the identity-provider subject,
				// despite User.required in the public schema. This is a response-model
				// correction only: UserPostSchema.subject remains required for creation.
				RemoveRequired(result, "User", "subject");
				if (result.SelectToken("components.schemas.User.properties.subject") is JObject subject)
				{
					const string responseNote = "May be omitted in OAuth user-list responses.";
					var description = subject.Value<string>("description") ?? "";
					if (!description.Contains(responseNote, StringComparison.Ordinal))
						subject["description"] = string.IsNullOrWhiteSpace(description) ? responseNote : description.TrimEnd() + "\n" + responseNote;
				}
				PreserveUserRoles(result);
				if (result.SelectToken("components.schemas.JSONPatch.properties") is JObject patch &&
					result.SelectToken("components.schemas") is JObject schemas)
				{
					AddEnumValue(patch.SelectToken("path.enum") as JArray, "/email");
					patch["value"] = new JObject { ["$ref"] = "#/components/schemas/JSONPatchValue" };
					// The generated partial type is extended by src/Custom/Users.JSONPatch.cs.
					schemas["JSONPatchValue"] = new JObject { ["type"] = "object" };
				}
				break;
			case "ml":
				AddEnumValue(result.SelectToken("components.schemas.ExperimentVersion.properties.status.enum") as JArray, "pending");
				break;
			case "spaces":
				PreserveEnum(result, "ActionName", ["create", "read", "update", "delete", "publish", "change_owner"]);
				PreserveEnum(result, "RoleType", ["consumer", "contributor", "dataconsumer", "facilitator", "operator", "producer", "publisher", "basicconsumer", "codeveloper", "datapreview"]);
				break;
			case "identityproviders":
				PreserveIdentityProviderType(result);
				break;
			// DataStores.ps1 only added a LINQ using directive required by NSwag's
			// generated helpers. The custom renderer does not need a schema rewrite.
		}

		RemoveDeprecated(result);
		return result;
	}

	private static void PreserveEnum(JObject document, string name, IReadOnlyCollection<string> legacyValues)
	{
		if (document["components"]?["schemas"] is not JObject schemas) return;
		var schema = schemas[name] as JObject ?? new JObject();
		var existing = (schema["enum"] as JArray)?.Values<string>().Where(v => v != null).Cast<string>().ToArray() ?? [];
		var suppliedNames = schema["x-enumNames"] as JArray ?? schema["x-enum-varnames"] as JArray;
		var existingNames = existing.Select((value, index) => (value, name: suppliedNames?.ElementAtOrDefault(index)?.Value<string>()))
			.GroupBy(entry => entry.value, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().name, StringComparer.Ordinal);
		// Keep numeric enum identities stable while retaining capabilities introduced
		// by newer public schemas; the old output regex discarded every new value.
		var values = legacyValues.Concat(existing).Distinct(StringComparer.Ordinal).ToArray();
		schema["type"] = "string";
		schema["enum"] = new JArray(values);
		schema["x-enumNames"] = new JArray(values.Select(value => legacyValues.Contains(value, StringComparer.Ordinal)
			? ModelRenderer.Identifier(value)
			: existingNames.GetValueOrDefault(value) ?? ModelRenderer.Identifier(value)));
		schema.Remove("x-enum-varnames");
		schemas[name] = schema;
	}

	private static void PreserveUserRoles(JObject document)
	{
		if (document["components"]?["schemas"] is not JObject schemas) return;
		// Older schemas embedded the Roles enum in an array property. Promote that
		// shape to a named component so all uses retain the same public enum type.
		var roleProperties = schemas.Descendants().OfType<JProperty>().Where(p => p.Name == "roles" &&
			p.Parent is JObject parent && parent.Parent is JProperty container && container.Name == "properties" &&
			p.Value is JObject value && value["items"] is JObject item && item["enum"] is JArray).ToArray();
		var extraRoles = roleProperties.SelectMany(p => p.Value["items"]!["enum"]!.Values<string>()).Where(v => v != null).Cast<string>();
		PreserveEnum(document, "Roles", UserRoles.Concat(extraRoles).Distinct(StringComparer.Ordinal).ToArray());
		foreach (var property in roleProperties)
			property.Value["items"] = new JObject { ["$ref"] = "#/components/schemas/Roles" };
	}

	private static void PreserveIdentityProviderType(JObject document)
	{
		if (document.SelectToken("components.schemas.IDP") is not JObject idp) return;
		// NSwag emitted a named extension-data wrapper for the object-only union.
		// Keep its oneOf/discriminator metadata, with an explicit renderer hint.
		idp["x-generator-preserve-named-object"] = true;
		if (document.SelectToken("components.schemas.IDPArray.properties.data") is JObject data)
		{
			data["type"] = "array";
			data["items"] = new JObject { ["$ref"] = "#/components/schemas/IDP" };
		}
	}

	private static void RemoveRequired(JObject document, string schema, params string[] names)
	{
		if (document["components"]?["schemas"]?[schema]?["required"] is not JArray required) return;
		var removedNames = new HashSet<string>(names, StringComparer.Ordinal);
		foreach (var value in required.Where(v => v.Type == JTokenType.String && removedNames.Contains((string)v!)).ToArray())
		{
			value.Remove();
		}
		if (required.Count == 0) required.Parent?.Remove();
	}

	private static void AddEnumValue(JArray? values, string value)
	{
		if (values != null && !values.Any(v => v.Type == JTokenType.String && (string?)v == value)) values.Add(value);
	}

	private static bool IsDeprecated(JObject value) =>
		value["x-qlik-deprecated"]?.Type == JTokenType.Boolean && (bool)value["x-qlik-deprecated"]!;

	private static void RemoveDeprecated(JObject document)
	{
		// Only removed component schemas are reference targets. The old script also
		// recorded deprecated property names, which could remove an unrelated schema.
		var schemas = document["components"]?["schemas"] as JObject;
		var deprecatedSchemas = new HashSet<string>(
			schemas?.Properties().Where(p => p.Value is JObject value && IsDeprecated(value)).Select(p => p.Name)
				?? Enumerable.Empty<string>(), StringComparer.Ordinal);

		bool ReferencesRemovedSchema(JObject value)
		{
			if (value["$ref"]?.Type != JTokenType.String) return false;
			var reference = (string)value["$ref"]!;
			const string prefix = "#/components/schemas/";
			if (!reference.StartsWith(prefix, StringComparison.Ordinal)) return false;
			// JSON Pointer escaping also permits schema names containing '-' or '/'.
			var name = reference[prefix.Length..].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
			return deprecatedSchemas.Contains(name);
		}

		// A named alias is also removed when its target is deprecated. Follow those
		// aliases before pruning so callers cannot retain a newly dangling reference.
		if (schemas != null)
		{
			bool added;
			do
			{
				added = false;
				foreach (var property in schemas.Properties())
					if (property.Value is JObject value && ReferencesRemovedSchema(value)) added |= deprecatedSchemas.Add(property.Name);
			} while (added);
		}

		bool Prune(JToken token)
		{
			var changed = false;
			if (token is JObject obj)
			{
				var originalProperties = (obj["properties"] as JObject)?.Properties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
				foreach (var property in obj.Properties().ToArray())
				{
					if (property.Value is JObject child && (IsDeprecated(child) || ReferencesRemovedSchema(child)))
					{
						property.Remove();
						changed = true;
						continue;
					}
					if (!Prune(property.Value)) continue;
					changed = true;
					// Preserve deliberately empty schemas; remove containers emptied by a fix.
					if (!ReferenceEquals(obj, schemas) &&
						(property.Value is JObject emptyObject && !emptyObject.HasValues ||
						property.Value is JArray emptyArray && !emptyArray.HasValues))
					{
						property.Remove();
					}
				}
				if (obj["required"] is JArray required && originalProperties != null)
				{
					// Only names removed from local properties are affected; required
					// properties supplied by allOf must keep their original semantics.
					var properties = obj["properties"] as JObject;
					foreach (var value in required.Where(v => v.Type == JTokenType.String &&
						originalProperties.Contains((string)v!) && properties?[(string)v!] == null).ToArray()) value.Remove();
					if (required.Count == 0) obj.Remove("required");
				}
			}
			else if (token is JArray array)
			{
				foreach (var item in array.ToArray())
				{
					if (item is JObject child && (IsDeprecated(child) || ReferencesRemovedSchema(child)))
					{
						item.Remove();
						changed = true;
					}
					else changed |= Prune(item);
				}
			}
			return changed;
		}

		if (schemas != null) Prune(schemas);
		if (document["paths"] is JObject paths) Prune(paths);
		// References can also appear in reusable responses and request bodies.
		Prune(document);
	}
}
