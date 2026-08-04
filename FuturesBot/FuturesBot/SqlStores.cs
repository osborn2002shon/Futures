using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

internal sealed partial class FuturesSqlStore
{
    private static readonly int[] PersistedIntervals = [5, 15, 30, 60];

    private readonly string _connectionString;
    private readonly string _symbol;
    private readonly TimeZoneInfo _taipeiTimeZone;
    private readonly List<FuturesTick> _ticks;
    private readonly Dictionary<StrategySide, string> _lastStrategyScoreStateKeys;

    private FuturesSqlStore(
        string connectionString,
        string symbol,
        TimeZoneInfo taipeiTimeZone,
        List<FuturesTick> ticks,
        Dictionary<StrategySide, string> lastStrategyScoreStateKeys)
    {
        _connectionString = connectionString;
        _symbol = symbol;
        _taipeiTimeZone = taipeiTimeZone;
        _ticks = ticks;
        _lastStrategyScoreStateKeys = lastStrategyScoreStateKeys;
    }

    public static async Task<FuturesSqlStore> CreateAsync(
        string masterConnectionString,
        string databaseConnectionString,
        string databaseName,
        string symbol,
        TimeZoneInfo taipeiTimeZone,
        CancellationToken cancellationToken)
    {
        await EnsureDatabaseAsync(masterConnectionString, databaseName, cancellationToken);
        await EnsureSchemaAsync(databaseConnectionString, cancellationToken);
        await EnsureEntryRecommendationEventSchemaAsync(databaseConnectionString, cancellationToken);

        var ticks = await LoadExistingTicksAsync(databaseConnectionString, symbol, cancellationToken);
        var lastStrategyScoreStateKeys = await LoadLatestStrategyScoreStateKeysAsync(databaseConnectionString, cancellationToken);
        return new FuturesSqlStore(databaseConnectionString, symbol, taipeiTimeZone, ticks, lastStrategyScoreStateKeys);
    }

    public async Task<bool> TryAppendTickAndWriteBarsAsync(FuturesTick tick, CancellationToken cancellationToken)
    {
        var sampleMinute = MarketDataClock.GetSampleMinute(tick);
        if (HasTickForSameSourceMinute(tick, sampleMinute))
        {
            return false;
        }

        var inserted = await TryInsertTickAsync(tick, sampleMinute, cancellationToken);
        if (!inserted)
        {
            return false;
        }

        _ticks.Add(tick);

        var affectedBars = new List<KBar>();
        foreach (var interval in PersistedIntervals)
        {
            var barStart = TaiwanFuturesMarketHours.GetMinuteCloseBarStart(sampleMinute.DateTime, interval);
            if (barStart is not null)
            {
                affectedBars.AddRange(BuildBars(interval, barStart.Value));
            }
        }

        await UpsertBarsAsync(affectedBars, cancellationToken);

        return true;
    }

    public IReadOnlyList<KBar> GetBarsSnapshot(int intervalMinutes) => BuildBars(intervalMinutes).ToArray();

    public IReadOnlyList<FuturesTick> GetTicksSnapshot() => _ticks.ToArray();

