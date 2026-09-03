using System.Data;
using Microsoft.Data.SqlClient;

internal sealed partial class FuturesSqlStore
{
    private static async Task EnsureEntryRecommendationEventSchemaAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        var batches = new[]
        {
            """
            IF OBJECT_ID(N'dbo.EntryRecommendationEvents', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.EntryRecommendationEvents
                (
                    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_EntryRecommendationEvents PRIMARY KEY,
                    Symbol nvarchar(32) NOT NULL,
                    EvaluatedAtTaipei datetimeoffset(0) NOT NULL,
                    TriggerAtTaipei datetimeoffset(0) NOT NULL,
                    TriggerBarStartTaipei datetimeoffset(0) NOT NULL,
                    TriggerBarEndTaipei datetimeoffset(0) NOT NULL,
                    Side nvarchar(16) NOT NULL,
                    EventType nvarchar(32) NOT NULL,
                    TriggerRule nvarchar(64) NOT NULL,
                    PreviousClose decimal(18,4) NOT NULL,
                    PreviousSma76 decimal(18,4) NOT NULL,
                    TriggerClose decimal(18,4) NOT NULL,
                    TriggerSma76 decimal(18,4) NOT NULL,
                    TriggerSma20 decimal(18,4) NULL,
                    TriggerAtr5 decimal(18,4) NULL,
                    RecommendationPrice decimal(18,4) NULL,
                    MarketContextSnapshot nvarchar(max) NOT NULL,
                    ReferenceEventId bigint NULL,
                    ReferenceAtrRatio decimal(18,4) NULL,
                    ReferenceAnchor decimal(18,4) NULL,
                    ReferenceAtr5 decimal(18,4) NULL,
                    EntryLow decimal(18,4) NULL,
                    EntryHigh decimal(18,4) NULL,
                    StopLoss decimal(18,4) NULL,
                    TakeProfit decimal(18,4) NULL,
                    RewardRiskRatio decimal(18,4) NULL,
                    Status nvarchar(64) NOT NULL,
                    IsActive bit NOT NULL,
                    EnteredAtTaipei datetimeoffset(0) NULL,
                    EntryPrice decimal(18,4) NULL,
                    ExitPrice decimal(18,4) NULL,
                    ProfitPoints decimal(18,4) NULL,
                    CompletedAtTaipei datetimeoffset(0) NULL,
                    Outcome nvarchar(32) NULL,
                    ConfidenceStatus nvarchar(32) NOT NULL,
                    ConfidenceScore decimal(9,4) NULL,
                    ConfidenceSampleCount int NOT NULL,
                    EntryHitRate decimal(9,4) NULL,
                    SourceMode nvarchar(16) NOT NULL,
                    CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_EntryRecommendationEvents_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
                    ModifiedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_EntryRecommendationEvents_ModifiedAtUtc DEFAULT SYSUTCDATETIME()
                );
            END;

            IF COL_LENGTH(N'dbo.EntryRecommendationEvents', N'RecommendationPrice') IS NULL
            BEGIN
                ALTER TABLE dbo.EntryRecommendationEvents
                    ADD RecommendationPrice decimal(18,4) NULL;
            END;

            IF COL_LENGTH(N'dbo.EntryRecommendationEvents', N'ExitPrice') IS NULL
            BEGIN
                ALTER TABLE dbo.EntryRecommendationEvents
                    ADD ExitPrice decimal(18,4) NULL;
            END;

            IF COL_LENGTH(N'dbo.EntryRecommendationEvents', N'ProfitPoints') IS NULL
            BEGIN
                ALTER TABLE dbo.EntryRecommendationEvents
                    ADD ProfitPoints decimal(18,4) NULL;
            END;
            """,
            """

            UPDATE events
            SET RecommendationPrice = backfill.Price,
                ModifiedAtUtc = SYSUTCDATETIME()
            FROM dbo.EntryRecommendationEvents AS events
            CROSS APPLY
            (
                SELECT TOP (1) ticks.Price
                FROM dbo.FuturesTicks AS ticks
                WHERE ticks.Symbol = events.Symbol
                  AND ticks.CapturedAtTaipei <= events.EvaluatedAtTaipei
                ORDER BY ticks.CapturedAtTaipei DESC, ticks.Id DESC
            ) AS backfill
            WHERE events.RecommendationPrice IS NULL;

            IF NOT EXISTS
            (
                SELECT 1
                FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'dbo.EntryRecommendationEvents')
                  AND name = N'UX_EntryRecommendationEvents_Trigger'
            )
            BEGIN
                CREATE UNIQUE INDEX UX_EntryRecommendationEvents_Trigger
                    ON dbo.EntryRecommendationEvents(Symbol, TriggerBarStartTaipei, Side, EventType);
            END;

            IF NOT EXISTS
            (
                SELECT 1
                FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'dbo.EntryRecommendationEvents')
                  AND name = N'UX_EntryRecommendationEvents_Active'
            )
            BEGIN
                CREATE UNIQUE INDEX UX_EntryRecommendationEvents_Active
                    ON dbo.EntryRecommendationEvents(Symbol)
                    WHERE IsActive = 1;
            END;

            IF NOT EXISTS
            (
                SELECT 1
                FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'dbo.EntryRecommendationEvents')
                  AND name = N'IX_EntryRecommendationEvents_Side_Trigger'
            )
            BEGIN
                CREATE INDEX IX_EntryRecommendationEvents_Side_Trigger
                    ON dbo.EntryRecommendationEvents(Symbol, Side, EventType, TriggerAtTaipei DESC);
            END;
            """,
            """
            IF OBJECT_ID(N'dbo.EntryRecommendationStatusHistory', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.EntryRecommendationStatusHistory
                (
                    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_EntryRecommendationStatusHistory PRIMARY KEY,
                    RecommendationEventId bigint NOT NULL,
                    ChangedAtTaipei datetimeoffset(0) NOT NULL,
                    PreviousStatus nvarchar(64) NULL,
                    NewStatus nvarchar(64) NOT NULL,
                    ObservedPrice decimal(18,4) NULL,
                    Note nvarchar(512) NOT NULL,
                    CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_EntryRecommendationStatusHistory_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
                    CONSTRAINT FK_EntryRecommendationStatusHistory_Event
                        FOREIGN KEY (RecommendationEventId) REFERENCES dbo.EntryRecommendationEvents(Id)
                );
            END;

            IF NOT EXISTS
            (
                SELECT 1
                FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'dbo.EntryRecommendationStatusHistory')
                  AND name = N'IX_EntryRecommendationStatusHistory_Event_Changed'
            )
            BEGIN
                CREATE INDEX IX_EntryRecommendationStatusHistory_Event_Changed
                    ON dbo.EntryRecommendationStatusHistory(RecommendationEventId, ChangedAtTaipei, Id);
            END;

            UPDATE history
            SET ObservedPrice = events.RecommendationPrice
            FROM dbo.EntryRecommendationStatusHistory AS history
            INNER JOIN dbo.EntryRecommendationEvents AS events
                ON events.Id = history.RecommendationEventId
            WHERE history.PreviousStatus IS NULL
              AND history.ObservedPrice IS NULL
              AND events.RecommendationPrice IS NOT NULL;
            """
        };

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (var batch in batches)
        {
            using var command = connection.CreateCommand();
            command.CommandText = batch;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<EntryRecommendationEvent?> LoadActiveRecommendationEventAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT TOP (1)
                {EntryRecommendationSelectColumns}
            FROM dbo.EntryRecommendationEvents
            WHERE Symbol = @symbol
              AND IsActive = 1
            ORDER BY TriggerAtTaipei DESC, Id DESC;
            """;
        AddStringParameter(command, "@symbol", _symbol, 32);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? await ReadEntryRecommendationEventAsync(reader, cancellationToken)
            : null;
    }

    public async Task<IReadOnlyList<EntryRecommendationEvent>> LoadRecommendationEventsAsync(
        int maximumRows,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT TOP (@maximumRows)
                {EntryRecommendationSelectColumns}
            FROM dbo.EntryRecommendationEvents
            WHERE Symbol = @symbol
            ORDER BY TriggerAtTaipei DESC, Id DESC;
            """;
        command.Parameters.Add("@maximumRows", SqlDbType.Int).Value = maximumRows;
        AddStringParameter(command, "@symbol", _symbol, 32);

        var events = new List<EntryRecommendationEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(await ReadEntryRecommendationEventAsync(reader, cancellationToken));
        }

