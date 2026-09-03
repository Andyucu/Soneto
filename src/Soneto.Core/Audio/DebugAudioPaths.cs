namespace Soneto.Core.Audio;

/// <summary>
/// Resolves the on-disk directory debug audio clips are written into (Phase 3 item 10, §3.14),
/// mirroring <see cref="Soneto.Core.Configuration.ConfigPaths"/>/
/// <see cref="Soneto.Core.History.HistoryPaths"/>'s exact pattern -- an explicit override wins,
/// otherwise the SAME base directory those two resolve to (<c>%LOCALAPPDATA%\Soneto\</c> on
/// Windows / <c>~/.config/soneto/</c> on Linux), with a <c>debug-audio</c> subdirectory (a
/// directory, not a single file, since <see cref="DebugAudioStore"/> writes one WAV file per
/// correlated dictation). Deliberately uses only <see cref="Environment.GetFolderPath"/> and
/// <see cref="OperatingSystem.IsWindows"/> -- both platform-agnostic APIs already available in
/// Soneto.Core without a platform project reference, per item 1's hard rule (unchanged for this
/// item).
/// </summary>
public static class DebugAudioPaths
{
    public const string DirectoryName = "debug-audio";

    public static string Resolve(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
            return Path.GetFullPath(overridePath);

        if (OperatingSystem.IsWindows())
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(baseDir, "Soneto", DirectoryName);
        }

        var configDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(configDir, "soneto", DirectoryName);
    }
}
