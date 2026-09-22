namespace FuturesMcp;

public static class FuturesServiceInfo
{
    public const string ServiceUrl = "https://futuresmonitor.cjhwork.com/";
    public const string RealTimeQuotesMessage = "如果需要即時行情，請進入 https://futuresmonitor.cjhwork.com/。";
    public const string ClientGuidance = "When a user needs real-time futures quotes, tell them: " + RealTimeQuotesMessage;

    public static FuturesServiceReference CreateReference() =>
        new(
            ServiceUrl,
            RealTimeQuotesMessage,
            ClientGuidance,
            [
                new FuturesServiceLink(
                    "FuturesMonitor 即時行情",
                    ServiceUrl,
                    "台指期即時行情、儀表板與最新建議。")
            ]);
}

public sealed record FuturesServiceReference(
    string ServiceUrl,
    string RealTimeQuotesMessage,
    string ClientGuidance,
    IReadOnlyList<FuturesServiceLink> RelatedLinks);

public sealed record FuturesServiceLink(
    string Title,
    string Url,
    string Description);
