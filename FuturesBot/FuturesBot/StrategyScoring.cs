using System.Globalization;

/// <summary>
/// 策略評估排程。
/// 目前主流程每分鐘收到新 tick 後都會評估；此工具只保留時間判斷輔助，避免未來需要節流時重寫。
/// </summary>
internal static class StrategyEvaluationSchedule
{
    /// <summary>
    /// 判斷 Yahoo 報價時間是否落在完整分鐘。
    /// </summary>
    public static bool IsEvaluationMinute(DateTimeOffset sampleMinute) =>
        sampleMinute.Second == 0;
}

/// <summary>
/// 市場資料時間工具。
/// 用 Yahoo 回傳的市場時間建立資料所屬分鐘，避免因 HTTP 抓取延遲讓 K 線歸到錯誤分鐘。
/// </summary>
internal static class MarketDataClock
{
    /// <summary>
    /// 取得 tick 實際代表的市場分鐘。
    /// 若 Yahoo 只提供 HH:mm:ss，會套用擷取當天日期；若推算時間比擷取時間晚超過 2 分鐘，視為跨午夜夜盤資料並往前一天。
    /// </summary>
    public static DateTimeOffset GetSampleMinute(FuturesTick tick)
    {
        if (!string.IsNullOrWhiteSpace(tick.SourceMarketTime)
            && TimeOnly.TryParseExact(
                tick.SourceMarketTime,
                ["H:mm:ss", "HH:mm:ss", "H:mm", "HH:mm"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var timeOnly))
        {
            var timeOnlySourceAt = new DateTimeOffset(
                DateOnly.FromDateTime(tick.CapturedAt.DateTime).ToDateTime(timeOnly),
                tick.CapturedAt.Offset);

            if (timeOnlySourceAt - tick.CapturedAt > TimeSpan.FromMinutes(2))
            {
                timeOnlySourceAt = timeOnlySourceAt.AddDays(-1);
            }

            return TruncateToMinute(timeOnlySourceAt);
        }

        if (!string.IsNullOrWhiteSpace(tick.SourceMarketTime)
            && DateTimeOffset.TryParse(tick.SourceMarketTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var sourceAt))
        {
            return TruncateToMinute(sourceAt);
        }

        return TruncateToMinute(tick.CapturedAt);
    }

    /// <summary>
    /// 將時間截斷到分鐘，秒數與毫秒歸零，供 1 分 K 與 5 分鐘排程判斷使用。
    /// </summary>
    public static DateTimeOffset TruncateToMinute(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, value.Offset);
}

/// <summary>
/// 進場策略參數。
/// 策略以 15 分 K 的 SMA20、SMA76 判斷趨勢與穿越，並用盤整偵測與 1 分 K SMA60 作為過濾條件。
/// </summary>
/// <param name="FastSmaPeriod">短期均線週期，預設為 SMA20。</param>
/// <param name="SlowSmaPeriod">長期均線週期，預設為 SMA76。</param>
/// <param name="TrendLookbackBars">趨勢比較回看根數，預設拿目前 K 棒與 5 根 15 分 K 前比較。</param>
/// <param name="ConsolidationRecentBars">盤整偵測的近期觀察根數；8 根 15 分 K 代表 2 小時。</param>
/// <param name="ConsolidationBaselineBars">近期區間的比較基準根數；32 根 15 分 K 代表前 8 小時。</param>
/// <param name="ConsolidationCompressionThreshold">進入盤整所需的區間壓縮比上限。</param>
/// <param name="ConsolidationDirectionEfficiencyThreshold">進入盤整所需的方向效率上限。</param>
/// <param name="ConsolidationConfirmationBars">盤整條件需要連續成立的評估根數。</param>
/// <param name="ConsolidationExitCompressionThreshold">盤整狀態因區間重新擴張而退出的壓縮比門檻。</param>
internal sealed record EntryStrategySettings(
    int FastSmaPeriod = 20,
    int SlowSmaPeriod = 76,
    int TrendLookbackBars = 5,
    int ConsolidationRecentBars = 8,
    int ConsolidationBaselineBars = 32,
    decimal ConsolidationCompressionThreshold = 0.60m,
    decimal ConsolidationDirectionEfficiencyThreshold = 0.35m,
    int ConsolidationConfirmationBars = 2,
    decimal ConsolidationExitCompressionThreshold = 0.80m)
{
    /// <summary>
    /// 預設進場策略參數。
    /// </summary>
    public static EntryStrategySettings Default { get; } = new();
}

/// <summary>
/// 15 分 K 進場策略評估器。
/// 多方與空方各有 6 個條件：SMA76 穿越、SMA76 趨勢、SMA20 趨勢、SMA20/SMA76 相對位置、非盤整、1 分 K SMA60 過濾。
/// </summary>
internal static class EntryStrategyEvaluator
{
    private const string SignalType = "entry";

