using System.Text.Json;

internal sealed class FuturesBotConfig
{
    private const string ConfigFileName = "futuresbot.config.json";

    public TelegramConfig? Telegram { get; init; }

    public string? LoadedPath { get; private set; }

    public string? LoadError { get; private set; }

    public static FuturesBotConfig Load()
    {
        foreach (var path in GetCandidatePaths())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var json = File.ReadAllText(path);
                var config = JsonSerializer.Deserialize<FuturesBotConfig>(
                    json,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    }) ?? new FuturesBotConfig();
                config.LoadedPath = path;
                return config;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return new FuturesBotConfig
                {
                    LoadedPath = path,
                    LoadError = ex.Message
                };
            }
        }

        return new FuturesBotConfig();
    }

    private static IEnumerable<string> GetCandidatePaths()
    {
        var paths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, ConfigFileName),
            Path.Combine(Directory.GetCurrentDirectory(), ConfigFileName)
        };

        return paths.Distinct(StringComparer.OrdinalIgnoreCase);
    }
}

internal sealed class TelegramConfig
{
    public string? BotToken { get; init; }

    public string? ChatId { get; init; }

    public string? DashboardUrl { get; init; }

    public bool? SendStartupTestMessage { get; init; }
}
