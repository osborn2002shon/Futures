using System.Globalization;

internal sealed record EntryRecommendationSettings(
    int AtrPeriod = 14,
    int WaitingFiveMinuteBars = 12,
    int TrackingFiveMinuteBars = 24,
    int MinimumReferenceFiveMinuteBars = 6,
    decimal MinimumAtrRatio = 0.5m,
    decimal MaximumAtrRatio = 2.0m,
    decimal EntryLowPercentile = 0.25m,
    decimal EntryHighPercentile = 0.50m,
    decimal StopMaePercentile = 0.90m,
    decimal TakeProfitMfePercentile = 0.60m,
    decimal MinimumRewardRiskRatio = 1.5m)
{
    public static EntryRecommendationSettings Default { get; } = new();
}

internal static class EntryRecommendationStatuses
{
    public const string TriggeredNoPrice = "triggered_no_price";
    public const string InsufficientPriceData = "insufficient_price_data";
    public const string NoReferenceEvent = "no_reference_event";
    public const string InvalidPriceStructure = "invalid_price_structure";
    public const string WaitingEntry = "waiting_entry";
    public const string Entered = "entered";
    public const string TakeProfit = "take_profit";
    public const string StopLoss = "stop_loss";
    public const string Expired = "expired";
    public const string TimedOut = "timed_out";
    public const string Cancelled = "cancelled";
    public const string SuppressedByActiveRecommendation = "suppressed_active_recommendation";

    public static bool IsActive(string status) => status is WaitingEntry or Entered;
}

internal sealed record Sma76Trigger(
    string Symbol,
    DateTimeOffset EvaluatedAt,
    DateTimeOffset SampleTime,
    DateTimeOffset TriggerBarStart,
    DateTimeOffset TriggerBarEnd,
    StrategySide Side,
    string EventType,
    string TriggerRule,
    decimal PreviousClose,
    decimal PreviousSma76,
    decimal TriggerClose,
    decimal TriggerSma76,
    decimal? TriggerSma20,
    decimal? TriggerAtr5,
    string MarketContextSnapshot);

internal static class Sma76TriggerEvaluator
{
    public static Sma76Trigger? Evaluate(
        string symbol,
        DateTimeOffset evaluatedAt,
        DateTimeOffset sampleTime,
        IReadOnlyCollection<KBar> fifteenMinuteBars,
        IReadOnlyCollection<KBar> fiveMinuteBars,
        IReadOnlyList<StrategyScore> marketContexts,
        EntryRecommendationSettings? settings = null)
    {
        settings ??= EntryRecommendationSettings.Default;

        var completedBars = fifteenMinuteBars
            .Where(bar => bar.IntervalMinutes == 15 && bar.BarEnd <= sampleTime)
            .OrderBy(bar => bar.BarEnd)
            .ToArray();
        if (completedBars.Length < 2)
        {
            return null;
        }

        var enriched = SmaSeries.Build(completedBars, 20, 76);
        var previous = enriched[^2];
        var current = enriched[^1];
        if (previous.SlowSma is not { } previousSma76 || current.SlowSma is not { } currentSma76)
        {
            return null;
        }

        var side = GetCrossSide(previous.Bar.Close, previousSma76, current.Bar.Close, currentSma76);
        if (side is null)
        {
            return null;
        }

        var triggerRule = side == StrategySide.Long
            ? "15m_close_cross_above_sma76"
            : "15m_close_cross_below_sma76";
        var atr5 = AtrCalculator.CalculateLatest(fiveMinuteBars, sampleTime, settings.AtrPeriod);

        return new Sma76Trigger(
            symbol,
            evaluatedAt,
            sampleTime,
            current.Bar.BarStart,
            current.Bar.BarEnd,
            side.Value,
            "reversal",
            triggerRule,
            previous.Bar.Close,
            previousSma76,
            current.Bar.Close,
            currentSma76,
            current.FastSma,
            atr5,
            BuildMarketContextSnapshot(marketContexts));
    }

    private static StrategySide? GetCrossSide(
        decimal previousClose,
        decimal previousSma76,
        decimal currentClose,
        decimal currentSma76)
    {
        if (previousClose <= previousSma76 && currentClose > currentSma76)
        {
            return StrategySide.Long;
        }

        if (previousClose >= previousSma76 && currentClose < currentSma76)
        {
            return StrategySide.Short;
        }

        return null;
    }

