using System.Globalization;

internal static class EntryRecommendationRecalculator
{
    public static async Task<RecommendationRecalculationSummary> RecalculateAsync(
        FuturesSqlStore store,
        CancellationToken cancellationToken)
    {
        var originalEvents = (await store.LoadRecommendationEventsAsync(int.MaxValue, cancellationToken))
            .OrderBy(item => item.TriggerAt)
            .ThenBy(item => item.Id)
            .ToArray();
        if (originalEvents.Length == 0)
        {
            return new RecommendationRecalculationSummary(0, 0, 0, 0, 0);
        }

        var ticks = store.GetTicksSnapshot();
        var fiveMinuteBars = store.GetBarsSnapshot(5);
        var replayEnd = GetReplayEnd(ticks, fiveMinuteBars, originalEvents);
        var results = new Dictionary<long, RecommendationReplayResult>();
        RecommendationReplayResult? activeReplay = null;

        foreach (var original in originalEvents)
        {
            if (activeReplay is not null)
            {
                activeReplay = Advance(activeReplay, original.TriggerAt, ticks, fiveMinuteBars);
                results[activeReplay.Event.Id] = activeReplay;

                if (!activeReplay.Event.IsActive)
                {
                    activeReplay = null;
                }
            }

            var confidence = CalculateConfidence(
                results.Values.Select(result => result.Event),
                original.Side,
                original.EventType);
            var draft = EntryRecommendationPriceCalculator.Build(
                ToTrigger(original),
                originalEvents,
                fiveMinuteBars,
                confidence);
            var initial = ApplyDraft(original, draft);
            var replay = new RecommendationReplayResult(
                initial,
                [CreateInitialHistory(initial)]);

            if (activeReplay is not null && replay.Event.IsActive)
            {
                activeReplay = ApplyTransition(
                    activeReplay,
                    RecommendationLifecycleEvaluator.CloseAtPrice(
                        activeReplay.Event,
                        original.EvaluatedAt,
                        GetReplacementPrice(original, ticks),
                        "Closed because a newer entry recommendation replaced it during recalculation."));
                results[activeReplay.Event.Id] = activeReplay;
                activeReplay = null;
            }

            if (replay.Event.IsActive)
            {
                replay = Advance(replay, original.TriggerAt, ticks, fiveMinuteBars);
            }

            results[original.Id] = replay;
            if (replay.Event.IsActive)
            {
                activeReplay = replay;
            }
        }

        if (activeReplay is not null)
        {
            activeReplay = Advance(activeReplay, replayEnd, ticks, fiveMinuteBars);
            results[activeReplay.Event.Id] = activeReplay;
        }

        var orderedResults = originalEvents
            .Select(original => results[original.Id])
            .ToArray();
        var updated = await store.RewriteRecommendationEventsAsync(orderedResults, cancellationToken);
        var priceChanged = orderedResults.Count(result => PriceFieldsChanged(originalEvents.First(item => item.Id == result.Event.Id), result.Event));
        var statusChanged = orderedResults.Count(result =>
        {
            var original = originalEvents.First(item => item.Id == result.Event.Id);
            return !string.Equals(original.Status, result.Event.Status, StringComparison.OrdinalIgnoreCase)
                   || original.IsActive != result.Event.IsActive
                   || original.EnteredAt != result.Event.EnteredAt
                   || original.EntryPrice != result.Event.EntryPrice
                   || original.ExitPrice != result.Event.ExitPrice
                   || original.ProfitPoints != result.Event.ProfitPoints
                   || original.CompletedAt != result.Event.CompletedAt
                   || !string.Equals(original.Outcome, result.Event.Outcome, StringComparison.OrdinalIgnoreCase);
        });
        var historyRows = orderedResults.Sum(result => result.StatusHistory.Count);

        return new RecommendationRecalculationSummary(
            originalEvents.Length,
            updated,
            priceChanged,
            statusChanged,
            historyRows);
    }

