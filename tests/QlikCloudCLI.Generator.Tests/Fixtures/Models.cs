namespace QlikCloud
{
    public sealed class APIResponse<T> { public T? Object { get; set; } }
}

namespace QlikCloud.Widgets
{
    public sealed class Client
    {
        public Task<APIResponse<Widget>> V1WidgetsGetAsync(string? filter = null, int? limit = null, bool? enabled = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<APIResponse<Widget>> V1WidgetsGetAsync(string widgetId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<APIResponse<Widget>> V1WidgetsPostAsync(WidgetRequest body, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<APIResponse<Widget>> V1WidgetsMeAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<APIResponse<Widget>> V1WidgetsPartsGetAsync(string widgetId, int? limit = null, bool? enabled = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<APIResponse<Widget>> V1WidgetsPartsGetAsync(string widgetId, string id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<APIResponse<Widget>> V1WidgetsFilteredAsync(string? id = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<APIResponse<Widget>> V1WidgetsAuditAsync(string id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<APIResponse<Widget>> V1WidgetsTypedListAsync(int? limit = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<APIResponse<Widget>> V1WidgetsTypedItemAsync(string widgetId, string? limit = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    public sealed class Widget { public string? Id { get; set; } }
    public sealed class WidgetRequest
    {
        public bool Enabled { get; set; }
        public int Count { get; set; }
        public string? Host { get; set; }
        public NestedModel? Child { get; set; }
        public System.Collections.Generic.ICollection<NestedModel>? Children { get; set; }
        public static WidgetRequest FromJson(string json) => new();
    }
    public sealed class NestedModel
    {
        public string? Name { get; set; }
        public static NestedModel FromJson(string json) => new();
    }

    [Obsolete("Use WidgetRequest instead.")]
    public sealed class DeprecatedRequest { public string? Name { get; set; } }

    [Obsolete]
    public sealed class DeprecatedWithoutMessageRequest { public string? Name { get; set; } }

    public sealed class DeprecatedConstructorRequest
    {
        [Obsolete("Use the replacement constructor.")]
        public DeprecatedConstructorRequest() { }
    }

    public sealed class ActiveRequestWithDeprecatedProperty
    {
        [Obsolete("Use NewName instead.")]
        public string? OldName { get; set; }
        public string? NewName { get; set; }
    }
}

namespace QlikCloud.AnalyticsWidgets
{
    public sealed class Client
    {
        public Task<APIResponse<string>> AnalyticsWidgetsGetAsync(string? filter = null, int? limit = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<APIResponse<string>> AnalyticsWidgetsGetAsync(string widgetId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<APIResponse<string>> AnalyticsWidgetsPostAsync(WidgetRequest body, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<APIResponse<string>> AnalyticsWidgetsActionsRestoreAsync(string widgetId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<APIResponse<string>> AnalyticsWidgetsEvaluationsGetAsync(string guid, int? limit = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<APIResponse<string>> AnalyticsWidgetsEvaluationsGetAsync(string id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    public sealed class WidgetRequest { public string? Name { get; set; } }
}

namespace QlikCloud.WorkflowsAutomations
{
    public sealed class Client
    {
        public Task<APIResponse<string>> WorkflowsAutomationsGetAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<APIResponse<string>> WorkflowsAutomationsPostAsync(AutomationRequest body, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    public sealed class AutomationRequest { public string? Name { get; set; } }
}

namespace QlikCloud.AnalyticsDiscoveryAgentAdaptiveCards
{
    public sealed class Client
    {
        public Task<APIResponse<string>> AnalyticsDiscoveryAgentAdaptiveCardsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