    private static string BuildMarketContextSnapshot(IEnumerable<StrategyScore> scores) =>
        string.Join(
            ';',
            scores.Select(score => string.Join(
                ':',
                score.Side.ToString().ToLowerInvariant(),
                $"{score.MatchedCount}/{score.TotalCount}",
                score.Status,
                score.ConsolidationState,
                score.MatchedConditions)));
}

internal static class AtrCalculator
{
    public static decimal? CalculateLatest(
        IReadOnlyCollection<KBar> bars,
        DateTimeOffset sampleTime,
        int period)
    {
        var completed = bars
            .Where(bar => bar.IntervalMinutes == 5 && bar.BarEnd <= sampleTime)
            .OrderBy(bar => bar.BarEnd)
            .ToArray();
        if (completed.Length < period + 1)
        {
            return null;
        }

        var ranges = new decimal[period];
        var start = completed.Length - period;
        for (var i = start; i < completed.Length; i++)
        {
            var bar = completed[i];
            var previousClose = completed[i - 1].Close;
            ranges[i - start] = Max(
                bar.High - bar.Low,
                Math.Abs(bar.High - previousClose),
                Math.Abs(bar.Low - previousClose));
        }

        var atr = ranges.Average();
        return atr > 0 ? atr : null;
    }

    private static decimal Max(decimal first, decimal second, decimal third) =>
        Math.Max(first, Math.Max(second, third));
}

internal sealed record EntryRecommendationEvent(
    long Id,
    string Symbol,
    DateTimeOffset EvaluatedAt,
    DateTimeOffset TriggerAt,
    DateTimeOffset TriggerBarStart,
    DateTimeOffset TriggerBarEnd,
    StrategySide Side,
    string EventType,
    string TriggerRule,
    decimal PreviousClose,
    decimal PreviousSma76,
    decimal TriggerClose,
    decimal TriggerSma76,
    decimal? TriggerSma20,
    decimal? TriggerAtr5,
    string MarketContextSnapshot,
    long? ReferenceEventId,
    decimal? ReferenceAtrRatio,
    decimal? ReferenceAnchor,
    decimal? ReferenceAtr5,
    decimal? EntryLow,
    decimal? EntryHigh,
    decimal? StopLoss,
    decimal? TakeProfit,
    decimal? RewardRiskRatio,
    string Status,
    bool IsActive,
    DateTimeOffset? EnteredAt,
    decimal? EntryPrice,
    DateTimeOffset? CompletedAt,
    string? Outcome,
    string ConfidenceStatus,
    decimal? ConfidenceScore,
    int ConfidenceSampleCount,
    decimal? EntryHitRate,
    string SourceMode)
{
    public static EntryRecommendationEvent FromDraft(
        Sma76Trigger trigger,
        EntryRecommendationDraft draft) =>
        new(
            0,
            trigger.Symbol,
            trigger.EvaluatedAt,
            trigger.TriggerBarEnd,
            trigger.TriggerBarStart,
            trigger.TriggerBarEnd,
            trigger.Side,
            trigger.EventType,
            trigger.TriggerRule,
            trigger.PreviousClose,
            trigger.PreviousSma76,
            trigger.TriggerClose,
            trigger.TriggerSma76,
            trigger.TriggerSma20,
            trigger.TriggerAtr5,
            trigger.MarketContextSnapshot,
            draft.ReferenceEventId,
            draft.ReferenceAtrRatio,
            draft.ReferenceAnchor,
            draft.ReferenceAtr5,
            draft.EntryLow,
            draft.EntryHigh,
            draft.StopLoss,
            draft.TakeProfit,
            draft.RewardRiskRatio,
            draft.Status,
            EntryRecommendationStatuses.IsActive(draft.Status),
            null,
            null,
            null,
            null,
            draft.Confidence.Status,
            draft.Confidence.Score,
            draft.Confidence.SampleCount,
            draft.Confidence.EntryHitRate,
            "live");

    public EntryRecommendationEvent Apply(RecommendationTransition transition) =>
        this with
        {
            Status = transition.NewStatus,
            IsActive = EntryRecommendationStatuses.IsActive(transition.NewStatus),
            EnteredAt = transition.EnteredAt ?? EnteredAt,
            EntryPrice = transition.EntryPrice ?? EntryPrice,
            CompletedAt = transition.CompletedAt ?? CompletedAt,
            Outcome = transition.Outcome ?? Outcome
        };
}

