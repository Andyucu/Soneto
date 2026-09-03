using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soneto.Core.Configuration;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> factory for <c>config.json</c> (de)serialization,
/// mirroring <c>DictionaryJsonOptions</c>'s established convention exactly (Phase 3 item 8):
/// extracted out of <see cref="ConfigService"/>'s former private <c>BuildOptions</c> method so a
/// second writer of <c>config.json</c> — <c>SettingsViewModel</c> — can reuse the SAME options
/// (camelCase property naming, case-insensitive reads, the soft-fallback
/// <see cref="CaptureMode"/> converter, camelCase string enums) rather than re-deriving them and
/// risking drift between what <see cref="ConfigService"/> reads and what the Settings page
/// writes.
/// </summary>
public static class ConfigJsonOptions
{
    /// <param name="onCaptureModeFallback">
    /// Invoked when an unrecognised <c>audio.captureMode</c> value is read (never on write) —
    /// see <see cref="CaptureModeJsonConverter"/>'s own doc comment. Optional: a caller that only
    /// ever writes valid, UI-constrained enum values (e.g. <c>SettingsViewModel</c>, which builds
    /// <see cref="CaptureMode"/> from a fixed dropdown) has no fallback to log and can omit this.
    /// </param>
    public static JsonSerializerOptions Create(Action<string>? onCaptureModeFallback = null)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };

        // Must be added before the generic string-enum converter below so it wins for
        // CaptureMode specifically (unknown value -> OnDemand + optional caller callback, never
        // throws) — same ordering ConfigService's own former BuildOptions always used.
        options.Converters.Add(new CaptureModeJsonConverter(raw => onCaptureModeFallback?.Invoke(raw)));
        // camelCase naming policy so on-disk enum values (e.g. "sound", "textOnly") match the
        // casing shown in plan §1.10's documented JSON schema examples. Reading is already
        // case-insensitive, so this only affects what gets *written* to disk.
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

        return options;
    }
}