    private static Sma76Trigger ToTrigger(EntryRecommendationEvent recommendation) =>
        new(
            recommendation.Symbol,
            recommendation.EvaluatedAt,
            recommendation.TriggerAt,
            recommendation.TriggerBarStart,
            recommendation.TriggerBarEnd,
            recommendation.Side,
            recommendation.EventType,
            recommendation.TriggerRule,
            recommendation.PreviousClose,
            recommendation.PreviousSma76,
            recommendation.TriggerClose,
            recommendation.TriggerSma76,
            recommendation.TriggerSma20,
            recommendation.TriggerAtr5,
            recommendation.MarketContextSnapshot);

    private static EntryRecommendationEvent ApplyDraft(
        EntryRecommendationEvent original,
        EntryRecommendationDraft draft) =>
        original with
        {
            ReferenceEventId = draft.ReferenceEventId,
            ReferenceAtrRatio = draft.ReferenceAtrRatio,
            ReferenceAnchor = draft.ReferenceAnchor,
            ReferenceAtr5 = draft.ReferenceAtr5,
            EntryLow = draft.EntryLow,
            EntryHigh = draft.EntryHigh,
            StopLoss = draft.StopLoss,
            TakeProfit = draft.TakeProfit,
            RewardRiskRatio = draft.RewardRiskRatio,
            Status = draft.Status,
            IsActive = EntryRecommendationStatuses.IsActive(draft.Status),
            EnteredAt = null,
            EntryPrice = null,
            ExitPrice = null,
            ProfitPoints = null,
            CompletedAt = null,
            Outcome = null,
            ConfidenceStatus = draft.Confidence.Status,
            ConfidenceScore = draft.Confidence.Score,
            ConfidenceSampleCount = draft.Confidence.SampleCount,
            EntryHitRate = draft.Confidence.EntryHitRate,
            SourceMode = "recalc"
        };

    private static RecommendationReplayResult Advance(
        RecommendationReplayResult replay,
        DateTimeOffset sampleTime,
        IReadOnlyCollection<FuturesTick> ticks,
        IReadOnlyCollection<KBar> fiveMinuteBars)
    {
        var current = replay.Event;
        var history = replay.StatusHistory.ToList();

        while (current.IsActive)
        {
            var transition = RecommendationLifecycleEvaluator.Evaluate(
                current,
                ticks,
                fiveMinuteBars,
                sampleTime);
            if (transition is null)
            {
                break;
            }

            var previousStatus = current.Status;
            current = current.Apply(transition);
            history.Add(new RecommendationStatusHistoryDraft(
                transition.ChangedAt,
                previousStatus,
                transition.NewStatus,
                transition.ObservedPrice,
                transition.Note));
        }

        return replay with
        {
            Event = current,
            StatusHistory = history
        };
    }

    private static RecommendationReplayResult ApplyTransition(
        RecommendationReplayResult replay,
        RecommendationTransition transition)
    {
        var previousStatus = replay.Event.Status;
        var updated = replay.Event.Apply(transition);
        var history = replay.StatusHistory
            .Append(new RecommendationStatusHistoryDraft(
                transition.ChangedAt,
                previousStatus,
                transition.NewStatus,
                transition.ObservedPrice,
                transition.Note))
            .ToArray();

        return replay with
        {
            Event = updated,
            StatusHistory = history
        };
    }

    private static RecommendationStatusHistoryDraft CreateInitialHistory(EntryRecommendationEvent recommendation) =>
        new(
            recommendation.EvaluatedAt,
            null,
            recommendation.Status,
            recommendation.RecommendationPrice,
            $"Recalculated with structure stop logic at {DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}.");