internal sealed record EntryRecommendationDraft(
    string Status,
    long? ReferenceEventId,
    decimal? ReferenceAtrRatio,
    decimal? ReferenceAnchor,
    decimal? ReferenceAtr5,
    decimal? EntryLow,
    decimal? EntryHigh,
    decimal? StopLoss,
    decimal? TakeProfit,
    decimal? RewardRiskRatio,
    RecommendationConfidence Confidence);

internal sealed record RecommendationConfidence(
    string Status,
    decimal? Score,
    int SampleCount,
    decimal? EntryHitRate)
{
    public static RecommendationConfidence Unavailable { get; } =
        new("unavailable", null, 0, null);
}

internal sealed record ReferenceEventPattern(
    decimal EntryZ25,
    decimal EntryZ50,
    decimal StopMae90,
    decimal TakeProfitMfe60);

internal static class EntryRecommendationPriceCalculator
{
    public static EntryRecommendationDraft Build(
        Sma76Trigger trigger,
        IReadOnlyList<EntryRecommendationEvent> previousEvents,
        IReadOnlyCollection<KBar> fiveMinuteBars,
        RecommendationConfidence confidence,
        EntryRecommendationSettings? settings = null)
    {
        settings ??= EntryRecommendationSettings.Default;
        if (trigger.TriggerAtr5 is not { } currentAtr || currentAtr <= 0)
        {
            return Empty(EntryRecommendationStatuses.InsufficientPriceData, confidence);
        }

        var foundPattern = false;
        foreach (var reference in previousEvents
                     .Where(item =>
                         item.Side == trigger.Side
                         && item.EventType == trigger.EventType
                         && item.TriggerAt < trigger.TriggerBarEnd
                         && item.TriggerAtr5 is > 0)
                     .OrderByDescending(item => item.TriggerAt))
        {
            var historicalAtr = reference.TriggerAtr5!.Value;
            var atrRatio = currentAtr / historicalAtr;
            if (atrRatio < settings.MinimumAtrRatio || atrRatio > settings.MaximumAtrRatio)
            {
                continue;
            }

            if (!ReferenceEventPatternBuilder.TryBuild(
                    reference,
                    previousEvents,
                    fiveMinuteBars,
                    trigger.TriggerBarEnd,
                    settings,
                    out var pattern))
            {
                continue;
            }

            foundPattern = true;
            var side = trigger.Side == StrategySide.Long ? 1m : -1m;
            var entryA = RoundPoint(trigger.TriggerSma76 + side * pattern.EntryZ25 * currentAtr);
            var entryB = RoundPoint(trigger.TriggerSma76 + side * pattern.EntryZ50 * currentAtr);
            var entryLow = Math.Min(entryA, entryB);
            var entryHigh = Math.Max(entryA, entryB);
            var entryMid = (entryLow + entryHigh) / 2m;
            var stopLoss = RoundPoint(entryMid - side * pattern.StopMae90 * currentAtr);
            var takeProfit = RoundPoint(entryMid + side * pattern.TakeProfitMfe60 * currentAtr);
            var riskPoints = Math.Abs(entryMid - stopLoss);
            var rewardPoints = Math.Abs(takeProfit - entryMid);
            var rewardRiskRatio = riskPoints > 0 ? rewardPoints / riskPoints : 0m;

            if (!IsValidPriceStructure(
                    trigger.Side,
                    entryLow,
                    entryHigh,
                    stopLoss,
                    takeProfit,
                    rewardRiskRatio,
                    settings.MinimumRewardRiskRatio))
            {
                continue;
            }

            return new EntryRecommendationDraft(
                EntryRecommendationStatuses.WaitingEntry,
                reference.Id,
                decimal.Round(atrRatio, 4, MidpointRounding.AwayFromZero),
                reference.TriggerSma76,
                historicalAtr,
                entryLow,
                entryHigh,
                stopLoss,
                takeProfit,
                decimal.Round(rewardRiskRatio, 4, MidpointRounding.AwayFromZero),
                confidence);
        }

        return Empty(
            foundPattern
                ? EntryRecommendationStatuses.InvalidPriceStructure
                : EntryRecommendationStatuses.NoReferenceEvent,
            confidence);
    }

