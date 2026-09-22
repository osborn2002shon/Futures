using System.ComponentModel;
using Futures.ReadModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace FuturesMcp.Resources;

[McpServerResourceType]
public static class FuturesResources
{
    [McpServerResource(
        UriTemplate = "futures://service/info",
        Name = "futures.service.info",
        Title = "FuturesMonitor Service Info",
        MimeType = "application/json")]
    [Description("Service URL, related links, and client guidance. " + FuturesServiceInfo.ClientGuidance)]
    public static string GetServiceInfo() =>
        FuturesMcpJson.Serialize(FuturesServiceInfo.CreateReference());

    [McpServerResource(
        UriTemplate = "futures://dashboard/current",
        Name = "futures.dashboard.current",
        Title = "Current Futures Dashboard",
        MimeType = "application/json")]
    [Description("Current dashboard snapshot for the configured futures symbol, including ServiceInfo. " + FuturesServiceInfo.ClientGuidance)]
    public static async Task<string> GetCurrentDashboardAsync(
        FuturesDashboardService dashboardService,
        CancellationToken cancellationToken)
    {
        var snapshot = await dashboardService.GetSnapshotAsync(cancellationToken);
        var resource = new
        {
            snapshot.IsConnected,
            snapshot.Message,
            snapshot.ServerTimeTaipei,
            snapshot.Quote,
            snapshot.CurrentSma,
            snapshot.QuoteHistory,
            snapshot.MarketContexts,
            snapshot.LatestRecommendation,
            snapshot.RecommendationHistory,
            ServiceInfo = FuturesServiceInfo.CreateReference()
        };

        return FuturesMcpJson.Serialize(resource);
    }

    [McpServerResource(
        UriTemplate = "futures://recommendations/latest",
        Name = "futures.recommendations.latest",
        Title = "Latest Futures Recommendation",
        MimeType = "application/json")]
    [Description("Latest recommendation event for the configured futures symbol, preferring an active event when present. Includes ServiceInfo. " + FuturesServiceInfo.ClientGuidance)]
    public static async Task<string> GetLatestRecommendationAsync(
        FuturesDashboardService dashboardService,
        CancellationToken cancellationToken)
    {
        var snapshot = await dashboardService.GetSnapshotAsync(cancellationToken);
        var resource = new
        {
            snapshot.IsConnected,
            snapshot.Message,
            snapshot.ServerTimeTaipei,
            Recommendation = snapshot.LatestRecommendation,
            ServiceInfo = FuturesServiceInfo.CreateReference()
        };

        return FuturesMcpJson.Serialize(resource);
    }

    [McpServerResource(
        UriTemplate = "futures://strategy/current-calculation",
        Name = "futures.strategy.current_calculation",
        Title = "Current Futures Strategy Calculation",
        MimeType = "text/markdown")]
    [Description("Markdown documentation that describes the current strategy calculation and recommendation lifecycle. " + FuturesServiceInfo.ClientGuidance)]
    public static async Task<string> GetCurrentStrategyCalculationAsync(
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        var strategyDocumentPath = Path.GetFullPath(Path.Combine(
            environment.ContentRootPath,
            "..",
            "docs",
            "current-strategy-calculation.md"));

        if (!File.Exists(strategyDocumentPath))
        {
            throw new McpException("Strategy calculation document was not found.");
        }

        var strategyDocument = await File.ReadAllTextAsync(strategyDocumentPath, cancellationToken);
        return $"""
            {FuturesServiceInfo.RealTimeQuotesMessage}

            {strategyDocument}
            """;
    }
}