    private const int TotalConditions = 6;

    /// <summary>
    /// 評估指定時間點的多方與空方進場條件。
    /// 只使用 sampleMinute 之前已完成的 15 分 K，避免未收完的 K 棒造成訊號漂移。
    /// </summary>
    /// <param name="evaluatedAt">實際執行評估的時間。</param>
    /// <param name="sampleMinute">Yahoo 報價所屬市場分鐘。</param>
    /// <param name="fifteenMinuteBars">已建立的 15 分 K 資料。</param>
    /// <param name="oneMinuteBars">已建立的 1 分 K 資料，用於 SMA60 過濾。</param>
    /// <param name="settings">策略參數；未提供時使用預設值。</param>
    public static IReadOnlyList<StrategyScore> Evaluate(
        DateTimeOffset evaluatedAt,
        DateTimeOffset sampleMinute,
        IReadOnlyCollection<KBar> fifteenMinuteBars,
        IReadOnlyCollection<KBar> oneMinuteBars,
        EntryStrategySettings? settings = null)
    {
        settings ??= EntryStrategySettings.Default;

        var completedBars = fifteenMinuteBars
            .Where(bar => bar.IntervalMinutes == 15 && bar.BarEnd <= sampleMinute)
            .OrderBy(bar => bar.BarEnd)
            .ToArray();

        if (completedBars.Length == 0)
        {
            return CreateNoCompletedBarScores(evaluatedAt, sampleMinute);
        }

        var enrichedBars = SmaSeries.Build(completedBars, settings.FastSmaPeriod, settings.SlowSmaPeriod);
        var currentIndex = enrichedBars.Length - 1;
        var consolidation = ConsolidationDetector.Evaluate(enrichedBars, currentIndex, settings);
        var oneMinuteSma60 = OneMinuteSmaFilter.Evaluate(oneMinuteBars, sampleMinute);
        var status = GetStatus(enrichedBars, currentIndex, consolidation, oneMinuteSma60, settings);

        return
        [
            EvaluateSide(
                StrategySide.Long,
                evaluatedAt,
                sampleMinute,
                enrichedBars,
                currentIndex,
                consolidation,
                oneMinuteSma60,
                status,
                settings),
            EvaluateSide(
                StrategySide.Short,
                evaluatedAt,
                sampleMinute,
                enrichedBars,
                currentIndex,
                consolidation,
                oneMinuteSma60,
                status,
                settings)
        ];
    }

    /// <summary>
    /// 評估單一方向的 6 個條件，並整理為可寫入資料庫的策略分數。
    /// </summary>
    private static StrategyScore EvaluateSide(
        StrategySide side,
        DateTimeOffset evaluatedAt,
        DateTimeOffset sampleMinute,
        IReadOnlyList<SmaKBar> bars,
        int currentIndex,
        ConsolidationResult consolidation,
        OneMinuteSmaResult oneMinuteSma60,
        string status,
        EntryStrategySettings settings)
    {
        var current = bars[currentIndex];

        SmaKBar? previous = currentIndex > 0 ? bars[currentIndex - 1] : null;
        SmaKBar? lookback = currentIndex >= settings.TrendLookbackBars ? bars[currentIndex - settings.TrendLookbackBars] : null;

        var notConsolidating = consolidation.HasEnoughData && !consolidation.IsConsolidating;

        var conditions = side == StrategySide.Long
            ? BuildLongConditions(current, previous, lookback, notConsolidating, oneMinuteSma60)
            : BuildShortConditions(current, previous, lookback, notConsolidating, oneMinuteSma60);

        var matchedConditions = conditions
            .Where(condition => condition.Passed)
            .Select(condition => condition.Name)
            .ToArray();

        var missingConditions = conditions
            .Where(condition => !condition.Passed)
            .Select(condition => condition.Name)
            .ToArray();

        return new StrategyScore(
            evaluatedAt,
            sampleMinute,
            current.Bar.BarStart,
            current.Bar.BarEnd,
            SignalType,
            side,
            matchedConditions.Length,
            TotalConditions,
            status,
            consolidation.State,
            consolidation.RangeCompressionRatio,
            consolidation.DirectionEfficiency,
            string.Join('|', matchedConditions),
            string.Join('|', missingConditions));
    }

