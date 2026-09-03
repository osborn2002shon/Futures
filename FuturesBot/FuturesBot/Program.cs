using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

var options = AppOptions.Parse(args);
var taipeiTimeZone = TaipeiClock.GetTimeZone();
var strategyTextReporter = new StrategyTextReporter(options.DataDirectory);
using var shutdown = new CancellationTokenSource();

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

var httpClient = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds)
};

httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-TW,zh;q=0.9,en;q=0.6");

var quoteClient = new YahooFuturesQuoteClient(httpClient, options);
var telegramNotifier = TelegramNotifier.Create(
    httpClient,
    options.TelegramBotToken,
    options.TelegramChatId,
    options.TelegramDashboardUrl);
var store = options.DryRun && !options.InitDatabase && !options.RecalculateRecommendations
    ? null
    : await TryCreateSqlStoreAsync(options, taipeiTimeZone, shutdown.Token);

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine($"Yahoo futures collector started for {options.Symbol}");
if (store is not null)
{
    Console.WriteLine($"Database: {options.DatabaseDisplayName}");
    Console.WriteLine("Tables: dbo.FuturesTicks, dbo.FuturesKBars, dbo.StrategyScores, dbo.EntryRecommendationEvents, dbo.EntryRecommendationStatusHistory, dbo.FuturesErrorLogs");
}
else if (!options.DryRun)
{
    Console.WriteLine($"Database unavailable: {options.DatabaseDisplayName}. Will retry each minute.");
}

Console.WriteLine($"Market hours filter: {(options.IgnoreMarketHours ? "off" : "on")}");
Console.WriteLine($"Fetch timing: once per minute at +{options.FetchDelaySeconds}s after the minute closes");
Console.WriteLine($"Text output: {options.DataDirectory}");
WriteConfigStatus(options);
WriteTelegramStatus(telegramNotifier, options);
await TrySendStartupTestMessageAsync(telegramNotifier, options, taipeiTimeZone, shutdown.Token);

if (options.InitDatabase)
{
    Console.WriteLine(store is null ? "Database schema initialization failed." : "Database schema initialized.");
    return;
}

if (options.RecalculateRecommendations)
{
    if (store is null)
    {
        Console.WriteLine("Recommendation recalculation failed: database is unavailable.");
        return;
    }

    var summary = await EntryRecommendationRecalculator.RecalculateAsync(store, shutdown.Token);
    Console.WriteLine(
        "Recommendation recalculation complete: " +
        $"{summary.EventCount} events, {summary.UpdatedCount} rewritten, " +
        $"{summary.PriceChangedCount} price changes, {summary.StatusChangedCount} status changes, " +
        $"{summary.StatusHistoryRowsWritten} status history rows.");
    return;
}

if (options.RunOnce || options.DryRun)
{
    await RunCollectionOnceAsync(quoteClient, store, strategyTextReporter, telegramNotifier, options, taipeiTimeZone, shutdown.Token);
    return;
}

while (!shutdown.IsCancellationRequested)
{
    try
    {
        await DelayUntilNextMinuteCloseAsync(options.FetchDelaySeconds, shutdown.Token);

        if (store is null)
        {
            store = await TryCreateSqlStoreAsync(options, taipeiTimeZone, shutdown.Token);
            if (store is null)
            {
                continue;
            }

            Console.WriteLine($"Database connected: {options.DatabaseDisplayName}");
        }

        await RunCollectionOnceAsync(quoteClient, store, strategyTextReporter, telegramNotifier, options, taipeiTimeZone, shutdown.Token);
    }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
    {
        break;
    }
    catch (Exception ex)
    {
        var failedAt = TaipeiClock.Now(taipeiTimeZone);
        Console.Error.WriteLine($"{failedAt:yyyy-MM-dd HH:mm:ss zzz} collector loop failed; skipped minute: {ex.Message}");
        if (store is not null)
        {
            await store.TryAppendErrorAsync(failedAt, "collector_loop", ex);
        }
    }
}

static async Task<FuturesSqlStore?> TryCreateSqlStoreAsync(
    AppOptions options,
    TimeZoneInfo taipeiTimeZone,
    CancellationToken cancellationToken)
{
    try
    {
        return await FuturesSqlStore.CreateAsync(
            options.MasterConnectionString,
            options.DatabaseConnectionString,
            options.SqlDatabase,
            options.Symbol,
            taipeiTimeZone,
            cancellationToken);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        var failedAt = TaipeiClock.Now(taipeiTimeZone);
        Console.Error.WriteLine($"{failedAt:yyyy-MM-dd HH:mm:ss zzz} SQL initialization failed; skipped minute: {ex.Message}");
        return null;
    }
}

