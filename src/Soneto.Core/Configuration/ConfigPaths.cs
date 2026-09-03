namespace Soneto.Core.Configuration;

/// <summary>
/// Resolves the on-disk config path per plan §1.10: an explicit override wins, otherwise
/// `%LOCALAPPDATA%\Soneto\config.json` on Windows / `~/.config/soneto/config.json` on
/// Linux. Deliberately uses only <see cref="Environment.GetFolderPath"/> and
/// <see cref="OperatingSystem.IsWindows"/> — both platform-agnostic APIs available in
/// Soneto.Core without a platform project reference, per item 1's hard rule.
/// </summary>
public static class ConfigPaths
{
    public const string FileName = "config.json";

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
        // (or ~/.config when unset), which is exactly the plan's target path.
        var configDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(configDir, "soneto", FileName);
    }
}