    /// <summary>
    /// 建立多方進場條件。
    /// 條件依序為：收盤向上穿越 SMA76、SMA76 上升、SMA20 上升、SMA20 大於 SMA76、非盤整、1 分 K 收盤大於 SMA60。
    /// </summary>
    private static StrategyCondition[] BuildLongConditions(
        SmaKBar current,
        SmaKBar? previous,
        SmaKBar? lookback,
        bool notConsolidating,
        OneMinuteSmaResult oneMinuteSma60) =>
    [
        new("close_cross_above_sma76", previous?.SlowSma is not null && current.SlowSma is not null && previous.Value.Bar.Close < previous.Value.SlowSma && current.Bar.Close > current.SlowSma),
        new("sma76_gt_sma76_5_bars_ago", current.SlowSma is not null && lookback?.SlowSma is not null && current.SlowSma > lookback.Value.SlowSma),
        new("sma20_gt_sma20_5_bars_ago", current.FastSma is not null && lookback?.FastSma is not null && current.FastSma > lookback.Value.FastSma),
        new("sma20_gt_sma76", current.FastSma is not null && current.SlowSma is not null && current.FastSma > current.SlowSma),
        new("not_consolidating", notConsolidating),
        new("one_min_close_gt_sma60", oneMinuteSma60.HasEnoughData && oneMinuteSma60.Close > oneMinuteSma60.Sma60)
    ];

    /// <summary>
    /// 建立空方進場條件。
    /// 條件依序為：收盤向下穿越 SMA76、SMA76 下降、SMA20 下降、SMA20 小於 SMA76、非盤整、1 分 K 收盤小於 SMA60。
    /// </summary>
    private static StrategyCondition[] BuildShortConditions(
        SmaKBar current,
        SmaKBar? previous,
        SmaKBar? lookback,
        bool notConsolidating,
        OneMinuteSmaResult oneMinuteSma60) =>
    [
        new("close_cross_below_sma76", previous?.SlowSma is not null && current.SlowSma is not null && previous.Value.Bar.Close > previous.Value.SlowSma && current.Bar.Close < current.SlowSma),
        new("sma76_lt_sma76_5_bars_ago", current.SlowSma is not null && lookback?.SlowSma is not null && current.SlowSma < lookback.Value.SlowSma),
        new("sma20_lt_sma20_5_bars_ago", current.FastSma is not null && lookback?.FastSma is not null && current.FastSma < lookback.Value.FastSma),
        new("sma20_lt_sma76", current.FastSma is not null && current.SlowSma is not null && current.FastSma < current.SlowSma),
        new("not_consolidating", notConsolidating),
        new("one_min_close_lt_sma60", oneMinuteSma60.HasEnoughData && oneMinuteSma60.Close < oneMinuteSma60.Sma60)
    ];

    /// <summary>
    /// 沒有任何已完成 15 分 K 時，回傳多空各一筆空評分，方便資料庫保留評估紀錄。
    /// </summary>
    private static IReadOnlyList<StrategyScore> CreateNoCompletedBarScores(DateTimeOffset evaluatedAt, DateTimeOffset sampleMinute) =>
    [
        StrategyScore.Empty(evaluatedAt, sampleMinute, SignalType, StrategySide.Long, TotalConditions, "no_completed_15m_bar"),
        StrategyScore.Empty(evaluatedAt, sampleMinute, SignalType, StrategySide.Short, TotalConditions, "no_completed_15m_bar")
    ];

    /// <summary>
    /// 取得本次評估狀態。
    /// SMA76 搭配 5 根回看至少需要 81 根已完成 15 分 K；1 分 K SMA60 與盤整判斷也各自需要足夠資料。
    /// </summary>
    private static string GetStatus(
        IReadOnlyList<SmaKBar> bars,
        int currentIndex,
        ConsolidationResult consolidation,
        OneMinuteSmaResult oneMinuteSma60,
        EntryStrategySettings settings)
    {
        if (currentIndex < settings.SlowSmaPeriod + settings.TrendLookbackBars - 1)
        {
            return "insufficient_sma_data";
        }

        if (!oneMinuteSma60.HasEnoughData)
        {
            return "insufficient_1m_sma_data";
        }

        return consolidation.HasEnoughData ? "ok" : "insufficient_consolidation_data";
    }
}

/// <summary>
/// 1 分 K SMA60 過濾器。
/// 因 Yahoo 不一定每分鐘都有成交或更新，本計算會用前一筆 1 分 K 收盤價補足缺漏分鐘，再計算連續 60 分鐘的 SMA。
/// </summary>
internal static class OneMinuteSmaFilter
{
    private const int Period = 60;

