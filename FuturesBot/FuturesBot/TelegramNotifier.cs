using System.Globalization;

internal sealed class TelegramNotifier
{
    private readonly HttpClient _httpClient;
    private readonly string _botToken;
    private readonly string _chatId;
    private readonly string _dashboardUrl;

    private TelegramNotifier(
        HttpClient httpClient,
        string botToken,
        string chatId,
        string dashboardUrl)
    {
        _httpClient = httpClient;
        _botToken = botToken;
        _chatId = chatId;
        _dashboardUrl = dashboardUrl;
    }

    public static TelegramNotifier? Create(
        HttpClient httpClient,
        string? botToken,
        string? chatId,
        string dashboardUrl)
    {
        if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(chatId))
        {
            return null;
        }

        return new TelegramNotifier(
            httpClient,
            botToken.Trim(),
            chatId.Trim(),
            string.IsNullOrWhiteSpace(dashboardUrl) ? "https://futuresmonitor.cjhwork.com/" : dashboardUrl.Trim());
    }

    public async Task SendRecommendationChangesAsync(
        IEnumerable<RecommendationChange> changes,
        CancellationToken cancellationToken)
    {
        foreach (var change in changes)
        {
            if (!ShouldNotify(change))
            {
                continue;
            }

            await TrySendMessageAsync(BuildMessage(change), cancellationToken);
        }
    }

    public Task<bool> SendStartupTestMessageAsync(
        string symbol,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var lines = new[]
        {
            "FuturesBot 啟動測試通知",
            $"商品: {symbol}",
            $"時間: {FormatDateTime(startedAt)}",
            $"網址: {_dashboardUrl}"
        };

        return TrySendMessageAsync(string.Join(Environment.NewLine, lines), cancellationToken);
    }

    private static bool ShouldNotify(RecommendationChange change) =>
        change.Recommendation.Status is EntryRecommendationStatuses.WaitingEntry
            or EntryRecommendationStatuses.TakeProfit
            or EntryRecommendationStatuses.StopLoss;

    private async Task<bool> TrySendMessageAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            using var content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("chat_id", _chatId),
                new KeyValuePair<string, string>("text", message),
                new KeyValuePair<string, string>("disable_web_page_preview", "true")
            ]);

            using var response = await _httpClient.PostAsync(
                $"https://api.telegram.org/bot{_botToken}/sendMessage",
                content,
                cancellationToken);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine(
                    $"Telegram notification failed: {(int)response.StatusCode} {response.ReasonPhrase}. {responseText}");
                return false;
            }

            return true;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine($"Telegram notification failed: {ex.Message}");
            return false;
        }
    }

    private string BuildMessage(RecommendationChange change)
    {
        var recommendation = change.Recommendation;
        var title = recommendation.Status switch
        {
            EntryRecommendationStatuses.WaitingEntry => "台指期入場建議",
            EntryRecommendationStatuses.TakeProfit => "台指期到達停利",
            EntryRecommendationStatuses.StopLoss => "台指期到達停損",
            _ => "台指期策略通知"
        };

        var lines = new List<string>
        {
            title,
            $"#{recommendation.Id} {recommendation.Symbol} {FormatSide(recommendation.Side)}",
            $"時間: {FormatDateTime(change.ChangedAt)}",
            $"建議價: {FormatPoint(recommendation.RecommendationPrice)}",
            $"入場區間: {FormatRange(recommendation.EntryLow, recommendation.EntryHigh)}",
            $"停損: {FormatPoint(recommendation.StopLoss)}",
            $"停利: {FormatPoint(recommendation.TakeProfit)}"
        };

        if (recommendation.Status == EntryRecommendationStatuses.WaitingEntry)
        {
            lines.Add($"信心: {recommendation.ConfidenceStatus}/{recommendation.ConfidenceSampleCount}");
        }
        else
        {
            lines.Add($"進場價: {FormatPoint(recommendation.EntryPrice)}");
            lines.Add($"出場價: {FormatPoint(recommendation.ExitPrice ?? change.ObservedPrice)}");
            lines.Add($"損益點數: {FormatSignedPoint(recommendation.ProfitPoints)}");
        }

        lines.Add($"網址: {_dashboardUrl}");
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatSide(StrategySide side) =>
        side == StrategySide.Long ? "多單" : "空單";

    private static string FormatRange(decimal? low, decimal? high) =>
        low is null || high is null ? "-" : $"{FormatPoint(low)}-{FormatPoint(high)}";

    private static string FormatPoint(decimal? value) =>
        value?.ToString("0.####", CultureInfo.InvariantCulture) ?? "-";

    private static string FormatSignedPoint(decimal? value) =>
        value is null
            ? "-"
            : value.Value.ToString(value.Value > 0 ? "+0.####" : "0.####", CultureInfo.InvariantCulture);

    private static string FormatDateTime(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
}