static async Task RunCollectionOnceAsync(
    YahooFuturesQuoteClient quoteClient,
    FuturesSqlStore? store,
    StrategyTextReporter strategyTextReporter,
    TelegramNotifier? telegramNotifier,
    AppOptions options,
    TimeZoneInfo taipeiTimeZone,
    CancellationToken cancellationToken)
{
    var capturedAt = TaipeiClock.Now(taipeiTimeZone);

    try
    {
        if (!options.DryRun && store is not null)
        {
            await TrySettleRecommendationAtNightCloseAsync(
                capturedAt,
                store,
                strategyTextReporter,
                telegramNotifier,
                cancellationToken);
        }

        if (!options.IgnoreMarketHours && !TaiwanFuturesMarketHours.IsCollectionTime(capturedAt.DateTime, options.FetchDelaySeconds))
        {
            Console.WriteLine($"{capturedAt:yyyy-MM-dd HH:mm:ss zzz} market closed; waiting.");
            return;
        }

        if (!options.DryRun && store is null)
        {
            throw new InvalidOperationException("SQL store is unavailable.");
        }

        var quote = await quoteClient.FetchQuoteAsync(capturedAt, cancellationToken);
        var maxQuoteAge = TimeSpan.FromMinutes(options.MaxQuoteAgeMinutes);

        if (quote.MarketDateTime is null)
        {
            Console.WriteLine($"{capturedAt:yyyy-MM-dd HH:mm:ss zzz} skipped: Yahoo update time was not found.");
            return;
        }

        var quoteAge = capturedAt - quote.MarketDateTime.Value;
        if (quoteAge > maxQuoteAge)
        {
            Console.WriteLine($"{capturedAt:yyyy-MM-dd HH:mm:ss zzz} skipped stale quote: Yahoo updated at {quote.MarketDateTime.Value:yyyy-MM-dd HH:mm:ss zzz}, age {quoteAge.TotalMinutes:0.0} minutes.");
            return;
        }

        if (quoteAge < TimeSpan.FromMinutes(-2))
        {
            Console.WriteLine($"{capturedAt:yyyy-MM-dd HH:mm:ss zzz} skipped: Yahoo update time is unexpectedly in the future ({quote.MarketDateTime.Value:yyyy-MM-dd HH:mm:ss zzz}).");
            return;
        }

        var point = new FuturesTick(
            options.Symbol,
            capturedAt,
            FormatDateTime(quote.MarketDateTime.Value),
            quote.Price,
            quote.SourceUrl);

        if (options.DryRun)
        {
            Console.WriteLine($"{point.CapturedAt:yyyy-MM-dd HH:mm:ss zzz} {point.Symbol} price={point.Price} yahooUpdatedAt={point.SourceMarketTime}");
            return;
        }

        var sqlStore = store ?? throw new InvalidOperationException("SQL store is unavailable.");
        var stored = await sqlStore.TryAppendTickAndWriteBarsAsync(point, cancellationToken);
        if (!stored)
        {
            Console.WriteLine($"{point.CapturedAt:yyyy-MM-dd HH:mm:ss zzz} skipped duplicate minute: Yahoo updated at {point.SourceMarketTime}");
            return;
        }

        Console.WriteLine($"{point.CapturedAt:yyyy-MM-dd HH:mm:ss zzz} stored {point.Symbol} price={point.Price} yahooUpdatedAt={point.SourceMarketTime}");
        await TryEvaluateEntryStrategyAsync(point, sqlStore, strategyTextReporter, telegramNotifier, cancellationToken);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        Console.Error.WriteLine($"{capturedAt:yyyy-MM-dd HH:mm:ss zzz} collection failed; skipped minute: {ex.Message}");
        if (store is not null)
        {
            await store.TryAppendErrorAsync(capturedAt, "collection", ex);
        }
    }
}

static string FormatDateTime(DateTimeOffset value) => value.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

static async Task TrySettleRecommendationAtNightCloseAsync(
    DateTimeOffset evaluatedAt,
    FuturesSqlStore store,
    StrategyTextReporter strategyTextReporter,
    TelegramNotifier? telegramNotifier,
    CancellationToken cancellationToken)
{
    var active = await store.LoadActiveRecommendationEventAsync(cancellationToken);
    if (active is null)
    {
        return;
    }

    var nightClose = TaiwanFuturesMarketHours.GetTradingDayNightSessionEnd(active.TriggerAt);
    if (nightClose is null || evaluatedAt < nightClose.Value)
    {
        return;
    }

    var changes = new List<RecommendationChange>();
    await ApplyLifecycleTransitionsAsync(
        active,
        evaluatedAt,
        store.GetTicksSnapshot(),
        store.GetBarsSnapshot(5),
        store,
        changes,
        cancellationToken);

    if (changes.Count == 0)
    {
        return;
    }

    await strategyTextReporter.AppendRecommendationChangesAsync(changes, cancellationToken);
    foreach (var change in changes)
    {
        WriteRecommendationChangeLine(change);
    }

    if (telegramNotifier is not null)
    {
        await telegramNotifier.SendRecommendationChangesAsync(changes, cancellationToken);
    }
}