    /// <summary>
    /// 評估 sampleMinute 當下的 1 分 K 收盤價與 SMA60。
    /// 若無法從 sampleMinute 往前取得可補值的 60 分鐘資料，回傳資料不足。
    /// </summary>
    public static OneMinuteSmaResult Evaluate(IReadOnlyCollection<KBar> oneMinuteBars, DateTimeOffset sampleMinute)
    {
        var firstMinute = sampleMinute.AddMinutes(-(Period - 1));
        var completedBars = oneMinuteBars
            .Where(bar => bar.IntervalMinutes == 1 && bar.BarEnd <= sampleMinute)
            .OrderBy(bar => bar.BarEnd)
            .ToArray();

        if (completedBars.Length == 0 || completedBars[0].BarEnd > firstMinute)
        {
            return OneMinuteSmaResult.Unknown;
        }

        var barIndex = 0;
        decimal? lastClose = null;

        while (barIndex < completedBars.Length && completedBars[barIndex].BarEnd <= firstMinute)
        {
            lastClose = completedBars[barIndex].Close;
            barIndex++;
        }

        if (lastClose is null)
        {
            return OneMinuteSmaResult.Unknown;
        }

        var sum = 0m;
        var minute = firstMinute;
        for (var i = 0; i < Period; i++, minute = minute.AddMinutes(1))
        {
            while (barIndex < completedBars.Length && completedBars[barIndex].BarEnd <= minute)
            {
                lastClose = completedBars[barIndex].Close;
                barIndex++;
            }

            sum += lastClose.Value;
        }

        return new OneMinuteSmaResult(true, lastClose.Value, sum / Period);
    }
}

/// <summary>
/// 盤整偵測器。
/// 盤整定義為最近兩小時價格區間相對前段行情明顯壓縮，且價格缺乏單一方向的推進效率。
/// </summary>
internal static class ConsolidationDetector
{
    /// <summary>
    /// 評估目前 15 分 K 是否處於盤整狀態。
    /// </summary>
    public static ConsolidationResult Evaluate(
        IReadOnlyList<SmaKBar> bars,
        int currentIndex,
        EntryStrategySettings settings)
    {
        if (settings.ConsolidationRecentBars <= 0
            || settings.ConsolidationBaselineBars < settings.ConsolidationRecentBars
            || settings.ConsolidationBaselineBars % settings.ConsolidationRecentBars != 0
            || settings.ConsolidationCompressionThreshold < 0m
            || settings.ConsolidationDirectionEfficiencyThreshold is < 0m or > 1m
            || settings.ConsolidationConfirmationBars <= 0
            || settings.ConsolidationExitCompressionThreshold < settings.ConsolidationCompressionThreshold)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "盤整參數無效：根數必須為正數、基準必須是近期根數的整數倍，且進出門檻必須依合理順序設定。");
        }

        var firstMeasurableIndex = settings.ConsolidationRecentBars + settings.ConsolidationBaselineBars - 1;
        if (currentIndex < firstMeasurableIndex)
        {
            return ConsolidationResult.Unknown;
        }

        var isConsolidating = false;
        var consecutiveEntrySignals = 0;
        ConsolidationMetrics currentMetrics = default;

        for (var index = firstMeasurableIndex; index <= currentIndex; index++)
        {
            currentMetrics = CalculateMetrics(bars, index, settings);

            if (isConsolidating)
            {
                var rangeExpanded = currentMetrics.RangeCompressionRatio is null
                    || currentMetrics.RangeCompressionRatio >= settings.ConsolidationExitCompressionThreshold;
                var brokePreviousRange = ClosedOutsidePreviousRange(
                    bars,
                    index,
                    settings.ConsolidationRecentBars);

                if (rangeExpanded || brokePreviousRange)
                {
                    isConsolidating = false;
                    consecutiveEntrySignals = 0;
                }

                continue;
            }

            var entrySignal = currentMetrics.RangeCompressionRatio <= settings.ConsolidationCompressionThreshold
                && currentMetrics.DirectionEfficiency <= settings.ConsolidationDirectionEfficiencyThreshold;

            consecutiveEntrySignals = entrySignal ? consecutiveEntrySignals + 1 : 0;
            if (consecutiveEntrySignals >= settings.ConsolidationConfirmationBars)
            {
                isConsolidating = true;
            }
        }

