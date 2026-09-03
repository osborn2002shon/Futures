using System.ComponentModel;
using Futures.ReadModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace FuturesMcp.Tools;

[McpServerToolType]
public static class FuturesMarketTools
{
    [McpServerTool(
        Name = "futures.health",
        Title = "Futures Health",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Checks whether the configured futures SQL read model is reachable and reports latest data freshness.")]
    public static async Task<FuturesHealthResult> GetHealthAsync(
        FuturesDashboardService dashboardService,
        CancellationToken cancellationToken)
    {
        var snapshot = await dashboardService.GetSnapshotAsync(cancellationToken);
        return new FuturesHealthResult(
            snapshot.IsConnected,
            snapshot.Message,
            snapshot.ServerTimeTaipei,
            snapshot.Quote?.Symbol,
            snapshot.Quote?.SampleMinuteTaipei,
            snapshot.Quote?.AgeSeconds,
            snapshot.LatestRecommendation?.Id,
            snapshot.LatestRecommendation?.Status);
    }

    [McpServerTool(
        Name = "futures.get_dashboard_snapshot",
        Title = "Get Futures Dashboard Snapshot",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Returns the same read-only snapshot used by the FuturesMonitor dashboard, including quote, SMA, market context, and recent recommendations.")]
    public static Task<FuturesDashboardSnapshot> GetDashboardSnapshotAsync(
        FuturesDashboardService dashboardService,
        CancellationToken cancellationToken) =>
        dashboardService.GetSnapshotAsync(cancellationToken);

    [McpServerTool(
        Name = "futures.get_latest_quote",
        Title = "Get Latest Futures Quote",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Returns the latest collected futures quote and data freshness from the SQL read model.")]
    public static async Task<LatestQuoteResult> GetLatestQuoteAsync(
        FuturesDashboardService dashboardService,
        CancellationToken cancellationToken)
    {
        var snapshot = await dashboardService.GetSnapshotAsync(cancellationToken);
        return new LatestQuoteResult(
            snapshot.IsConnected,
            snapshot.Message,
            snapshot.ServerTimeTaipei,
            snapshot.Quote);
    }

    [McpServerTool(
        Name = "futures.get_minute_k_bars",
        Title = "Get Futures Minute K Bars",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Returns recent minute K bars for the configured futures symbol. Supported intervals are 1, 5, 15, 30, and 60 minutes.")]
    public static Task<MinuteKBarsSnapshot> GetMinuteKBarsAsync(
        FuturesDashboardService dashboardService,
        [Description("K bar interval in minutes. Supported values are 1, 5, 15, 30, and 60.")]
        int intervalMinutes,
        CancellationToken cancellationToken)
    {
        if (!FuturesDashboardService.SupportedMinuteKIntervals.Contains(intervalMinutes))
        {
            throw new McpException("Unsupported intervalMinutes. Supported values are 1, 5, 15, 30, and 60.");
        }

        return dashboardService.GetMinuteKBarsAsync(intervalMinutes, cancellationToken);
    }

    [McpServerTool(
        Name = "futures.get_market_context",
        Title = "Get Futures Market Context",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Returns the latest long and short market context checks, including passed and missing strategy conditions.")]
    public static async Task<MarketContextResult> GetMarketContextAsync(
        FuturesDashboardService dashboardService,
        CancellationToken cancellationToken)
    {
        var snapshot = await dashboardService.GetSnapshotAsync(cancellationToken);
        return new MarketContextResult(
            snapshot.IsConnected,
            snapshot.Message,
            snapshot.ServerTimeTaipei,
            snapshot.CurrentSma,
            snapshot.MarketContexts);
    }

    [McpServerTool(
        Name = "futures.get_latest_recommendation",
        Title = "Get Latest Futures Recommendation",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Returns the latest futures recommendation event, preferring the active recommendation when one exists.")]
    public static async Task<LatestRecommendationResult> GetLatestRecommendationAsync(
        FuturesDashboardService dashboardService,
        CancellationToken cancellationToken)
    {
        var snapshot = await dashboardService.GetSnapshotAsync(cancellationToken);
        return new LatestRecommendationResult(
            snapshot.IsConnected,
            snapshot.Message,
            snapshot.ServerTimeTaipei,
            snapshot.LatestRecommendation);
    }

    [McpServerTool(
        Name = "futures.list_recommendations",
        Title = "List Futures Recommendations",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Returns recent futures recommendation events from newest to oldest. The limit must be between 1 and 24.")]
    public static async Task<RecommendationHistoryResult> ListRecommendationsAsync(
        FuturesDashboardService dashboardService,
        [Description("Maximum number of recommendation events to return. Must be between 1 and 24.")]
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 24)
        {
            throw new McpException("Unsupported limit. Supported values are 1 through 24.");
        }

        var snapshot = await dashboardService.GetSnapshotAsync(cancellationToken);
        return new RecommendationHistoryResult(
            snapshot.IsConnected,
            snapshot.Message,
            snapshot.ServerTimeTaipei,
            limit,
            snapshot.RecommendationHistory.Take(limit).ToArray());
    }
}

public sealed record FuturesHealthResult(
    bool IsConnected,
    string? Message,
    DateTimeOffset ServerTimeTaipei,
    string? Symbol,
    DateTimeOffset? LatestQuoteTimeTaipei,
    int? LatestQuoteAgeSeconds,
    long? LatestRecommendationId,
    string? LatestRecommendationStatus);

public sealed record LatestQuoteResult(
    bool IsConnected,
    string? Message,
    DateTimeOffset ServerTimeTaipei,
    FuturesQuoteSnapshot? Quote);

public sealed record MarketContextResult(
    bool IsConnected,
    string? Message,
    DateTimeOffset ServerTimeTaipei,
    CurrentSmaSnapshot? CurrentSma,
    IReadOnlyList<MarketContextSnapshot> MarketContexts);

public sealed record LatestRecommendationResult(
    bool IsConnected,
    string? Message,
    DateTimeOffset ServerTimeTaipei,
    EntryRecommendationSnapshot? Recommendation);

public sealed record RecommendationHistoryResult(
    bool IsConnected,
    string? Message,
    DateTimeOffset ServerTimeTaipei,
    int Limit,
    IReadOnlyList<EntryRecommendationSnapshot> Recommendations);