static async Task TryEvaluateEntryStrategyAsync(
    FuturesTick point,
    FuturesSqlStore store,
    StrategyTextReporter strategyTextReporter,
    TelegramNotifier? telegramNotifier,
    CancellationToken cancellationToken)
{
    var sampleMinute = MarketDataClock.GetSampleMinute(point);
    var fifteenMinuteBars = store.GetBarsSnapshot(15);
    var fiveMinuteBars = store.GetBarsSnapshot(5);
    var oneMinuteBars = store.GetBarsSnapshot(1);
    var ticks = store.GetTicksSnapshot();
    var scores = EntryStrategyEvaluator.Evaluate(
        point.CapturedAt,
        sampleMinute,
        fifteenMinuteBars,
        oneMinuteBars);

    // 六個舊條件只保存為盤勢燈號；自動建議不再讀取分數門檻。
    var changedScores = await store.AppendChangedStrategyScoresAsync(scores, cancellationToken);
    await strategyTextReporter.AppendStrategyScoresAsync(changedScores, cancellationToken);
    foreach (var score in changedScores)
    {
        WriteMarketContextLine(point.CapturedAt, score);
    }

    var changes = new List<RecommendationChange>();
    var active = await store.LoadActiveRecommendationEventAsync(cancellationToken);
    active = await ApplyLifecycleTransitionsAsync(
        active,
        sampleMinute,
        ticks,
        fiveMinuteBars,
        store,
        changes,
        cancellationToken);

    var trigger = Sma76TriggerEvaluator.Evaluate(
        point.Symbol,
        point.CapturedAt,
        sampleMinute,
        fifteenMinuteBars,
        fiveMinuteBars,
        scores);
    if (trigger is not null)
    {
        var confidence = await store.LoadRecommendationConfidenceAsync(
            trigger.Side,
            trigger.EventType,
            cancellationToken);
        var previousEvents = await store.LoadRecommendationEventsAsync(500, cancellationToken);
        var draft = EntryRecommendationPriceCalculator.Build(
            trigger,
            previousEvents,
            fiveMinuteBars,
            confidence);
        var shouldInsertRecommendation = true;

        if (active is not null && EntryRecommendationStatuses.IsActive(draft.Status))
        {
            if (IsSameRecommendationTrigger(active, trigger))
            {
                shouldInsertRecommendation = false;
            }
            else if (active.Status == EntryRecommendationStatuses.WaitingEntry
                     && active.Side != trigger.Side)
            {
                var cancellation = RecommendationLifecycleEvaluator.CancelForOppositeTrigger(active, trigger);
                var cancelledChange = await store.TryTransitionRecommendationAsync(active, cancellation, cancellationToken);
                if (cancelledChange is not null)
                {
                    changes.Add(cancelledChange);
                    active = null;
                }
                else
                {
                    active = await store.LoadActiveRecommendationEventAsync(cancellationToken);
                }
            }
            else
            {
                draft = EntryRecommendationPriceCalculator.Suppressed(confidence);
            }
        }

        if (shouldInsertRecommendation)
        {
            var recommendation = EntryRecommendationEvent.FromDraft(trigger, draft, point.Price);
            var insertedChange = await store.TryInsertRecommendationEventAsync(
                recommendation,
                point.CapturedAt,
                GetInitialRecommendationNote(draft.Status),
                cancellationToken);
            if (insertedChange is not null)
            {
                changes.Add(insertedChange);
                if (insertedChange.Recommendation.IsActive)
                {
                    active = insertedChange.Recommendation;
                }

                active = await ApplyLifecycleTransitionsAsync(
                    active,
                    sampleMinute,
                    ticks,
                    fiveMinuteBars,
                    store,
                    changes,
                    cancellationToken);
            }
        }
    }

    await strategyTextReporter.AppendRecommendationChangesAsync(changes, cancellationToken);
    foreach (var change in changes)
    {
        WriteRecommendationChangeLine(change);
    }

    if (telegramNotifier is not null)
    {
        await telegramNotifier.SendRecommendationChangesAsync(changes, cancellationToken);
    }
}

static async Task<EntryRecommendationEvent?> ApplyLifecycleTransitionsAsync(
    EntryRecommendationEvent? active,
    DateTimeOffset sampleMinute,
    IReadOnlyCollection<FuturesTick> ticks,
    IReadOnlyCollection<KBar> fiveMinuteBars,
    FuturesSqlStore store,
    ICollection<RecommendationChange> changes,
    CancellationToken cancellationToken)
{
    while (active is not null)
    {
        var transition = RecommendationLifecycleEvaluator.Evaluate(
            active,
            ticks,
            fiveMinuteBars,
            sampleMinute);
        if (transition is null)
        {
            break;
        }

        var change = await store.TryTransitionRecommendationAsync(active, transition, cancellationToken);
        if (change is null)
        {
            return await store.LoadActiveRecommendationEventAsync(cancellationToken);
        }

        changes.Add(change);
        active = change.Recommendation.IsActive ? change.Recommendation : null;
    }

    return active;
}

static bool IsSameRecommendationTrigger(EntryRecommendationEvent active, Sma76Trigger trigger) =>
    active.Symbol == trigger.Symbol
    && active.TriggerBarStart == trigger.TriggerBarStart
    && active.Side == trigger.Side
    && string.Equals(active.EventType, trigger.EventType, StringComparison.Ordinal);

static string GetInitialRecommendationNote(string status) =>
    status switch
    {
        EntryRecommendationStatuses.WaitingEntry => "SMA76 觸發成立，已依最近同方向事件建立價格",
        EntryRecommendationStatuses.NoReferenceEvent => "SMA76 觸發成立，但沒有合格歷史參考事件",
        EntryRecommendationStatuses.InsufficientPriceData => "SMA76 觸發成立，但 5 分 K ATR14 資料不足",
        EntryRecommendationStatuses.InvalidPriceStructure => "歷史事件存在，但映射後價格結構或報酬風險比無效",
        EntryRecommendationStatuses.SuppressedByActiveRecommendation => "已有未結束建議，本次只保留觸發事件",
        _ => "SMA76 觸發事件建立"
    };

static void WriteMarketContextLine(DateTimeOffset capturedAt, StrategyScore score)
{
    var side = score.Side.ToString().ToLowerInvariant();
    var barEnd = score.BarEnd is null ? "-" : FormatDateTime(score.BarEnd.Value);
    var statusColor = score.Status == "ok" ? ConsoleColor.Green : ConsoleColor.Yellow;
    var consolidationColor = score.ConsolidationState switch
    {
        "false" => ConsoleColor.Green,
        "true" => ConsoleColor.Red,
        _ => ConsoleColor.Yellow
    };

    WriteColored($"{capturedAt:yyyy-MM-dd HH:mm:ss zzz} ", ConsoleColor.DarkGray);
    WriteColored($"market-context {side} ", side == "long" ? ConsoleColor.Cyan : ConsoleColor.Magenta);
    WriteColored("status=", ConsoleColor.DarkGray);
    WriteColored($"{score.Status} ", statusColor);
    WriteColored("consolidation=", ConsoleColor.DarkGray);
    WriteColored($"{score.ConsolidationState} ", consolidationColor);
    WriteColored($"barEnd={barEnd} ", ConsoleColor.DarkGray);
    Console.WriteLine();
}

