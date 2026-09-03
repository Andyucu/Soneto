using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soneto.Core.Configuration;

/// <summary>
/// Deserializes <see cref="CaptureMode"/> case-insensitively, and — per plan §1.13's
/// explicit test requirement — falls back to <see cref="CaptureMode.OnDemand"/> instead
/// of throwing when the JSON value is missing/unrecognised, invoking
/// <paramref name="onFallback"/> so the caller can log a warning (never silent).
/// </summary>
internal sealed class CaptureModeJsonConverter : JsonConverter<CaptureMode>
{
    private readonly Action<string> _onFallback;

    public CaptureModeJsonConverter(Action<string> onFallback)
    {
        _onFallback = onFallback;
    }

    public override CaptureMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;

        if (!string.IsNullOrEmpty(raw) && Enum.TryParse<CaptureMode>(raw, ignoreCase: true, out var mode)
            && Enum.IsDefined(mode))
        {
            return mode;
        }

        _onFallback(raw ?? string.Empty);
        return CaptureMode.OnDemand;
    }

    public override void Write(Utf8JsonWriter writer, CaptureMode value, JsonSerializerOptions options)
    {
        // camelCase to match the other enums (see ConfigService.BuildOptions's
        // JsonStringEnumConverter(JsonNamingPolicy.CamelCase)) and plan §1.10's
        // documented schema casing, e.g. "onDemand" not "OnDemand".
        writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
    }
}
