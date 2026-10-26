using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace QlikCloud.Generator;

/// <summary>Renders the data contracts in a single OpenAPI document without a code-generation dependency.</summary>
public sealed class ModelRenderer
{
    private readonly JObject document;
    private readonly Dictionary<JToken, string> names = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<JToken, string> rootNames = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> usedNames = new(StringComparer.Ordinal) { "Client" };
    private readonly List<(JObject Schema, string Name)> models = new();
    private readonly Dictionary<JObject, Dictionary<JObject, JObject>> mergedProperties = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> warnings = new(StringComparer.Ordinal);
    private static readonly HashSet<string> ValueTypes = new(StringComparer.Ordinal)
    {
        "bool", "int", "long", "float", "double", "decimal", "global::System.DateTimeOffset", "global::System.Guid"
    };

    public ModelRenderer(string apiName, JObject document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiName);
        this.document = document ?? throw new ArgumentNullException(nameof(document));
        Namespace = "QlikCloud." + Identifier(apiName);
        foreach (var container in new[] { document["components"]?["schemas"] as JObject, document["definitions"] as JObject })
        {
            if (container == null) continue;
            foreach (var property in container.Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (property.Value is not JObject schema)
                    throw new InvalidDataException("A schema must be an object: " + property.Path);
                rootNames[schema] = Identifier(property.Name);
                if (NeedsDeclaration(schema)) Register(schema, property.Name);
            }
        }
        // Fail on broken schema references even if an endpoint does not currently use the model.
        foreach (var schema in rootNames.Keys.Cast<JObject>())
            foreach (var reference in schema.DescendantsAndSelf().OfType<JObject>().Where(o => o["$ref"] != null))
                Dereference(reference["$ref"]!.Value<string>()!);
    }

    public string Namespace { get; }
    public IReadOnlyList<string> Warnings => warnings.OrderBy(w => w, StringComparer.Ordinal).ToArray();

    /// <summary>Resolves a schema and queues any newly encountered inline contracts for rendering.</summary>
    public string ResolveType(JToken? schema, string suggestedName, bool nullable = false)
        => ResolveType(schema, suggestedName, nullable, new HashSet<JToken>(ReferenceEqualityComparer.Instance));

    private string ResolveType(JToken? token, string suggestedName, bool nullable, HashSet<JToken> aliasChain)
    {
        if (token == null || token.Type == JTokenType.Boolean && token.Value<bool>()) return "object";
        if (token is not JObject schema) throw new InvalidDataException("Unsupported schema at " + token.Path + ": " + token);
        nullable |= IsNullable(schema);
        if (schema["$ref"] is JValue reference)
        {
            var target = Dereference(reference.Value<string>()!);
            if (names.TryGetValue(target, out var referenceName))
                return NullableType(referenceName, nullable || IsNullable(target), IsEnum(target));
            if (!aliasChain.Add(target)) throw new InvalidDataException("Cyclic schema aliases: " + reference);
            var result = ResolveType(target, rootNames.GetValueOrDefault(target) ?? suggestedName, nullable, aliasChain);
            aliasChain.Remove(target);
            return result;
        }

        if (names.TryGetValue(schema, out var name)) return NullableType(name, nullable, IsEnum(schema));
        var alternatives = schema["oneOf"] as JArray ?? schema["anyOf"] as JArray;
        if (alternatives != null)
        {
            var nonNull = alternatives.Where(s => SchemaType(s as JObject) != "null").ToArray();
            if (nonNull.Length == 1)
                return ResolveType(nonNull[0], suggestedName, nullable || nonNull.Length != alternatives.Count, aliasChain);
            Warn(schema, "Union retained as object; branch validation remains the server's responsibility.");
            return "object";
        }
        var type = SchemaType(schema);
        if (type == "union")
        {
            Warn(schema, "Multiple schema types retained as object; type validation remains the server's responsibility.");
            return "object";
        }
        if (IsEnum(schema)) return NullableType(Register(schema, suggestedName), nullable, true);
        if (schema["enum"] != null) Warn(schema, "This enum cannot be represented by a C# enum; its scalar value is retained.");
        if (schema["allOf"] is JArray) return Register(schema, suggestedName);
        switch (type)
        {
            case "string":
                return schema.Value<string>("format") switch
                {
                    "date-time" => NullableType("global::System.DateTimeOffset", nullable, true),
                    "byte" or "binary" => "byte[]",
                    // Dates remain strings so a date-only value never gains a time zone on the wire.
                    _ => "string"
                };
            case "integer":
                return NullableType(schema.Value<string>("format") == "int64" ? "long" : "int", nullable, true);
            case "number":
                var numberType = schema.Value<string>("format") switch { "float" => "float", "decimal" => "decimal", _ => "double" };
                return NullableType(numberType, nullable, true);
            case "boolean": return NullableType("bool", nullable, true);
            case "array": return "global::System.Collections.Generic.ICollection<" + ResolveType(schema["items"], suggestedName + "Item", false, aliasChain) + ">";
            case "file": return "byte[]";
            case "null": return "object";
            case "object":
            case "":
                if (schema["properties"] is JObject { Count: > 0 }) return Register(schema, suggestedName);
                if (schema["additionalProperties"] is JObject additional)
                    return "global::System.Collections.Generic.IDictionary<string, " + ResolveType(additional, suggestedName + "Value", false, aliasChain) + ">";
                return "object";
            default: throw new InvalidDataException("Unsupported schema type '" + type + "' at " + schema.Path);
        }
    }

    /// <summary>Returns declarations to be placed inside <see cref="Namespace"/>.</summary>
    public string RenderModels()
    {
        var output = new StringBuilder();
        // Rendering properties may discover inline contracts, so this is deliberately a growing queue.
        for (var i = 0; i < models.Count; i++)
        {
            var (schema, name) = models[i];
            if (IsEnum(schema)) RenderEnum(output, schema, name);
            else RenderClass(output, schema, name);
        }
        return output.ToString();
    }

    private void RenderClass(StringBuilder output, JObject schema, string name)
    {
        Documentation(output, schema, "    ");
        DeprecationAttribute(output, schema, "    ", Namespace + "." + name, true);
        if (schema["oneOf"] != null || schema["anyOf"] != null)
            Warn(schema, "Named object union retains its JSON properties in extension data; branch and discriminator validation remain the server's responsibility.");
        var type = SchemaType(schema);
        var array = type == "array";
        var dictionary = !array && schema["properties"] is not JObject { Count: > 0 } && schema["allOf"] == null && schema["additionalProperties"] is JObject;
        var baseType = array
            ? " : global::System.Collections.ObjectModel.Collection<" + ResolveType(schema["items"], name + "Item") + ">"
            : dictionary ? " : global::System.Collections.Generic.Dictionary<string, " + ResolveType(schema["additionalProperties"], name + "Value") + ">" : "";
        output.Append("    public partial class ").Append(name).AppendLine(baseType).AppendLine("    {");
        if (!array && !dictionary)
        {
            var properties = new Dictionary<string, JObject>(StringComparer.Ordinal);
            var required = new HashSet<string>(StringComparer.Ordinal);
            var extraSchemas = new List<JToken>();
            CollectProperties(schema, properties, required, extraSchemas, new HashSet<JToken>(ReferenceEqualityComparer.Instance));
            var memberNames = new HashSet<string>(StringComparer.Ordinal) { name, "ToJson", "FromJson", "AdditionalProperties", "RejectUnknownProperties", "RejectUnknownPropertiesOnWrite" };
            foreach (var property in properties)
            {
                var propertyName = Unique(Identifier(property.Key), memberNames);
                var isRequired = required.Contains(property.Key);
                var propertyType = ResolveType(property.Value, name + propertyName, !isRequired);
                var permitsNull = IsNullableResolved(property.Value);
                Documentation(output, property.Value, "        ");
                DeprecationAttribute(output, property.Value, "        ", Namespace + "." + name + "." + propertyName, false);
                var requiredKind = isRequired ? permitsNull ? "AllowNull" : "Always" : "Default";
                output.Append("        [global::Newtonsoft.Json.JsonProperty(").Append(Literal(property.Key))
                    .Append(", Required = global::Newtonsoft.Json.Required.").Append(requiredKind);
                if (!isRequired) output.Append(", NullValueHandling = global::Newtonsoft.Json.NullValueHandling.Ignore");
                output.AppendLine(")]");
                if (isRequired && !permitsNull)
                    output.AppendLine("        [global::System.ComponentModel.DataAnnotations.Required(AllowEmptyStrings = true)]");
                RenderValidationAttributes(output, property.Value, propertyType);
                output.Append("        public ").Append(propertyType).Append(' ').Append(propertyName).AppendLine(" { get; set; }").AppendLine();
            }
            var forbidden = extraSchemas.Any(s => s.Type == JTokenType.Boolean && !s.Value<bool>());
            var additionalSchema = extraSchemas.OfType<JObject>().FirstOrDefault();
            if (additionalSchema != null)
            {
                Documentation(output, additionalSchema, "        ", "Additional values retain their full JSON representation. Schema type: " + ResolveType(additionalSchema, name + "AdditionalProperty"));
                Warn(additionalSchema, "Typed additional properties on an object use extension data; their schema is documented and validation remains the server's responsibility.");
            }
            output.AppendLine("        [global::Newtonsoft.Json.JsonExtensionData]")
                .AppendLine("        public global::System.Collections.Generic.IDictionary<string, object> AdditionalProperties { get; set; } = new global::System.Collections.Generic.Dictionary<string, object>();")
                .AppendLine();
            if (forbidden)
            {
                output.AppendLine("        [global::System.Runtime.Serialization.OnDeserialized]")
                    .AppendLine("        private void RejectUnknownProperties(global::System.Runtime.Serialization.StreamingContext context)")
                    .AppendLine("        {")
                    .AppendLine("            if (AdditionalProperties != null && AdditionalProperties.Count != 0)")
                    .AppendLine("                throw new global::Newtonsoft.Json.JsonSerializationException(\"This schema forbids additional properties.\");")
                    .AppendLine("        }").AppendLine()
                    .AppendLine("        [global::System.Runtime.Serialization.OnSerializing]")
                    .AppendLine("        private void RejectUnknownPropertiesOnWrite(global::System.Runtime.Serialization.StreamingContext context)")
                    .AppendLine("        {")
                    .AppendLine("            RejectUnknownProperties(context);")
                    .AppendLine("        }").AppendLine();
            }
        }
        output.AppendLine("        public string ToJson()")
            .AppendLine("        {")
            .AppendLine("            return global::Newtonsoft.Json.JsonConvert.SerializeObject(this);")
            .AppendLine("        }")
            .Append("        public static ").Append(name).AppendLine(" FromJson(string data)")
            .AppendLine("        {")
            .Append("            return global::Newtonsoft.Json.JsonConvert.DeserializeObject<").Append(name).AppendLine(">(data);")
            .AppendLine("        }")
            .AppendLine("    }").AppendLine();
    }

    private void CollectProperties(JObject schema, Dictionary<string, JObject> properties, HashSet<string> required, List<JToken> additional, HashSet<JToken> chain)
    {
        if (!chain.Add(schema)) throw new InvalidDataException("Cyclic allOf inheritance at " + schema.Path);
        if (schema["$ref"] != null) CollectProperties(Dereference(schema.Value<string>("$ref")!), properties, required, additional, chain);
        if (schema["allOf"] is JArray allOf)
            foreach (var item in allOf)
            {
                if (item is not JObject part) throw new InvalidDataException("allOf member must be an object at " + item.Path);
                CollectProperties(part, properties, required, additional, chain);
            }
        if (schema["properties"] is JObject ownProperties)
            foreach (var property in ownProperties.Properties())
            {
                if (property.Value is not JObject propertySchema) throw new InvalidDataException("Property schema must be an object at " + property.Path);
                if (properties.TryGetValue(property.Name, out var existing) && !JToken.DeepEquals(existing, propertySchema))
                {
                    // Retain both constraints and values when allOf repeats a member; choosing one silently loses information.
                    properties[property.Name] = MergePropertySchemas(existing, propertySchema);
                }
                else properties[property.Name] = propertySchema;
            }
        if (schema["required"] is JArray requiredProperties)
            foreach (var property in requiredProperties.Values<string>()) if (property != null) required.Add(property);
        if (schema["additionalProperties"] is JToken additionalSchema) additional.Add(additionalSchema);
        chain.Remove(schema);
    }

    private JObject MergePropertySchemas(JObject first, JObject second)
    {
        if (!mergedProperties.TryGetValue(first, out var secondSchemas))
            mergedProperties[first] = secondSchemas = new(ReferenceEqualityComparer.Instance);
        if (secondSchemas.TryGetValue(second, out var cached)) return cached;
        var merged = (JObject)first.DeepClone();
        var conflictingType = false;
        foreach (var property in second.Properties())
        {
            if (merged[property.Name] == null) merged[property.Name] = property.Value.DeepClone();
            else if (!JToken.DeepEquals(merged[property.Name], property.Value))
            {
                var previous = merged[property.Name]!;
                switch (property.Name)
                {
                    case "type":
                        var left = previous is JArray leftTypes ? leftTypes.Values<string>().ToArray() : [previous.Value<string>()];
                        var right = property.Value is JArray rightTypes ? rightTypes.Values<string>().ToArray() : [property.Value.Value<string>()];
                        var intersection = left.Intersect(right, StringComparer.Ordinal).ToArray();
                        if (intersection.Length == 0 && left.Contains("integer") && right.Contains("number") || intersection.Length == 0 && left.Contains("number") && right.Contains("integer")) intersection = ["integer"];
                        if (intersection.Length == 0) conflictingType = true;
                        else merged["type"] = intersection.Length == 1 ? new JValue(intersection[0]) : new JArray(intersection.Select(value => new JValue(value)));
                        break;
                    case "$ref":
                        conflictingType = true;
                        break;
                    case "enum":
                        var enumValues = ((JArray)previous).Where(value => ((JArray)property.Value).Any(other => JToken.DeepEquals(value, other))).ToArray();
                        if (enumValues.Length == 0)
                        {
                            merged.Remove("enum");
                            Warn(first, "Conflicting allOf enum constraints have no valid value; scalar data is retained and both constraints are documented.");
                        }
                        else merged["enum"] = new JArray(enumValues.Select(value => value.DeepClone()));
                        break;
                    case "minimum":
                    case "minLength":
                    case "minItems":
                    case "minProperties":
                        if (property.Value.Value<double>() > previous.Value<double>()) merged[property.Name] = property.Value.DeepClone();
                        break;
                    case "maximum":
                    case "maxLength":
                    case "maxItems":
                    case "maxProperties":
                        if (property.Value.Value<double>() < previous.Value<double>()) merged[property.Name] = property.Value.DeepClone();
                        break;
                    case "required" when previous is JArray previousRequired && property.Value is JArray nextRequired:
                        merged[property.Name] = new JArray(previousRequired.Values<string>().Concat(nextRequired.Values<string>()).Distinct(StringComparer.Ordinal));
                        break;
                    case "properties" when previous is JObject previousProperties && property.Value is JObject nextProperties:
                        foreach (var child in nextProperties.Properties())
                            previousProperties[child.Name] = previousProperties[child.Name] is JObject oldChild && child.Value is JObject newChild
                                ? MergePropertySchemas(oldChild, newChild) : child.Value.DeepClone();
                        break;
                    case "items" when previous is JObject previousItems && property.Value is JObject nextItems:
                        merged[property.Name] = MergePropertySchemas(previousItems, nextItems);
                        break;
                    case "nullable":
                    case "x-nullable":
                        merged[property.Name] = previous.Value<bool>() && property.Value.Value<bool>();
                        break;
                    case "format":
                        // If two formats disagree, retain the underlying wire scalar instead of coercing it.
                        merged.Remove("format");
                        break;
                }
            }
        }
        if (conflictingType)
        {
            merged = new JObject();
            Warn(first, "Conflicting allOf property types retained as object; the source intersection is unsatisfiable or cannot be represented by one CLR type.");
        }
        // Either constituent can deprecate the combined property, including when its CLR type falls back to object.
        if (DeprecationPolicy.IsDeprecated(first) || DeprecationPolicy.IsDeprecated(second)) merged["deprecated"] = true;
        // Keep the original intersection as reviewable metadata, including conflicting validation bounds.
        merged["x-generator-allOf-constraints"] = new JArray(first.DeepClone(), second.DeepClone());
        Warn(first, "Repeated allOf property constraints are retained in documentation; the server validates their intersection.");
        secondSchemas[second] = merged;
        return merged;
    }

    private void RenderEnum(StringBuilder output, JObject schema, string name)
    {
        Documentation(output, schema, "    ");
        DeprecationAttribute(output, schema, "    ", Namespace + "." + name, true);
        var values = ((JArray)schema["enum"]!).Where(v => v.Type != JTokenType.Null).ToArray();
        var strings = values.All(v => v.Type == JTokenType.String);
        if (strings) output.AppendLine("    [global::Newtonsoft.Json.JsonConverter(typeof(global::Newtonsoft.Json.Converters.StringEnumConverter))]");
        output.Append("    public enum ").Append(name).AppendLine(strings ? "" : " : long").AppendLine("    {");
        var memberNames = new HashSet<string>(StringComparer.Ordinal) { name, "value__" };
        var suppliedNames = schema["x-enumNames"] as JArray ?? schema["x-enum-varnames"] as JArray;
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            var raw = value.Type == JTokenType.String ? value.Value<string>()! : value.ToString(Formatting.None);
            var member = Unique(Identifier(suppliedNames?.ElementAtOrDefault(i)?.Value<string>() ?? (strings ? raw.Length == 0 ? "Empty" : raw : "Value" + raw.Replace("-", "Minus", StringComparison.Ordinal))), memberNames);
            output.Append("        [global::System.Runtime.Serialization.EnumMember(Value = ").Append(Literal(raw)).AppendLine(")]")
                .Append("        ").Append(member).Append(" = ").Append(strings ? i.ToString(CultureInfo.InvariantCulture) : raw).AppendLine(",");
        }
        output.AppendLine("    }").AppendLine();
    }

    private void RenderValidationAttributes(StringBuilder output, JObject schema, string type)
    {
        if (type == "string")
        {
            if (schema["minLength"] is JValue min && min.Type == JTokenType.Integer)
                output.Append("        [global::System.ComponentModel.DataAnnotations.MinLength(").Append(min).AppendLine(")]");
            if (schema["maxLength"] is JValue max && max.Type == JTokenType.Integer)
                output.Append("        [global::System.ComponentModel.DataAnnotations.MaxLength(").Append(max).AppendLine(")]");
            if (schema.Value<string>("pattern") is string pattern)
                output.Append("        [global::System.ComponentModel.DataAnnotations.RegularExpression(").Append(Literal(pattern)).AppendLine(")]");
        }
    }

    private void DeprecationAttribute(StringBuilder output, JObject schema, string indent, string subject, bool inheritApi)
    {
        string? message = DeprecationPolicy.Message(subject, inheritApi ? document : null,
            inheritApi ? document["info"] as JObject : null, schema);
        if (message != null) output.Append(indent).Append("[global::System.Obsolete(").Append(Literal(message)).AppendLine(")]");
    }

    private void Documentation(StringBuilder output, JObject schema, string indent, string? extra = null)
    {
        var description = schema.Value<string>("description");
        if (!string.IsNullOrWhiteSpace(description) || extra != null)
        {
            output.Append(indent).AppendLine("/// <summary>");
            foreach (var line in ((description ?? "") + (extra == null ? "" : "\n" + extra)).Split('\n'))
                output.Append(indent).Append("/// ").AppendLine(Xml(line.TrimEnd('\r')));
            output.Append(indent).AppendLine("/// </summary>");
        }
        var constraints = new JObject();
        foreach (var property in schema.Properties())
            if (property.Name is "allOf" or "oneOf" or "anyOf" or "not" or "minimum" or "maximum" or "exclusiveMinimum" or "exclusiveMaximum"
                or "multipleOf" or "minItems" or "maxItems" or "uniqueItems" or "minProperties" or "maxProperties" or "pattern"
                or "minLength" or "maxLength" or "enum" or "default" or "readOnly" or "writeOnly" or "discriminator"
                or "patternProperties" or "dependencies" or "dependentRequired" or "if" or "then" or "else" or "const" or "x-generator-allOf-constraints")
                constraints[property.Name] = property.Value.DeepClone();
        if (constraints.Count > 0)
        {
            output.Append(indent).Append("/// <remarks>OpenAPI constraints (the server performs schema validation): ")
                .Append(Xml(constraints.ToString(Formatting.None))).AppendLine("</remarks>");
        }
    }

    private static string Xml(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
    private static string Literal(string value) => JsonConvert.ToString(value);
    private void Warn(JObject schema, string message) => warnings.Add((string.IsNullOrEmpty(schema.Path) ? "schema" : schema.Path) + ": " + message);

    private string Register(JObject schema, string suggestedName)
    {
        if (names.TryGetValue(schema, out var existing)) return existing;
        var name = Unique(Identifier(rootNames.GetValueOrDefault(schema) ?? suggestedName), usedNames);
        names[schema] = name;
        models.Add((schema, name));
        return name;
    }

    private JObject Dereference(string reference)
    {
        if (!reference.StartsWith("#/", StringComparison.Ordinal))
            throw new InvalidDataException("Only local OpenAPI schema references are supported: " + reference);
        JToken? current = document;
        foreach (var raw in reference[2..].Split('/'))
        {
            var segment = Uri.UnescapeDataString(raw).Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            current = current is JObject obj ? obj[segment]
                : current is JArray array && int.TryParse(segment, out var index) && index >= 0 && index < array.Count ? array[index] : null;
            if (current == null) throw new InvalidDataException("Unresolved OpenAPI schema reference: " + reference);
        }
        return current as JObject ?? throw new InvalidDataException("Reference is not an object schema: " + reference);
    }

    private bool IsNullableResolved(JObject schema)
    {
        var seen = new HashSet<JToken>(ReferenceEqualityComparer.Instance);
        while (true)
        {
            if (IsNullable(schema)) return true;
            if (schema["$ref"] == null || !seen.Add(schema)) return false;
            schema = Dereference(schema.Value<string>("$ref")!);
        }
    }

    private static bool IsNullable(JObject schema) => schema.Value<bool?>("nullable") == true || schema.Value<bool?>("x-nullable") == true
        || schema["type"] is JArray types && types.Values<string>().Contains("null", StringComparer.Ordinal)
        || schema["enum"] is JArray values && values.Any(v => v.Type == JTokenType.Null)
        || (schema["oneOf"] as JArray ?? schema["anyOf"] as JArray)?.Any(s => SchemaType(s as JObject) == "null") == true;

    private static bool IsEnum(JObject schema)
    {
        if (schema["enum"] is not JArray values || values.Count == 0) return false;
        var real = values.Where(v => v.Type != JTokenType.Null).ToArray();
        return real.Length != 0 && (real.All(v => v.Type == JTokenType.String) || real.All(v => v.Type == JTokenType.Integer && long.TryParse(v.ToString(), out _)));
    }

    private static bool NeedsDeclaration(JObject schema)
    {
        if (schema.Value<bool?>("x-generator-preserve-named-object") == true) return true;
        if (schema["$ref"] != null || schema["oneOf"] != null || schema["anyOf"] != null) return false;
        return IsEnum(schema) || schema["allOf"] != null || SchemaType(schema) is "object" or "array" || schema["properties"] != null;
    }

    private static string SchemaType(JObject? schema)
    {
        if (schema == null) return "";
        if (schema["type"] is JArray types)
        {
            var nonNull = types.Values<string>().Where(t => t != "null").Distinct(StringComparer.Ordinal).ToArray();
            return nonNull.Length == 1 ? nonNull[0] ?? "" : nonNull.Length == 0 ? "null" : "union";
        }
        return schema.Value<string>("type") ?? (schema["properties"] != null || schema["additionalProperties"] != null ? "object" : "");
    }

    private static string NullableType(string type, bool nullable, bool valueType) => nullable && (valueType || ValueTypes.Contains(type)) ? type + "?" : type;

    private static string Unique(string desired, HashSet<string> used)
    {
        if (used.Add(desired)) return desired;
        for (var i = 2; ; i++) if (used.Add(desired + i.ToString(CultureInfo.InvariantCulture))) return desired + i.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Produces a stable Pascal-cased C# identifier while preserving existing acronym casing.</summary>
    public static string Identifier(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Value";
        var output = new StringBuilder();
        var capitalize = true;
        foreach (var character in raw)
        {
            if (!char.IsLetterOrDigit(character) && character != '_') { capitalize = true; continue; }
            if (character == '_') { capitalize = true; continue; }
            if (output.Length == 0 && char.IsDigit(character)) output.Append('_');
            output.Append(capitalize ? char.ToUpperInvariant(character) : character);
            capitalize = false;
        }
        return output.Length == 0 ? "Value" : output.ToString();
    }
}