static void WriteRecommendationChangeLine(RecommendationChange change)
{
    var recommendation = change.Recommendation;
    var color = recommendation.Side == StrategySide.Long ? ConsoleColor.Cyan : ConsoleColor.Magenta;
    WriteColored($"{change.ChangedAt:yyyy-MM-dd HH:mm:ss zzz} ", ConsoleColor.DarkGray);
    WriteColored($"recommendation #{recommendation.Id} ", color);
    WriteColored($"{recommendation.Side.ToString().ToLowerInvariant()} ", color);
    WriteColored($"{change.PreviousStatus ?? "new"} -> {recommendation.Status} ", ConsoleColor.Yellow);
    WriteColored($"recommendationPrice={FormatNullablePoint(recommendation.RecommendationPrice)} ", ConsoleColor.White);
    WriteColored($"entry={FormatRange(recommendation.EntryLow, recommendation.EntryHigh)} ", ConsoleColor.White);
    WriteColored($"stop={FormatNullablePoint(recommendation.StopLoss)} ", ConsoleColor.White);
    WriteColored($"take={FormatNullablePoint(recommendation.TakeProfit)} ", ConsoleColor.White);
    WriteColored($"exit={FormatNullablePoint(recommendation.ExitPrice)} ", ConsoleColor.White);
    WriteColored($"pnl={FormatNullableSignedPoint(recommendation.ProfitPoints)} ", ConsoleColor.White);
    WriteColored($"confidence={recommendation.ConfidenceStatus}/{recommendation.ConfidenceSampleCount}", ConsoleColor.DarkGray);
    Console.WriteLine();
}

static string FormatRange(decimal? low, decimal? high) =>
    low is null || high is null ? "-" : $"{low:0}-{high:0}";

static string FormatNullablePoint(decimal? value) => value?.ToString("0", CultureInfo.InvariantCulture) ?? "-";

static string FormatNullableSignedPoint(decimal? value) =>
    value is { } points
        ? points.ToString(points > 0 ? "+0" : "0", CultureInfo.InvariantCulture)
        : "-";

static void WriteColored(string text, ConsoleColor color)
{
    var previousColor = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.Write(text);
    Console.ForegroundColor = previousColor;
}

static async Task DelayUntilNextMinuteCloseAsync(int fetchDelaySeconds, CancellationToken cancellationToken)
{
    var now = DateTimeOffset.Now;
    var currentMinuteTrigger = new DateTimeOffset(
        now.Year,
        now.Month,
        now.Day,
        now.Hour,
        now.Minute,
        0,
        now.Offset).AddSeconds(fetchDelaySeconds);

    var nextRunAt = now < currentMinuteTrigger
        ? currentMinuteTrigger
        : currentMinuteTrigger.AddMinutes(1);

    await Task.Delay(nextRunAt - now, cancellationToken);
}

static void WriteConfigStatus(AppOptions options)
{
    if (!string.IsNullOrWhiteSpace(options.ConfigLoadError))
    {
        Console.WriteLine($"Config file: error reading {options.ConfigFilePath}: {options.ConfigLoadError}");
        return;
    }

    Console.WriteLine(string.IsNullOrWhiteSpace(options.ConfigFilePath)
        ? "Config file: not found (futuresbot.config.json)"
        : $"Config file: loaded {options.ConfigFilePath}");
}

static void WriteTelegramStatus(TelegramNotifier? telegramNotifier, AppOptions options)
{
    Console.WriteLine($"Telegram notifications: {(telegramNotifier is null ? "off" : "on")}");
    Console.WriteLine($"Telegram bot token: {(string.IsNullOrWhiteSpace(options.TelegramBotToken) ? "missing" : "set")}");
    Console.WriteLine($"Telegram chat id: {FormatConfiguredValue(options.TelegramChatId)}");
    Console.WriteLine($"Telegram dashboard URL: {options.TelegramDashboardUrl}");
    Console.WriteLine($"Telegram startup test: {(options.SendStartupTestMessage ? "on" : "off")}");

    if (telegramNotifier is null)
    {
        Console.WriteLine("Telegram setup: missing bot token or chat id.");
    }
}

static async Task TrySendStartupTestMessageAsync(
    TelegramNotifier? telegramNotifier,
    AppOptions options,
    TimeZoneInfo taipeiTimeZone,
    CancellationToken cancellationToken)
{
    if (!options.SendStartupTestMessage || telegramNotifier is null)
    {
        return;
    }

    Console.Write("Telegram startup test send: ");
    var sent = await telegramNotifier.SendStartupTestMessageAsync(
        options.Symbol,
        TaipeiClock.Now(taipeiTimeZone),
        cancellationToken);
    Console.WriteLine(sent ? "sent." : "failed.");
}

static string FormatConfiguredValue(string? value) =>
    string.IsNullOrWhiteSpace(value) ? "missing" : value;