        return new ConsolidationResult(
            true,
            isConsolidating,
            currentMetrics.RangeCompressionRatio,
            currentMetrics.DirectionEfficiency);
    }

    private static ConsolidationMetrics CalculateMetrics(
        IReadOnlyList<SmaKBar> bars,
        int endIndex,
        EntryStrategySettings settings)
    {
        var recentStart = endIndex - settings.ConsolidationRecentBars + 1;
        var baselineStart = recentStart - settings.ConsolidationBaselineBars;
        var recentRange = CalculateRange(bars, recentStart, settings.ConsolidationRecentBars);

        var baselineBlockCount = settings.ConsolidationBaselineBars / settings.ConsolidationRecentBars;
        var baselineRanges = new decimal[baselineBlockCount];
        for (var blockIndex = 0; blockIndex < baselineBlockCount; blockIndex++)
        {
            baselineRanges[blockIndex] = CalculateRange(
                bars,
                baselineStart + blockIndex * settings.ConsolidationRecentBars,
                settings.ConsolidationRecentBars);
        }

        Array.Sort(baselineRanges);
        var baselineMedianRange = CalculateMedian(baselineRanges);
        decimal? compressionRatio = baselineMedianRange == 0m
            ? recentRange == 0m ? 0m : null
            : recentRange / baselineMedianRange;

        var pathLength = Math.Abs(bars[recentStart].Bar.Close - bars[recentStart].Bar.Open);
        for (var index = recentStart + 1; index <= endIndex; index++)
        {
            pathLength += Math.Abs(bars[index].Bar.Close - bars[index - 1].Bar.Close);
        }

        var directionalMove = Math.Abs(bars[endIndex].Bar.Close - bars[recentStart].Bar.Open);
        var directionEfficiency = pathLength == 0m ? 0m : directionalMove / pathLength;

        return new ConsolidationMetrics(compressionRatio, directionEfficiency);
    }

    private static decimal CalculateRange(IReadOnlyList<SmaKBar> bars, int startIndex, int count)
    {
        var highest = bars[startIndex].Bar.High;
        var lowest = bars[startIndex].Bar.Low;

        for (var index = startIndex + 1; index < startIndex + count; index++)
        {
            highest = Math.Max(highest, bars[index].Bar.High);
            lowest = Math.Min(lowest, bars[index].Bar.Low);
        }

        return highest - lowest;
    }

    private static decimal CalculateMedian(IReadOnlyList<decimal> sortedValues)
    {
        var middle = sortedValues.Count / 2;
        return sortedValues.Count % 2 == 0
            ? (sortedValues[middle - 1] + sortedValues[middle]) / 2m
            : sortedValues[middle];
    }

    private static bool ClosedOutsidePreviousRange(
        IReadOnlyList<SmaKBar> bars,
        int currentIndex,
        int recentBars)
    {
        var previousRangeStart = currentIndex - recentBars;
        var previousHighest = bars[previousRangeStart].Bar.High;
        var previousLowest = bars[previousRangeStart].Bar.Low;

        for (var index = previousRangeStart + 1; index < currentIndex; index++)
        {
            previousHighest = Math.Max(previousHighest, bars[index].Bar.High);
            previousLowest = Math.Min(previousLowest, bars[index].Bar.Low);
        }

        var currentClose = bars[currentIndex].Bar.Close;
        return currentClose > previousHighest || currentClose < previousLowest;
    }

    private readonly record struct ConsolidationMetrics(
        decimal? RangeCompressionRatio,
        decimal DirectionEfficiency);
}

/// <summary>
/// SMA 序列計算工具。
/// 用 K 棒收盤價計算短期 SMA20 與長期 SMA76。
/// </summary>
internal static class SmaSeries
{
    /// <summary>
    /// 為每根 K 棒補上短期與長期 SMA。
    /// 資料筆數不足以計算指定週期時，該 SMA 會是 null。
    /// </summary>
    public static SmaKBar[] Build(IReadOnlyList<KBar> bars, int fastPeriod, int slowPeriod)
    {
        var enriched = new SmaKBar[bars.Count];

        for (var i = 0; i < bars.Count; i++)
        {
            enriched[i] = new SmaKBar(
                bars[i],
                CalculateSma(bars, i, fastPeriod),
                CalculateSma(bars, i, slowPeriod));
        }

        return enriched;
    }

