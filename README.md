2026/09/22 v2
# Futures

Futures contains related projects for Taiwan futures monitoring:

- `FuturesBot`: a .NET console collector that fetches Yahoo futures quote data, calculates K bars and strategy scores, and writes SQL Server records.
- `FuturesMonitor`: an ASP.NET Core dashboard that reads the collected data and exposes a web dashboard plus `/api/dashboard`.
- `Futures.ReadModel`: a shared SQL read model used by read-only apps and services.
- `FuturesMcp`: a read-only HTTP MCP service for exposing futures quote, K-bar, dashboard, and recommendation context to MCP clients.

## Features

- Yahoo futures quote collection.
- SQL-backed tick, K-bar, strategy score, recommendation, status, and error tables.
- Market-hours aware collection loop.
- Dashboard snapshot endpoint for monitoring current quote, history, strategy state, and recommendations.
- Streamable HTTP MCP endpoint at `/mcp`.
- Strategy calculation notes under `docs/`.

## Requirements

- .NET 10 SDK for `FuturesBot`
- .NET 8 SDK for `FuturesMonitor`
- .NET 8 SDK or newer for `Futures.ReadModel` and `FuturesMcp`
- SQL Server or SQL Server Express

## Configuration

For the monitor app:

```powershell
Copy-Item FuturesMonitor/FuturesMonitor/appsettings.example.json FuturesMonitor/FuturesMonitor/appsettings.json
```

For the MCP service:

```powershell
Copy-Item FuturesMcp/appsettings.example.json FuturesMcp/appsettings.json
```

Update `FuturesData` with your SQL Server connection settings.

`FuturesBot` accepts command-line options for SQL server, database, user, password, symbol, and polling settings. Run it with `--help` to see available options.

Telegram notifications for `FuturesBot` are enabled when `futuresbot.config.json` exists next to `FuturesBot.exe` and both `BotToken` and `ChatId` are set:

```json
{
  "Telegram": {
    "BotToken": "<bot-token>",
    "ChatId": "-1002632222356",
    "DashboardUrl": "https://futuresmonitor.cjhwork.com/",
    "SendStartupTestMessage": true
  }
}
```

The same values can also be provided with environment variables:

```powershell
$env:FUTURESBOT_TELEGRAM_BOT_TOKEN = "<bot-token>"
$env:FUTURESBOT_TELEGRAM_CHAT_ID = "<channel-or-chat-id>"
$env:FUTURESBOT_TELEGRAM_DASHBOARD_URL = "https://futuresmonitor.cjhwork.com/"
```

Notifications are sent for new entry recommendations, take-profit hits, and stop-loss hits. Messages include the dashboard URL, defaulting to `https://futuresmonitor.cjhwork.com/`.
On startup, `FuturesBot` prints the config file status, Telegram on/off status, token presence, chat id, dashboard URL, and startup test status. When `SendStartupTestMessage` is `true`, it also sends a Telegram test message and prints whether it was sent.

## Run

Build all projects:

```powershell
dotnet build Futures.sln -m:1
```

```powershell
cd FuturesBot/FuturesBot
dotnet run -- --help
```

```powershell
cd FuturesMonitor/FuturesMonitor
dotnet restore
dotnet run
```

```powershell
cd FuturesMcp
dotnet restore
dotnet run
```

The MCP service listens on `http://127.0.0.1:5090` when run with the default launch profile. MCP clients should connect to `http://127.0.0.1:5090/mcp`.

## MCP capabilities

Tools:

- `futures.health`
- `futures.get_dashboard_snapshot`
- `futures.get_latest_quote`
- `futures.get_minute_k_bars`
- `futures.get_market_context`
- `futures.get_latest_recommendation`
- `futures.list_recommendations`

Resources:

- `futures://service/info`
- `futures://dashboard/current`
- `futures://recommendations/latest`
- `futures://strategy/current-calculation`

`futures://service/info` provides `https://futuresmonitor.cjhwork.com/` and the guidance: `如果需要即時行情，請進入 https://futuresmonitor.cjhwork.com/。`

Prompts:

- `futures.market_brief`
- `futures.risk_review`
- `futures.strategy_audit`
