# Futures

Futures contains two related projects for Taiwan futures monitoring:

- `FuturesBot`: a .NET console collector that fetches Yahoo futures quote data, calculates K bars and strategy scores, and writes SQL Server records.
- `FuturesMonitor`: an ASP.NET Core dashboard that reads the collected data and exposes a web dashboard plus `/api/dashboard`.

## Features

- Yahoo futures quote collection.
- SQL-backed tick, K-bar, strategy score, recommendation, status, and error tables.
- Market-hours aware collection loop.
- Dashboard snapshot endpoint for monitoring current quote, history, strategy state, and recommendations.
- Strategy calculation notes under `docs/`.

## Requirements

- .NET 10 SDK for `FuturesBot`
- .NET 8 SDK for `FuturesMonitor`
- SQL Server or SQL Server Express

## Configuration

For the monitor app:

```powershell
Copy-Item FuturesMonitor/FuturesMonitor/appsettings.example.json FuturesMonitor/FuturesMonitor/appsettings.json
```

Update `FuturesData` with your SQL Server connection settings.

`FuturesBot` accepts command-line options for SQL server, database, user, password, symbol, and polling settings. Run it with `--help` to see available options.

## Run

```powershell
cd FuturesBot/FuturesBot
dotnet run -- --help
```

```powershell
cd FuturesMonitor/FuturesMonitor
dotnet restore
dotnet run
```
