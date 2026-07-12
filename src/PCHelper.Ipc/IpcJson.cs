using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCHelper.Ipc;

public static class IpcJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    public static T? FromElement<T>(JsonElement? value) =>
        value is JsonElement element ? element.Deserialize<T>(Options) : default;

    private static JsonSerializerOptions Create()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
