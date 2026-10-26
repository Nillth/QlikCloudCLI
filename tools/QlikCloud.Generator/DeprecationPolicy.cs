using Newtonsoft.Json.Linq;

namespace QlikCloud.Generator;

/// <summary>Advisory lifecycle metadata shared by client and model rendering.</summary>
public static class DeprecationPolicy
{
    public static bool IsDeprecated(JObject? value) => value != null &&
        (IsTrue(value["x-qlik-deprecated"]) || IsTrue(value["deprecated"]));

    public static string? Message(string subject, params JObject?[] scopes)
    {
        var deprecated = scopes.Where(IsDeprecated).Reverse().ToArray();
        if (deprecated.Length == 0) return null;
        string message = subject + " is deprecated.";
        string? detail = deprecated.SelectMany(value => new[]
        {
            Text(value!["x-qlik-deprecation-message"]), Text(value["x-deprecation-message"]), Text(value["deprecationMessage"])
        }).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (detail != null) message += " " + detail.Trim();
        var replacements = deprecated.SelectMany(value => (value!["x-qlik-replaced-by"] as JArray ?? []).OfType<JObject>())
            .Select(value => Text(value["uniqueIdentifier"]))
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray();
        if (replacements.Length != 0) message += " Replacement API operation(s): " + string.Join(", ", replacements) + ".";
        return message;
    }

    private static bool IsTrue(JToken? value) => value?.Type == JTokenType.Boolean && value.Value<bool>();
    private static string? Text(JToken? value) => value?.Type == JTokenType.String ? value.Value<string>() : null;
}
