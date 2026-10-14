using System.Text.RegularExpressions;

namespace QlikCloudCLI.Generator;

/// <summary>Only structurally adjacent GET list/detail routes can share a cmdlet.</summary>
public static class CommandGrouping
{
    public static List<EndpointCommandGroup> Create(IEnumerable<EndpointCommand> operations, ICollection<string>? warnings = null)
    {
        var result = new List<EndpointCommandGroup>();
        foreach (var group in operations.GroupBy(operation => operation.CommandName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var routes = group.OrderBy(operation => operation.Path, StringComparer.Ordinal).ThenBy(operation => operation.HttpMethod, StringComparer.Ordinal).ToList();
            if (routes.Count > 32) throw new InvalidDataException($"Command {group.Key} exceeds PowerShell's 32 parameter-set limit ({routes.Count} routes). Split it with explicit command naming policy.");
            string defaultSet = "Default";
            if (routes.Count > 1)
            {
                if (routes.Count != 2 || routes.Any(route => route.HttpMethod != "GET") ||
                    routes.Select(route => route.Api).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1 ||
                    routes.Select(route => route.ClientType).Distinct(StringComparer.Ordinal).Count() != 1)
                    throw Collision(group.Key, "only one direct GET list/detail pair may share a command", routes);

                routes = routes.OrderBy(route => Segments(route.Path).Length).ThenBy(route => route.Path, StringComparer.Ordinal).ToList();
                var parent = routes[0];
                var detail = routes[1];
                string[] parentParts = Segments(parent.Path), detailParts = Segments(detail.Path);
                var last = Regex.Match(detailParts.LastOrDefault() ?? "", "^\\{([^}]+)\\}$", RegexOptions.CultureInvariant);
                if (!last.Success || detailParts.Length != parentParts.Length + 1 ||
                    !parentParts.Select(Shape).SequenceEqual(detailParts[..^1].Select(Shape), StringComparer.Ordinal))
                    throw Collision(group.Key, "routes are not a direct parent and trailing-identifier detail pair", routes);

                string identifier = TypeNames.Identifier(last.Groups[1].Value);
                CommandParameter id = detail.Parameters.SingleOrDefault(parameter => parameter.ClrName.Equals(identifier, StringComparison.OrdinalIgnoreCase))
                    ?? throw Collision(group.Key, "the detail path identifier has no SDK parameter", routes);
                if (!id.Mandatory || !id.PipelineByPropertyName)
                    throw Collision(group.Key, "the detail path identifier must be required", routes);
                if (parent.Parameters.Any(parameter => parameter.Name.Equals(id.Name, StringComparison.OrdinalIgnoreCase)))
                    throw Collision(group.Key, $"detail parameter {id.Name} is also a list parameter; an explicit parameter-name policy must distinguish query filters from path identifiers", routes);
                parent.ParameterSetName = "List";
                detail.ParameterSetName = "By" + id.Name;
                defaultSet = "List";
            }
            else routes[0].ParameterSetName = "Default";

            NormalizeParameters(group.Key, routes, warnings);
            result.Add(new EndpointCommandGroup { CommandName = group.Key, DefaultParameterSetName = defaultSet, Operations = routes });
        }
        return result;
    }

    private static void NormalizeParameters(string commandName, List<EndpointCommand> routes, ICollection<string>? warnings)
    {
        var properties = routes.SelectMany(route => route.Parameters).GroupBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties)
        {
            if (property.Value.Select(parameter => parameter.Type).Distinct(StringComparer.Ordinal).Count() != 1)
                throw new InvalidDataException($"Command {commandName} has incompatible types for parameter {property.Key}: {string.Join(", ", property.Value.Select(parameter => parameter.Type).Distinct())}. Split the detail route with explicit command naming policy.");
            // PowerShell treats names without case sensitivity; C# properties do not.
            foreach (var parameter in property.Value) parameter.Name = property.Key;
        }
        var aliasTargets = properties.SelectMany(property => property.Value.SelectMany(parameter => parameter.Aliases)
                .Select(alias => (Alias: alias, Property: property.Key)))
            .GroupBy(value => value.Alias, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(value => value.Property).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties)
        {
            var candidates = property.Value.SelectMany(parameter => parameter.Aliases).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(alias => alias, StringComparer.Ordinal).ToArray();
            var safe = candidates.Where(alias => !properties.ContainsKey(alias) && aliasTargets[alias].Length == 1).ToArray();
            foreach (string removed in candidates.Except(safe, StringComparer.OrdinalIgnoreCase))
                warnings?.Add($"{commandName}: removed alias {removed} from {property.Key} because aliases apply to the whole command and collide with another parameter.");
            // AliasAttribute belongs to the property, so every set sees the same aliases.
            foreach (var parameter in property.Value) parameter.Aliases = safe;
        }
    }

    private static string[] Segments(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries);
    private static string Shape(string segment) => Regex.IsMatch(segment, "^\\{[^}]+\\}$", RegexOptions.CultureInvariant) ? "{}" : segment;
    private static InvalidDataException Collision(string name, string reason, IEnumerable<EndpointCommand> routes) =>
        new($"Command name collision for {name}: {reason}. Routes: {string.Join("; ", routes.Select(route => route.HttpMethod + " " + route.Path))}. Add explicit command-rules.json policy.");
}