    public async Task<IReadOnlyList<StrategyScore>> AppendChangedStrategyScoresAsync(
        IEnumerable<StrategyScore> scores,
        CancellationToken cancellationToken)
    {
        var changedScores = scores
            .Where(score =>
            {
                var stateKey = StrategyScoreStateKey.Build(score);
                return !_lastStrategyScoreStateKeys.TryGetValue(score.Side, out var lastStateKey) || lastStateKey != stateKey;
            })
            .ToArray();

        if (changedScores.Length == 0)
        {
            return [];
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        foreach (var score in changedScores)
        {
            await InsertStrategyScoreAsync(connection, transaction, score, cancellationToken);
        }

        transaction.Commit();

        foreach (var score in changedScores)
        {
            _lastStrategyScoreStateKeys[score.Side] = StrategyScoreStateKey.Build(score);
        }

        return changedScores;
    }

    public async Task AppendStrategyScoresAsync(IEnumerable<StrategyScore> scores, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        foreach (var score in scores)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO dbo.StrategyScores
                    (EvaluatedAtTaipei, SampleTimeTaipei, BarStartTaipei, BarEndTaipei, SignalType, Side,
                     MatchedCount, TotalCount, Status, ConsolidationState, ConsolidationRangeCompressionRatio,
                     ConsolidationDirectionEfficiency, MatchedConditions, MissingConditions)
                SELECT
                    @evaluatedAtTaipei, @sampleTimeTaipei, @barStartTaipei, @barEndTaipei, @signalType, @side,
                    @matchedCount, @totalCount, @status, @consolidationState, @consolidationRangeCompressionRatio,
                    @consolidationDirectionEfficiency, @matchedConditions, @missingConditions
                WHERE NOT EXISTS
                (
                    SELECT 1
                    FROM dbo.StrategyScores WITH (UPDLOCK, HOLDLOCK)
                    WHERE SampleTimeTaipei = @sampleTimeTaipei
                      AND SignalType = @signalType
                      AND Side = @side
                );
                """;

            AddDateTimeOffsetParameter(command, "@evaluatedAtTaipei", score.EvaluatedAt);
            AddDateTimeOffsetParameter(command, "@sampleTimeTaipei", score.SampleTime);
            AddNullableDateTimeOffsetParameter(command, "@barStartTaipei", score.BarStart);
            AddNullableDateTimeOffsetParameter(command, "@barEndTaipei", score.BarEnd);
            AddStringParameter(command, "@signalType", score.SignalType, 32);
            AddStringParameter(command, "@side", score.Side.ToString().ToLowerInvariant(), 16);
            command.Parameters.Add("@matchedCount", SqlDbType.Int).Value = score.MatchedCount;
            command.Parameters.Add("@totalCount", SqlDbType.Int).Value = score.TotalCount;
            AddStringParameter(command, "@status", score.Status, 64);
            AddStringParameter(command, "@consolidationState", score.ConsolidationState, 16);
            AddNullableDecimalParameter(command, "@consolidationRangeCompressionRatio", score.ConsolidationRangeCompressionRatio);
            AddNullableDecimalParameter(command, "@consolidationDirectionEfficiency", score.ConsolidationDirectionEfficiency);
            AddStringParameter(command, "@matchedConditions", score.MatchedConditions, -1);
            AddStringParameter(command, "@missingConditions", score.MissingConditions, -1);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        transaction.Commit();
    }

    public async Task TryAppendErrorAsync(DateTimeOffset occurredAt, string operation, Exception exception)
    {
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(CancellationToken.None);

            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO dbo.FuturesErrorLogs
                    (OccurredAtTaipei, Operation, Message, Details)
                VALUES
                    (@occurredAtTaipei, @operation, @message, @details);
                """;

            AddDateTimeOffsetParameter(command, "@occurredAtTaipei", occurredAt);
            AddStringParameter(command, "@operation", operation, 64);
            AddStringParameter(command, "@message", exception.Message, -1);
            AddStringParameter(command, "@details", exception.ToString(), -1);

            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception logException) when (logException is not OperationCanceledException)
        {
            Console.Error.WriteLine($"{FormatDateTime(occurredAt)} failed to write SQL error log: {logException.Message}");
        }
    }

