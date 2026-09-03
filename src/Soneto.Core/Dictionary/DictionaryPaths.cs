namespace Soneto.Core.Dictionary;

/// <summary>
/// Resolves the on-disk <c>dictionary.json</c> path per Phase 2 plan §2.7, mirroring
/// <see cref="Soneto.Core.Configuration.ConfigPaths"/>'s exact pattern: an explicit override
/// wins, otherwise the SAME directory <c>ConfigPaths.Resolve()</c> defaults to
/// (<c>%LOCALAPPDATA%\Soneto\</c> on Windows / <c>~/.config/soneto/</c> on Linux), just with
/// <c>dictionary.json</c> as the filename instead of <c>config.json</c> -- the two files live
/// side by side. Deliberately uses only <see cref="Environment.GetFolderPath"/> and
/// <see cref="OperatingSystem.IsWindows"/> -- both platform-agnostic APIs already available in
/// Soneto.Core without a platform project reference, per item 1's hard rule (unchanged for this
/// item).
/// </summary>
public static class DictionaryPaths
{
    public const string FileName = "dictionary.json";

    public static string Resolve(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
            return Path.GetFullPath(overridePath);

        if (OperatingSystem.IsWindows())
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(baseDir, "Soneto", FileName);
        }

        // On Linux, .NET's ApplicationData special folder resolves to $XDG_CONFIG_HOME
        // (or ~/.config when unset), which is exactly the plan's target path -- same
        // resolution ConfigPaths.Resolve() already uses for config.json.
        var configDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(configDir, "soneto", FileName);
    }
}