    public static EntryRecommendationDraft Suppressed(RecommendationConfidence confidence) =>
        Empty(EntryRecommendationStatuses.SuppressedByActiveRecommendation, confidence);

    private static EntryRecommendationDraft Empty(string status, RecommendationConfidence confidence) =>
        new(status, null, null, null, null, null, null, null, null, null, confidence);

    private static bool IsValidPriceStructure(
        StrategySide side,
        decimal entryLow,
        decimal entryHigh,
        decimal stopLoss,
        decimal takeProfit,
        decimal rewardRiskRatio,
        decimal minimumRewardRiskRatio)
    {
        if (rewardRiskRatio < minimumRewardRiskRatio)
        {
            return false;
        }

        return side == StrategySide.Long
            ? stopLoss < entryLow && entryHigh < takeProfit
            : takeProfit < entryLow && entryHigh < stopLoss;
    }

    private static decimal RoundPoint(decimal value) =>
        decimal.Round(value, 0, MidpointRounding.AwayFromZero);
}

internal static class ReferenceEventPatternBuilder
{
    public static bool TryBuild(
        EntryRecommendationEvent reference,
        IReadOnlyList<EntryRecommendationEvent> allEvents,
        IReadOnlyCollection<KBar> fiveMinuteBars,
        DateTimeOffset currentTriggerAt,
        EntryRecommendationSettings settings,
        out ReferenceEventPattern pattern)
    {
        pattern = default!;
        if (reference.TriggerAtr5 is not { } atr || atr <= 0)
        {
            return false;
        }

        var sessionEnd = TaiwanFuturesMarketHours.GetSessionEnd(reference.TriggerBarEnd);
        if (sessionEnd is null)
        {
            return false;
        }

        var nextOppositeTrigger = allEvents
            .Where(item => item.TriggerAt > reference.TriggerAt && item.Side != reference.Side)
            .OrderBy(item => item.TriggerAt)
            .Select(item => (DateTimeOffset?)item.TriggerAt)
            .FirstOrDefault();
        var eventEnd = Min(sessionEnd.Value, currentTriggerAt);
        if (nextOppositeTrigger is { } oppositeAt)
        {
            eventEnd = Min(eventEnd, oppositeAt);
        }

        var eventBars = fiveMinuteBars
            .Where(bar =>
                bar.IntervalMinutes == 5
                && bar.BarStart >= reference.TriggerBarEnd
                && bar.BarEnd <= eventEnd)
            .OrderBy(bar => bar.BarStart)
            .ToArray();
        var waitingBars = eventBars
            .Take(settings.WaitingFiveMinuteBars)
            .ToArray();
        if (waitingBars.Length < settings.MinimumReferenceFiveMinuteBars)
        {
            return false;
        }

        // 多空都轉換成「數值越小，回檔越有利」的標準化距離，才能共用百分位規則。
        var side = reference.Side == StrategySide.Long ? 1m : -1m;
        var entryDistances = waitingBars
            .Select(bar =>
            {
                var favorablePrice = reference.Side == StrategySide.Long ? bar.Low : bar.High;
                return side * (favorablePrice - reference.TriggerSma76) / atr;
            })
            .ToArray();
        var entryZ25 = Percentile(entryDistances, settings.EntryLowPercentile);
        var entryZ50 = Percentile(entryDistances, settings.EntryHighPercentile);
        var referenceEntryA = reference.TriggerSma76 + side * entryZ25 * atr;
        var referenceEntryB = reference.TriggerSma76 + side * entryZ50 * atr;
        var referenceEntryLow = Math.Min(referenceEntryA, referenceEntryB);
        var referenceEntryHigh = Math.Max(referenceEntryA, referenceEntryB);
        var referenceEntryMid = (referenceEntryLow + referenceEntryHigh) / 2m;
        var entryBar = waitingBars.FirstOrDefault(bar =>
            bar.Low <= referenceEntryHigh && bar.High >= referenceEntryLow);
        if (entryBar == default)
        {
            return false;
        }

        var trackingBars = eventBars
            .Where(bar => bar.BarStart >= entryBar.BarStart)
            .Take(settings.TrackingFiveMinuteBars)
            .ToArray();
        if (trackingBars.Length == 0)
        {
            return false;
        }

        var adverse = trackingBars
            .Select(bar => reference.Side == StrategySide.Long
                ? Math.Max(0m, referenceEntryMid - bar.Low) / atr
                : Math.Max(0m, bar.High - referenceEntryMid) / atr)
            .ToArray();
        var favorable = trackingBars
            .Select(bar => reference.Side == StrategySide.Long
                ? Math.Max(0m, bar.High - referenceEntryMid) / atr
                : Math.Max(0m, referenceEntryMid - bar.Low) / atr)
            .ToArray();
        var stopMae90 = Percentile(adverse, settings.StopMaePercentile);
        var takeProfitMfe60 = Percentile(favorable, settings.TakeProfitMfePercentile);
        if (stopMae90 <= 0 || takeProfitMfe60 <= 0)
        {
            return false;
        }

        pattern = new ReferenceEventPattern(entryZ25, entryZ50, stopMae90, takeProfitMfe60);
        return true;
    }