    private static async Task EnsureDatabaseAsync(
        string masterConnectionString,
        string databaseName,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(masterConnectionString);
        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            IF DB_ID(@databaseName) IS NULL
            BEGIN
                EXEC(N'CREATE DATABASE {QuoteSqlIdentifier(databaseName)}');
            END;
            """;

        command.Parameters.Add("@databaseName", SqlDbType.NVarChar, 128).Value = databaseName;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureSchemaAsync(string databaseConnectionString, CancellationToken cancellationToken)
    {
        var batches = new[]
        {
            """
            IF OBJECT_ID(N'dbo.FuturesTicks', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.FuturesTicks
                (
                    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_FuturesTicks PRIMARY KEY,
                    Symbol nvarchar(32) NOT NULL,
                    CapturedAtTaipei datetimeoffset(0) NOT NULL,
                    SampleMinuteTaipei datetimeoffset(0) NOT NULL,
                    SourceMarketTime nvarchar(64) NULL,
                    Price decimal(18,4) NOT NULL,
                    SourceUrl nvarchar(2048) NOT NULL,
                    CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_FuturesTicks_CreatedAtUtc DEFAULT SYSUTCDATETIME()
                );
            END;

            IF NOT EXISTS
            (
                SELECT 1
                FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'dbo.FuturesTicks')
                  AND name = N'UX_FuturesTicks_Symbol_SampleMinuteTaipei'
            )
            BEGIN
                CREATE UNIQUE INDEX UX_FuturesTicks_Symbol_SampleMinuteTaipei
                    ON dbo.FuturesTicks(Symbol, SampleMinuteTaipei);
            END;
            """,
            """
            IF OBJECT_ID(N'dbo.FuturesKBars', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.FuturesKBars
                (
                    Symbol nvarchar(32) NOT NULL,
                    IntervalMinutes int NOT NULL,
                    BarStartTaipei datetimeoffset(0) NOT NULL,
                    BarEndTaipei datetimeoffset(0) NOT NULL,
                    OpenPrice decimal(18,4) NOT NULL,
                    HighPrice decimal(18,4) NOT NULL,
                    LowPrice decimal(18,4) NOT NULL,
                    ClosePrice decimal(18,4) NOT NULL,
                    SourceCount int NOT NULL,
                    UpdatedAtTaipei datetimeoffset(0) NOT NULL,
                    CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_FuturesKBars_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
                    ModifiedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_FuturesKBars_ModifiedAtUtc DEFAULT SYSUTCDATETIME(),
                    CONSTRAINT PK_FuturesKBars PRIMARY KEY (Symbol, IntervalMinutes, BarStartTaipei)
                );
            END;
            """,
            """
            IF OBJECT_ID(N'dbo.StrategyScores', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.StrategyScores
                (
                    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_StrategyScores PRIMARY KEY,
                    EvaluatedAtTaipei datetimeoffset(0) NOT NULL,
                    SampleTimeTaipei datetimeoffset(0) NOT NULL,
                    BarStartTaipei datetimeoffset(0) NULL,
                    BarEndTaipei datetimeoffset(0) NULL,
                    SignalType nvarchar(32) NOT NULL,
                    Side nvarchar(16) NOT NULL,
                    MatchedCount int NOT NULL,
                    TotalCount int NOT NULL,
                    Status nvarchar(64) NOT NULL,
                    ConsolidationState nvarchar(16) NOT NULL,
                    ConsolidationFlatSma76DiffSum decimal(18,4) NULL,
                    ConsolidationCrossCount int NULL,
                    ConsolidationRangeCompressionRatio decimal(18,6) NULL,
                    ConsolidationDirectionEfficiency decimal(18,6) NULL,
                    MatchedConditions nvarchar(max) NOT NULL,
                    MissingConditions nvarchar(max) NOT NULL,
                    CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_StrategyScores_CreatedAtUtc DEFAULT SYSUTCDATETIME()
                );
            END;

            IF COL_LENGTH(N'dbo.StrategyScores', N'ConsolidationRangeCompressionRatio') IS NULL
            BEGIN
                ALTER TABLE dbo.StrategyScores
                    ADD ConsolidationRangeCompressionRatio decimal(18,6) NULL;
            END;

            IF COL_LENGTH(N'dbo.StrategyScores', N'ConsolidationDirectionEfficiency') IS NULL
            BEGIN
                ALTER TABLE dbo.StrategyScores
                    ADD ConsolidationDirectionEfficiency decimal(18,6) NULL;
            END;

            IF NOT EXISTS
            (
                SELECT 1
                FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'dbo.StrategyScores')
                  AND name = N'UX_StrategyScores_SampleTime_SignalType_Side'
            )
            BEGIN
                CREATE UNIQUE INDEX UX_StrategyScores_SampleTime_SignalType_Side
                    ON dbo.StrategyScores(SampleTimeTaipei, SignalType, Side);
            END;
            """,
            """
            IF OBJECT_ID(N'dbo.FuturesErrorLogs', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.FuturesErrorLogs
                (
                    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_FuturesErrorLogs PRIMARY KEY,
                    OccurredAtTaipei datetimeoffset(0) NOT NULL,
                    Operation nvarchar(64) NOT NULL,
                    Message nvarchar(max) NOT NULL,
                    Details nvarchar(max) NOT NULL,
                    CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_FuturesErrorLogs_CreatedAtUtc DEFAULT SYSUTCDATETIME()
                );
            END;
            """
        };

        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync(cancellationToken);

        foreach (var batch in batches)
        {
            using var command = connection.CreateCommand();
            command.CommandText = batch;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<Dictionary<StrategySide, string>> LoadLatestStrategyScoreStateKeysAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        var stateKeys = new Dictionary<StrategySide, string>();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH RankedScores AS
            (
                SELECT
                    Side,
                    EvaluatedAtTaipei,
                    SampleTimeTaipei,
                    BarStartTaipei,
                    BarEndTaipei,
                    SignalType,
                    MatchedCount,
                    TotalCount,
                    Status,
                    ConsolidationState,
                    ConsolidationRangeCompressionRatio,
                    ConsolidationDirectionEfficiency,
                    MatchedConditions,
                    MissingConditions,
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
                SignalType,
                MatchedCount,
                TotalCount,
                Status,
                ConsolidationState,
                ConsolidationRangeCompressionRatio,
                ConsolidationDirectionEfficiency,
                MatchedConditions,
                MissingConditions
            FROM RankedScores
            WHERE RowNumber = 1;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (TryParseStrategySide(reader.GetString(0), out var side))
            {
                var score = new StrategyScore(
                    reader.GetDateTimeOffset(1),
                    reader.GetDateTimeOffset(2),
                    await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetDateTimeOffset(3),
                    await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetDateTimeOffset(4),
                    reader.GetString(5),
                    side,
                    reader.GetInt32(6),
                    reader.GetInt32(7),
                    reader.GetString(8),
                    reader.GetString(9),
                    await reader.IsDBNullAsync(10, cancellationToken) ? null : reader.GetDecimal(10),
                    await reader.IsDBNullAsync(11, cancellationToken) ? null : reader.GetDecimal(11),
                    reader.GetString(12),
                    reader.GetString(13));
                stateKeys[side] = StrategyScoreStateKey.Build(score);
            }
        }

        return stateKeys;
    }

    private static bool TryParseStrategySide(string value, out StrategySide side)
    {
        if (value.Equals("long", StringComparison.OrdinalIgnoreCase))
        {
            side = StrategySide.Long;
            return true;
        }

        if (value.Equals("short", StringComparison.OrdinalIgnoreCase))
        {
            side = StrategySide.Short;
            return true;
        }

        side = default;
        return false;
    }

    private async Task<bool> TryInsertTickAsync(
        FuturesTick tick,
        DateTimeOffset sampleMinute,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO dbo.FuturesTicks
                    (Symbol, CapturedAtTaipei, SampleMinuteTaipei, SourceMarketTime, Price, SourceUrl)
                SELECT
                    @symbol, @capturedAtTaipei, @sampleMinuteTaipei, @sourceMarketTime, @price, @sourceUrl
                WHERE NOT EXISTS
                (
                    SELECT 1
                    FROM dbo.FuturesTicks WITH (UPDLOCK, HOLDLOCK)
                    WHERE Symbol = @symbol
                      AND SampleMinuteTaipei = @sampleMinuteTaipei
                );
                """;

            AddStringParameter(command, "@symbol", tick.Symbol, 32);
            AddDateTimeOffsetParameter(command, "@capturedAtTaipei", tick.CapturedAt);
            AddDateTimeOffsetParameter(command, "@sampleMinuteTaipei", sampleMinute);
            AddNullableStringParameter(command, "@sourceMarketTime", tick.SourceMarketTime, 64);
            AddDecimalParameter(command, "@price", tick.Price);
            AddStringParameter(command, "@sourceUrl", tick.SourceUrl, 2048);

            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        catch (SqlException ex) when (IsUniqueConstraintViolation(ex))
        {
            return false;
        }
    }

    private static async Task InsertStrategyScoreAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        StrategyScore score,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO dbo.StrategyScores
                (EvaluatedAtTaipei, SampleTimeTaipei, BarStartTaipei, BarEndTaipei, SignalType, Side,
                 MatchedCount, TotalCount, Status, ConsolidationState, ConsolidationRangeCompressionRatio,
                 ConsolidationDirectionEfficiency, MatchedConditions, MissingConditions)
            SELECT
                @evaluatedAtTaipei, @sampleTimeTaipei, @barStartTaipei, @barEndTaipei, @signalType, @side,
                @matchedCount, @totalCount, @status, @consolidationState, @consolidationRangeCompressionRatio,
                @consolidationDirectionEfficiency, @matchedConditions, @missingConditions
            WHERE NOT EXISTS
            (
                SELECT 1
                FROM dbo.StrategyScores WITH (UPDLOCK, HOLDLOCK)
                WHERE SampleTimeTaipei = @sampleTimeTaipei
                  AND SignalType = @signalType
                  AND Side = @side
            );
            """;

        AddStrategyScoreParameters(command, score);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddStrategyScoreParameters(SqlCommand command, StrategyScore score)
    {
        AddDateTimeOffsetParameter(command, "@evaluatedAtTaipei", score.EvaluatedAt);
        AddDateTimeOffsetParameter(command, "@sampleTimeTaipei", score.SampleTime);
        AddNullableDateTimeOffsetParameter(command, "@barStartTaipei", score.BarStart);
        AddNullableDateTimeOffsetParameter(command, "@barEndTaipei", score.BarEnd);
        AddStringParameter(command, "@signalType", score.SignalType, 32);
        AddStringParameter(command, "@side", score.Side.ToString().ToLowerInvariant(), 16);
        command.Parameters.Add("@matchedCount", SqlDbType.Int).Value = score.MatchedCount;
        command.Parameters.Add("@totalCount", SqlDbType.Int).Value = score.TotalCount;
        AddStringParameter(command, "@status", score.Status, 64);
        AddStringParameter(command, "@consolidationState", score.ConsolidationState, 16);
        AddNullableDecimalParameter(command, "@consolidationRangeCompressionRatio", score.ConsolidationRangeCompressionRatio);
        AddNullableDecimalParameter(command, "@consolidationDirectionEfficiency", score.ConsolidationDirectionEfficiency);
        AddStringParameter(command, "@matchedConditions", score.MatchedConditions, -1);
        AddStringParameter(command, "@missingConditions", score.MissingConditions, -1);
    }

    private static async Task<List<FuturesTick>> LoadExistingTicksAsync(
        string connectionString,
        string symbol,
        CancellationToken cancellationToken)
    {
        var ticks = new List<FuturesTick>();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Symbol, CapturedAtTaipei, SourceMarketTime, Price, SourceUrl
            FROM dbo.FuturesTicks
            WHERE Symbol = @symbol
            ORDER BY SampleMinuteTaipei, CapturedAtTaipei, Id;
            """;

        AddStringParameter(command, "@symbol", symbol, 32);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ticks.Add(new FuturesTick(
                reader.GetString(0),
                reader.GetDateTimeOffset(1),
                await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2),
                reader.GetDecimal(3),
                reader.GetString(4)));
        }

