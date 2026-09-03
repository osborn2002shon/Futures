using System.Globalization;
using System.Text;

internal sealed class StrategyTextReporter
{
    private readonly string _dataDirectory;

    public StrategyTextReporter(string dataDirectory)
    {
        _dataDirectory = dataDirectory;
    }

    public async Task AppendStrategyScoresAsync(
        IEnumerable<StrategyScore> scores,
        CancellationToken cancellationToken)
    {
        var scoreArray = scores.ToArray();
        if (scoreArray.Length == 0)
        {
            return;
        }

        Directory.CreateDirectory(_dataDirectory);
        var path = Path.Combine(_dataDirectory, "strategy_score_15m_v2.txt");
        await EnsureHeaderAsync(
            path,
            "evaluated_at_taipei,sample_time_taipei,bar_start_taipei,bar_end_taipei,signal_type,side,matched_count,total_count,status,consolidation_state,consolidation_range_compression_ratio,consolidation_direction_efficiency,matched_conditions,missing_conditions",
            cancellationToken);

        var lines = scoreArray.Select(score => string.Join(
            ',',
            FormatDateTime(score.EvaluatedAt),
            FormatDateTime(score.SampleTime),
            FormatNullableDateTime(score.BarStart),
            FormatNullableDateTime(score.BarEnd),
            score.SignalType,
            score.Side.ToString().ToLowerInvariant(),
            score.MatchedCount.ToString(CultureInfo.InvariantCulture),
            score.TotalCount.ToString(CultureInfo.InvariantCulture),
            score.Status,
            score.ConsolidationState,
            FormatNullableDecimal(score.ConsolidationRangeCompressionRatio),
            FormatNullableDecimal(score.ConsolidationDirectionEfficiency),
            score.MatchedConditions,
            score.MissingConditions));

        await File.AppendAllLinesAsync(path, lines, Encoding.UTF8, cancellationToken);
    }

    public async Task AppendRecommendationChangesAsync(
        IEnumerable<RecommendationChange> changes,
        CancellationToken cancellationToken)
    {
        var changeArray = changes.ToArray();
        if (changeArray.Length == 0)
        {
            return;
        }

        Directory.CreateDirectory(_dataDirectory);
        var path = Path.Combine(_dataDirectory, "entry_recommendation_events.txt");
        await EnsureHeaderAsync(
            path,
            "event_id,changed_at_taipei,previous_status,status,trigger_at_taipei,side,event_type,trigger_rule,reference_event_id,reference_atr_ratio,entry_low,entry_high,stop_loss,take_profit,reward_risk_ratio,entered_at_taipei,entry_price,exit_price,profit_points,completed_at_taipei,outcome,confidence_status,confidence_score,confidence_sample_count,entry_hit_rate,observed_price,note,recommendation_price",
            cancellationToken);

        var lines = changeArray.Select(change =>
        {
            var recommendation = change.Recommendation;
            return string.Join(
                ',',
                recommendation.Id.ToString(CultureInfo.InvariantCulture),
                FormatDateTime(change.ChangedAt),
                Escape(change.PreviousStatus),
                recommendation.Status,
                FormatDateTime(recommendation.TriggerAt),
                recommendation.Side.ToString().ToLowerInvariant(),
                recommendation.EventType,
                recommendation.TriggerRule,
                recommendation.ReferenceEventId?.ToString(CultureInfo.InvariantCulture) ?? "",
                FormatNullableDecimal(recommendation.ReferenceAtrRatio),
                FormatNullableDecimal(recommendation.EntryLow),
                FormatNullableDecimal(recommendation.EntryHigh),
                FormatNullableDecimal(recommendation.StopLoss),
                FormatNullableDecimal(recommendation.TakeProfit),
                FormatNullableDecimal(recommendation.RewardRiskRatio),
                FormatNullableDateTime(recommendation.EnteredAt),
                FormatNullableDecimal(recommendation.EntryPrice),
                FormatNullableDecimal(recommendation.ExitPrice),
                FormatNullableDecimal(recommendation.ProfitPoints),
                FormatNullableDateTime(recommendation.CompletedAt),
                Escape(recommendation.Outcome),
                recommendation.ConfidenceStatus,
                FormatNullableDecimal(recommendation.ConfidenceScore),
                recommendation.ConfidenceSampleCount.ToString(CultureInfo.InvariantCulture),
                FormatNullableDecimal(recommendation.EntryHitRate),
                FormatNullableDecimal(change.ObservedPrice),
                Escape(change.Note),
                FormatNullableDecimal(recommendation.RecommendationPrice));
        });

        await File.AppendAllLinesAsync(path, lines, Encoding.UTF8, cancellationToken);
    }

    private static async Task EnsureHeaderAsync(
        string path,
        string header,
        CancellationToken cancellationToken)
    {
        if (File.Exists(path) && new FileInfo(path).Length > 0)
        {
            return;
        }

        await File.WriteAllTextAsync(path, header + Environment.NewLine, Encoding.UTF8, cancellationToken);
    }

    private static string FormatDateTime(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

    private static string FormatNullableDateTime(DateTimeOffset? value) =>
        value is { } dateTime ? FormatDateTime(dateTime) : "";

    private static string FormatNullableDecimal(decimal? value) =>
        value?.ToString("0.####", CultureInfo.InvariantCulture) ?? "";

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
