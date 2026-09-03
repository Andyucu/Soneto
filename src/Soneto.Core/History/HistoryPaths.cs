namespace Soneto.Core.History;

/// <summary>
/// Resolves the on-disk <c>history.db</c> path, mirroring
/// <see cref="Soneto.Core.Configuration.ConfigPaths"/>/<see cref="Soneto.Core.Dictionary.DictionaryPaths"/>'s
/// exact pattern: an explicit override wins, otherwise the SAME directory those two resolve to
/// (<c>%LOCALAPPDATA%\Soneto\</c> on Windows / <c>~/.config/soneto/</c> on Linux), just with
/// <c>history.db</c> as the filename -- all three files live side by side. Deliberately uses only
/// <see cref="Environment.GetFolderPath"/> and <see cref="OperatingSystem.IsWindows"/> -- both
/// platform-agnostic APIs already available in Soneto.Core without a platform project reference,
/// per item 1's hard rule (unchanged for this item).
///
/// <para>
/// <b>Phase 3 item 6's architecture decision (see this item's own build-order row/PROJECT-MEMORY
/// entry for the full writeup):</b> <see cref="Soneto.Core.History.SqliteHistoryStore"/> is
/// constructed eagerly at <c>Soneto.App</c> startup via <c>HistoryPaths.Resolve()</c>, BEFORE
/// <c>PipelineHost</c>'s async background pipeline-startup task has had any chance to run or
/// fail -- history persistence must be usable (browse/search past sessions) even in a session
/// where the live dictation pipeline never successfully starts (e.g. a missing ASR model), so it
/// is deliberately NOT gated behind <c>PipelineHost.Started</c>'s success.
/// </para>
/// </summary>
public static class HistoryPaths
{
    public const string FileName = "history.db";

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
        // resolution ConfigPaths.Resolve()/DictionaryPaths.Resolve() already use.
        var configDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(configDir, "soneto", FileName);
    }
}
