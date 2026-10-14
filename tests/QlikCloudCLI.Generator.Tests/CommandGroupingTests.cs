using QlikCloudCLI.Generator;
using Xunit;

namespace QlikCloudCLI.Generator.Tests;

public sealed class CommandGroupingTests
{
    [Fact]
    public void DirectGetPairHasDistinctOutputMetadataAndDefaultList()
    {
        EndpointCommand list = Route("/api/v1/widgets", output: "global::QlikCloud.Widgets.Page");
        EndpointCommand detail = Route("/api/v1/widgets/{widgetId}", [PathParameter("WidgetId")], output: "global::QlikCloud.Widgets.Widget");
        EndpointCommandGroup group = Assert.Single(CommandGrouping.Create([detail, list]));
        Assert.Equal("List", group.DefaultParameterSetName);
        Assert.Equal(new[] { "List", "ByWidgetId" }, group.ParameterSetNames);
        Assert.Equal(2, group.OperationCount);
        Assert.Same(list, group.Operations[0]);
        Assert.Same(detail, group.Operations[1]);
        Assert.NotEqual(list.OutputType, detail.OutputType);
        Assert.NotEqual(list.RawResponseType, detail.RawResponseType);
    }

    [Fact]
    public void SharedAliasesDescribeTheirActualCommandWideVisibility()
    {
        CommandParameter listParent = PathParameter("WidgetId", aliases: ["Id"]);
        CommandParameter detailParent = PathParameter("WidgetId");
        var group = Assert.Single(CommandGrouping.Create([
            Route("/api/v1/widgets/{widgetId}/parts", [listParent]),
            Route("/api/v1/widgets/{widgetId}/parts/{partId}", [detailParent, PathParameter("PartId")]) ]));
        Assert.Equal(new[] { "Id" }, listParent.Aliases);
        Assert.Equal(new[] { "Id" }, detailParent.Aliases);
        Assert.Equal(new[] { "List", "ByPartId" }, group.ParameterSetNames);
    }

    [Fact]
    public void DifferentlyNamedParentPlaceholdersKeepTheirPerRouteParameters()
    {
        var group = Assert.Single(CommandGrouping.Create([
            Route("/api/v1/widgets/{taskId}/executions", [PathParameter("TaskId")]),
            Route("/api/v1/widgets/{alertId}/executions/{executionId}", [PathParameter("AlertId"), PathParameter("ExecutionId")]) ]));
        Assert.Equal(new[] { "List", "ByExecutionId" }, group.ParameterSetNames);
        Assert.Equal("TaskId", Assert.Single(group.Operations[0].Parameters).Name);
        Assert.Equal(new[] { "AlertId", "ExecutionId" }, group.Operations[1].Parameters.Select(parameter => parameter.Name));
    }

    [Theory]
    [InlineData("/api/v1/widgets/summary")]
    [InlineData("/api/v1/other/{widgetId}")]
    [InlineData("/api/v1/widgets/{widgetId}/parts")]
    [InlineData("/api/v1/widgets/{widgetId}/parts/{partId}")]
    public void SameNameCannotSilentlyMergeUnrelatedOrNonAdjacentRoutes(string detailPath)
    {
        var exception = Assert.Throws<InvalidDataException>(() => CommandGrouping.Create([
            Route("/api/v1/widgets"), Route(detailPath, [PathParameter("WidgetId"), PathParameter("PartId")]) ]));
        Assert.Contains("collision", exception.Message);
        Assert.Contains("not a direct parent", exception.Message);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public void NonGetRoutesAreNeverGroupedImplicitly(string method)
    {
        Assert.Contains("only one direct GET", Assert.Throws<InvalidDataException>(() => CommandGrouping.Create([
            Route("/api/v1/widgets", method: method),
            Route("/api/v1/widgets/{widgetId}", [PathParameter("WidgetId")], method: method) ])).Message);
    }

    [Fact]
    public void CrossApiOverrideCannotMergeRoutes()
    {
        Assert.Contains("only one direct GET", Assert.Throws<InvalidDataException>(() => CommandGrouping.Create([
            Route("/api/v1/widgets"),
            Route("/api/v1/widgets/{widgetId}", [PathParameter("WidgetId")], api: "other") ])).Message);
    }

    [Fact]
    public void MultipleDetailRoutesRequireAnExplicitSplit()
    {
        Assert.Contains("only one direct GET", Assert.Throws<InvalidDataException>(() => CommandGrouping.Create([
            Route("/api/v1/widgets"),
            Route("/api/v1/widgets/{widgetId}", [PathParameter("WidgetId")]),
            Route("/api/v1/widgets/{widgetName}", [PathParameter("WidgetName")]) ] )).Message);
    }

    [Fact]
    public void PowerShellSetLimitIsCheckedBeforeGroupShape()
    {
        var routes = Enumerable.Range(0, 33).Select(index => Route("/api/v1/widgets/route" + index)).ToArray();
        Assert.Contains("32 parameter-set limit (33 routes)", Assert.Throws<InvalidDataException>(() => CommandGrouping.Create(routes)).Message);
        Assert.Contains("only one direct GET", Assert.Throws<InvalidDataException>(() => CommandGrouping.Create(routes.Take(32))).Message);
    }

    [Fact]
    public void DetailIdentifierMustExistAndBeMandatory()
    {
        Assert.Contains("no SDK parameter", Assert.Throws<InvalidDataException>(() => CommandGrouping.Create([
            Route("/api/v1/widgets"), Route("/api/v1/widgets/{widgetId}") ])).Message);
        Assert.Contains("must be required", Assert.Throws<InvalidDataException>(() => CommandGrouping.Create([
            Route("/api/v1/widgets"), Route("/api/v1/widgets/{widgetId}", [PathParameter("WidgetId", mandatory: false)]) ])).Message);
    }

    private static EndpointCommand Route(string path, CommandParameter[]? parameters = null, string method = "GET", string api = "widgets", string output = "global::System.String") => new()
    {
        Api = api, Path = path, HttpMethod = method, CommandName = "Get-QCWidgets", ClientType = "QlikCloud.Widgets.Client",
        MethodName = "FixtureAsync", Implementation = "fixture", OutputType = output, RawResponseType = "global::QlikCloud.APIResponse<" + output + ">",
        Parameters = parameters?.ToList() ?? []
    };

    private static CommandParameter PathParameter(string name, bool mandatory = true, string[]? aliases = null) => new()
    {
        Name = name, ClrName = char.ToLowerInvariant(name[0]) + name[1..], Type = "global::System.String", ClrType = "global::System.String",
        Mandatory = mandatory, PipelineByPropertyName = true, Aliases = aliases ?? []
    };
}