    /// <summary>
    /// 計算指定 index 結尾、長度為 period 的簡單移動平均；資料不足時回傳 null。
    /// </summary>
    private static decimal? CalculateSma(IReadOnlyList<KBar> bars, int index, int period)
    {
        if (index + 1 < period)
        {
            return null;
        }

        var sum = 0m;
        for (var i = index - period + 1; i <= index; i++)
        {
            sum += bars[i].Close;
        }

        return sum / period;
    }
}

/// <summary>
/// 單一策略條件的判斷結果。
/// Name 是穩定識別碼，Passed 代表該條件是否成立。
/// </summary>
internal readonly record struct StrategyCondition(string Name, bool Passed);

/// <summary>
/// 補上 SMA 後的 K 棒資料。
/// FastSma 對應 SMA20，SlowSma 對應 SMA76。
/// </summary>
internal readonly record struct SmaKBar(KBar Bar, decimal? FastSma, decimal? SlowSma);

/// <summary>
/// 1 分 K SMA60 過濾結果。
/// HasEnoughData 為 false 時代表 1 分 K 資料不足，不應使用 Close 與 Sma60 判斷方向。
/// </summary>
internal readonly record struct OneMinuteSmaResult(bool HasEnoughData, decimal? Close, decimal? Sma60)
{
    /// <summary>
    /// 1 分 K 資料不足 60 分鐘時使用的結果。
    /// </summary>
    public static OneMinuteSmaResult Unknown => new(false, null, null);
}

/// <summary>
/// 盤整判斷結果。
/// HasEnoughData 為 false 時代表尚無法判斷盤整狀態，不應視為盤整或非盤整。
/// </summary>
internal readonly record struct ConsolidationResult(
    bool HasEnoughData,
    bool IsConsolidating,
    decimal? RangeCompressionRatio,
    decimal? DirectionEfficiency)
{
    /// <summary>
    /// 盤整資料不足時使用的結果。
    /// </summary>
    public static ConsolidationResult Unknown => new(false, false, null, null);

    /// <summary>
    /// 輸出到資料庫的盤整狀態文字：true、false 或 unknown。
    /// </summary>
    public string State => HasEnoughData ? IsConsolidating.ToString().ToLowerInvariant() : "unknown";
}

/// <summary>
/// 策略評分結果。
/// 每次 5 分鐘評估會各產生一筆多方與空方分數；SignalType 目前固定為 entry，未來可擴充 exit。
/// </summary>
internal readonly record struct StrategyScore(
    DateTimeOffset EvaluatedAt,
    DateTimeOffset SampleTime,
    DateTimeOffset? BarStart,
    DateTimeOffset? BarEnd,
    string SignalType,
    StrategySide Side,
    int MatchedCount,
    int TotalCount,
    string Status,
    string ConsolidationState,
    decimal? ConsolidationRangeCompressionRatio,
    decimal? ConsolidationDirectionEfficiency,
    string MatchedConditions,
    string MissingConditions)
{
    /// <summary>
    /// 建立資料不足或無 K 棒時的空評分結果。
    /// </summary>
    public static StrategyScore Empty(
        DateTimeOffset evaluatedAt,
        DateTimeOffset sampleTime,
        string signalType,
        StrategySide side,
        int totalConditions,
        string status) =>
        new(
            evaluatedAt,
            sampleTime,
            null,
            null,
            signalType,
            side,
            0,
            totalConditions,
            status,
            "unknown",
            null,
            null,
            "",
            "");
}

/// <summary>
/// 盤勢參考資料只在內容改變時寫入；此鍵不參與自動入場決策。
/// </summary>
internal static class StrategyScoreStateKey
{
    private const string ConsolidationMetricVersion = "range_v2";

    public static string Build(StrategyScore score) =>
        string.Join(
            '|',
            score.Side.ToString().ToLowerInvariant(),
            score.MatchedCount.ToString(CultureInfo.InvariantCulture),
            score.TotalCount.ToString(CultureInfo.InvariantCulture),
            score.Status,
            score.ConsolidationState,
            score.ConsolidationDirectionEfficiency is null ? "consolidation_metrics_unknown" : ConsolidationMetricVersion,
            score.MatchedConditions,
            score.MissingConditions);
}

/// <summary>
/// 策略方向。
/// Long 代表多方進場，Short 代表空方進場。
/// </summary>
internal enum StrategySide
{
    Long,
    Short
}
