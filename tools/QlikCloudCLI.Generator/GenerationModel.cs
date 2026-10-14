using System.Reflection;
using System.Text.Json.Serialization;

namespace QlikCloudCLI.Generator;

public sealed class CommandInventory
{
    public string Generator { get; init; } = SafeOutput.GeneratorName;
    public int FormatVersion { get; init; } = 2;
    public string AssemblySha256 { get; init; } = "";
    public string ClientManifestSha256 { get; init; } = "";
    public string RulesSha256 { get; init; } = "";
    public string NamingPolicy { get; init; } = "Literal API route nouns omit path placeholders. Direct GET list/detail pairs share List and By<Id> parameter sets; other routes remain separate. Root POST New, action POST Invoke, PUT Set, PATCH Update, DELETE Remove, GET Get; explicit policy resolves incompatible collisions.";
    public int CommandCount => Commands.Count;
    public List<EndpointCommandGroup> Commands { get; init; } = [];
    public List<EndpointCommand> Operations { get; init; } = [];
    public List<ModelCommand> Constructors { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

public sealed class EndpointCommandGroup
{
    public required string CommandName { get; init; }
    public required string DefaultParameterSetName { get; init; }
    public string[] ParameterSetNames => Operations.Select(operation => operation.ParameterSetName).ToArray();
    public int OperationCount => Operations.Count;
    [JsonIgnore] public List<EndpointCommand> Operations { get; init; } = [];
}

public sealed class EndpointCommand
{
    public required string Api { get; init; }
    public required string Path { get; init; }
    public required string HttpMethod { get; init; }
    public required string CommandName { get; set; }
    public string ParameterSetName { get; set; } = "Default";
    public required string ClientType { get; init; }
    public required string MethodName { get; init; }
    public required string Implementation { get; init; }
    public required string OutputType { get; init; }
    public required string RawResponseType { get; init; }
    public bool SupportsShouldProcess { get; init; }
    public string? NamingReason { get; set; }
    public List<CommandParameter> Parameters { get; init; } = [];
    [JsonIgnore] public MethodInfo Method { get; init; } = null!;
}

public sealed class ModelCommand
{
    public required string CommandName { get; init; }
    public required string ModelType { get; init; }
    public required string Api { get; init; }
    public bool SupportsJson { get; init; }
    public string? CollectionElementType { get; init; }
    public List<CommandParameter> Parameters { get; init; } = [];
    [JsonIgnore] public Type Type { get; init; } = null!;
    [JsonIgnore] public ConstructorInfo Constructor { get; init; } = null!;
}

public sealed class CommandParameter
{
    public required string Name { get; set; }
    public required string ClrName { get; init; }
    public required string Type { get; init; }
    public required string ClrType { get; init; }
    public bool Mandatory { get; init; }
    public bool PipelineByValue { get; init; }
    public bool PipelineByPropertyName { get; init; }
    public string[] Aliases { get; set; } = [];
    public string? NamingReason { get; init; }
    public bool ConstructorOnly { get; init; }
    [JsonIgnore] public Type ReflectedType { get; init; } = null!;
}

public sealed class CommandRules
{
    public int FormatVersion { get; set; } = 1;
    public List<CommandNameRule> CommandNames { get; set; } = [];
    public List<MethodBindingRule> MethodBindings { get; set; } = [];
    public List<ModelRootRule> ModelRoots { get; set; } = [];
    public List<ParameterNameRule> ParameterNames { get; set; } = [];
}

public sealed class ParameterNameRule
{
    public string Operation { get; set; } = "";
    public string Parameter { get; set; } = "";
    public string Name { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class MethodBindingRule
{
    public string Operation { get; set; } = "";
    public string[] ParameterNames { get; set; } = [];
    public string[]? ParameterTypes { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class ModelRootRule
{
    public string Type { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class CommandNameRule
{
    public string Operation { get; set; } = "";
    public string CommandName { get; set; } = "";
    public string Reason { get; set; } = "";
}
