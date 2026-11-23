using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatHub.Contracts;

public static class MessageJson
{
    public const string ContentType = "application/json";

    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