    private static decimal Percentile(IReadOnlyCollection<decimal> values, decimal percentile)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        if (ordered.Length == 1)
        {
            return ordered[0];
        }

        var position = (ordered.Length - 1) * percentile;
        var lowerIndex = (int)Math.Floor(position);
        var upperIndex = (int)Math.Ceiling(position);
        if (lowerIndex == upperIndex)
        {
            return ordered[lowerIndex];
        }

        var fraction = position - lowerIndex;
        return ordered[lowerIndex] + (ordered[upperIndex] - ordered[lowerIndex]) * fraction;
    }

    private static DateTimeOffset Min(DateTimeOffset first, DateTimeOffset second) =>
        first <= second ? first : second;
}

internal sealed record RecommendationTransition(
    string NewStatus,
    DateTimeOffset ChangedAt,
    decimal? ObservedPrice,
    DateTimeOffset? EnteredAt,
    decimal? EntryPrice,
    DateTimeOffset? CompletedAt,
    string? Outcome,
    string Note);

internal static class RecommendationLifecycleEvaluator
{
    public static RecommendationTransition? Evaluate(
        EntryRecommendationEvent active,
        IReadOnlyCollection<FuturesTick> ticks,
        IReadOnlyCollection<KBar> fiveMinuteBars,
        DateTimeOffset sampleTime,
        EntryRecommendationSettings? settings = null)
    {
        settings ??= EntryRecommendationSettings.Default;
        return active.Status switch
        {
            EntryRecommendationStatuses.WaitingEntry =>
                EvaluateWaiting(active, ticks, fiveMinuteBars, sampleTime, settings),
            EntryRecommendationStatuses.Entered =>
                EvaluateEntered(active, ticks, fiveMinuteBars, sampleTime, settings),
            _ => null
        };
    }

    public static RecommendationTransition CancelForOppositeTrigger(
        EntryRecommendationEvent active,
        Sma76Trigger oppositeTrigger) =>
        new(
            EntryRecommendationStatuses.Cancelled,
            oppositeTrigger.TriggerBarEnd,
            oppositeTrigger.TriggerClose,
            null,
            null,
            oppositeTrigger.TriggerBarEnd,
            EntryRecommendationStatuses.Cancelled,
            "入場前出現反方向 SMA76 突破／跌破事件");