        return events;
    }

    public async Task<RecommendationConfidence> LoadRecommendationConfidenceAsync(
        StrategySide side,
        string eventType,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COALESCE(SUM(CASE WHEN Outcome = N'take_profit' THEN 1 ELSE 0 END), 0) AS TakeProfitCount,
                COALESCE(SUM(CASE WHEN Outcome = N'stop_loss' THEN 1 ELSE 0 END), 0) AS StopLossCount,
                COALESCE(SUM(CASE WHEN EntryLow IS NOT NULL AND IsActive = 0 THEN 1 ELSE 0 END), 0) AS QuotedCount,
                COALESCE(SUM(CASE WHEN EntryLow IS NOT NULL AND EnteredAtTaipei IS NOT NULL THEN 1 ELSE 0 END), 0) AS EnteredCount
            FROM dbo.EntryRecommendationEvents
            WHERE Symbol = @symbol
              AND Side = @side
              AND EventType = @eventType;
            """;
        AddStringParameter(command, "@symbol", _symbol, 32);
        AddStringParameter(command, "@side", side.ToString().ToLowerInvariant(), 16);
        AddStringParameter(command, "@eventType", eventType, 32);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return RecommendationConfidence.Unavailable;
        }

        var wins = reader.GetInt32(0);
        var losses = reader.GetInt32(1);
        var quoted = reader.GetInt32(2);
        var entered = reader.GetInt32(3);
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

    public async Task<RecommendationChange?> TryInsertRecommendationEventAsync(
        EntryRecommendationEvent recommendation,
        DateTimeOffset changedAt,
        string note,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);

        if (recommendation.IsActive)
        {
            using var activeCommand = connection.CreateCommand();
            activeCommand.Transaction = transaction;
            activeCommand.CommandText = """
                SELECT COUNT_BIG(*)
                FROM dbo.EntryRecommendationEvents WITH (UPDLOCK, HOLDLOCK)
                WHERE Symbol = @symbol
                  AND IsActive = 1;
                """;
            AddStringParameter(activeCommand, "@symbol", recommendation.Symbol, 32);
            if (Convert.ToInt64(await activeCommand.ExecuteScalarAsync(cancellationToken)) > 0)
            {
                transaction.Rollback();
                return null;
            }
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO dbo.EntryRecommendationEvents
                (Symbol, EvaluatedAtTaipei, TriggerAtTaipei, TriggerBarStartTaipei, TriggerBarEndTaipei,
                 Side, EventType, TriggerRule, PreviousClose, PreviousSma76, TriggerClose, TriggerSma76,
                 TriggerSma20, TriggerAtr5, MarketContextSnapshot, ReferenceEventId, ReferenceAtrRatio,
                 ReferenceAnchor, ReferenceAtr5, EntryLow, EntryHigh, StopLoss, TakeProfit, RewardRiskRatio,
                 Status, IsActive, EnteredAtTaipei, EntryPrice, ExitPrice, ProfitPoints, CompletedAtTaipei, Outcome,
                 ConfidenceStatus, ConfidenceScore, ConfidenceSampleCount, EntryHitRate, RecommendationPrice, SourceMode)
            OUTPUT INSERTED.Id
            SELECT
                @symbol, @evaluatedAtTaipei, @triggerAtTaipei, @triggerBarStartTaipei, @triggerBarEndTaipei,
                @side, @eventType, @triggerRule, @previousClose, @previousSma76, @triggerClose, @triggerSma76,
                @triggerSma20, @triggerAtr5, @marketContextSnapshot, @referenceEventId, @referenceAtrRatio,
                @referenceAnchor, @referenceAtr5, @entryLow, @entryHigh, @stopLoss, @takeProfit, @rewardRiskRatio,
                @status, @isActive, @enteredAtTaipei, @entryPrice, @exitPrice, @profitPoints, @completedAtTaipei, @outcome,
                @confidenceStatus, @confidenceScore, @confidenceSampleCount, @entryHitRate, @recommendationPrice, @sourceMode
            WHERE NOT EXISTS
            (
                SELECT 1
                FROM dbo.EntryRecommendationEvents WITH (UPDLOCK, HOLDLOCK)
                WHERE Symbol = @symbol
                  AND TriggerBarStartTaipei = @triggerBarStartTaipei
                  AND Side = @side
                  AND EventType = @eventType
            );
            """;
        AddEntryRecommendationParameters(command, recommendation);

        var insertedId = await command.ExecuteScalarAsync(cancellationToken);
        if (insertedId is null or DBNull)
        {
            transaction.Rollback();
            return null;
        }

        var stored = recommendation with { Id = Convert.ToInt64(insertedId) };
        await InsertStatusHistoryAsync(
            connection,
            transaction,
            stored.Id,
            changedAt,
            null,
            stored.Status,
            stored.RecommendationPrice,
            note,
            cancellationToken);
        transaction.Commit();

        return new RecommendationChange(stored, null, changedAt, stored.RecommendationPrice, note);
    }

    public async Task<RecommendationChange?> TryTransitionRecommendationAsync(
        EntryRecommendationEvent recommendation,
        RecommendationTransition transition,
        CancellationToken cancellationToken)
    {
        var updated = recommendation.Apply(transition);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE dbo.EntryRecommendationEvents WITH (UPDLOCK)
            SET Status = @newStatus,
                IsActive = @isActive,
                StopLoss = @stopLoss,
                TakeProfit = @takeProfit,
                EnteredAtTaipei = @enteredAtTaipei,
                EntryPrice = @entryPrice,
                ExitPrice = @exitPrice,
                ProfitPoints = @profitPoints,
                CompletedAtTaipei = @completedAtTaipei,
                Outcome = @outcome,
                ModifiedAtUtc = SYSUTCDATETIME()
            WHERE Id = @id
              AND Status = @expectedStatus;
            """;
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = recommendation.Id;
        AddStringParameter(command, "@expectedStatus", recommendation.Status, 64);
        AddStringParameter(command, "@newStatus", updated.Status, 64);
        command.Parameters.Add("@isActive", SqlDbType.Bit).Value = updated.IsActive;
        AddNullableDecimalParameter(command, "@stopLoss", updated.StopLoss);
        AddNullableDecimalParameter(command, "@takeProfit", updated.TakeProfit);
        AddNullableDateTimeOffsetParameter(command, "@enteredAtTaipei", updated.EnteredAt);
        AddNullableDecimalParameter(command, "@entryPrice", updated.EntryPrice);
        AddNullableDecimalParameter(command, "@exitPrice", updated.ExitPrice);
        AddNullableDecimalParameter(command, "@profitPoints", updated.ProfitPoints);
        AddNullableDateTimeOffsetParameter(command, "@completedAtTaipei", updated.CompletedAt);
        AddNullableStringParameter(command, "@outcome", updated.Outcome, 32);

        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            transaction.Rollback();
            return null;
        }

        await InsertStatusHistoryAsync(
            connection,
            transaction,
            recommendation.Id,
            transition.ChangedAt,
            recommendation.Status,
            transition.NewStatus,
            transition.ObservedPrice,
            transition.Note,
            cancellationToken);
        transaction.Commit();

        return new RecommendationChange(
            updated,
            recommendation.Status,
            transition.ChangedAt,
            transition.ObservedPrice,
            transition.Note);
    }

    public async Task<int> RewriteRecommendationEventsAsync(
        IReadOnlyList<RecommendationReplayResult> results,
        CancellationToken cancellationToken)
    {
        if (results.Count == 0)
        {
            return 0;
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);

        using (var clearActiveCommand = connection.CreateCommand())
        {
            clearActiveCommand.Transaction = transaction;
            clearActiveCommand.CommandText = """
                UPDATE dbo.EntryRecommendationEvents WITH (UPDLOCK)
                SET IsActive = 0,
                    ModifiedAtUtc = SYSUTCDATETIME()
                WHERE Symbol = @symbol;
                """;
            AddStringParameter(clearActiveCommand, "@symbol", _symbol, 32);
            await clearActiveCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var updated = 0;
        foreach (var result in results)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE dbo.EntryRecommendationEvents WITH (UPDLOCK)
                SET ReferenceEventId = @referenceEventId,
                    ReferenceAtrRatio = @referenceAtrRatio,
                    ReferenceAnchor = @referenceAnchor,
                    ReferenceAtr5 = @referenceAtr5,
                    EntryLow = @entryLow,
                    EntryHigh = @entryHigh,
                    StopLoss = @stopLoss,
                    TakeProfit = @takeProfit,
                    RewardRiskRatio = @rewardRiskRatio,
                    Status = @status,
                    IsActive = @isActive,
                    EnteredAtTaipei = @enteredAtTaipei,
                    EntryPrice = @entryPrice,
                    ExitPrice = @exitPrice,
                    ProfitPoints = @profitPoints,
                    CompletedAtTaipei = @completedAtTaipei,
                    Outcome = @outcome,
                    ConfidenceStatus = @confidenceStatus,
                    ConfidenceScore = @confidenceScore,
                    ConfidenceSampleCount = @confidenceSampleCount,
                    EntryHitRate = @entryHitRate,
                    SourceMode = @sourceMode,
                    ModifiedAtUtc = SYSUTCDATETIME()
                WHERE Id = @id
                  AND Symbol = @symbol;
                """;
            command.Parameters.Add("@id", SqlDbType.BigInt).Value = result.Event.Id;
            AddEntryRecommendationParameters(command, result.Event);
            updated += await command.ExecuteNonQueryAsync(cancellationToken);

            using var deleteHistoryCommand = connection.CreateCommand();
            deleteHistoryCommand.Transaction = transaction;
            deleteHistoryCommand.CommandText = """
                DELETE FROM dbo.EntryRecommendationStatusHistory
                WHERE RecommendationEventId = @recommendationEventId;
                """;
            deleteHistoryCommand.Parameters.Add("@recommendationEventId", SqlDbType.BigInt).Value = result.Event.Id;
            await deleteHistoryCommand.ExecuteNonQueryAsync(cancellationToken);

            foreach (var history in result.StatusHistory)
            {
                await InsertStatusHistoryAsync(
                    connection,
                    transaction,
                    result.Event.Id,
                    history.ChangedAt,
                    history.PreviousStatus,
                    history.NewStatus,
                    history.ObservedPrice,
                    history.Note,
                    cancellationToken);
            }
        }

        transaction.Commit();
        return updated;
    }

    private static async Task InsertStatusHistoryAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long recommendationEventId,
        DateTimeOffset changedAt,
        string? previousStatus,
        string newStatus,
        decimal? observedPrice,
        string note,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO dbo.EntryRecommendationStatusHistory
                (RecommendationEventId, ChangedAtTaipei, PreviousStatus, NewStatus, ObservedPrice, Note)
            VALUES
                (@recommendationEventId, @changedAtTaipei, @previousStatus, @newStatus, @observedPrice, @note);
            """;
        command.Parameters.Add("@recommendationEventId", SqlDbType.BigInt).Value = recommendationEventId;
        AddDateTimeOffsetParameter(command, "@changedAtTaipei", changedAt);
        AddNullableStringParameter(command, "@previousStatus", previousStatus, 64);
        AddStringParameter(command, "@newStatus", newStatus, 64);
        AddNullableDecimalParameter(command, "@observedPrice", observedPrice);
        AddStringParameter(command, "@note", note, 512);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddEntryRecommendationParameters(
        SqlCommand command,
        EntryRecommendationEvent recommendation)
    {
        AddStringParameter(command, "@symbol", recommendation.Symbol, 32);
        AddDateTimeOffsetParameter(command, "@evaluatedAtTaipei", recommendation.EvaluatedAt);
        AddDateTimeOffsetParameter(command, "@triggerAtTaipei", recommendation.TriggerAt);
        AddDateTimeOffsetParameter(command, "@triggerBarStartTaipei", recommendation.TriggerBarStart);
        AddDateTimeOffsetParameter(command, "@triggerBarEndTaipei", recommendation.TriggerBarEnd);
        AddStringParameter(command, "@side", recommendation.Side.ToString().ToLowerInvariant(), 16);
        AddStringParameter(command, "@eventType", recommendation.EventType, 32);
        AddStringParameter(command, "@triggerRule", recommendation.TriggerRule, 64);
        AddDecimalParameter(command, "@previousClose", recommendation.PreviousClose);
        AddDecimalParameter(command, "@previousSma76", recommendation.PreviousSma76);
        AddDecimalParameter(command, "@triggerClose", recommendation.TriggerClose);
        AddDecimalParameter(command, "@triggerSma76", recommendation.TriggerSma76);
        AddNullableDecimalParameter(command, "@triggerSma20", recommendation.TriggerSma20);
        AddNullableDecimalParameter(command, "@triggerAtr5", recommendation.TriggerAtr5);
        AddStringParameter(command, "@marketContextSnapshot", recommendation.MarketContextSnapshot, -1);
        command.Parameters.Add("@referenceEventId", SqlDbType.BigInt).Value =
            recommendation.ReferenceEventId is { } referenceEventId ? referenceEventId : DBNull.Value;
        AddNullableDecimalParameter(command, "@referenceAtrRatio", recommendation.ReferenceAtrRatio);
        AddNullableDecimalParameter(command, "@referenceAnchor", recommendation.ReferenceAnchor);
        AddNullableDecimalParameter(command, "@referenceAtr5", recommendation.ReferenceAtr5);
        AddNullableDecimalParameter(command, "@entryLow", recommendation.EntryLow);
        AddNullableDecimalParameter(command, "@entryHigh", recommendation.EntryHigh);
        AddNullableDecimalParameter(command, "@stopLoss", recommendation.StopLoss);
        AddNullableDecimalParameter(command, "@takeProfit", recommendation.TakeProfit);
        AddNullableDecimalParameter(command, "@rewardRiskRatio", recommendation.RewardRiskRatio);
        AddStringParameter(command, "@status", recommendation.Status, 64);
        command.Parameters.Add("@isActive", SqlDbType.Bit).Value = recommendation.IsActive;
        AddNullableDateTimeOffsetParameter(command, "@enteredAtTaipei", recommendation.EnteredAt);
        AddNullableDecimalParameter(command, "@entryPrice", recommendation.EntryPrice);
        AddNullableDecimalParameter(command, "@exitPrice", recommendation.ExitPrice);
        AddNullableDecimalParameter(command, "@profitPoints", recommendation.ProfitPoints);
        AddNullableDateTimeOffsetParameter(command, "@completedAtTaipei", recommendation.CompletedAt);
        AddNullableStringParameter(command, "@outcome", recommendation.Outcome, 32);
        AddStringParameter(command, "@confidenceStatus", recommendation.ConfidenceStatus, 32);
        AddNullableDecimalParameter(command, "@confidenceScore", recommendation.ConfidenceScore);
        command.Parameters.Add("@confidenceSampleCount", SqlDbType.Int).Value = recommendation.ConfidenceSampleCount;
        AddNullableDecimalParameter(command, "@entryHitRate", recommendation.EntryHitRate);
        AddNullableDecimalParameter(command, "@recommendationPrice", recommendation.RecommendationPrice);
        AddStringParameter(command, "@sourceMode", recommendation.SourceMode, 16);
    }

    private static async Task<EntryRecommendationEvent> ReadEntryRecommendationEventAsync(
        SqlDataReader reader,
        CancellationToken cancellationToken)
    {
        var sideText = reader.GetString(6);
        if (!TryParseStrategySide(sideText, out var side))
        {
            throw new InvalidOperationException($"Unsupported recommendation side: {sideText}");
        }

        return new EntryRecommendationEvent(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetDateTimeOffset(2),
            reader.GetDateTimeOffset(3),
            reader.GetDateTimeOffset(4),
            reader.GetDateTimeOffset(5),
            side,
            reader.GetString(7),
            reader.GetString(8),
            reader.GetDecimal(9),
            reader.GetDecimal(10),
            reader.GetDecimal(11),
            reader.GetDecimal(12),
            await reader.IsDBNullAsync(13, cancellationToken) ? null : reader.GetDecimal(13),
            await reader.IsDBNullAsync(14, cancellationToken) ? null : reader.GetDecimal(14),
            reader.GetString(15),
            await reader.IsDBNullAsync(16, cancellationToken) ? null : reader.GetInt64(16),
            await reader.IsDBNullAsync(17, cancellationToken) ? null : reader.GetDecimal(17),
            await reader.IsDBNullAsync(18, cancellationToken) ? null : reader.GetDecimal(18),
            await reader.IsDBNullAsync(19, cancellationToken) ? null : reader.GetDecimal(19),
            await reader.IsDBNullAsync(20, cancellationToken) ? null : reader.GetDecimal(20),
            await reader.IsDBNullAsync(21, cancellationToken) ? null : reader.GetDecimal(21),
            await reader.IsDBNullAsync(22, cancellationToken) ? null : reader.GetDecimal(22),
            await reader.IsDBNullAsync(23, cancellationToken) ? null : reader.GetDecimal(23),
            await reader.IsDBNullAsync(24, cancellationToken) ? null : reader.GetDecimal(24),
            reader.GetString(25),
            reader.GetBoolean(26),
            await reader.IsDBNullAsync(27, cancellationToken) ? null : reader.GetDateTimeOffset(27),
            await reader.IsDBNullAsync(28, cancellationToken) ? null : reader.GetDecimal(28),
            await reader.IsDBNullAsync(37, cancellationToken) ? null : reader.GetDecimal(37),
            await reader.IsDBNullAsync(38, cancellationToken) ? null : reader.GetDecimal(38),
            await reader.IsDBNullAsync(29, cancellationToken) ? null : reader.GetDateTimeOffset(29),
            await reader.IsDBNullAsync(30, cancellationToken) ? null : reader.GetString(30),
            reader.GetString(31),
            await reader.IsDBNullAsync(32, cancellationToken) ? null : reader.GetDecimal(32),
            reader.GetInt32(33),
            await reader.IsDBNullAsync(34, cancellationToken) ? null : reader.GetDecimal(34),
            await reader.IsDBNullAsync(35, cancellationToken) ? null : reader.GetDecimal(35),
            reader.GetString(36));
    }

    private const string EntryRecommendationSelectColumns = """
        Id,
        Symbol,
        EvaluatedAtTaipei,
        TriggerAtTaipei,
        TriggerBarStartTaipei,
        TriggerBarEndTaipei,
        Side,
        EventType,
        TriggerRule,
        PreviousClose,
        PreviousSma76,
        TriggerClose,
        TriggerSma76,
        TriggerSma20,
        TriggerAtr5,
        MarketContextSnapshot,
        ReferenceEventId,
        ReferenceAtrRatio,
        ReferenceAnchor,
        ReferenceAtr5,
        EntryLow,
        EntryHigh,
        StopLoss,
        TakeProfit,
        RewardRiskRatio,
        Status,
        IsActive,
        EnteredAtTaipei,
        EntryPrice,
        CompletedAtTaipei,
        Outcome,
        ConfidenceStatus,
        ConfidenceScore,
        ConfidenceSampleCount,
        EntryHitRate,
        RecommendationPrice,
        SourceMode,
        ExitPrice,
        ProfitPoints
        """;
}
