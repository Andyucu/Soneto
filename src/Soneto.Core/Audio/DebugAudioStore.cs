using Microsoft.Extensions.Logging;
using Soneto.Core.Wav;

namespace Soneto.Core.Audio;

/// <summary>
/// Phase 3 item 10 (§3.14) -- writes the opt-in "keep last N clips for debugging" WAV files and
/// enforces their own, separate, count-bounded retention. Off by default (plan §8: audio is
/// never written to disk unless a user explicitly opts in via
/// <see cref="Soneto.Core.Configuration.DataPrivacyConfig.DebugAudioRetentionEnabled"/>).
///
/// <para>
/// <b>Correlation mechanism, and the Id-ordering question this item's own instructions asked to
/// be designed and documented (rather than assumed):</b> <see cref="Soneto.Core.History.HistoryEntry.Id"/>
/// is only known after <see cref="Soneto.Core.History.IHistoryStore.AppendAsync"/> returns (a
/// SQLite autoincrement rowid) -- so the caller (<c>Soneto.App</c>'s composition root) must
/// AWAIT <c>AppendAsync</c> first and only THEN call <see cref="SaveClipAsync"/> with the real
/// returned Id, never write speculatively before the row exists. This is the option chosen over
/// a separate correlation key (e.g. a client-generated GUID written first, then reconciled) --
/// simpler, and the write ordering (append succeeds, then optionally save its audio) is already
/// the natural sequence for a fire-and-forget history-append handler to follow. Each clip's
/// filename is literally <c>{historyId}.wav</c> -- trivial, unambiguous correlation with no
/// separate index/manifest needed.
/// </para>
///
/// <para>
/// <b>Retention -- count-bounded ("keep last N"), enforced immediately after each write, not on
/// a timer.</b> Distinct from <c>Soneto.App.HistoryRetentionSweeper</c>'s daily,
/// AGE-bounded text-history sweep, per plan §3.14's own explicit "its own separate auto-purge...
/// since audio clips are far larger and more sensitive" requirement. Since
/// <see cref="Soneto.Core.History.HistoryEntry.Id"/> values are monotonically increasing (SQLite
/// autoincrement), "the last N clips" is simply "the N highest-numbered <c>{id}.wav</c> files
/// still present" -- no timestamp/metadata bookkeeping needed beyond the filename itself.
/// </para>
///
/// <para>
/// <b>Never throws</b> -- every public method here catches and logs, matching
/// <see cref="Soneto.Core.History.IHistoryStore"/>'s own "hot dictation-completion path must
/// never fault the caller" discipline: losing a debug audio clip must never affect the dictation
/// session or history row that produced it.
/// </para>
/// </summary>
public static class DebugAudioStore
{
    /// <summary>
    /// Writes <paramref name="samples"/> as <c>{historyId}.wav</c> under <paramref name="dir"/>
    /// (created if missing), then purges down to <paramref name="maxClips"/> most-recent clips.
    /// Never throws -- logs and returns on any I/O failure.
    /// </summary>
    public static async Task SaveClipAsync(
        string dir, long historyId, ReadOnlyMemory<float> samples, int sampleRate, int maxClips,
        ILogger logger, CancellationToken ct = default)
    {
        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, $"{historyId}.wav");
                WavWriter.Write(path, samples.Span, sampleRate);
                PurgeToMaxClips(dir, maxClips, logger);
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex, "Failed to write debug audio clip for history entry {HistoryId} to {Dir}.", historyId, dir);
        }
    }

    /// <summary>
    /// Deletes every <c>*.wav</c> file in <paramref name="dir"/> beyond the
    /// <paramref name="maxClips"/> highest-numbered ones (oldest deleted first). A
    /// non-positive <paramref name="maxClips"/> deletes everything (treated as "keep none").
    /// Files whose name doesn't parse as a plain <see cref="long"/> (unexpected content in this
    /// directory) are left alone -- this method only ever touches files it recognizes as its own.
    /// Internal (not private) so <c>Soneto.Core.Tests</c> can exercise the purge ordering
    /// directly without going through a real WAV write each time.
    /// </summary>
    internal static void PurgeToMaxClips(string dir, int maxClips, ILogger logger)
    {
        if (!Directory.Exists(dir))
            return;

        var clips = Directory.GetFiles(dir, "*.wav")
            .Select(path => (Path: path, Id: ParseClipId(path)))
            .Where(x => x.Id.HasValue)
            .OrderBy(x => x.Id!.Value)
            .ToList();

        int keep = Math.Max(0, maxClips);
        int toDelete = clips.Count - keep;
        if (toDelete <= 0)
            return;

        foreach (var (path, _) in clips.Take(toDelete))
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to delete old debug audio clip {Path}.", path);
            }
        }
    }

    /// <summary>
    /// The §3.14/§3.15 panic-wipe control's debug-audio counterpart: deletes every clip in
    /// <paramref name="dir"/> unconditionally (they'd otherwise be orphaned, correlated to
    /// history rows a panic wipe just deleted). Never throws.
    /// </summary>
    public static void WipeAll(string dir, ILogger logger)
    {
        if (!Directory.Exists(dir))
            return;

        foreach (var path in Directory.GetFiles(dir, "*.wav"))
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to delete debug audio clip {Path} during panic wipe.", path);
            }
        }
    }

    private static long? ParseClipId(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return long.TryParse(name, out var id) ? id : null;
    }
}