    private static RecommendationTransition? EvaluateWaiting(
        EntryRecommendationEvent active,
        IReadOnlyCollection<FuturesTick> ticks,
        IReadOnlyCollection<KBar> fiveMinuteBars,
        DateTimeOffset sampleTime,
        EntryRecommendationSettings settings)
    {
        if (active.EntryLow is not { } entryLow || active.EntryHigh is not { } entryHigh)
        {
            return null;
        }

        var entryTick = ticks
            .Where(tick =>
                tick.Symbol == active.Symbol
                && tick.CapturedAt >= active.TriggerAt
                && tick.CapturedAt <= sampleTime
                && tick.Price >= entryLow
                && tick.Price <= entryHigh)
            .OrderBy(tick => tick.CapturedAt)
            .FirstOrDefault();
        if (entryTick != default)
        {
            return new RecommendationTransition(
                EntryRecommendationStatuses.Entered,
                entryTick.CapturedAt,
                entryTick.Price,
                entryTick.CapturedAt,
                entryTick.Price,
                null,
                null,
                "即時報價首次進入建議區間");
        }

        var completedWaitingBars = CountCompletedBars(
            fiveMinuteBars,
            active.TriggerBarEnd,
            sampleTime);
        var sessionEnd = TaiwanFuturesMarketHours.GetSessionEnd(active.TriggerAt);
        if (completedWaitingBars >= settings.WaitingFiveMinuteBars
            || sessionEnd is { } end && sampleTime >= end)
        {
            var completedAt = sessionEnd is { } close && close < sampleTime ? close : sampleTime;
            return new RecommendationTransition(
                EntryRecommendationStatuses.Expired,
                completedAt,
                null,
                null,
                null,
                completedAt,
                EntryRecommendationStatuses.Expired,
                "等待 12 根 5 分 K 或至當盤收盤仍未入場");
        }

        return null;
    }

    private static RecommendationTransition? EvaluateEntered(
        EntryRecommendationEvent active,
        IReadOnlyCollection<FuturesTick> ticks,
        IReadOnlyCollection<KBar> fiveMinuteBars,
        DateTimeOffset sampleTime,
        EntryRecommendationSettings settings)
    {
        if (active.EnteredAt is not { } enteredAt
            || active.StopLoss is not { } stopLoss
            || active.TakeProfit is not { } takeProfit)
        {
            return null;
        }

        // 逐分鐘報價依時間排序，確保停損與停利只採用第一個真正碰到的結果。
        foreach (var tick in ticks
                     .Where(tick =>
                         tick.Symbol == active.Symbol
                         && tick.CapturedAt >= enteredAt
                         && tick.CapturedAt <= sampleTime)
                     .OrderBy(tick => tick.CapturedAt))
        {
            var stopHit = active.Side == StrategySide.Long
                ? tick.Price <= stopLoss
                : tick.Price >= stopLoss;
            var takeProfitHit = active.Side == StrategySide.Long
                ? tick.Price >= takeProfit
                : tick.Price <= takeProfit;

            if (stopHit)
            {
                return Complete(
                    EntryRecommendationStatuses.StopLoss,
                    tick.CapturedAt,
                    tick.Price,
                    "即時報價先碰到停損");
            }

            if (takeProfitHit)
            {
                return Complete(
                    EntryRecommendationStatuses.TakeProfit,
                    tick.CapturedAt,
                    tick.Price,
                    "即時報價先碰到停利");
            }
        }

        var completedTrackingBars = CountCompletedBars(fiveMinuteBars, enteredAt, sampleTime);
        var sessionEnd = TaiwanFuturesMarketHours.GetSessionEnd(active.TriggerAt);
        if (completedTrackingBars >= settings.TrackingFiveMinuteBars
            || sessionEnd is { } end && sampleTime >= end)
        {
            var completedAt = sessionEnd is { } close && close < sampleTime ? close : sampleTime;
            return Complete(
                EntryRecommendationStatuses.TimedOut,
                completedAt,
                null,
                "入場後追蹤 24 根 5 分 K 或至當盤收盤仍未完成");
        }

        return null;
    }

    private static RecommendationTransition Complete(
        string status,
        DateTimeOffset changedAt,
        decimal? observedPrice,
        string note) =>
        new(status, changedAt, observedPrice, null, null, changedAt, status, note);

    private static int CountCompletedBars(
        IReadOnlyCollection<KBar> fiveMinuteBars,
        DateTimeOffset start,
        DateTimeOffset sampleTime) =>
        fiveMinuteBars.Count(bar =>
            bar.IntervalMinutes == 5
            && bar.BarEnd > start
            && bar.BarEnd <= sampleTime);
}

internal sealed record RecommendationChange(
    EntryRecommendationEvent Recommendation,
    string? PreviousStatus,
    DateTimeOffset ChangedAt,
    decimal? ObservedPrice,
    string Note);

