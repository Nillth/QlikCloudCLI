using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace QlikCloud.Generator;

public sealed class ClientRenderer
{
    private static readonly HashSet<string> Verbs = new(StringComparer.OrdinalIgnoreCase) { "get", "post", "put", "patch", "delete", "head", "options", "trace" };
    private readonly string api;
    private readonly JObject document;
    private readonly ModelRenderer models;
    private readonly HashSet<string> customOperations;
    private readonly IReadOnlyDictionary<string, string> methodNames;
    public List<object> Operations { get; } = new();
    public IReadOnlyList<string> Warnings => models.Warnings;

    public ClientRenderer(string api, JObject document, IEnumerable<string>? customOperations = null, IReadOnlyDictionary<string, string>? methodNames = null)
    {
        this.api = api;
        this.document = document;
        models = new ModelRenderer(api, document);
        this.customOperations = new HashSet<string>(customOperations ?? [], StringComparer.OrdinalIgnoreCase);
        this.methodNames = methodNames ?? new Dictionary<string, string>();
    }

    public string Render(string templateDirectory)
    {
        var operations = new List<(string Path, JObject Item, string Verb, JObject Operation, string BaseName)>();
        foreach (var path in ((JObject?)document["paths"] ?? throw new InvalidDataException("Schema has no paths.")).Properties())
        {
            if (path.Name.StartsWith("x-", StringComparison.Ordinal) || path.Name is "deprecated" or "deprecationMessage") continue;
            var item = Resolve(path.Value);
            foreach (var method in item.Properties().Where(p => Verbs.Contains(p.Name)))
                operations.Add((path.Name, item, method.Name, Resolve(method.Value), MethodBase(path.Name)));
        }
        var groups = operations.GroupBy(o => o.BaseName).ToDictionary(g => g.Key, g => g.Count());
        var operationKeys = operations.Select(o => $"{api} {o.Verb.ToUpperInvariant()} {o.Path}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string policyKey in methodNames.Keys.Concat(customOperations).Where(key => key.StartsWith(api + " ", StringComparison.OrdinalIgnoreCase)))
            if (!operationKeys.Contains(policyKey)) throw new InvalidDataException("Compatibility policy no longer matches a schema operation: " + policyKey);
        var methods = new StringBuilder();
        var signatures = new HashSet<string>();
        foreach (var op in operations)
        {
            string name = op.BaseName + (groups[op.BaseName] > 1 ? ModelRenderer.Identifier(op.Verb) : "");
            string key = $"{api} {op.Verb.ToUpperInvariant()} {op.Path}";
            string? deprecationMessage = DeprecationPolicy.Message(key, document, document["info"] as JObject,
                document["paths"] as JObject, op.Item, op.Operation);
            if (methodNames.TryGetValue(key, out string? compatibilityName))
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(compatibilityName, @"^[A-Za-z_][A-Za-z0-9_]*$")) throw new InvalidDataException("Invalid compatibility method name: " + compatibilityName);
                name = compatibilityName;
            }
            if (customOperations.Contains(key))
            {
                Operations.Add(new { path = op.Path, method = op.Verb, name, implementation = "custom", deprecated = deprecationMessage != null, deprecationMessage });
                continue;
            }
            var parameters = Parameters(op.Item, op.Operation);
            var placeholders = System.Text.RegularExpressions.Regex.Matches(op.Path, @"\{([^{}]+)\}")
                .Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            var pathParameters = parameters.Where(p => p.Location == "path").Select(p => p.WireName).ToHashSet(StringComparer.Ordinal);
            if (!placeholders.SetEquals(pathParameters))
                throw new InvalidDataException($"Path parameters do not match URL placeholders for {key}; correct the schema explicitly before rendering.");
            string signature = name + "(" + string.Join(",", parameters.Select(p => p.Type)) + ")";
            if (!signatures.Add(signature))
                throw new InvalidDataException($"Ambiguous generated method {api}.{signature} for {op.Path}; add an explicit naming policy.");
            var responses = Responses(op.Operation);
            var success = responses.Where(r => r.Code.StartsWith('2')).ToList();
            string? returnType = success.FirstOrDefault(r => r.Type != null)?.Type;
            if (success.Where(r => r.Type != null).Select(r => r.Type).Distinct().Count() > 1) returnType = "object";
            bool file = returnType == "FileResponse";
            string wrapped = returnType == null ? "APIResponse" : file ? "FileResponse" : $"APIResponse<{returnType}>";
            string args = string.Join(", ", parameters.Select(p => p.Name));
            string declarations = string.Join(", ", parameters.Select(p => $"{p.Type} {p.Name}" + (p.Required ? "" : " = null")));
            string comma = parameters.Count == 0 ? "" : ", ";
            methods.AppendLine("        /// <summary>" + Xml((string?)op.Operation["summary"] ?? key) + "</summary>");
            if (deprecationMessage != null) methods.AppendLine("        [global::System.Obsolete(" + Literal(deprecationMessage) + ")]");
            methods.AppendLine($"        public virtual {returnType ?? "APIResponse"} {name}({declarations})");
            methods.AppendLine("        {");
            methods.AppendLine($"            return System.Threading.Tasks.Task.Run(async () => await {name}Async({args}{comma}System.Threading.CancellationToken.None)).GetAwaiter().GetResult(){(returnType != null && !file ? ".Object" : "")};");
            methods.AppendLine("        }");
            if (deprecationMessage != null) methods.AppendLine("        [global::System.Obsolete(" + Literal(deprecationMessage) + ")]");
            methods.AppendLine($"        public virtual async System.Threading.Tasks.Task<{wrapped}> {name}Async({declarations}{comma}System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))");
            methods.AppendLine("        {");
            foreach (var p in parameters.Where(p => p.Required && !IsValueType(p.Type)))
                methods.AppendLine($"            if ((object){p.Name} == null) throw new System.ArgumentNullException(nameof({p.Name}));");
            methods.AppendLine($"            var __qcUrl = {Literal(op.Path)};");
            foreach (var p in parameters.Where(p => p.Location == "path"))
                methods.AppendLine($"            __qcUrl = __qcUrl.Replace({Literal("{" + p.WireName + "}")}, System.Uri.EscapeDataString(WireValue({p.Name}, {Literal(p.Format ?? "")})));");
            methods.AppendLine("            var __qcQuery = new System.Collections.Generic.List<string>();");
            foreach (var p in parameters.Where(p => p.Location == "query"))
                methods.AppendLine($"            AddQuery(__qcQuery, {Literal(p.WireName)}, {p.Name}, {Literal(p.Style)}, {(p.Explode ? "true" : "false")}, {Literal(p.Format ?? "")});");
            methods.AppendLine("            if (__qcQuery.Count > 0) __qcUrl += \"?\" + string.Join(\"&\", __qcQuery);");
            methods.AppendLine("            using (var __qcRequest = new System.Net.Http.HttpRequestMessage())");
            methods.AppendLine("            {");
            methods.AppendLine($"                __qcRequest.Method = new System.Net.Http.HttpMethod({Literal(op.Verb.ToUpperInvariant())});");
            foreach (var p in parameters.Where(p => p.Location == "header"))
                methods.AppendLine($"                if ((object){p.Name} != null) __qcRequest.Headers.TryAddWithoutValidation({Literal(p.WireName)}, WireValue({p.Name}));");
            foreach (var p in parameters.Where(p => p.Location == "cookie"))
                methods.AppendLine($"                if ((object){p.Name} != null) __qcRequest.Headers.TryAddWithoutValidation(\"Cookie\", {Literal(p.WireName + "=")} + System.Uri.EscapeDataString(WireValue({p.Name})));");
            EmitBody(methods, parameters);
            string? accept = success.Select(r => r.MediaType).FirstOrDefault(m => m != null);
            if (accept != null) methods.AppendLine($"                if (__qcRequest.Headers.Accept.Count == 0) __qcRequest.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue({Literal(accept)}));");
            methods.AppendLine("                var __qcUrlBuilder = new System.Text.StringBuilder(__qcUrl);");
            methods.AppendLine("                await PrepareRequestAsync(client_, __qcRequest, __qcUrlBuilder, cancellationToken).ConfigureAwait(false);");
            methods.AppendLine("                __qcUrl = __qcUrlBuilder.ToString();");
            methods.AppendLine("                __qcRequest.RequestUri = new System.Uri(__qcUrl, System.UriKind.RelativeOrAbsolute);");
            methods.AppendLine("                await PrepareRequestAsync(client_, __qcRequest, __qcUrl, cancellationToken).ConfigureAwait(false);");
            methods.AppendLine("                var __qcResponse = await SendRequestAsync(__qcRequest, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);");
            methods.AppendLine("                var __qcDisposeResponse = true;");
            methods.AppendLine("                try");
            methods.AppendLine("                {");
            methods.AppendLine("                    var __qcHeaders = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.IEnumerable<string>>(System.StringComparer.OrdinalIgnoreCase);");
            methods.AppendLine("                    foreach (var __qcHeader in __qcResponse.Headers) __qcHeaders[__qcHeader.Key] = __qcHeader.Value;");
            methods.AppendLine("                    if (__qcResponse.Content != null) foreach (var __qcHeader in __qcResponse.Content.Headers) __qcHeaders[__qcHeader.Key] = __qcHeader.Value;");
            methods.AppendLine("                    var __qcStatus = (int)__qcResponse.StatusCode;");
            // Avoid the legacy response logger eagerly reading binary streams.
            if (!file) methods.AppendLine("                    await ProcessResponseAsync(client_, __qcResponse, cancellationToken).ConfigureAwait(false);");
            foreach (var response in responses.OrderBy(r => r.Code == "default" ? 1 : 0))
            {
                string condition = response.Code == "default" ? "true" : response.Code.Length == 3 && response.Code.EndsWith("XX", StringComparison.OrdinalIgnoreCase) ? $"__qcStatus / 100 == {response.Code[0]}" : $"__qcStatus == {int.Parse(response.Code)}";
                methods.AppendLine($"                    if ({condition})");
                methods.AppendLine("                    {");
                if (response.Code.StartsWith('2'))
                {
                    if (file)
                    {
                        methods.AppendLine("                        var __qcStream = __qcResponse.Content == null ? System.IO.Stream.Null : await __qcResponse.Content.ReadAsStreamAsync().ConfigureAwait(false);");
                        methods.AppendLine("                        __qcDisposeResponse = false;");
                        methods.AppendLine("                        return new FileResponse(__qcStatus, __qcHeaders, __qcStream, null, __qcResponse);");
                    }
                    else if (returnType == null) methods.AppendLine("                        return new APIResponse(__qcStatus, __qcHeaders);");
                    else if (response.Type == null) methods.AppendLine($"                        return new APIResponse<{returnType}>(__qcStatus, __qcHeaders, default({returnType}));");
                    else
                    {
                        if (response.MediaType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true && response.Type == "string")
                            methods.AppendLine("                        var __qcResult = new ObjectResponseResult<string>(await __qcResponse.Content.ReadAsStringAsync().ConfigureAwait(false), null);");
                        else methods.AppendLine($"                        var __qcResult = await ReadObjectResponseAsync<{response.Type}>(__qcResponse, __qcHeaders, cancellationToken).ConfigureAwait(false);");
                        methods.AppendLine($"                        return new APIResponse<{returnType}>(__qcStatus, __qcHeaders, __qcResult.Text, __qcResult.Object);");
                    }
                }
                else if (response.Type != null && response.Type != "FileResponse")
                {
                    methods.AppendLine($"                        var __qcResult = await ReadObjectResponseAsync<{response.Type}>(__qcResponse, __qcHeaders, cancellationToken).ConfigureAwait(false);");
                    methods.AppendLine($"                        throw new ApiException<{response.Type}>({Literal(response.Description)}, __qcStatus, __qcResult.Text, __qcHeaders, __qcResult.Object, null);");
                }
                else methods.AppendLine($"                        throw new ApiException({Literal(response.Description)}, __qcStatus, __qcResponse.Content == null ? null : await __qcResponse.Content.ReadAsStringAsync().ConfigureAwait(false), __qcHeaders, null);");
                methods.AppendLine("                    }");
            }
            if (success.Count == 0) methods.AppendLine("                    if (__qcStatus == 200 || __qcStatus == 204) return new APIResponse(__qcStatus, __qcHeaders);");
            methods.AppendLine("                    throw new ApiException(\"Unexpected HTTP status.\", __qcStatus, __qcResponse.Content == null ? null : await __qcResponse.Content.ReadAsStringAsync().ConfigureAwait(false), __qcHeaders, null);");
            methods.AppendLine("                }");
            methods.AppendLine("                finally { if (__qcDisposeResponse) __qcResponse.Dispose(); }");
            methods.AppendLine("            }");
            methods.AppendLine("        }");
            Operations.Add(new { path = op.Path, method = op.Verb, name, implementation = "generated", deprecated = deprecationMessage != null, deprecationMessage });
        }
        string helper = File.ReadAllText(Path.Combine(templateDirectory, "ClientHelpers.cs.template"));
        string modelSource = models.RenderModels();
        string files = File.ReadAllText(Path.Combine(templateDirectory, "Files.cs.template"));
        return $"// <auto-generated by QlikCloud.Generator; do not edit />\nnamespace {models.Namespace}\n{{\n    using QlikCloud.Clients;\n    public partial class Client : RestClientBase\n    {{\n        public Client(RestClientConfig configuration) : base(configuration) {{ Initialize(); }}\n        partial void Initialize();\n{helper}\n{methods}    }}\n{modelSource}\n{files}\n}}\n";
    }

    private List<Parameter> Parameters(JObject path, JObject operation)
    {
        var parameters = new Dictionary<string, Parameter>();
        foreach (var token in ((JArray?)path["parameters"] ?? []).Concat((JArray?)operation["parameters"] ?? []))
        {
            var p = Resolve(token);
            string wire = (string?)p["name"] ?? throw new InvalidDataException("Unnamed parameter.");
            string location = (string?)p["in"] ?? throw new InvalidDataException("Parameter location missing.");
            bool required = location == "path" || (bool?)p["required"] == true;
            if (location == "header" && wire.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) continue;
            var schema = p["schema"] ?? p;
            string type = RequestType(schema, location == "body" ? "Body" : ModelRenderer.Identifier(wire), !required);
            string? format = (string?)Resolve(schema)["format"];
            if (format == "date") type = "global::System.DateTimeOffset" + (required ? "" : "?");
            string style = (string?)p["style"] ?? (location == "query" ? "form" : "simple");
            bool explode = (bool?)p["explode"] ?? style == "form";
            if (p["collectionFormat"] != null) { style = (string)p["collectionFormat"]!; explode = style == "multi"; }
            parameters[location + ":" + wire] = new Parameter(wire, ParameterName(wire), location, type, required, style, explode, null, Format: format);
        }
        if (operation["requestBody"] != null)
        {
            var body = Resolve(operation["requestBody"]!);
            var content = SelectContent(body);
            if (content == null) throw new InvalidDataException("Request body has no supported content.");
            bool required = (bool?)body["required"] == true;
            if (content.Name is "multipart/form-data" or "application/x-www-form-urlencoded")
            {
                var schema = Resolve(content.Value["schema"]!);
                foreach (var property in ((JObject?)schema["properties"] ?? []).Properties())
                {
                    bool propRequired = required && ((JArray?)schema["required"] ?? []).Values<string>().Contains(property.Name);
                    var propertySchema = Resolve(property.Value);
                    string? partContentType = ((string?)content.Value["encoding"]?[property.Name]?["contentType"])?.Split(',')[0].Trim();
                    bool jsonPart = partContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true || (string?)propertySchema["type"] is "object" or "array" || propertySchema["properties"] != null;
                    partContentType ??= jsonPart ? "application/json" : (string?)propertySchema["format"] == "binary" ? "application/octet-stream" : "text/plain";
                    parameters["form:" + property.Name] = new Parameter(property.Name, ParameterName(property.Name), "form", RequestType(property.Value, ModelRenderer.Identifier(property.Name), !propRequired), propRequired, "form", true, content.Name, partContentType, jsonPart);
                }
            }
            else parameters["body:body"] = new Parameter("body", "body", "body", RequestType(content.Value["schema"], "Body", !required), required, "form", false, content.Name);
        }
        var result = parameters.Values.OrderBy(p => p.Required ? 0 : 1).ToList();
        if (result.Select(p => p.Name).Distinct().Count() != result.Count) throw new InvalidDataException("Parameter names collide after normalization.");
        return result;
    }

    private string RequestType(JToken? schema, string name, bool nullable)
    {
        var resolved = schema == null ? null : Resolve(schema);
        if ((string?)resolved?["format"] == "binary" || (string?)resolved?["type"] == "file") return "FileParameter";
        string type = models.ResolveType(schema, name, nullable);
        return type.Replace("System.Collections.Generic.ICollection<", "System.Collections.Generic.IEnumerable<");
    }

    private List<Response> Responses(JObject operation)
    {
        return ((JObject?)operation["responses"] ?? throw new InvalidDataException("No responses.")).Properties().Where(p => p.Name == "default" || p.Name.Length == 3 && char.IsDigit(p.Name[0])).Select(p =>
        {
            var response = Resolve(p.Value);
            // A documented text representation retains the legacy string contract;
            // callers choosing JSON through Accept can still receive its raw text.
            var plain = ((JObject?)response["content"])?.Property("text/plain");
            var content = (string?)plain?.Value["schema"]?["type"] == "string" ? plain : SelectContent(response);
            var schema = content?.Value["schema"] ?? response["schema"];
            var resolved = schema == null ? null : Resolve(schema);
            bool binary = (string?)resolved?["format"] == "binary" || (string?)resolved?["type"] == "file" || content?.Name is "application/octet-stream" or "*/*";
            string? type = binary ? "FileResponse" : schema == null ? null : models.ResolveType(schema, "Response");
            return new Response(p.Name, type, content?.Name ?? (schema == null ? null : "application/json"), (string?)response["description"] ?? "HTTP error.");
        }).ToList();
    }

    private void EmitBody(StringBuilder builder, List<Parameter> parameters)
    {
        var body = parameters.FirstOrDefault(p => p.Location == "body");
        if (body != null)
        {
            string content = body.Type == "FileParameter" ? $"new System.Net.Http.StreamContent({body.Name}.Data)" : body.MediaType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true ? $"new System.Net.Http.StringContent(WireValue({body.Name}), System.Text.Encoding.UTF8)" : $"new System.Net.Http.StringContent(Newtonsoft.Json.JsonConvert.SerializeObject({body.Name}, JsonSerializerSettings), System.Text.Encoding.UTF8)";
            builder.AppendLine($"                if ((object){body.Name} != null)");
            builder.AppendLine("                {");
            builder.AppendLine($"                    __qcRequest.Content = {content};");
            builder.AppendLine($"                    __qcRequest.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue({Literal(body.MediaType ?? "application/json")});");
            builder.AppendLine("                }");
        }
        var form = parameters.Where(p => p.Location is "form" or "formData").ToList();
        if (form.Count == 0) return;
        bool urlEncoded = form.All(p => p.MediaType == "application/x-www-form-urlencoded");
        if (urlEncoded)
        {
            builder.AppendLine("                var __qcForm = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>>();");
            foreach (var p in form) builder.AppendLine($"                if ((object){p.Name} != null) __qcForm.Add(new System.Collections.Generic.KeyValuePair<string, string>({Literal(p.WireName)}, WireValue({p.Name})));");
            builder.AppendLine("                __qcRequest.Content = new System.Net.Http.FormUrlEncodedContent(__qcForm);");
        }
        else
        {
            builder.AppendLine("                var __qcForm = new System.Net.Http.MultipartFormDataContent();");
            foreach (var p in form)
            {
                builder.AppendLine($"                if ((object){p.Name} != null)");
                builder.AppendLine("                {");
                if (p.Type == "FileParameter")
                {
                    builder.AppendLine($"                    var __qcPart = new System.Net.Http.StreamContent({p.Name}.Data);");
                    builder.AppendLine($"                    __qcPart.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue({p.Name}.ContentType ?? {Literal(p.PartContentType ?? "application/octet-stream")});");
                    builder.AppendLine($"                    __qcForm.Add(__qcPart, {Literal(p.WireName)}, {p.Name}.FileName ?? {Literal(p.WireName)});");
                }
                else
                {
                    string value = p.JsonPart ? $"Newtonsoft.Json.JsonConvert.SerializeObject({p.Name}, JsonSerializerSettings)" : $"WireValue({p.Name})";
                    builder.AppendLine($"                    __qcForm.Add(new System.Net.Http.StringContent({value}, System.Text.Encoding.UTF8, {Literal(p.PartContentType ?? "text/plain")}), {Literal(p.WireName)});");
                }
                builder.AppendLine("                }");
            }
            builder.AppendLine("                __qcRequest.Content = __qcForm;");
        }
    }

    private JObject Resolve(JToken token)
    {
        var seen = new HashSet<string>();
        while (token["$ref"] is JValue reference)
        {
            string path = (string)reference!;
            if (!path.StartsWith("#/", StringComparison.Ordinal) || !seen.Add(path)) throw new InvalidDataException($"Unsupported or recursive structural reference: {path}");
            token = document;
            foreach (string segment in path[2..].Split('/')) token = token[segment.Replace("~1", "/").Replace("~0", "~")] ?? throw new InvalidDataException($"Unresolved reference: {path}");
        }
        return token as JObject ?? throw new InvalidDataException("Expected schema object.");
    }

    private static JProperty? SelectContent(JObject value) => ((JObject?)value["content"])?.Properties().OrderBy(p => p.Name == "application/json" ? 0 : p.Name.EndsWith("+json", StringComparison.Ordinal) ? 1 : 2).FirstOrDefault();
    private static string RequestPath(string path) => path.StartsWith("/api/", StringComparison.Ordinal) ? path[5..] : path.TrimStart('/');
    private static string MethodBase(string path) => string.Concat(RequestPath(path).Split('/').Where(s => !s.Contains('{')).Select(ModelRenderer.Identifier));
    private static string ParameterName(string name) { string id = ModelRenderer.Identifier(name); return "@" + char.ToLowerInvariant(id[0]) + id[1..]; }
    private static bool IsValueType(string type) => type is "int" or "long" or "double" or "float" or "decimal" or "bool" or "System.DateTimeOffset" or "System.Guid" or "global::System.DateTimeOffset" or "global::System.Guid";
    public static string Literal(string value) => JsonConvert.ToString(value);
    private static string Xml(string value) => System.Security.SecurityElement.Escape(value.Replace("\r", " ").Replace("\n", " ")) ?? "";
    private sealed record Parameter(string WireName, string Name, string Location, string Type, bool Required, string Style, bool Explode, string? MediaType, string? PartContentType = null, bool JsonPart = false, string? Format = null);
    private sealed record Response(string Code, string? Type, string? MediaType, string Description);
}
