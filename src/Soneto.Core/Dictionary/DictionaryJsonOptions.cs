using System.Text.Json;

namespace Soneto.Core.Dictionary;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> factory for <c>dictionary.json</c>
/// (de)serialization, mirroring <c>ConfigService.BuildOptions</c>'s established
/// convention (camelCase property naming + case-insensitive reads) so
/// <c>dictionary.json</c> and <c>config.json</c> read the same way by hand. Kept as its
/// own small type rather than inlined so item 9's <c>DictionaryService</c> can reuse it
/// verbatim instead of re-deriving the same options.
/// </summary>
public static class DictionaryJsonOptions
{
    public static JsonSerializerOptions Create() => new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
}
