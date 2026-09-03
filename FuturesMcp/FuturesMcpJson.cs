using System.Text.Json;

namespace FuturesMcp;

internal static class FuturesMcpJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, Options);
}
