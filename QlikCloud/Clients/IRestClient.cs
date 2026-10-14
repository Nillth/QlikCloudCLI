using Microsoft.Extensions.Logging;

namespace QlikCloud.Clients
{
    public interface IRestClient
    {
        //internal HttpClient Client { get; }
        public ILogger Logger { get; }
        bool Authenticate();
        public RestClientConfig Config { get; }
        ApiKeys.Client ApiKeys { get; }
        Apps.Client Apps { get; }
        Assistants.Client Assistants { get; }
        Audits.Client Audits { get; }
        AutomationConnections.Client AutomationConnections { get; }
        Automations.Client Automations { get; }
        AutomlDeployments.Client AutomlDeployments { get; }
        AutomlPredictions.Client AutomlPredictions { get; }
        Brands.Client Brands { get; }
        Collections.Client Collections { get; }
        Conditions.Client Conditions { get; }
        Consumption.Client Consumption { get; }
        CspOrigins.Client CspOrigins { get; }
        CsrfToken.Client CsrfToken { get; }
        DataAlerts.Client DataAlerts { get; }
        DataAssets.Client DataAssets { get; }
        DataConnections.Client DataConnections { get; }
        DataCredentials.Client DataCredentials { get; }
        DataFiles.Client DataFiles { get; }
        DataSets.Client DataSets { get; }
        DataSources.Client DataSources { get; }
        DataStores.Client DataStores { get; }
        Encryption.Client Encryption { get; }
        Extensions.Client Extensions { get; }
        Glossaries.Client Glossaries { get; }
        Groups.Client Groups { get; }
        IdentityProviders.Client IdentityProviders { get; }
        Items.Client Items { get; }
        Knowledgebases.Client Knowledgebases { get; }
        Licenses.Client Licenses { get; }
        LineageGraphs.Client LineageGraphs { get; }
        Login.Client Login { get; }
        Ml.Client Ml { get; }
        Notes.Client Notes { get; }
        Notifications.Client Notifications { get; }
        Oauth.Client Oauth { get; }
        OauthClients.Client OauthClients { get; }
        OauthTokens.Client OauthTokens { get; }
        Questions.Client Questions { get; }
        Quotas.Client Quotas { get; }
        Reloads.Client Reloads { get; }
        ReloadTasks.Client ReloadTasks { get; }
        Reports.Client Reports { get; }
        ReportTemplates.Client ReportTemplates { get; }
        Roles.Client Roles { get; }
        SharingTasks.Client SharingTasks { get; }
        Spaces.Client Spaces { get; }
        TempContents.Client TempContents { get; }
        Tenants.Client Tenants { get; }
        Themes.Client Themes { get; }
        Transports.Client Transports { get; }
        UiConfig.Client UiConfig { get; }
        Users.Client Users { get; }
        Webhooks.Client Webhooks { get; }
        WebIntegrations.Client WebIntegrations { get; }
        WebNotifications.Client WebNotifications { get; }
        WellKnown.Client WellKnown { get; }
    }
}
