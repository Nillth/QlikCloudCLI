using System.Reflection;
using System.Text;

namespace QlikCloudCLI.Generator;

internal static class TypeNames
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "Verbose", "Debug", "Logger", "ErrorAction", "WarningAction", "InformationAction", "ProgressAction", "ErrorVariable", "WarningVariable",
        "InformationVariable", "OutVariable", "OutBuffer", "PipelineVariable", "WhatIf", "Confirm", "Connection", "RawResponse", "OutFile",
        "Host", "SessionState", "MyInvocation", "CommandRuntime", "Events", "InvokeCommand", "InvokeProvider", "PagingParameters", "ParameterSetName",
        "StopProcessing", "BeginProcessing", "EndProcessing", "ProcessRecord", "ExecuteApi", "CancellationToken", "InputObject", "Json"
    };
    private static readonly HashSet<string> SequenceTypes = new(StringComparer.Ordinal)
    {
        "System.Collections.Generic.IEnumerable`1", "System.Collections.Generic.ICollection`1", "System.Collections.Generic.IList`1",
        "System.Collections.Generic.List`1", "System.Collections.ObjectModel.Collection`1", "System.Collections.Generic.IReadOnlyList`1",
        "System.Collections.Generic.IReadOnlyCollection`1"
    };

    public static string Identifier(string raw)
    {
        var output = new StringBuilder();
        bool capitalize = true;
        foreach (char value in raw)
        {
            if (!char.IsLetterOrDigit(value)) { capitalize = true; continue; }
            if (output.Length == 0 && char.IsDigit(value)) output.Append('_');
            output.Append(capitalize ? char.ToUpperInvariant(value) : value);
            capitalize = false;
        }
        return output.Length == 0 ? "Value" : output.ToString();
    }

    public static string Parameter(string raw)
    {
        string value = Identifier(raw);
        return Reserved.Contains(value) ? value + "Parameter" : value;
    }

    public static string Format(Type type)
    {
        if (type.IsArray) return Format(type.GetElementType()!) + "[]";
        if (type.IsGenericType)
        {
            string definition = type.GetGenericTypeDefinition().FullName!;
            if (definition == "System.Nullable`1") return Format(type.GetGenericArguments()[0]) + "?";
            return "global::" + definition[..definition.IndexOf('`')].Replace('+', '.') + "<" + string.Join(", ", type.GetGenericArguments().Select(Format)) + ">";
        }
        return "global::" + (type.FullName ?? throw new InvalidDataException("Unnamed reflected type.")).Replace('+', '.');
    }

    public static string ParameterType(Type type) => IsSequence(type) ? Format(type.GetGenericArguments()[0]) + "[]" : Format(type);
    public static bool IsSequence(Type type) => type.IsGenericType && SequenceTypes.Contains(type.GetGenericTypeDefinition().FullName!);
    public static string Assignment(Type type, string expression)
    {
        if (!IsSequence(type)) return expression;
        string definition = type.GetGenericTypeDefinition().FullName!;
        if (definition is "System.Collections.Generic.List`1" or "System.Collections.ObjectModel.Collection`1")
            return expression + " == null ? null : new " + Format(type) + "(" + expression + ")";
        return expression;
    }

    public static bool IsModel(Type type) => type.IsClass && !type.IsAbstract && !type.IsGenericType && !type.IsNested
        && type.Namespace?.StartsWith("QlikCloud.", StringComparison.Ordinal) == true
        && type.Name is not ("Client" or "FileResponse") && type.Namespace != "QlikCloud.Clients";

    public static Type? CollectionElement(Type type)
    {
        for (Type? current = type.BaseType; current != null; current = current.BaseType)
            if (current.IsGenericType && current.GetGenericTypeDefinition().FullName == "System.Collections.ObjectModel.Collection`1")
                return current.GetGenericArguments()[0];
        return null;
    }
}
