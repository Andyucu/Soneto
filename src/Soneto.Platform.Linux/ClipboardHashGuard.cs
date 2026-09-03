using System.Security.Cryptography;
using System.Text;

namespace Soneto.Platform.Linux;

/// <summary>
/// Pure content-hash restore guard, per plan §1.9: "minus the clipboard sequence number
/// (no equivalent [on Linux] -- use a content hash comparison instead: hash what you
/// wrote, hash again before restoring, skip if it differs)." Linux-side analogue of
/// <c>Soneto.Platform.Windows.ClipboardManager</c>'s <c>GetClipboardSequenceNumber()</c>
/// guard, adapted to a hash comparison since Linux clipboard tooling has no OS-level
/// sequence counter to query.
///
/// <para>
/// Deliberately pulled out as pure, dependency-free logic (SHA-256 over UTF-8 bytes, same
/// hash algorithm/convention this project already uses in <c>ModelManager</c>/
/// <c>SileroVadDetector</c>) so the restore-skip-on-mismatch decision is fully
/// unit-testable without any real <c>wl-paste</c>/<c>xclip</c> process call -- see
/// <c>LinuxClipboardManager</c> for where this is actually wired into the real,
/// process-shelling-out restore path.
/// </para>
/// </summary>
public static class ClipboardHashGuard
{
    public static byte[] ComputeHash(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// True if it is safe to restore: the clipboard's content immediately before the
    /// restore attempt still hashes to the same value it did right after our own write
    /// (i.e. nothing else has touched the clipboard in between). False means "abort the
    /// restore" -- something else (almost certainly the user hitting Ctrl+C) changed the
    /// clipboard during the restore window, and silently overwriting that is the failure
    /// mode plan §1.8/§1.9 explicitly calls out as never acceptable.
    /// </summary>
    public static bool IsSafeToRestore(byte[] hashAfterOurWrite, byte[] currentHashBeforeRestore)
    {
        ArgumentNullException.ThrowIfNull(hashAfterOurWrite);
        ArgumentNullException.ThrowIfNull(currentHashBeforeRestore);
        return hashAfterOurWrite.AsSpan().SequenceEqual(currentHashBeforeRestore);
    }
}
