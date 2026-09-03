using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Futures.ReadModel;

public sealed class FuturesDashboardService(IOptions<FuturesDataOptions> options, ILogger<FuturesDashboardService> logger)
{
    private const int QuoteHistoryPointCount = 180;
    private const int MarketContextHistoryRowsPerSide = 500;
    private const int RecommendationHistoryRows = 24;
    private const int MinuteKBarsWindowHours = 24;
    private const int CurrentSmaIntervalMinutes = 15;
    private const int FastSmaPeriod = 20;
    private const int SlowSmaPeriod = 76;

    public static readonly IReadOnlySet<int> SupportedMinuteKIntervals = new HashSet<int> { 1, 5, 15, 30, 60 };

    private static readonly TimeZoneInfo TaipeiTimeZone = GetTaipeiTimeZone();

    private static readonly IReadOnlyDictionary<string, string[]> ConditionOrderBySide =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["long"] =
            [
                "close_cross_above_sma76",
                "sma76_gt_sma76_5_bars_ago",
                "sma20_gt_sma20_5_bars_ago",
                "fifteen_min_close_gt_sma20",
                "sma20_gt_sma76",
                "not_consolidating",
                "one_min_close_gt_sma60"
            ],
            ["short"] =
            [
                "close_cross_below_sma76",
                "sma76_lt_sma76_5_bars_ago",
                "sma20_lt_sma20_5_bars_ago",
                "fifteen_min_close_lt_sma20",
                "sma20_lt_sma76",
                "not_consolidating",
                "one_min_close_lt_sma60"
            ]
        };

    private static readonly IReadOnlyDictionary<string, string> ConditionLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["close_cross_above_sma76"] = "15分K收盤突破 SMA76",
            ["close_cross_below_sma76"] = "15分K收盤跌破 SMA76",
            ["sma76_gt_sma76_5_bars_ago"] = "SMA76 高於 5 根前",
            ["sma76_lt_sma76_5_bars_ago"] = "SMA76 低於 5 根前",
            ["sma20_gt_sma20_5_bars_ago"] = "SMA20 高於 5 根前",
            ["sma20_lt_sma20_5_bars_ago"] = "SMA20 低於 5 根前",
            ["fifteen_min_close_gt_sma20"] = "15分K收盤位於 SMA20 上方",
            ["fifteen_min_close_lt_sma20"] = "15分K收盤位於 SMA20 下方",
            ["sma20_gt_sma76"] = "SMA20 位於 SMA76 上方",
            ["sma20_lt_sma76"] = "SMA20 位於 SMA76 下方",
            ["not_consolidating"] = "目前非盤整",
            ["one_min_close_gt_sma60"] = "1分K收盤位於 SMA60 上方",
            ["one_min_close_lt_sma60"] = "1分K收盤位於 SMA60 下方"
        };

    private static readonly IReadOnlyDictionary<string, string> RecommendationStatusLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["triggered_no_price"] = "觸發待計算",
            ["insufficient_price_data"] = "5分K資料不足",
            ["no_reference_event"] = "尚無同向參考事件",
            ["invalid_price_structure"] = "價格結構無效",
            ["waiting_entry"] = "等待進入區間",
            ["entered"] = "已進入建議區間",
            ["take_profit"] = "先觸發停利",
            ["stop_loss"] = "先觸發停損",
            ["expired"] = "等待入場逾期",
            ["timed_out"] = "入場後追蹤逾時",
            ["cancelled"] = "反向突破取消",
            ["suppressed_active_recommendation"] = "已有進行中建議"
        };

    private static readonly IReadOnlyDictionary<string, string> ConfidenceLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["unavailable"] = "尚無樣本",
            ["insufficient"] = "樣本不足",
            ["preliminary"] = "初步信心",
            ["normal"] = "正式信心"
        };

    private readonly FuturesDataOptions _options = options.Value;

    public async Task<FuturesDashboardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var serverTime = GetTaipeiNow();

        try
        {
            await using var connection = new SqlConnection(_options.BuildConnectionString());
            await connection.OpenAsync(cancellationToken);

            var quote = await ReadLatestQuoteAsync(connection, serverTime, cancellationToken);
            var currentSma = await ReadCurrentSmaAsync(
                connection,
                quote?.SampleMinuteTaipei ?? serverTime,
                cancellationToken);
            var quoteHistory = await ReadQuoteHistoryAsync(connection, cancellationToken);
            var marketContexts = await ReadLatestMarketContextsAsync(
                connection,
                quote?.SampleMinuteTaipei ?? serverTime,
                cancellationToken);
            var latestRecommendation = await ReadLatestRecommendationAsync(connection, cancellationToken);
            var recommendationHistory = await ReadRecommendationHistoryAsync(connection, cancellationToken);

            return new FuturesDashboardSnapshot(
                true,
                null,
                serverTime,
                quote,
                currentSma,
                quoteHistory,
                marketContexts,
                latestRecommendation,
                recommendationHistory);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Unable to read futures dashboard data.");

            return new FuturesDashboardSnapshot(
                false,
                $"無法讀取 SQL 資料：{ex.Message}",
                serverTime,
                null,
                null,
                [],
                [],
                null,
                []);
        }
    }

    public async Task<MinuteKBarsSnapshot> GetMinuteKBarsAsync(int intervalMinutes, CancellationToken cancellationToken)
    {
        var generatedAtTaipei = GetTaipeiNow();
        var toTaipei = generatedAtTaipei;
        var fromTaipei = generatedAtTaipei.AddHours(-MinuteKBarsWindowHours);

        try
        {
            await using var connection = new SqlConnection(_options.BuildConnectionString());
            await connection.OpenAsync(cancellationToken);

            var latestDataTime = await ReadLatestMinuteKBarDataTimeAsync(connection, intervalMinutes, cancellationToken);
            if (latestDataTime is null)
            {
                return new MinuteKBarsSnapshot(
                    true,
                    null,
                    _options.Symbol,
                    intervalMinutes,
                    generatedAtTaipei,
                    fromTaipei,
                    toTaipei,
                    []);
            }

            toTaipei = latestDataTime.Value;
            fromTaipei = toTaipei.AddHours(-MinuteKBarsWindowHours);

            var bars = intervalMinutes == 1
                ? await ReadOneMinuteKBarsAsync(connection, fromTaipei, toTaipei, cancellationToken)
                : await ReadPersistedMinuteKBarsAsync(connection, intervalMinutes, fromTaipei, toTaipei, cancellationToken);

            return new MinuteKBarsSnapshot(
                true,
                null,
                _options.Symbol,
                intervalMinutes,
                generatedAtTaipei,
                fromTaipei,
                toTaipei,
                bars);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Unable to read futures minute K bars.");

            return new MinuteKBarsSnapshot(
                false,
                $"Unable to read SQL data: {ex.Message}",
                _options.Symbol,
                intervalMinutes,
                generatedAtTaipei,
                fromTaipei,
                toTaipei,
                []);
        }
    }

    private async Task<DateTimeOffset?> ReadLatestMinuteKBarDataTimeAsync(
        SqlConnection connection,
        int intervalMinutes,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        if (intervalMinutes == 1)
        {
            command.CommandText = """
                SELECT TOP (1) SampleMinuteTaipei
                FROM dbo.FuturesTicks
                WHERE Symbol = @symbol
                ORDER BY SampleMinuteTaipei DESC, CapturedAtTaipei DESC, Id DESC;
                """;
        }
        else
        {
            command.CommandText = """
                SELECT TOP (1) BarEndTaipei
                FROM dbo.FuturesKBars
                WHERE Symbol = @symbol
                  AND IntervalMinutes = @intervalMinutes
                ORDER BY BarEndTaipei DESC, UpdatedAtTaipei DESC, BarStartTaipei DESC;
                """;
            command.Parameters.Add("@intervalMinutes", SqlDbType.Int).Value = intervalMinutes;
        }

        AddStringParameter(command, "@symbol", _options.Symbol, 32);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? reader.GetDateTimeOffset(0)
            : null;
    }

    private async Task<IReadOnlyList<MinuteKBarSnapshot>> ReadOneMinuteKBarsAsync(
        SqlConnection connection,
        DateTimeOffset fromTaipei,
        DateTimeOffset toTaipei,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                SampleMinuteTaipei,
                Price,
                CapturedAtTaipei
            FROM dbo.FuturesTicks
            WHERE Symbol = @symbol
              AND SampleMinuteTaipei >= @fromTaipei
              AND SampleMinuteTaipei <= @toTaipei
            ORDER BY SampleMinuteTaipei, CapturedAtTaipei, Id;
            """;
        AddStringParameter(command, "@symbol", _options.Symbol, 32);
        AddDateTimeOffsetParameter(command, "@fromTaipei", fromTaipei);
        AddDateTimeOffsetParameter(command, "@toTaipei", toTaipei);

        var bars = new List<MinuteKBarSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var sampleMinute = reader.GetDateTimeOffset(0);
            var price = reader.GetDecimal(1);
            var capturedAt = reader.GetDateTimeOffset(2);
            var barStart = GetMinuteCloseBarStart(sampleMinute.DateTime, 1)
                ?? sampleMinute.DateTime.AddMinutes(-1);
            var barEnd = barStart.AddMinutes(1);

            bars.Add(new MinuteKBarSnapshot(
                new DateTimeOffset(barStart, TaipeiTimeZone.GetUtcOffset(barStart)),
                new DateTimeOffset(barEnd, TaipeiTimeZone.GetUtcOffset(barEnd)),
                price,
                price,
                price,
                price,
                1,
                capturedAt));
        }

        return bars;
    }

    private async Task<IReadOnlyList<MinuteKBarSnapshot>> ReadPersistedMinuteKBarsAsync(
        SqlConnection connection,
        int intervalMinutes,
        DateTimeOffset fromTaipei,
        DateTimeOffset toTaipei,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                BarStartTaipei,
                BarEndTaipei,
                OpenPrice,
                HighPrice,
                LowPrice,
                ClosePrice,
                SourceCount,
                UpdatedAtTaipei
            FROM dbo.FuturesKBars
            WHERE Symbol = @symbol
              AND IntervalMinutes = @intervalMinutes
              AND BarEndTaipei >= @fromTaipei
              AND BarStartTaipei <= @toTaipei
            ORDER BY BarStartTaipei;
            """;
        AddStringParameter(command, "@symbol", _options.Symbol, 32);
        command.Parameters.Add("@intervalMinutes", SqlDbType.Int).Value = intervalMinutes;
        AddDateTimeOffsetParameter(command, "@fromTaipei", fromTaipei);
        AddDateTimeOffsetParameter(command, "@toTaipei", toTaipei);

        var bars = new List<MinuteKBarSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            bars.Add(new MinuteKBarSnapshot(
                reader.GetDateTimeOffset(0),
                reader.GetDateTimeOffset(1),
                reader.GetDecimal(2),
                reader.GetDecimal(3),
                reader.GetDecimal(4),
                reader.GetDecimal(5),
                reader.GetInt32(6),
                reader.GetDateTimeOffset(7)));
        }

        return bars;
    }

    private async Task<CurrentSmaSnapshot?> ReadCurrentSmaAsync(
        SqlConnection connection,
        DateTimeOffset sampleMinuteTaipei,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (@barCount)
                Symbol,
                BarStartTaipei,
                BarEndTaipei,
                ClosePrice,
                UpdatedAtTaipei
            FROM dbo.FuturesKBars
            WHERE Symbol = @symbol
              AND IntervalMinutes = @intervalMinutes
              AND BarEndTaipei <= @sampleMinuteTaipei
            ORDER BY BarEndTaipei DESC, UpdatedAtTaipei DESC, BarStartTaipei DESC;
            """;
        command.Parameters.Add("@barCount", SqlDbType.Int).Value = SlowSmaPeriod;
        AddStringParameter(command, "@symbol", _options.Symbol, 32);
        command.Parameters.Add("@intervalMinutes", SqlDbType.Int).Value = CurrentSmaIntervalMinutes;
        AddDateTimeOffsetParameter(command, "@sampleMinuteTaipei", sampleMinuteTaipei);

        var rows = new List<SmaBarRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new SmaBarRow(
                reader.GetString(0),
                reader.GetDateTimeOffset(1),
                reader.GetDateTimeOffset(2),
                reader.GetDecimal(3),
                reader.GetDateTimeOffset(4)));
        }

        if (rows.Count == 0)
        {
            return null;
        }

        var latest = rows[0];
        return new CurrentSmaSnapshot(
            latest.Symbol,
            CurrentSmaIntervalMinutes,
            latest.BarStartTaipei,
            latest.BarEndTaipei,
            latest.ClosePrice,
            CalculateSma(rows, FastSmaPeriod),
            CalculateSma(rows, SlowSmaPeriod),
            rows.Count,
            latest.UpdatedAtTaipei);
    }

    private async Task<FuturesQuoteSnapshot?> ReadLatestQuoteAsync(
        SqlConnection connection,
        DateTimeOffset serverTime,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (1)
                Symbol,
                CapturedAtTaipei,
                SampleMinuteTaipei,
                SourceMarketTime,
                Price,
                SourceUrl
            FROM dbo.FuturesTicks
            WHERE Symbol = @symbol
            ORDER BY SampleMinuteTaipei DESC, CapturedAtTaipei DESC, Id DESC;
            """;
        AddStringParameter(command, "@symbol", _options.Symbol, 32);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var current = new QuoteRow(
            reader.GetString(0),
            reader.GetDateTimeOffset(1),
            reader.GetDateTimeOffset(2),
            await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetString(3),
            reader.GetDecimal(4),
            reader.GetString(5));
        await reader.CloseAsync();

        var referenceClose = await ReadPreviousDaySessionCloseAsync(
            connection,
            current.SampleMinuteTaipei,
            cancellationToken);
        decimal? priceChange = null;
        decimal? priceChangePercent = null;
        if (referenceClose is not null)
        {
            priceChange = current.Price - referenceClose.Price;
            priceChangePercent = referenceClose.Price == 0
                ? null
                : priceChange / referenceClose.Price * 100m;
        }

        return new FuturesQuoteSnapshot(
            current.Symbol,
            current.Price,
            priceChange,
            priceChangePercent,
            current.CapturedAtTaipei,
            current.SampleMinuteTaipei,
            current.SourceMarketTime,
            current.SourceUrl,
            (int)Math.Round((serverTime - current.SampleMinuteTaipei).TotalSeconds),
            referenceClose?.Price,
            referenceClose?.SampleMinuteTaipei);
    }

    private async Task<IReadOnlyList<QuoteHistoryPointSnapshot>> ReadQuoteHistoryAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT SampleMinuteTaipei, Price
            FROM
            (
                SELECT TOP (@pointCount)
                    SampleMinuteTaipei,
                    Price,
                    CapturedAtTaipei,
                    Id
                FROM dbo.FuturesTicks
                WHERE Symbol = @symbol
                ORDER BY SampleMinuteTaipei DESC, CapturedAtTaipei DESC, Id DESC
            ) AS recent
            ORDER BY SampleMinuteTaipei, CapturedAtTaipei, Id;
            """;
        AddStringParameter(command, "@symbol", _options.Symbol, 32);
        command.Parameters.Add("@pointCount", SqlDbType.Int).Value = QuoteHistoryPointCount;

        var points = new List<QuoteHistoryPointSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            points.Add(new QuoteHistoryPointSnapshot(
                reader.GetDateTimeOffset(0),
                reader.GetDecimal(1)));
        }

        return points;
    }

    private async Task<ReferenceCloseRow?> ReadPreviousDaySessionCloseAsync(
        SqlConnection connection,
        DateTimeOffset sampleMinute,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (1)
                Price,
                SampleMinuteTaipei
            FROM dbo.FuturesTicks
            WHERE Symbol = @symbol
              AND SampleMinuteTaipei <= @daySessionCloseCutoff
              AND CONVERT(time(0), SampleMinuteTaipei) >= CONVERT(time(0), '08:45:00')
              AND CONVERT(time(0), SampleMinuteTaipei) <= CONVERT(time(0), '13:45:00')
            ORDER BY SampleMinuteTaipei DESC, CapturedAtTaipei DESC, Id DESC;
            """;
        AddStringParameter(command, "@symbol", _options.Symbol, 32);
        AddDateTimeOffsetParameter(
            command,
            "@daySessionCloseCutoff",
            GetPreviousCompletedDaySessionCloseCutoff(sampleMinute));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ReferenceCloseRow(reader.GetDecimal(0), reader.GetDateTimeOffset(1))
            : null;
    }

    private static DateTimeOffset GetPreviousCompletedDaySessionCloseCutoff(DateTimeOffset sampleMinute)
    {
        var daySessionClose = new DateTimeOffset(
            sampleMinute.Year,
            sampleMinute.Month,
            sampleMinute.Day,
            13,
            45,
            0,
            sampleMinute.Offset);
        return sampleMinute <= daySessionClose
            ? daySessionClose.AddDays(-1)
            : daySessionClose;
    }

    private static async Task<IReadOnlyList<MarketContextSnapshot>> ReadLatestMarketContextsAsync(
        SqlConnection connection,
        DateTimeOffset durationReferenceTime,
        CancellationToken cancellationToken)
    {
        var hasCompressionRatio = await ColumnExistsAsync(
            connection,
            "dbo.StrategyScores",
            "ConsolidationRangeCompressionRatio",
            cancellationToken);
        var hasDirectionEfficiency = await ColumnExistsAsync(
            connection,
            "dbo.StrategyScores",
            "ConsolidationDirectionEfficiency",
            cancellationToken);
        var compressionRatioColumn = hasCompressionRatio
            ? "ConsolidationRangeCompressionRatio"
            : "CAST(NULL AS decimal(18,6)) AS ConsolidationRangeCompressionRatio";
        var directionEfficiencyColumn = hasDirectionEfficiency
            ? "ConsolidationDirectionEfficiency"
            : "CAST(NULL AS decimal(18,6)) AS ConsolidationDirectionEfficiency";

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH RankedScores AS
            (
                SELECT
                    Side,
                    EvaluatedAtTaipei,
                    SampleTimeTaipei,
                    BarStartTaipei,
                    BarEndTaipei,
                    Status,
                    ConsolidationState,
                    {compressionRatioColumn},
                    {directionEfficiencyColumn},
                    MatchedConditions,
                    MissingConditions,
                    TotalCount,
                    ROW_NUMBER() OVER
                    (
                        PARTITION BY Side
                        ORDER BY SampleTimeTaipei DESC, EvaluatedAtTaipei DESC, Id DESC
                    ) AS RowNumber
                FROM dbo.StrategyScores
                WHERE SignalType = N'entry'
            )
            SELECT
                Side,
                EvaluatedAtTaipei,
                SampleTimeTaipei,
                BarStartTaipei,
                BarEndTaipei,
                Status,
                ConsolidationState,
                ConsolidationRangeCompressionRatio,
                ConsolidationDirectionEfficiency,
                MatchedConditions,
                MissingConditions,
                TotalCount,
                RowNumber
            FROM RankedScores
            WHERE RowNumber <= @historyRowsPerSide
            ORDER BY CASE LOWER(Side) WHEN N'long' THEN 0 WHEN N'short' THEN 1 ELSE 2 END,
                     RowNumber;
            """;
        command.Parameters.Add("@historyRowsPerSide", SqlDbType.Int).Value = MarketContextHistoryRowsPerSide;

        var rowsBySide = new Dictionary<string, List<StrategyScoreHistoryRow>>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var side = reader.GetString(0).ToLowerInvariant();
            if (!rowsBySide.TryGetValue(side, out var sideRows))
            {
                sideRows = [];
                rowsBySide[side] = sideRows;
            }

            sideRows.Add(new StrategyScoreHistoryRow(
                side,
                reader.GetDateTimeOffset(1),
                reader.GetDateTimeOffset(2),
                await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetDateTimeOffset(3),
                await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetDateTimeOffset(4),
                reader.GetString(5),
                reader.GetString(6),
                await reader.IsDBNullAsync(7, cancellationToken) ? null : reader.GetDecimal(7),
                await reader.IsDBNullAsync(8, cancellationToken) ? null : reader.GetDecimal(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetInt32(11)));
        }

        return rowsBySide
            .OrderBy(group => group.Key.Equals("long", StringComparison.OrdinalIgnoreCase) ? 0 :
                              group.Key.Equals("short", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .Select(group => BuildMarketContextSnapshot(group.Value, durationReferenceTime))
            .ToArray();
    }

    private static MarketContextSnapshot BuildMarketContextSnapshot(
        IReadOnlyList<StrategyScoreHistoryRow> rows,
        DateTimeOffset durationReferenceTime)
    {
        var latest = rows[0];
        var durations = CalculateConditionDurations(rows, durationReferenceTime);

        return new MarketContextSnapshot(
            latest.Side,
            GetSideLabel(latest.Side),
            latest.EvaluatedAtTaipei,
            latest.SampleTimeTaipei,
            latest.BarStartTaipei,
            latest.BarEndTaipei,
            latest.Status,
            GetMarketContextStatusLabel(latest.Status),
            latest.ConsolidationState,
            GetConsolidationLabel(latest.ConsolidationState),
            latest.ConsolidationRangeCompressionRatio,
            latest.ConsolidationDirectionEfficiency,
            BuildConditionSnapshots(
                latest.Side,
                latest.MatchedConditions,
                latest.MissingConditions,
                latest.TotalCount,
                durations));
    }

    private async Task<EntryRecommendationSnapshot?> ReadLatestRecommendationAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "dbo.EntryRecommendationEvents", cancellationToken))
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT TOP (1)
                {RecommendationSelectColumns}
            FROM dbo.EntryRecommendationEvents AS events
            LEFT JOIN dbo.EntryRecommendationEvents AS reference
                ON reference.Id = events.ReferenceEventId
            WHERE events.Symbol = @symbol
            ORDER BY events.IsActive DESC, events.TriggerAtTaipei DESC, events.Id DESC;
            """;
        AddStringParameter(command, "@symbol", _options.Symbol, 32);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? await ReadRecommendationAsync(reader, cancellationToken)
            : null;
    }

    private async Task<IReadOnlyList<EntryRecommendationSnapshot>> ReadRecommendationHistoryAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "dbo.EntryRecommendationEvents", cancellationToken))
        {
            return [];
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT TOP (@rowCount)
                {RecommendationSelectColumns}
            FROM dbo.EntryRecommendationEvents AS events
            LEFT JOIN dbo.EntryRecommendationEvents AS reference
                ON reference.Id = events.ReferenceEventId
            WHERE events.Symbol = @symbol
            ORDER BY events.TriggerAtTaipei DESC, events.Id DESC;
            """;
        AddStringParameter(command, "@symbol", _options.Symbol, 32);
        command.Parameters.Add("@rowCount", SqlDbType.Int).Value = RecommendationHistoryRows;

        var recommendations = new List<EntryRecommendationSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            recommendations.Add(await ReadRecommendationAsync(reader, cancellationToken));
        }

        return recommendations;
    }

    private static async Task<EntryRecommendationSnapshot> ReadRecommendationAsync(
        SqlDataReader reader,
        CancellationToken cancellationToken)
    {
        var side = reader.GetString(6).ToLowerInvariant();
        var status = reader.GetString(25);
        var outcome = await reader.IsDBNullAsync(30, cancellationToken) ? null : reader.GetString(30);
        var confidenceStatus = reader.GetString(31);
        var referenceSide = await reader.IsDBNullAsync(38, cancellationToken)
            ? null
            : reader.GetString(38).ToLowerInvariant();
        var referenceStatus = await reader.IsDBNullAsync(41, cancellationToken)
            ? null
            : reader.GetString(41);

        return new EntryRecommendationSnapshot(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetDateTimeOffset(2),
            reader.GetDateTimeOffset(3),
            reader.GetDateTimeOffset(4),
            reader.GetDateTimeOffset(5),
            side,
            GetSideLabel(side),
            reader.GetString(7),
            reader.GetString(8),
            GetTriggerRuleLabel(reader.GetString(8)),
            reader.GetDecimal(9),
            reader.GetDecimal(10),
            reader.GetDecimal(11),
            reader.GetDecimal(12),
            await reader.IsDBNullAsync(13, cancellationToken) ? null : reader.GetDecimal(13),
            await reader.IsDBNullAsync(14, cancellationToken) ? null : reader.GetDecimal(14),
            await reader.IsDBNullAsync(16, cancellationToken) ? null : reader.GetInt64(16),
            await reader.IsDBNullAsync(37, cancellationToken) ? null : reader.GetDateTimeOffset(37),
            referenceSide,
            referenceSide is null ? null : GetSideLabel(referenceSide),
            await reader.IsDBNullAsync(39, cancellationToken) ? null : reader.GetDecimal(39),
            await reader.IsDBNullAsync(40, cancellationToken) ? null : reader.GetDecimal(40),
            referenceStatus,
            referenceStatus is null ? null : GetRecommendationStatusLabel(referenceStatus),
            await reader.IsDBNullAsync(17, cancellationToken) ? null : reader.GetDecimal(17),
            await reader.IsDBNullAsync(20, cancellationToken) ? null : reader.GetDecimal(20),
            await reader.IsDBNullAsync(21, cancellationToken) ? null : reader.GetDecimal(21),
            await reader.IsDBNullAsync(22, cancellationToken) ? null : reader.GetDecimal(22),
            await reader.IsDBNullAsync(23, cancellationToken) ? null : reader.GetDecimal(23),
            await reader.IsDBNullAsync(24, cancellationToken) ? null : reader.GetDecimal(24),
            status,
            GetRecommendationStatusLabel(status),
            reader.GetBoolean(26),
            await reader.IsDBNullAsync(27, cancellationToken) ? null : reader.GetDateTimeOffset(27),
            await reader.IsDBNullAsync(28, cancellationToken) ? null : reader.GetDecimal(28),
            await reader.IsDBNullAsync(42, cancellationToken) ? null : reader.GetDecimal(42),
            await reader.IsDBNullAsync(43, cancellationToken) ? null : reader.GetDecimal(43),
            await reader.IsDBNullAsync(29, cancellationToken) ? null : reader.GetDateTimeOffset(29),
            outcome,
            GetOutcomeLabel(outcome),
            confidenceStatus,
            GetConfidenceLabel(confidenceStatus),
            await reader.IsDBNullAsync(32, cancellationToken) ? null : reader.GetDecimal(32),
            reader.GetInt32(33),
            await reader.IsDBNullAsync(34, cancellationToken) ? null : reader.GetDecimal(34),
            await reader.IsDBNullAsync(35, cancellationToken) ? null : reader.GetDecimal(35),
            reader.GetString(36));
    }

    private static IReadOnlyList<MarketConditionSnapshot> BuildConditionSnapshots(
        string side,
        string matchedConditions,
        string missingConditions,
        int totalCount,
        IReadOnlyDictionary<string, ConditionDurationSnapshot> durations)
    {
        var matched = SplitConditions(matchedConditions);
        var missing = SplitConditions(missingConditions);
        var knownOrder = ConditionOrderBySide.GetValueOrDefault(side, []);
        var storedKeys = matched
            .Concat(missing)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var keys = storedKeys.Length == 0 && totalCount > 0
            ? knownOrder.Take(Math.Min(totalCount, knownOrder.Length)).ToArray()
            : knownOrder
                .Where(key => storedKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                .Concat(storedKeys.Where(key => !knownOrder.Contains(key, StringComparer.OrdinalIgnoreCase)))
                .ToArray();

        return keys
            .Select(key =>
            {
                durations.TryGetValue(key, out var duration);
                return new MarketConditionSnapshot(
                    key,
                    ConditionLabels.GetValueOrDefault(key, key),
                    matched.Contains(key),
                    key is "close_cross_above_sma76" or "close_cross_below_sma76",
                    duration?.PassedSinceTaipei,
                    duration?.PassedDurationSeconds,
                    duration?.PreviousPassedDurationSeconds);
            })
            .ToArray();
    }

    private static IReadOnlyDictionary<string, ConditionDurationSnapshot> CalculateConditionDurations(
        IReadOnlyList<StrategyScoreHistoryRow> rows,
        DateTimeOffset durationReferenceTime)
    {
        var conditionKeys = rows
            .SelectMany(row => SplitConditions(row.MatchedConditions).Concat(SplitConditions(row.MissingConditions)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var durations = new Dictionary<string, ConditionDurationSnapshot>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in conditionKeys)
        {
            durations[key] = CalculateConditionDuration(rows, key, durationReferenceTime);
        }

        return durations;
    }

    private static ConditionDurationSnapshot CalculateConditionDuration(
        IReadOnlyList<StrategyScoreHistoryRow> rows,
        string conditionKey,
        DateTimeOffset durationReferenceTime)
    {
        var latestReferenceTime = durationReferenceTime >= rows[0].SampleTimeTaipei
            ? durationReferenceTime
            : rows[0].SampleTimeTaipei;
        var passedSegments = new List<PassedConditionSegment>();
        var index = 0;

        while (index < rows.Count)
        {
            var segmentPassed = HasMatchedCondition(rows[index], conditionKey);
            var segmentEnd = index == 0 ? latestReferenceTime : rows[index - 1].SampleTimeTaipei;

            while (index + 1 < rows.Count
                   && HasMatchedCondition(rows[index + 1], conditionKey) == segmentPassed)
            {
                index++;
            }

            var segmentStart = rows[index].SampleTimeTaipei;
            if (segmentPassed && segmentEnd >= segmentStart)
            {
                passedSegments.Add(new PassedConditionSegment(segmentStart, segmentEnd));
            }

            index++;
        }

        var currentlyPassed = HasMatchedCondition(rows[0], conditionKey);
        var currentSegment = currentlyPassed ? passedSegments.FirstOrDefault() : null;
        var previousSegment = currentlyPassed
            ? passedSegments.Skip(1).FirstOrDefault()
            : passedSegments.FirstOrDefault();

        return new ConditionDurationSnapshot(
            currentSegment?.StartTaipei,
            currentSegment is null ? null : ToWholeSeconds(currentSegment),
            previousSegment is null ? null : ToWholeSeconds(previousSegment));
    }

    private static bool HasMatchedCondition(StrategyScoreHistoryRow row, string conditionKey) =>
        SplitConditions(row.MatchedConditions).Contains(conditionKey);

    private static int ToWholeSeconds(PassedConditionSegment segment) =>
        Math.Max(0, (int)Math.Round((segment.EndTaipei - segment.StartTaipei).TotalSeconds));

    private static IReadOnlySet<string> SplitConditions(string conditions) =>
        conditions
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static decimal? CalculateSma(IReadOnlyList<SmaBarRow> rows, int period)
    {
        if (rows.Count < period)
        {
            return null;
        }

        var sum = 0m;
        for (var i = 0; i < period; i++)
        {
            sum += rows[i].ClosePrice;
        }

        return sum / period;
    }

    private static async Task<bool> TableExistsAsync(
        SqlConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT OBJECT_ID(@tableName, N'U');";
        AddStringParameter(command, "@tableName", tableName, 256);
        return await command.ExecuteScalarAsync(cancellationToken) is not DBNull and not null;
    }

    private static async Task<bool> ColumnExistsAsync(
        SqlConnection connection,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COL_LENGTH(@tableName, @columnName);";
        AddStringParameter(command, "@tableName", tableName, 256);
        AddStringParameter(command, "@columnName", columnName, 128);
        return await command.ExecuteScalarAsync(cancellationToken) is not DBNull and not null;
    }

    private static string GetSideLabel(string side) =>
        side.Equals("long", StringComparison.OrdinalIgnoreCase) ? "多方" :
        side.Equals("short", StringComparison.OrdinalIgnoreCase) ? "空方" :
        side;

    private static string GetMarketContextStatusLabel(string status) =>
        status switch
        {
            "ok" => "資料完整",
            "insufficient_sma_data" => "SMA 資料不足",
            "insufficient_1m_sma_data" => "1分K SMA60 資料不足",
            "insufficient_consolidation_data" => "盤整判斷資料不足",
            "no_completed_15m_bar" => "尚無已完成 15分K",
            _ => status
        };

    private static string GetConsolidationLabel(string state) =>
        state switch
        {
            "false" => "非盤整",
            "true" => "盤整",
            "unknown" => "資料不足",
            _ => state
        };

    private static string GetTriggerRuleLabel(string rule) =>
        rule switch
        {
            "15m_close_cross_above_sma76" => "15分K收盤突破 SMA76",
            "15m_close_cross_below_sma76" => "15分K收盤跌破 SMA76",
            _ => rule
        };

    private static string GetRecommendationStatusLabel(string status) =>
        RecommendationStatusLabels.GetValueOrDefault(status, status);

    private static string GetConfidenceLabel(string status) =>
        ConfidenceLabels.GetValueOrDefault(status, status);

    private static string GetOutcomeLabel(string? outcome) =>
        outcome switch
        {
            "take_profit" => "停利",
            "stop_loss" => "停損",
            "expired" => "未入場",
            "timed_out" => "追蹤逾時",
            "cancelled" => "取消",
            null => "尚未完成",
            _ => outcome
        };

    private static void AddStringParameter(SqlCommand command, string name, string value, int size) =>
        command.Parameters.Add(name, SqlDbType.NVarChar, size).Value = value;

    private static void AddDateTimeOffsetParameter(
        SqlCommand command,
        string name,
        DateTimeOffset value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.DateTimeOffset);
        parameter.Scale = 0;
        parameter.Value = value;
    }

    private static DateTimeOffset GetTaipeiNow() =>
        TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TaipeiTimeZone);

    private static DateTime? GetMinuteCloseBarStart(DateTime taipeiLocalTime, int intervalMinutes)
    {
        var sessionStart = GetSessionStart(taipeiLocalTime);
        if (sessionStart is null)
        {
            return null;
        }

        if (taipeiLocalTime <= sessionStart.Value)
        {
            return sessionStart.Value;
        }

        return GetBarStart(taipeiLocalTime.AddTicks(-1), intervalMinutes);
    }

    private static DateTime? GetBarStart(DateTime taipeiLocalTime, int intervalMinutes)
    {
        var sessionStart = GetSessionStart(taipeiLocalTime);
        if (sessionStart is null)
        {
            return null;
        }

        var elapsedMinutes = (int)Math.Floor((taipeiLocalTime - sessionStart.Value).TotalMinutes);
        if (elapsedMinutes < 0)
        {
            return null;
        }

        var bucketOffset = elapsedMinutes / intervalMinutes * intervalMinutes;
        return sessionStart.Value.AddMinutes(bucketOffset);
    }

    private static DateTime? GetSessionStart(DateTime taipeiLocalTime)
    {
        var time = TimeOnly.FromDateTime(taipeiLocalTime);
        var date = DateOnly.FromDateTime(taipeiLocalTime);
        var day = taipeiLocalTime.DayOfWeek;

        if (IsWeekday(day) && time >= new TimeOnly(8, 45) && time <= new TimeOnly(13, 45))
        {
            return date.ToDateTime(new TimeOnly(8, 45));
        }

        if (IsWeekday(day) && time >= new TimeOnly(15, 0))
        {
            return date.ToDateTime(new TimeOnly(15, 0));
        }

        if (IsTuesdayToSaturday(day) && time <= new TimeOnly(5, 0))
        {
            return date.AddDays(-1).ToDateTime(new TimeOnly(15, 0));
        }

        return null;
    }

    private static bool IsWeekday(DayOfWeek day) => day is >= DayOfWeek.Monday and <= DayOfWeek.Friday;

    private static bool IsTuesdayToSaturday(DayOfWeek day) => day is >= DayOfWeek.Tuesday and <= DayOfWeek.Saturday;

    private static TimeZoneInfo GetTaipeiTimeZone()
    {
        foreach (var id in new[] { "Taipei Standard Time", "Asia/Taipei" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.Local;
    }

    private sealed record StrategyScoreHistoryRow(
        string Side,
        DateTimeOffset EvaluatedAtTaipei,
        DateTimeOffset SampleTimeTaipei,
        DateTimeOffset? BarStartTaipei,
        DateTimeOffset? BarEndTaipei,
        string Status,
        string ConsolidationState,
        decimal? ConsolidationRangeCompressionRatio,
        decimal? ConsolidationDirectionEfficiency,
        string MatchedConditions,
        string MissingConditions,
        int TotalCount);

    private sealed record ConditionDurationSnapshot(
        DateTimeOffset? PassedSinceTaipei,
        int? PassedDurationSeconds,
        int? PreviousPassedDurationSeconds);

    private sealed record PassedConditionSegment(DateTimeOffset StartTaipei, DateTimeOffset EndTaipei);

    private sealed record QuoteRow(
        string Symbol,
        DateTimeOffset CapturedAtTaipei,
        DateTimeOffset SampleMinuteTaipei,
        string? SourceMarketTime,
        decimal Price,
        string SourceUrl);

    private sealed record SmaBarRow(
        string Symbol,
        DateTimeOffset BarStartTaipei,
        DateTimeOffset BarEndTaipei,
        decimal ClosePrice,
        DateTimeOffset UpdatedAtTaipei);

    private sealed record ReferenceCloseRow(decimal Price, DateTimeOffset SampleMinuteTaipei);

    private const string RecommendationSelectColumns = """
        events.Id,
        events.Symbol,
        events.EvaluatedAtTaipei,
        events.TriggerAtTaipei,
        events.TriggerBarStartTaipei,
        events.TriggerBarEndTaipei,
        events.Side,
        events.EventType,
        events.TriggerRule,
        events.PreviousClose,
        events.PreviousSma76,
        events.TriggerClose,
        events.TriggerSma76,
        events.TriggerSma20,
        events.TriggerAtr5,
        events.MarketContextSnapshot,
        events.ReferenceEventId,
        events.ReferenceAtrRatio,
        events.ReferenceAnchor,
        events.ReferenceAtr5,
        events.EntryLow,
        events.EntryHigh,
        events.StopLoss,
        events.TakeProfit,
        events.RewardRiskRatio,
        events.Status,
        events.IsActive,
        events.EnteredAtTaipei,
        events.EntryPrice,
        events.CompletedAtTaipei,
        events.Outcome,
        events.ConfidenceStatus,
        events.ConfidenceScore,
        events.ConfidenceSampleCount,
        events.EntryHitRate,
        events.RecommendationPrice,
        events.SourceMode,
        reference.TriggerAtTaipei,
        reference.Side,
        reference.TriggerClose,
        reference.TriggerSma76,
        reference.Status,
        events.ExitPrice,
        events.ProfitPoints
        """;
}

public sealed class FuturesDataOptions
{
    public const string SectionName = "FuturesData";

    public string Symbol { get; set; } = "WTX&";
    public string SqlServer { get; set; } = @".\SQLEXPRESS";
    public string SqlDatabase { get; set; } = "Futures";
    public string SqlUser { get; set; } = "admin";
    public string SqlPassword { get; set; } = "guitar";
    public string? ConnectionString { get; set; }
    public int RequestTimeoutSeconds { get; set; } = 15;

    public string BuildConnectionString()
    {
        var builder = string.IsNullOrWhiteSpace(ConnectionString)
            ? new SqlConnectionStringBuilder
            {
                DataSource = SqlServer,
                UserID = SqlUser,
                Password = SqlPassword
            }
            : new SqlConnectionStringBuilder(ConnectionString);

        if (!string.IsNullOrWhiteSpace(SqlDatabase))
        {
            builder.InitialCatalog = SqlDatabase;
        }

        builder.Encrypt = false;
        builder.TrustServerCertificate = true;
        builder.ConnectTimeout = RequestTimeoutSeconds;
        return builder.ConnectionString;
    }
}

public sealed record FuturesDashboardSnapshot(
    bool IsConnected,
    string? Message,
    DateTimeOffset ServerTimeTaipei,
    FuturesQuoteSnapshot? Quote,
    CurrentSmaSnapshot? CurrentSma,
    IReadOnlyList<QuoteHistoryPointSnapshot> QuoteHistory,
    IReadOnlyList<MarketContextSnapshot> MarketContexts,
    EntryRecommendationSnapshot? LatestRecommendation,
    IReadOnlyList<EntryRecommendationSnapshot> RecommendationHistory);

public sealed record FuturesQuoteSnapshot(
    string Symbol,
    decimal Price,
    decimal? PriceChange,
    decimal? PriceChangePercent,
    DateTimeOffset CapturedAtTaipei,
    DateTimeOffset SampleMinuteTaipei,
    string? SourceMarketTime,
    string SourceUrl,
    int AgeSeconds,
    decimal? ComparisonBasePrice,
    DateTimeOffset? ComparisonBaseTimeTaipei);

public sealed record CurrentSmaSnapshot(
    string Symbol,
    int IntervalMinutes,
    DateTimeOffset BarStartTaipei,
    DateTimeOffset BarEndTaipei,
    decimal Close,
    decimal? Sma20,
    decimal? Sma76,
    int AvailableBarCount,
    DateTimeOffset UpdatedAtTaipei);

public sealed record QuoteHistoryPointSnapshot(DateTimeOffset SampleTimeTaipei, decimal Price);

public sealed record MinuteKBarsSnapshot(
    bool IsConnected,
    string? Message,
    string Symbol,
    int IntervalMinutes,
    DateTimeOffset GeneratedAtTaipei,
    DateTimeOffset FromTaipei,
    DateTimeOffset ToTaipei,
    IReadOnlyList<MinuteKBarSnapshot> Bars);

public sealed record MinuteKBarSnapshot(
    DateTimeOffset BarStartTaipei,
    DateTimeOffset BarEndTaipei,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    int SourceCount,
    DateTimeOffset UpdatedAtTaipei);

public sealed record MarketContextSnapshot(
    string Side,
    string SideLabel,
    DateTimeOffset EvaluatedAtTaipei,
    DateTimeOffset SampleTimeTaipei,
    DateTimeOffset? BarStartTaipei,
    DateTimeOffset? BarEndTaipei,
    string Status,
    string StatusLabel,
    string ConsolidationState,
    string ConsolidationLabel,
    decimal? ConsolidationRangeCompressionRatio,
    decimal? ConsolidationDirectionEfficiency,
    IReadOnlyList<MarketConditionSnapshot> Conditions);

public sealed record MarketConditionSnapshot(
    string Key,
    string Label,
    bool Passed,
    bool IsAutomaticTrigger,
    DateTimeOffset? PassedSinceTaipei,
    int? PassedDurationSeconds,
    int? PreviousPassedDurationSeconds);

public sealed record EntryRecommendationSnapshot(
    long Id,
    string Symbol,
    DateTimeOffset EvaluatedAtTaipei,
    DateTimeOffset TriggerAtTaipei,
    DateTimeOffset TriggerBarStartTaipei,
    DateTimeOffset TriggerBarEndTaipei,
    string Side,
    string SideLabel,
    string EventType,
    string TriggerRule,
    string TriggerRuleLabel,
    decimal PreviousClose,
    decimal PreviousSma76,
    decimal TriggerClose,
    decimal TriggerSma76,
    decimal? TriggerSma20,
    decimal? TriggerAtr5,
    long? ReferenceEventId,
    DateTimeOffset? ReferenceEventTriggerAtTaipei,
    string? ReferenceEventSide,
    string? ReferenceEventSideLabel,
    decimal? ReferenceEventTriggerClose,
    decimal? ReferenceEventTriggerSma76,
    string? ReferenceEventStatus,
    string? ReferenceEventStatusLabel,
    decimal? ReferenceAtrRatio,
    decimal? EntryLow,
    decimal? EntryHigh,
    decimal? StopLoss,
    decimal? TakeProfit,
    decimal? RewardRiskRatio,
    string Status,
    string StatusLabel,
    bool IsActive,
    DateTimeOffset? EnteredAtTaipei,
    decimal? EntryPrice,
    decimal? ExitPrice,
    decimal? ProfitPoints,
    DateTimeOffset? CompletedAtTaipei,
    string? Outcome,
    string OutcomeLabel,
    string ConfidenceStatus,
    string ConfidenceLabel,
    decimal? ConfidenceScore,
    int ConfidenceSampleCount,
    decimal? EntryHitRate,
    decimal? RecommendationPrice,
    string SourceMode);