    private static decimal GetReplacementPrice(
        EntryRecommendationEvent replacementEvent,
        IReadOnlyCollection<FuturesTick> ticks)
    {
        if (replacementEvent.RecommendationPrice is { } recommendationPrice)
        {
            return recommendationPrice;
        }

        var latestTickPrice = ticks
            .Where(tick =>
                tick.Symbol == replacementEvent.Symbol
                && MarketDataClock.GetSampleMinute(tick) <= replacementEvent.TriggerAt)
            .OrderBy(MarketDataClock.GetSampleMinute)
            .ThenBy(tick => tick.CapturedAt)
            .Select(tick => (decimal?)tick.Price)
            .LastOrDefault();

        return latestTickPrice ?? replacementEvent.TriggerClose;
    }

    private static RecommendationConfidence CalculateConfidence(
        IEnumerable<EntryRecommendationEvent> events,
        StrategySide side,
        string eventType)
    {
        var sameType = events
            .Where(item => item.Side == side && item.EventType == eventType)
            .ToArray();
        var wins = sameType.Count(item => item.Outcome == EntryRecommendationStatuses.TakeProfit);
        var losses = sameType.Count(item => item.Outcome == EntryRecommendationStatuses.StopLoss);
        var quoted = sameType.Count(item => item.EntryLow is not null && !item.IsActive);
        var entered = sameType.Count(item => item.EntryLow is not null && item.EnteredAt is not null);
        var sampleCount = wins + losses;
        decimal? entryHitRate = quoted > 0 ? (decimal)entered / quoted : null;
        var status = sampleCount switch
        {
            0 => "unavailable",
            < 5 => "insufficient",
            < 10 => "preliminary",
            _ => "normal"
        };
        decimal? score = sampleCount >= 5
            ? (wins + 2m) / (sampleCount + 4m)
            : null;

        return new RecommendationConfidence(
            status,
            score is null ? null : decimal.Round(score.Value, 4, MidpointRounding.AwayFromZero),
            sampleCount,
            entryHitRate is null
                ? null
                : decimal.Round(entryHitRate.Value, 4, MidpointRounding.AwayFromZero));
    }

    private static DateTimeOffset GetReplayEnd(
        IReadOnlyCollection<FuturesTick> ticks,
        IReadOnlyCollection<KBar> fiveMinuteBars,
        IReadOnlyCollection<EntryRecommendationEvent> events)
    {
        var tickEnd = ticks.Count == 0
            ? (DateTimeOffset?)null
            : ticks.Select(MarketDataClock.GetSampleMinute).Max();
        var barEnd = fiveMinuteBars.Count == 0
            ? (DateTimeOffset?)null
            : fiveMinuteBars.Max(bar => bar.BarEnd);
        var eventEnd = events.Max(item => item.TriggerAt);

        return new[] { tickEnd, barEnd, eventEnd }
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .Max();
    }

    private static bool PriceFieldsChanged(
        EntryRecommendationEvent original,
        EntryRecommendationEvent recalculated) =>
        original.ReferenceEventId != recalculated.ReferenceEventId
        || original.ReferenceAtrRatio != recalculated.ReferenceAtrRatio
        || original.ReferenceAnchor != recalculated.ReferenceAnchor
        || original.ReferenceAtr5 != recalculated.ReferenceAtr5
        || original.EntryLow != recalculated.EntryLow
        || original.EntryHigh != recalculated.EntryHigh
        || original.StopLoss != recalculated.StopLoss
        || original.TakeProfit != recalculated.TakeProfit
        || original.RewardRiskRatio != recalculated.RewardRiskRatio;
}

internal sealed record RecommendationReplayResult(
    EntryRecommendationEvent Event,
    IReadOnlyList<RecommendationStatusHistoryDraft> StatusHistory);

internal sealed record RecommendationStatusHistoryDraft(
    DateTimeOffset ChangedAt,
    string? PreviousStatus,
    string NewStatus,
    decimal? ObservedPrice,
    string Note);

internal sealed record RecommendationRecalculationSummary(
    int EventCount,
    int UpdatedCount,
    int PriceChangedCount,
    int StatusChangedCount,
    int StatusHistoryRowsWritten);