internal sealed record AppOptions(
    string Symbol,
    string QuoteUrl,
    bool RunOnce,
    bool DryRun,
    bool InitDatabase,
    bool RecalculateRecommendations,
    bool IgnoreMarketHours,
    decimal MinimumValidPrice,
    int MaxQuoteAgeMinutes,
    int FetchDelaySeconds,
    int RequestTimeoutSeconds,
    string UserAgent,
    string DataDirectory,
    string SqlServer,
    string SqlDatabase,
    string SqlUser,
    string SqlPassword,
    string? SqlConnectionString,
    string? TelegramBotToken,
    string? TelegramChatId,
    string TelegramDashboardUrl,
    bool SendStartupTestMessage,
    string? ConfigFilePath,
    string? ConfigLoadError)
{
    public string MasterConnectionString => BuildConnectionString("master");

    public string DatabaseConnectionString => BuildConnectionString(SqlDatabase);

    public string DatabaseDisplayName
    {
        get
        {
            var builder = new SqlConnectionStringBuilder(DatabaseConnectionString);
            return $"{builder.DataSource}/{builder.InitialCatalog}";
        }
    }

    public static AppOptions Parse(string[] args)
    {
        var config = FuturesBotConfig.Load();
        var symbol = "WTX&";
        var quoteUrl = "https://tw.stock.yahoo.com/future/WTX&";
        var runOnce = false;
        var dryRun = false;
        var initDatabase = false;
        var recalculateRecommendations = false;
        var ignoreMarketHours = false;
        var minimumValidPrice = 10_000m;
        var maxQuoteAgeMinutes = 10;
        var fetchDelaySeconds = 5;
        var requestTimeoutSeconds = 15;
        var userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";
        var dataDirectory = Path.Combine("data", "yahoo-wtx");
        var sqlServer = @".\SQLEXPRESS";
        var sqlDatabase = "Futures";
        var sqlUser = "admin";
        var sqlPassword = "guitar";
        string? sqlConnectionString = null;
        string? telegramBotToken = FirstConfigured(
            Environment.GetEnvironmentVariable("FUTURESBOT_TELEGRAM_BOT_TOKEN"),
            Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN"),
            config.Telegram?.BotToken);
        string? telegramChatId = FirstConfigured(
            Environment.GetEnvironmentVariable("FUTURESBOT_TELEGRAM_CHAT_ID"),
            Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID"),
            config.Telegram?.ChatId);
        var telegramDashboardUrl = FirstConfigured(
            Environment.GetEnvironmentVariable("FUTURESBOT_TELEGRAM_DASHBOARD_URL"),
            Environment.GetEnvironmentVariable("FUTURESBOT_DASHBOARD_URL"),
            config.Telegram?.DashboardUrl)
            ?? "https://futuresmonitor.cjhwork.com/";
        var sendStartupTestMessage = FirstConfiguredBool(
            false,
            config.Telegram?.SendStartupTestMessage,
            Environment.GetEnvironmentVariable("FUTURESBOT_TELEGRAM_STARTUP_TEST"),
            Environment.GetEnvironmentVariable("FUTURESBOT_SEND_STARTUP_TEST_MESSAGE"));

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--symbol":
                    symbol = RequireValue(args, ref i, arg);
                    break;
                case "--url":
                    quoteUrl = RequireValue(args, ref i, arg);
                    break;
                case "--data-dir":
                    dataDirectory = RequireValue(args, ref i, arg);
                    break;
                case "--run-once":
                    runOnce = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    runOnce = true;
                    break;
                case "--init-db":
                    initDatabase = true;
                    break;
                case "--recalculate-recommendations":
                    recalculateRecommendations = true;
                    break;
                case "--ignore-market-hours":
                    ignoreMarketHours = true;
                    break;
                case "--min-price":
                    minimumValidPrice = decimal.Parse(RequireValue(args, ref i, arg), CultureInfo.InvariantCulture);
                    break;
                case "--max-quote-age-minutes":
                    maxQuoteAgeMinutes = int.Parse(RequireValue(args, ref i, arg), CultureInfo.InvariantCulture);
                    break;
                case "--fetch-delay-seconds":
                    fetchDelaySeconds = int.Parse(RequireValue(args, ref i, arg), CultureInfo.InvariantCulture);
                    break;
                case "--timeout-seconds":
                    requestTimeoutSeconds = int.Parse(RequireValue(args, ref i, arg), CultureInfo.InvariantCulture);
                    break;
                case "--user-agent":
                    userAgent = RequireValue(args, ref i, arg);
                    break;
                case "--sql-server":
                    sqlServer = RequireValue(args, ref i, arg);
                    break;
                case "--sql-database":
                    sqlDatabase = RequireValue(args, ref i, arg);
                    break;
                case "--sql-user":
                    sqlUser = RequireValue(args, ref i, arg);
                    break;
                case "--sql-password":
                    sqlPassword = RequireValue(args, ref i, arg);
                    break;
                case "--connection-string":
                    sqlConnectionString = RequireValue(args, ref i, arg);
                    break;
                case "--telegram-bot-token":
                    telegramBotToken = RequireValue(args, ref i, arg);
                    break;
                case "--telegram-chat-id":
                    telegramChatId = RequireValue(args, ref i, arg);
                    break;
                case "--telegram-dashboard-url":
                    telegramDashboardUrl = RequireValue(args, ref i, arg);
                    break;
                case "--telegram-startup-test":
                    sendStartupTestMessage = true;
                    break;
                case "--no-telegram-startup-test":
                    sendStartupTestMessage = false;
                    break;
                case "--help":
                case "-h":
                case "/?":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {arg}");
            }
        }

        return new AppOptions(
            symbol,
            quoteUrl,
            runOnce,
            dryRun,
            initDatabase,
            recalculateRecommendations,
            ignoreMarketHours,
            minimumValidPrice,
            maxQuoteAgeMinutes,
            fetchDelaySeconds,
            requestTimeoutSeconds,
            userAgent,
            dataDirectory,
            sqlServer,
            sqlDatabase,
            sqlUser,
            sqlPassword,
            sqlConnectionString,
            telegramBotToken,
            telegramChatId,
            telegramDashboardUrl,
            sendStartupTestMessage,
            config.LoadedPath,
            config.LoadError);
    }

    private string BuildConnectionString(string databaseName)
    {
        var builder = string.IsNullOrWhiteSpace(SqlConnectionString)
            ? new SqlConnectionStringBuilder
            {
                DataSource = SqlServer,
                UserID = SqlUser,
                Password = SqlPassword
            }
            : new SqlConnectionStringBuilder(SqlConnectionString);

        builder.InitialCatalog = databaseName;
        builder.Encrypt = false;
        builder.TrustServerCertificate = true;
        builder.ConnectTimeout = RequestTimeoutSeconds;
        return builder.ConnectionString;
    }

    private static string RequireValue(string[] args, ref int index, string optionName)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"{optionName} requires a value.");
        }

        index++;
        return args[index];
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
        Usage:
          FuturesBot [options]

        Options:
          --symbol <symbol>             Yahoo symbol to parse. Default: WTX&
          --url <url>                   Primary Yahoo URL. Default: https://tw.stock.yahoo.com/future/WTX&
          --run-once                    Fetch once, write SQL records, then exit
          --dry-run                     Fetch once and print the parsed quote without writing SQL records
          --init-db                     Create or update the SQL database schema, then exit
          --recalculate-recommendations Rebuild existing recommendation events with the current pricing logic, then exit
          --ignore-market-hours         Fetch even outside Taiwan futures trading hours
          --min-price <price>           Reject parsed prices below this value. Default: 10000
          --max-quote-age-minutes <n>   Skip writes if Yahoo update time is older than this. Default: 10
          --fetch-delay-seconds <n>     Continuous mode fetch delay after each minute closes. Default: 5
          --timeout-seconds <seconds>   HTTP timeout. Default: 15
          --sql-server <server>         SQL Server instance. Default: .\SQLEXPRESS
          --sql-database <database>     SQL database. Default: Futures
          --sql-user <user>             SQL login. Default: admin
          --sql-password <password>     SQL password. Default: guitar
          --connection-string <value>   Full SQL connection string. Database is still set from --sql-database.
          --data-dir <path>             Directory for changed strategy/recommendation text output. Default: data/yahoo-wtx
          --telegram-bot-token <token>  Telegram bot token. Can also use config or FUTURESBOT_TELEGRAM_BOT_TOKEN.
          --telegram-chat-id <chat-id>  Telegram channel/chat id. Can also use config or FUTURESBOT_TELEGRAM_CHAT_ID.
          --telegram-dashboard-url <url> Dashboard URL in Telegram messages. Can also use config. Default: https://futuresmonitor.cjhwork.com/
          --telegram-startup-test       Send a Telegram test message on startup.
          --no-telegram-startup-test    Disable the Telegram startup test message.
          --help                        Show help

        Output tables:
          dbo.FuturesTicks
          dbo.FuturesKBars
          dbo.StrategyScores
          dbo.EntryRecommendationEvents
          dbo.EntryRecommendationStatusHistory
          dbo.FuturesErrorLogs
        """);
    }

    private static string? FirstConfigured(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static bool FirstConfiguredBool(bool defaultValue, bool? configValue, params string?[] values)
    {
        foreach (var value in values)
        {
            if (TryParseBool(value, out var parsed))
            {
                return parsed;
            }
        }

        return configValue ?? defaultValue;
    }

    private static bool TryParseBool(string? value, out bool parsed)
    {
        if (bool.TryParse(value, out parsed))
        {
            return true;
        }

        switch (value?.Trim().ToLowerInvariant())
        {
            case "1":
            case "yes":
            case "y":
            case "on":
                parsed = true;
                return true;
            case "0":
            case "no":
            case "n":
            case "off":
                parsed = false;
                return true;
            default:
                parsed = false;
                return false;
        }
    }
}

internal sealed class YahooFuturesQuoteClient(HttpClient httpClient, AppOptions options)
{
    private readonly string[] _fallbackUrls =
    [
        "https://tw.stock.yahoo.com/future/",
        "https://tw.stock.yahoo.com/future/futures.html",
        "https://tw.stock.yahoo.com/quote/WTX%26"
    ];

    public async Task<YahooQuote> FetchQuoteAsync(DateTimeOffset capturedAt, CancellationToken cancellationToken)
    {
        var urls = _fallbackUrls
            .Prepend(options.QuoteUrl)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var errors = new List<string>();

        foreach (var url in urls)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Referrer = new Uri("https://tw.stock.yahoo.com/future/");

                using var response = await httpClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();

                var html = await response.Content.ReadAsStringAsync(cancellationToken);
                if (TryParseQuote(html, options.Symbol, url, options.MinimumValidPrice, capturedAt, out var quote))
                {
                    return quote;
                }

                errors.Add($"{url}: quote not found in response");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{url}: {ex.Message}");
            }
        }

        throw new InvalidOperationException($"Unable to fetch {options.Symbol} from Yahoo. {string.Join(" | ", errors)}");
    }

    private static bool TryParseQuote(
        string html,
        string symbol,
        string sourceUrl,
        decimal minimumValidPrice,
        DateTimeOffset capturedAt,
        out YahooQuote quote)
    {
        var text = HtmlText.ToPlainText(html);

        if (TryParseFutureTableQuote(text, symbol, sourceUrl, out quote) && quote.Price >= minimumValidPrice && quote.MarketDateTime is not null)
        {
            return true;
        }

        if (TryParseQuoteHeader(text, minimumValidPrice, sourceUrl, out quote) && quote.Price >= minimumValidPrice && quote.MarketDateTime is not null)
        {
            return true;
        }

        if (TryParseRegularMarketPriceJson(html, text, sourceUrl, capturedAt, out quote) && quote.Price >= minimumValidPrice && quote.MarketDateTime is not null)
        {
            return true;
        }

        quote = default;
        return false;
    }

    private static bool TryParseFutureTableQuote(string text, string symbol, string sourceUrl, out YahooQuote quote)
    {
        var dataDate = ExtractDataDate(text);
        if (dataDate is null)
        {
            quote = default;
            return false;
        }

        var currentIndex = 0;
        while (currentIndex < text.Length)
        {
            var symbolIndex = text.IndexOf(symbol, currentIndex, StringComparison.Ordinal);
            if (symbolIndex < 0)
            {
                break;
            }

            var tailLength = Math.Min(1_500, text.Length - symbolIndex - symbol.Length);
            var tail = text.Substring(symbolIndex + symbol.Length, tailLength);
            var tokens = YahooToken.Tokenize(tail).Take(24).ToArray();
            var numericTokens = tokens.Where(token => token.Number is not null).ToArray();
            var marketTime = tokens.FirstOrDefault(token => token.Time is not null).Time;

            // Yahoo's futures table columns after the symbol are:
            // bid, ask, last traded price, change, change %, volume, open, high, low, basis, reference, open interest, time.
            if (numericTokens.Length >= 12 && marketTime is not null)
            {
                var marketDateTime = CombineTaipeiDateTime(dataDate.Value, marketTime);
                quote = new YahooQuote(numericTokens[2].Number!.Value, marketTime, marketDateTime, sourceUrl);
                return true;
            }

            currentIndex = symbolIndex + symbol.Length;
        }

        quote = default;
        return false;
    }

    private static bool TryParseQuoteHeader(string text, decimal minimumValidPrice, string sourceUrl, out YahooQuote quote)
    {
        var updatedAt = ExtractUpdatedAtMatch(text);
        if (updatedAt is null)
        {
            quote = default;
            return false;
        }

        var prefixStart = Math.Max(0, updatedAt.Value.Index - 250);
        var prefix = text.Substring(prefixStart, updatedAt.Value.Index - prefixStart);
        var price = YahooToken.Tokenize(prefix)
            .Where(token => token.Number is decimal number && number >= minimumValidPrice)
            .Select(token => token.Number!.Value)
            .LastOrDefault();

        if (price == default)
        {
            quote = default;
            return false;
        }

        var marketTime = updatedAt.Value.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        quote = new YahooQuote(price, marketTime, updatedAt.Value.At, sourceUrl);
        return true;
    }

    private static bool TryParseRegularMarketPriceJson(
        string html,
        string text,
        string sourceUrl,
        DateTimeOffset capturedAt,
        out YahooQuote quote)
    {
        var match = Regex.Match(
            html,
            @"regularMarketPrice\\?""\s*:\s*\{[^}]*?\\?""raw\\?""\s*:\s*(?<price>\d+(?:\.\d+)?)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (!match.Success)
        {
            quote = default;
            return false;
        }

        var price = decimal.Parse(match.Groups["price"].Value, CultureInfo.InvariantCulture);
        var marketDateTime = ExtractRegularMarketTime(html, capturedAt) ?? ExtractUpdatedAt(text);
        var marketTime = marketDateTime?.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        quote = new YahooQuote(price, marketTime, marketDateTime, sourceUrl);
        return true;
    }

    private static DateOnly? ExtractDataDate(string text)
    {
        var match = Regex.Match(text, @"資料時間\s*[:：]\s*(?<date>\d{4}/\d{1,2}/\d{1,2})");
        return match.Success ? ParseYahooDate(match.Groups["date"].Value) : null;
    }

    private static DateTimeOffset? ExtractUpdatedAt(string text)
    {
        return ExtractUpdatedAtMatch(text)?.At;
    }

    private static (DateTimeOffset At, int Index)? ExtractUpdatedAtMatch(string text)
    {
        var match = Regex.Match(
            text,
            @"(?<date>\d{4}/\d{1,2}/\d{1,2})\s+(?<time>\d{1,2}:\d{2}(?::\d{2})?)\s*更新");

        if (!match.Success)
        {
            return null;
        }

        var date = ParseYahooDate(match.Groups["date"].Value);
        if (date is null)
        {
            return null;
        }

        var at = CombineTaipeiDateTime(date.Value, match.Groups["time"].Value);
        return at is null ? null : (at.Value, match.Index);
    }

    private static DateTimeOffset? ExtractRegularMarketTime(string html, DateTimeOffset capturedAt)
    {
        var match = Regex.Match(
            html,
            @"regularMarketTime\\?""\s*:\s*\{[^}]*?\\?""raw\\?""\s*:\s*(?<timestamp>\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (!match.Success)
        {
            return null;
        }

        if (!long.TryParse(match.Groups["timestamp"].Value, CultureInfo.InvariantCulture, out var unixSeconds))
        {
            return null;
        }

        // Yahoo's raw timestamps are Unix seconds. Convert to the same +08:00 wall clock used by the files.
        return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToOffset(capturedAt.Offset);
    }

    private static DateOnly? ParseYahooDate(string value)
    {
        return DateOnly.TryParseExact(
            value,
            ["yyyy/M/d", "yyyy/MM/dd"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var date)
            ? date
            : null;
    }

    private static DateTimeOffset? CombineTaipeiDateTime(DateOnly date, string timeText)
    {
        if (!TimeOnly.TryParseExact(
                timeText,
                ["H:mm:ss", "HH:mm:ss", "H:mm", "HH:mm"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var time))
        {
            return null;
        }

        return CombineTaipeiDateTime(date, time);
    }

    private static DateTimeOffset CombineTaipeiDateTime(DateOnly date, TimeOnly time) =>
        new(date.ToDateTime(time), TimeSpan.FromHours(8));
}

internal readonly record struct YahooQuote(decimal Price, string? MarketTime, DateTimeOffset? MarketDateTime, string SourceUrl);

internal readonly record struct FuturesTick(
    string Symbol,
    DateTimeOffset CapturedAt,
    string? SourceMarketTime,
    decimal Price,
    string SourceUrl);

internal readonly record struct KBar(
    string Symbol,
    int IntervalMinutes,
    DateTimeOffset BarStart,
    DateTimeOffset BarEnd,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    int SourceCount,
    DateTimeOffset UpdatedAt);

internal static class TaiwanFuturesMarketHours
{
    private static readonly TimeOnly DayOpen = new(8, 45);
    private static readonly TimeOnly DayClose = new(13, 45);
    private static readonly TimeOnly NightOpen = new(15, 0);
    private static readonly TimeOnly NightClose = new(5, 0);

    public static bool IsTradingTime(DateTime taipeiLocalTime) => GetSessionStart(taipeiLocalTime) is not null;

    public static bool IsCollectionTime(DateTime taipeiLocalTime, int fetchDelaySeconds)
    {
        var grace = TimeSpan.FromSeconds(Math.Max(fetchDelaySeconds + 5, 5));
        var date = DateOnly.FromDateTime(taipeiLocalTime);
        var day = taipeiLocalTime.DayOfWeek;

        if (IsWeekday(day))
        {
            var dayOpen = date.ToDateTime(DayOpen);
            var dayClose = date.ToDateTime(DayClose).Add(grace);
            if (taipeiLocalTime >= dayOpen && taipeiLocalTime <= dayClose)
            {
                return true;
            }

            if (taipeiLocalTime >= date.ToDateTime(NightOpen))
            {
                return true;
            }
        }

        if (IsTuesdayToSaturday(day))
        {
            var nightClose = date.ToDateTime(NightClose).Add(grace);
            return taipeiLocalTime <= nightClose;
        }

        return false;
    }

    public static DateTime? GetBarStart(DateTime taipeiLocalTime, int intervalMinutes)
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

    public static DateTime? GetMinuteCloseBarStart(DateTime taipeiLocalTime, int intervalMinutes)
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

    /// <summary>
    /// 取得指定台北時間所屬交易盤的收盤時間，供建議等待與持有期限判斷。
    /// </summary>
    public static DateTimeOffset? GetSessionEnd(DateTimeOffset taipeiTime)
    {
        var localTime = taipeiTime.DateTime;
        var sessionStart = GetSessionStart(localTime);
        if (sessionStart is null)
        {
            return null;
        }

        var sessionEnd = TimeOnly.FromDateTime(sessionStart.Value) == DayOpen
            ? DateOnly.FromDateTime(sessionStart.Value).ToDateTime(DayClose)
            : DateOnly.FromDateTime(sessionStart.Value).AddDays(1).ToDateTime(NightClose);
        return new DateTimeOffset(sessionEnd, taipeiTime.Offset);
    }

    public static DateTimeOffset? GetTradingDayNightSessionEnd(DateTimeOffset taipeiTime)
    {
        var localTime = taipeiTime.DateTime;
        var sessionStart = GetSessionStart(localTime);
        if (sessionStart is null)
        {
            return null;
        }

        var tradingDate = DateOnly.FromDateTime(sessionStart.Value);
        var sessionEnd = tradingDate.AddDays(1).ToDateTime(NightClose);
        return new DateTimeOffset(sessionEnd, taipeiTime.Offset);
    }

    private static DateTime? GetSessionStart(DateTime taipeiLocalTime)
    {
        var time = TimeOnly.FromDateTime(taipeiLocalTime);
        var date = DateOnly.FromDateTime(taipeiLocalTime);
        var day = taipeiLocalTime.DayOfWeek;

        if (IsWeekday(day) && time >= DayOpen && time <= DayClose)
        {
            return date.ToDateTime(DayOpen);
        }

        if (IsWeekday(day) && time >= NightOpen)
        {
            return date.ToDateTime(NightOpen);
        }

        if (IsTuesdayToSaturday(day) && time <= NightClose)
        {
            return date.AddDays(-1).ToDateTime(NightOpen);
        }

        return null;
    }

    private static bool IsWeekday(DayOfWeek day) => day is >= DayOfWeek.Monday and <= DayOfWeek.Friday;

    private static bool IsTuesdayToSaturday(DayOfWeek day) => day is >= DayOfWeek.Tuesday and <= DayOfWeek.Saturday;
}

internal static class TaipeiClock
{
    public static TimeZoneInfo GetTimeZone()
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

    public static DateTimeOffset Now(TimeZoneInfo taipeiTimeZone) =>
        TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, taipeiTimeZone);
}

internal readonly record struct YahooToken(decimal? Number, string? Time)
{
    private static readonly Regex TokenRegex = new(
        @"(?<time>\b\d{1,2}:\d{2}:\d{2}\b)|(?<num>-?\d{1,3}(?:,\d{3})*(?:\.\d+)?|-?\d+(?:\.\d+)?)",
        RegexOptions.Compiled);

    public static IEnumerable<YahooToken> Tokenize(string text)
    {
        foreach (Match match in TokenRegex.Matches(text))
        {
            if (match.Groups["time"].Success)
            {
                yield return new YahooToken(null, match.Groups["time"].Value);
                continue;
            }

            var rawNumber = match.Groups["num"].Value.Replace(",", "", StringComparison.Ordinal);
            if (decimal.TryParse(rawNumber, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
            {
                yield return new YahooToken(number, null);
            }
        }
    }
}

internal static class HtmlText
{
    public static string ToPlainText(string html)
    {
        var withoutScripts = Regex.Replace(html, "<script[^>]*>.*?</script>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var withoutStyles = Regex.Replace(withoutScripts, "<style[^>]*>.*?</style>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var withSpaces = Regex.Replace(withoutStyles, "<[^>]+>", " ");
        var decoded = WebUtility.HtmlDecode(withSpaces);
        return Regex.Replace(decoded, @"\s+", " ").Trim();
    }
}
