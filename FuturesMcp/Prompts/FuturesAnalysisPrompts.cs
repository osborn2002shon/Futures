using System.ComponentModel;
using ModelContextProtocol.Server;

namespace FuturesMcp.Prompts;

[McpServerPromptType]
public static class FuturesAnalysisPrompts
{
    [McpServerPrompt(
        Name = "futures.market_brief",
        Title = "台指期盤勢摘要")]
    [Description("根據最新報價、SMA、盤勢條件與最新建議，產生簡潔的台指期盤勢摘要。")]
    public static string MarketBrief() =>
        """
        請為使用者產生一份簡潔、可操作的台指期盤勢摘要。

        請使用以下 MCP 能力：
        - 讀取 futures://dashboard/current，或呼叫 futures.get_dashboard_snapshot。
        - 如果需要聚焦多空條件，呼叫 futures.get_market_context。
        - 如果需要確認目前建議狀態，呼叫 futures.get_latest_recommendation。

        請涵蓋：
        - 最新報價、樣本時間與資料新鮮度。
        - 如果有資料，說明目前 15 分 K 的 SMA20 / SMA76 狀態。
        - 多方與空方盤勢條件：已符合條件、尚缺條件、盤整狀態。
        - 如果有最新建議，說明方向、狀態、入場區間、停損、停利與信心快照。
        - 資料不足、資料過舊，或其他會影響判讀的限制。

        回答要實用、精簡，並使用使用者的語言。
        不要把任何建議描述成保證獲利或保證成交的交易建議。
        """;

    [McpServerPrompt(
        Name = "futures.risk_review",
        Title = "台指期風險檢查")]
    [Description("檢查目前台指期建議的資料新鮮度、價格結構、信心狀態與缺漏脈絡。")]
    public static string RiskReview() =>
        """
        請為使用者檢查目前台指期建議的風險。

        請使用以下 MCP 能力：
        - 呼叫 futures.get_latest_recommendation。
        - 呼叫 futures.get_latest_quote。
        - 如果需要確認規則細節，讀取 futures://strategy/current-calculation。

        請評估：
        - 最新報價與建議資料是否足夠新，是否適合用來討論。
        - 方向、入場區間、停損、停利、報酬風險比與有效狀態。
        - 建議目前是等待入場、已入場、已完成、逾期、取消，還是因已有有效建議而被抑制。
        - 如果有資料，說明信心狀態、樣本數與入場觸價率。
        - 策略文件中與規則限制或資料品質有關的注意事項。

        回答要直接，並使用使用者的語言。
        請明確說出不確定性，不要暗示系統已經下單、成交，或能保證價格會被觸及。
        """;

    [McpServerPrompt(
        Name = "futures.strategy_audit",
        Title = "台指期策略稽核")]
    [Description("依照策略文件檢查目前 Futures 服務輸出的策略計算與建議生命週期是否一致。")]
    public static string StrategyAudit() =>
        """
        請依照文件中的策略規則，稽核目前 Futures 服務輸出的狀態是否一致。

        請使用以下 MCP 能力：
        - 讀取 futures://strategy/current-calculation。
        - 讀取 futures://dashboard/current，或呼叫 futures.get_dashboard_snapshot。
        - 如果需要檢查近期生命週期一致性，呼叫 futures.list_recommendations，並使用較小的 limit。

        請檢查：
        - 最新建議狀態是否符合文件描述的生命週期語意。
        - 入場、停損、停利、報酬風險比、信心狀態與 source mode 是否內部一致。
        - 盤勢條件欄位是否符合文件中的多空各七個參考條件。
        - 資料過舊、SMA / ATR 缺漏，或歷史樣本不足是否會影響判讀。
        - 如果發現不一致，列出後續值得檢查的工程項目。

        請先列出發現，依嚴重程度排序，再摘要目前狀態。
        請使用使用者的語言回答。
        """;
}
