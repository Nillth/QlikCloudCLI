using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QlikCloudCLI.Generator;

public sealed class CmdletGenerator
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private sealed record Operation(string Api, string Path, string Verb, string Method, string Implementation, bool Deprecated, string? DeprecationMessage);

    public CommandInventory Generate(string assemblyPath, string clientManifestPath, string outputDirectory, string rulesPath, bool check = false)
    {
        foreach (string input in new[] { assemblyPath, clientManifestPath, rulesPath })
            if (!File.Exists(input)) throw new FileNotFoundException("Required generator input not found: " + input);
        using var outputLease = SafeOutput.AcquireLock(outputDirectory);
        byte[] assemblyBytes = File.ReadAllBytes(assemblyPath);
        byte[] manifestBytes = File.ReadAllBytes(clientManifestPath);
        byte[] rulesBytes = File.ReadAllBytes(rulesPath);
        using var manifest = JsonDocument.Parse(manifestBytes);
        if (manifest.RootElement.GetProperty("generator").GetString() != "QlikCloud.Generator" || manifest.RootElement.GetProperty("command").GetString() != "generate")
            throw new InvalidDataException("The client manifest must come from QlikCloud.Generator generate.");
        var rules = JsonSerializer.Deserialize<CommandRules>(rulesBytes, Json) ?? throw new InvalidDataException("Invalid command rules.");
        if (rules.FormatVersion != 1 || rules.CommandNames == null || rules.MethodBindings == null || rules.ModelRoots == null || rules.ParameterNames == null) throw new InvalidDataException("Unsupported command rules format.");
        if (rules.CommandNames.Any(r => string.IsNullOrWhiteSpace(r.Operation) || string.IsNullOrWhiteSpace(r.CommandName) || string.IsNullOrWhiteSpace(r.Reason)))
            throw new InvalidDataException("Every command naming rule requires operation, commandName, and reason.");
        var nameRules = rules.CommandNames.ToDictionary(r => r.Operation, StringComparer.OrdinalIgnoreCase);
        if (rules.MethodBindings.Any(r => string.IsNullOrWhiteSpace(r.Operation) || r.ParameterNames == null || string.IsNullOrWhiteSpace(r.Reason)))
            throw new InvalidDataException("Every method binding requires operation, parameterNames, and reason.");
        var methodRules = rules.MethodBindings.ToDictionary(r => r.Operation, StringComparer.OrdinalIgnoreCase);
        if (rules.ParameterNames.Any(rule => string.IsNullOrWhiteSpace(rule.Operation) || string.IsNullOrWhiteSpace(rule.Parameter)
            || string.IsNullOrWhiteSpace(rule.Name) || string.IsNullOrWhiteSpace(rule.Reason)
            || !Regex.IsMatch(rule.Name, "^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.CultureInvariant) || TypeNames.Parameter(rule.Name) != rule.Name))
            throw new InvalidDataException("Every parameter naming rule requires operation, parameter (SDK name), a valid unreserved public name, and reason.");
        var parameterRules = rules.ParameterNames.ToDictionary(rule => rule.Operation + "\n" + rule.Parameter, StringComparer.OrdinalIgnoreCase);
        var usedRules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedMethodRules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedParameterRules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var operations = manifest.RootElement.GetProperty("apis").EnumerateArray().Where(api => !api.TryGetProperty("excluded", out _))
            .SelectMany(api => api.GetProperty("operations").EnumerateArray().Select(op => new Operation(
                api.GetProperty("api").GetString()!, op.GetProperty("path").GetString()!, op.GetProperty("method").GetString()!.ToUpperInvariant(),
                op.GetProperty("name").GetString()!, op.GetProperty("implementation").GetString()!, ReadDeprecated(op), ReadDeprecationMessage(op))))
            .OrderBy(op => op.Api, StringComparer.Ordinal).ThenBy(op => op.Path, StringComparer.Ordinal).ThenBy(op => op.Verb, StringComparer.Ordinal).ToArray();
        if (operations.Length == 0) throw new InvalidDataException("The client manifest contains no API operations.");
        if (operations.GroupBy(OperationKey, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() != 1)) throw new InvalidDataException("Duplicate operation in client manifest.");
        var apiByNamespace = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string api in operations.Select(operation => operation.Api).Distinct(StringComparer.Ordinal))
        {
            string modelNamespace = "QlikCloud." + TypeNames.Identifier(api);
            if (!apiByNamespace.TryAdd(modelNamespace, api))
                throw new InvalidDataException($"API namespace collision between {apiByNamespace[modelNamespace]} and {api}: {modelNamespace}.");
        }

        string[] trusted = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var paths = Directory.EnumerateFiles(Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!, "*.dll")
            .Concat(trusted).GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase).Select(g => g.First());
        using var metadata = new MetadataLoadContext(new PathAssemblyResolver(paths));
        Assembly assembly = metadata.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
        if (assembly.GetName().Name != "QlikCloud") throw new InvalidDataException("Expected an assembly named QlikCloud.");
        var inventory = new CommandInventory { AssemblySha256 = Hash(assemblyBytes), ClientManifestSha256 = Hash(manifestBytes), RulesSha256 = Hash(rulesBytes) };
        var matched = new HashSet<int>();
        var requiredModels = new HashSet<Type>();

        foreach (Operation operation in operations)
        {
            string clientTypeName = "QlikCloud." + TypeNames.Identifier(operation.Api) + ".Client";
            Type client = assembly.GetType(clientTypeName, throwOnError: true)!;
            var siblings = operations.Where(op => op.Api == operation.Api && op.Method == operation.Method).ToArray();
            var pathNames = Placeholders(operation.Path);
            var siblingNames = siblings.SelectMany(op => Placeholders(op.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var methods = client.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(method => method.Name == operation.Method + "Async")
                .Where(method =>
                {
                    var required = method.GetParameters().Where(p => !p.IsOptional).Select(p => p.Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var declared = method.GetParameters().Select(p => p.Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    return pathNames.All(declared.Contains) && siblingNames.Where(required.Contains).All(pathNames.Contains);
                }).ToArray();
            // A list route may expose an optional filter with the same name as an item's required path parameter.
            // Prefer the overload whose mandatory parameters identify the requested route.
            if (methods.Length > 1)
            {
                int bestPathMatch = methods.Max(m => m.GetParameters().Count(p => !p.IsOptional && pathNames.Contains(p.Name!)));
                methods = methods.Where(m => m.GetParameters().Count(p => !p.IsOptional && pathNames.Contains(p.Name!)) == bestPathMatch).ToArray();
            }
            if (methodRules.TryGetValue(OperationKey(operation), out MethodBindingRule? methodRule))
            {
                methods = methods.Where(m => m.GetParameters().Select(p => p.Name).SequenceEqual(methodRule.ParameterNames, StringComparer.Ordinal)).ToArray();
                if (methodRule.ParameterTypes != null)
                    methods = methods.Where(m => m.GetParameters().Select(p => p.ParameterType.FullName).SequenceEqual(methodRule.ParameterTypes, StringComparer.Ordinal)).ToArray();
                usedMethodRules.Add(methodRule.Operation);
            }
            if (methods.Length != 1) throw new InvalidDataException($"Expected one SDK method for {OperationKey(operation)} ({operation.Method}Async); found {methods.Length}. Add explicit method binding policy before accepting this SDK.");
            MethodInfo method = methods[0];
            if (!matched.Add(method.MetadataToken)) throw new InvalidDataException("Two operations bind the same SDK method: " + OperationKey(operation));
            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length == 0 || parameters[^1].ParameterType.FullName != "System.Threading.CancellationToken"
                || !method.ReturnType.IsGenericType || method.ReturnType.GetGenericTypeDefinition().FullName != "System.Threading.Tasks.Task`1")
                throw new InvalidDataException("SDK method does not have the required asynchronous cancellation contract: " + method);
            Type resultType = method.ReturnType.GetGenericArguments()[0];
            Type outputType = resultType.IsGenericType && resultType.GetGenericTypeDefinition().FullName == "QlikCloud.APIResponse`1" ? resultType.GetGenericArguments()[0] : resultType;
            var command = new EndpointCommand
            {
                Api = operation.Api, Path = operation.Path, HttpMethod = operation.Verb, CommandName = DefaultName(operation), ClientType = clientTypeName,
                MethodName = method.Name, Implementation = operation.Implementation, Method = method,
                OutputType = TypeNames.Format(outputType), RawResponseType = TypeNames.Format(resultType),
                Deprecated = operation.Deprecated, DeprecationMessage = operation.Deprecated ? operation.DeprecationMessage : null,
                SupportsShouldProcess = operation.Verb is not ("GET" or "HEAD" or "OPTIONS")
            };
            if (nameRules.TryGetValue(OperationKey(operation), out CommandNameRule? rule))
            {
                command.CommandName = rule.CommandName;
                command.NamingReason = rule.Reason;
                usedRules.Add(rule.Operation);
            }
            ValidateCommandName(command.CommandName);
            foreach (ParameterInfo parameter in parameters[..^1])
            {
                string clrName = parameter.Name ?? throw new InvalidDataException("Unnamed SDK parameter.");
                bool path = pathNames.Contains(clrName);
                bool body = clrName == "body";
                string publicName = TypeNames.Parameter(clrName);
                string parameterRuleKey = OperationKey(operation) + "\n" + clrName;
                parameterRules.TryGetValue(parameterRuleKey, out ParameterNameRule? parameterRule);
                if (parameterRule != null)
                {
                    publicName = parameterRule.Name;
                    usedParameterRules.Add(parameterRuleKey);
                }
                string[] aliases = path && pathNames.Count == 1 && !publicName.Equals("Id", StringComparison.OrdinalIgnoreCase) && publicName.EndsWith("Id", StringComparison.OrdinalIgnoreCase) ? ["Id"] : [];
                command.Parameters.Add(new CommandParameter
                {
                    Name = publicName, ClrName = clrName, Type = TypeNames.ParameterType(parameter.ParameterType), ClrType = TypeNames.Format(parameter.ParameterType),
                    Mandatory = path || !parameter.IsOptional, PipelineByPropertyName = path,
                    PipelineByValue = path && pathNames.Count == 1 || body && parameter.ParameterType.FullName != "System.String",
                    Aliases = aliases, ReflectedType = parameter.ParameterType, NamingReason = parameterRule?.Reason
                });
                DiscoverModels(parameter.ParameterType, requiredModels);
            }
            ValidateParameters(command.CommandName, command.Parameters);
            inventory.Operations.Add(command);
        }
        foreach (string unused in nameRules.Keys.Except(usedRules, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("Unused command naming policy: " + unused);
        foreach (string unused in methodRules.Keys.Except(usedMethodRules, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("Unused method binding policy: " + unused);
        foreach (string unused in parameterRules.Keys.Except(usedParameterRules, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("Unused parameter naming policy: " + unused.Replace('\n', ' '));
        foreach (ModelRootRule rule in rules.ModelRoots)
        {
            if (string.IsNullOrWhiteSpace(rule.Type) || string.IsNullOrWhiteSpace(rule.Reason)) throw new InvalidDataException("Every model root policy requires type and reason.");
            Type model = assembly.GetType(rule.Type, throwOnError: true)!;
            if (!TypeNames.IsModel(model)) throw new InvalidDataException("Model root is not a constructible request model: " + rule.Type);
            DiscoverModels(model, requiredModels);
        }
        foreach (Type type in requiredModels.OrderBy(t => t.FullName, StringComparer.Ordinal)) inventory.Constructors.Add(CreateModelCommand(type, apiByNamespace));
        inventory.Commands.AddRange(CommandGrouping.Create(inventory.Operations, inventory.Warnings));
        var duplicate = inventory.Commands.Select(c => c.CommandName).Concat(inventory.Constructors.Select(c => c.CommandName))
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
        {
            string keys = string.Join("; ", inventory.Operations.Where(c => c.CommandName.Equals(duplicate.Key, StringComparison.OrdinalIgnoreCase)).Select(c => c.Api + " " + c.HttpMethod + " " + c.Path));
            throw new InvalidDataException("Command name collision requires explicit command-rules.json policy: " + duplicate.Key + " => " + keys);
        }

        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (EndpointCommandGroup command in inventory.Commands) files.Add("src/" + command.CommandName + ".cs", SourceRenderer.Endpoint(command));
        foreach (ModelCommand command in inventory.Constructors) files.Add("src/" + command.CommandName + ".cs", SourceRenderer.Constructor(command));
        files.Add("commands.json", JsonSerializer.Serialize(inventory, Json) + "\n");
        files.Add("COMMANDS.md", SourceRenderer.Help(inventory));
        var outputManifest = new
        {
            generator = SafeOutput.GeneratorName, formatVersion = 2, command = "generate", inventory.AssemblySha256, inventory.ClientManifestSha256, inventory.RulesSha256,
            generatorSha256 = Hash(File.ReadAllBytes(typeof(CmdletGenerator).Assembly.Location)), operationCount = inventory.Operations.Count, inventory.CommandCount, constructorCount = inventory.Constructors.Count,
            files = files.Select(f => new { path = f.Key, sha256 = Hash(Encoding.UTF8.GetBytes(f.Value)) }).ToArray()
        };
        files.Add("manifest.json", JsonSerializer.Serialize(outputManifest, Json) + "\n");
        SafeOutput.Write(outputDirectory, files, check);
        return inventory;
    }

    private static string OperationKey(Operation operation) => operation.Api + " " + operation.Verb + " " + operation.Path;
    private static bool ReadDeprecated(JsonElement operation)
    {
        if (!operation.TryGetProperty("deprecated", out JsonElement value)) return false;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("Operation deprecated metadata must be a Boolean.");
        return value.GetBoolean();
    }
    private static string? ReadDeprecationMessage(JsonElement operation)
    {
        if (!operation.TryGetProperty("deprecationMessage", out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Operation deprecationMessage metadata must be a string or null.");
        string? message = value.GetString();
        return string.IsNullOrWhiteSpace(message) ? null : message;
    }

    private static (bool Deprecated, string? Message) ReadObsolete(MemberInfo member)
    {
        CustomAttributeData? attribute = member.GetCustomAttributesData().FirstOrDefault(value => value.AttributeType.FullName == "System.ObsoleteAttribute");
        if (attribute == null) return (false, null);
        string? message = attribute.ConstructorArguments.Count > 0 ? attribute.ConstructorArguments[0].Value as string : null;
        return (true, string.IsNullOrWhiteSpace(message) ? null : message);
    }
    private static HashSet<string> Placeholders(string path) => Regex.Matches(path, "\\{([^}]+)\\}").Select(m => TypeNames.Identifier(m.Groups[1].Value)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static string DefaultName(Operation operation)
    {
        // Legacy routes use /api/v1/<resource>; namespaced APIs use /api/<namespace>/<resource>.
        // Keep the namespace in the noun, but never the API transport prefix.
        string path = Regex.Replace(operation.Path, "^/api/(?:v[0-9]+(?:/|$))?", "", RegexOptions.CultureInvariant).Trim('/');
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string noun = string.Concat(parts.Where(part => !(part.StartsWith('{') && part.EndsWith('}'))).Select(TypeNames.Identifier));
        string api = TypeNames.Identifier(operation.Api);
        if (!noun.StartsWith(api, StringComparison.OrdinalIgnoreCase)) noun = api + noun;
        string verb = operation.Verb switch
        {
            "GET" => "Get", "POST" => string.Equals(path, operation.Api, StringComparison.OrdinalIgnoreCase) ? "New" : "Invoke",
            "PUT" => "Set", "PATCH" => "Update", "DELETE" => "Remove", "HEAD" => "Get", "OPTIONS" => "Get", "TRACE" => "Invoke",
            _ => throw new InvalidDataException("Unsupported HTTP method: " + operation.Verb)
        };
        if (operation.Verb is "HEAD" or "OPTIONS" or "TRACE") noun += operation.Verb == "HEAD" ? "Headers" : TypeNames.Identifier(operation.Verb.ToLowerInvariant());
        return verb + "-QC" + noun;
    }

    private static void DiscoverModels(Type type, HashSet<Type> models)
    {
        if (type.IsArray) { DiscoverModels(type.GetElementType()!, models); return; }
        if (type.IsGenericType) foreach (Type argument in type.GetGenericArguments()) DiscoverModels(argument, models);
        if (!TypeNames.IsModel(type) || !models.Add(type)) return;
        if (type.BaseType != null) DiscoverModels(type.BaseType, models);
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)) DiscoverModels(property.PropertyType, models);
        foreach (ConstructorInfo constructor in type.GetConstructors()) foreach (ParameterInfo parameter in constructor.GetParameters()) DiscoverModels(parameter.ParameterType, models);
    }

    private static ModelCommand CreateModelCommand(Type type, IReadOnlyDictionary<string, string> apiByNamespace)
    {
        ConstructorInfo[] constructors = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public);
        ConstructorInfo constructor = constructors.FirstOrDefault(c => c.GetParameters().Length == 0)
            ?? constructors.OrderByDescending(c => c.GetParameters().Length).FirstOrDefault()
            ?? throw new InvalidDataException("Request model has no public constructor: " + type.FullName);
        Type? collectionElement = TypeNames.CollectionElement(type);
        if (!apiByNamespace.TryGetValue(type.Namespace!, out string? api))
            throw new InvalidDataException("Request model namespace has no API identity in the client manifest: " + type.FullName);
        var lifecycle = ReadObsolete(type);
        if (!lifecycle.Deprecated) lifecycle = ReadObsolete(constructor);
        var command = new ModelCommand
        {
            CommandName = "New-QC" + TypeNames.Identifier(api) + TypeNames.Identifier(type.Name) + "Object", ModelType = TypeNames.Format(type), Api = api,
            Type = type, Constructor = constructor, SupportsJson = type.GetMethods(BindingFlags.Public | BindingFlags.Static).Any(m => m.Name == "FromJson" && m.GetParameters().Length == 1),
            Deprecated = lifecycle.Deprecated, DeprecationMessage = lifecycle.Message,
            CollectionElementType = collectionElement == null ? null : TypeNames.Format(collectionElement)
        };
        foreach (ParameterInfo parameter in constructor.GetParameters())
            command.Parameters.Add(new CommandParameter { Name = TypeNames.Parameter(parameter.Name!), ClrName = parameter.Name!, Type = TypeNames.ParameterType(parameter.ParameterType),
                ClrType = TypeNames.Format(parameter.ParameterType), Mandatory = !parameter.IsOptional && parameter.Name == "data", ConstructorOnly = true, ReflectedType = parameter.ParameterType });
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.SetMethod?.IsPublic == true && p.GetIndexParameters().Length == 0).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (command.Parameters.Any(p => p.ClrName.Equals(property.Name, StringComparison.OrdinalIgnoreCase))) continue;
            command.Parameters.Add(new CommandParameter { Name = TypeNames.Parameter(property.Name), ClrName = property.Name, Type = TypeNames.ParameterType(property.PropertyType), ClrType = TypeNames.Format(property.PropertyType), ReflectedType = property.PropertyType });
        }
        if (collectionElement != null)
            command.Parameters.Add(new CommandParameter { Name = "Items", ClrName = "$items", Type = TypeNames.Format(collectionElement) + "[]", ClrType = TypeNames.Format(collectionElement) + "[]", ReflectedType = collectionElement.MakeArrayType() });
        ValidateParameters(command.CommandName, command.Parameters);
        return command;
    }

    private static void ValidateCommandName(string name)
    {
        if (!Regex.IsMatch(name, "^(Get|New|Set|Update|Remove|Invoke)-QC[A-Za-z0-9_]+$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Command naming policy must use an approved supported verb and QC-prefixed noun: " + name);
    }
    private static void ValidateParameters(string command, IEnumerable<CommandParameter> parameters)
    {
        var duplicate = parameters.SelectMany(p => p.Aliases.Prepend(p.Name)).GroupBy(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null) throw new InvalidDataException("Parameter or alias collision in " + command + ": " + duplicate.Key);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