        return ticks
            .GroupBy(tick => (tick.Symbol, SampleMinute: MarketDataClock.GetSampleMinute(tick)))
            .Select(group => group.Last())
            .ToList();
    }

    private IEnumerable<KBar> BuildBars(int intervalMinutes, DateTime? barStartFilter = null)
    {
        return _ticks
            .Where(tick => tick.Symbol == _symbol)
            .Select(tick =>
            {
                var sampleMinute = MarketDataClock.GetSampleMinute(tick);
                return (Tick: tick, SampleMinute: sampleMinute, Start: TaiwanFuturesMarketHours.GetMinuteCloseBarStart(sampleMinute.DateTime, intervalMinutes));
            })
            .Where(item => item.Start is not null && (barStartFilter is null || item.Start.Value == barStartFilter.Value))
            .GroupBy(item => item.Start!.Value)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var orderedTicks = group
                    .OrderBy(item => item.SampleMinute)
                    .ThenBy(item => item.Tick.CapturedAt)
                    .Select(item => item.Tick)
                    .ToArray();
                var prices = orderedTicks.Select(tick => tick.Price).ToArray();
                return new KBar(
                    _symbol,
                    intervalMinutes,
                    new DateTimeOffset(group.Key, _taipeiTimeZone.GetUtcOffset(group.Key)),
                    new DateTimeOffset(group.Key.AddMinutes(intervalMinutes), _taipeiTimeZone.GetUtcOffset(group.Key.AddMinutes(intervalMinutes))),
                    prices.First(),
                    prices.Max(),
                    prices.Min(),
                    prices.Last(),
                    orderedTicks.Length,
                    orderedTicks.Last().CapturedAt);
            });
    }

    private bool HasTickForSameSourceMinute(FuturesTick tick, DateTimeOffset sampleMinute) =>
        _ticks.Any(existing => existing.Symbol == tick.Symbol && MarketDataClock.GetSampleMinute(existing) == sampleMinute);

    private async Task UpsertBarsAsync(IEnumerable<KBar> bars, CancellationToken cancellationToken)
    {
        var barArray = bars.ToArray();
        if (barArray.Length == 0)
        {
            return;
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        foreach (var bar in barArray)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                MERGE dbo.FuturesKBars WITH (HOLDLOCK) AS target
                USING
                (
                    SELECT
                        @symbol AS Symbol,
                        @intervalMinutes AS IntervalMinutes,
                        @barStartTaipei AS BarStartTaipei
                ) AS source
                    ON target.Symbol = source.Symbol
                   AND target.IntervalMinutes = source.IntervalMinutes
                   AND target.BarStartTaipei = source.BarStartTaipei
                WHEN MATCHED AND
                (
                    target.BarEndTaipei <> @barEndTaipei
                    OR target.OpenPrice <> @openPrice
                    OR target.HighPrice <> @highPrice
                    OR target.LowPrice <> @lowPrice
                    OR target.ClosePrice <> @closePrice
                    OR target.SourceCount <> @sourceCount
                    OR target.UpdatedAtTaipei <> @updatedAtTaipei
                ) THEN
                    UPDATE SET
                        BarEndTaipei = @barEndTaipei,
                        OpenPrice = @openPrice,
                        HighPrice = @highPrice,
                        LowPrice = @lowPrice,
                        ClosePrice = @closePrice,
                        SourceCount = @sourceCount,
                        UpdatedAtTaipei = @updatedAtTaipei,
                        ModifiedAtUtc = SYSUTCDATETIME()
                WHEN NOT MATCHED THEN
                    INSERT
                        (Symbol, IntervalMinutes, BarStartTaipei, BarEndTaipei, OpenPrice, HighPrice, LowPrice,
                         ClosePrice, SourceCount, UpdatedAtTaipei)
                    VALUES
                        (@symbol, @intervalMinutes, @barStartTaipei, @barEndTaipei, @openPrice, @highPrice,
                         @lowPrice, @closePrice, @sourceCount, @updatedAtTaipei);
                """;

            AddStringParameter(command, "@symbol", bar.Symbol, 32);
            command.Parameters.Add("@intervalMinutes", SqlDbType.Int).Value = bar.IntervalMinutes;
            AddDateTimeOffsetParameter(command, "@barStartTaipei", bar.BarStart);
            AddDateTimeOffsetParameter(command, "@barEndTaipei", bar.BarEnd);
            AddDecimalParameter(command, "@openPrice", bar.Open);
            AddDecimalParameter(command, "@highPrice", bar.High);
            AddDecimalParameter(command, "@lowPrice", bar.Low);
            AddDecimalParameter(command, "@closePrice", bar.Close);
            command.Parameters.Add("@sourceCount", SqlDbType.Int).Value = bar.SourceCount;
            AddDateTimeOffsetParameter(command, "@updatedAtTaipei", bar.UpdatedAt);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        transaction.Commit();
    }

    private static void AddStringParameter(SqlCommand command, string name, string value, int size) =>
        command.Parameters.Add(name, SqlDbType.NVarChar, size).Value = value;

    private static void AddNullableStringParameter(SqlCommand command, string name, string? value, int size) =>
        command.Parameters.Add(name, SqlDbType.NVarChar, size).Value = string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

    private static void AddDateTimeOffsetParameter(SqlCommand command, string name, DateTimeOffset value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.DateTimeOffset);
        parameter.Scale = 0;
        parameter.Value = TruncateToSecond(value);
    }

    private static void AddNullableDateTimeOffsetParameter(SqlCommand command, string name, DateTimeOffset? value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.DateTimeOffset);
        parameter.Scale = 0;
        parameter.Value = value is { } dateTimeOffset ? TruncateToSecond(dateTimeOffset) : DBNull.Value;
    }

    private static void AddDecimalParameter(SqlCommand command, string name, decimal value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = 18;
        parameter.Scale = 4;
        parameter.Value = value;
    }

    private static void AddNullableDecimalParameter(SqlCommand command, string name, decimal? value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = 18;
        parameter.Scale = 4;
        parameter.Value = value is { } decimalValue ? decimalValue : DBNull.Value;
    }

    private static void AddNullableIntParameter(SqlCommand command, string name, int? value) =>
        command.Parameters.Add(name, SqlDbType.Int).Value = value is { } intValue ? intValue : DBNull.Value;

    private static DateTimeOffset TruncateToSecond(DateTimeOffset value) =>
        value.AddTicks(-(value.Ticks % TimeSpan.TicksPerSecond));

    private static string QuoteSqlIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier) || identifier.Length > 128)
        {
            throw new ArgumentException("SQL database name must be 1 to 128 characters.", nameof(identifier));
        }

        return "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
    }

    private static bool IsUniqueConstraintViolation(SqlException exception) =>
        exception.Errors
            .Cast<SqlError>()
            .Any(error => error.Number is 2601 or 2627);

    private static string FormatDateTime(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
}
