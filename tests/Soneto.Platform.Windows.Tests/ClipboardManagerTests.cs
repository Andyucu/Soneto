namespace Soneto.Platform.Windows.Tests;

/// <summary>
/// Exercises <see cref="ClipboardManager"/> against the real Windows clipboard. Not tagged
/// <c>Category=Hardware</c>: unlike a physical keyboard press or a real focused GUI app,
/// reading/writing the system clipboard is an ordinary OS call with no physical device or
/// human involved, and (unlike <c>SendInput</c>-based paste synthesis) has no risk of
/// altering the content of whatever window happens to have focus during a CI run -- the
/// worst-case side effect is leaving different clipboard content behind, which this test
/// itself restores. Mutates the real system clipboard for the duration of each test; if run
/// concurrently with another process actively using the clipboard, a transient collision is
/// possible (the same real-world scenario <see cref="ClipboardManager"/>'s own retry loop
/// exists to absorb).
/// </summary>
public sealed class ClipboardManagerTests
{
    /// <summary>
    /// Polls <see cref="ClipboardManager.Save"/> briefly instead of trusting a single
    /// immediate read right after a <c>Set</c>. Observed (this test suite, under load from
    /// the rest of the assembly running concurrently) that an immediate read-after-write can
    /// transiently race Windows' own clipboard-history/format-synthesis machinery and come
    /// back with no <c>CF_UNICODETEXT</c> at all for a few milliseconds -- the same class of
    /// "another process/feature briefly holds or reacts to the clipboard" risk plan §1.8
    /// explicitly calls out for <c>SetClipboardData</c> itself. This ordering (write, then
    /// immediately re-read the same process's own write) is specific to this test's
    /// round-trip verification and does not occur in the real injection algorithm, where
    /// <c>Save()</c> always happens BEFORE the <c>SetClipboardData</c> it is backing up.
    /// </summary>
    private static string? SaveStable(int timeoutMs = 1000, int pollIntervalMs = 25)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? lastSeen = null;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var backup = ClipboardManager.Save();
            if (backup.HadUnicodeText)
                return backup.UnicodeText;
            lastSeen = backup.UnicodeText;
            Thread.Sleep(pollIntervalMs);
        }
        return lastSeen;
    }

    [Fact]
    public void SetUnicodeTextWithRetry_then_Save_round_trips_the_exact_text_including_diacritics()
    {
        // Includes the same diacritic family (comma-below ș/ț, not cedilla) the S4 spike's
        // own byte-level check targets, so this round trip exercises the real risk area, not
        // just plain ASCII.
        const string text = "Ăsta e un test: șoseaua Ștefan cel Mare, țară, îngheț.";
        var originalBackup = ClipboardManager.Save();

        try
        {
            bool set = ClipboardManager.SetUnicodeTextWithRetry(text, attempts: 3, delay: TimeSpan.FromMilliseconds(20));
            Assert.True(set);

            Assert.Equal(text, SaveStable());
        }
        finally
        {
            // Best-effort cleanup via the atomic, sequence-guarded restore (item 7c) -- the
            // sequence number hasn't changed since our own set within this test, so this is
            // expected to actually restore.
            if (originalBackup.HadUnicodeText)
            {
                int seq = ClipboardManager.GetSequenceNumber();
                ClipboardManager.RestoreUnicodeTextWithSequenceGuard(originalBackup.UnicodeText!, seq, attempts: 3, delay: TimeSpan.FromMilliseconds(20));
            }
        }
    }

    [Fact]
    public void Save_after_a_successful_set_always_reports_HadUnicodeText_consistently_with_the_text_value()
    {
        const string text = "consistency-check value";
        Assert.True(ClipboardManager.SetUnicodeTextWithRetry(text, attempts: 3, delay: TimeSpan.FromMilliseconds(20)));

        Assert.Equal(text, SaveStable());
    }

    [Fact]
    public void RestoreUnicodeTextWithSequenceGuard_writes_back_the_previously_saved_text_when_sequence_unchanged()
    {
        const string original = "S4-style original clipboard content";
        const string transient = "transient text that should get overwritten";

        Assert.True(ClipboardManager.SetUnicodeTextWithRetry(original, attempts: 3, delay: TimeSpan.FromMilliseconds(20)));
        Assert.Equal(original, SaveStable());

        Assert.True(ClipboardManager.SetUnicodeTextWithRetry(transient, attempts: 3, delay: TimeSpan.FromMilliseconds(20)));
        Assert.Equal(transient, SaveStable());
        int expectedSeq = ClipboardManager.GetSequenceNumber();

        var outcome = ClipboardManager.RestoreUnicodeTextWithSequenceGuard(original, expectedSeq, attempts: 3, delay: TimeSpan.FromMilliseconds(20));
        Assert.Equal(ClipboardRestoreOutcome.Restored, outcome);
        Assert.Equal(original, SaveStable());
    }

    [Fact]
    public void RestoreUnicodeTextWithSequenceGuard_aborts_without_writing_when_sequence_number_changed()
    {
        const string original = "original before the guarded restore";
        const string userCopiedDuringWindow = "user copied something new during the restore window";
        const string wouldHaveBeenRestored = "text that must NOT be written back";

        Assert.True(ClipboardManager.SetUnicodeTextWithRetry(original, attempts: 3, delay: TimeSpan.FromMilliseconds(20)));
        int staleExpectedSeq = ClipboardManager.GetSequenceNumber();

        // Simulate a user copying something new after our own set but before the restore
        // attempt -- the sequence number is now different from staleExpectedSeq.
        Assert.True(ClipboardManager.SetUnicodeTextWithRetry(userCopiedDuringWindow, attempts: 3, delay: TimeSpan.FromMilliseconds(20)));
        Assert.Equal(userCopiedDuringWindow, SaveStable());

        var outcome = ClipboardManager.RestoreUnicodeTextWithSequenceGuard(
            wouldHaveBeenRestored, staleExpectedSeq, attempts: 3, delay: TimeSpan.FromMilliseconds(20));

        Assert.Equal(ClipboardRestoreOutcome.SkippedSequenceChanged, outcome);
        // The user's newer clipboard content must still be there, untouched.
        Assert.Equal(userCopiedDuringWindow, SaveStable());
    }

    [Fact]
    public async Task RestoreUnicodeTextWithSequenceGuardAsync_writes_back_the_previously_saved_text_when_sequence_unchanged()
    {
        const string original = "async guarded restore original";
        const string transient = "async guarded restore transient";

        Assert.True(ClipboardManager.SetUnicodeTextWithRetry(original, attempts: 3, delay: TimeSpan.FromMilliseconds(20)));
        Assert.Equal(original, SaveStable());

        Assert.True(ClipboardManager.SetUnicodeTextWithRetry(transient, attempts: 3, delay: TimeSpan.FromMilliseconds(20)));
        Assert.Equal(transient, SaveStable());
        int expectedSeq = ClipboardManager.GetSequenceNumber();

        var outcome = await ClipboardManager.RestoreUnicodeTextWithSequenceGuardAsync(
            original, expectedSeq, attempts: 3, delay: TimeSpan.FromMilliseconds(20), CancellationToken.None);
        Assert.Equal(ClipboardRestoreOutcome.Restored, outcome);
        Assert.Equal(original, SaveStable());
    }

    /// <summary>
    /// Done-when pass criterion: an image on the clipboard triggers skip-and-log under
    /// <c>textOnly</c>. Places a synthetic bitmap (raw <c>CF_BITMAP</c> via the same
    /// <c>OpenClipboard</c>/<c>SetClipboardData</c> pair this class already uses elsewhere --
    /// no <c>SendInput</c>/keyboard/window interaction, matching this test class's safety
    /// convention) and asserts <see cref="ClipboardManager.Save"/> reports
    /// <see cref="ClipboardTextBackup.HasNonTextFormats"/>, which is what
    /// <c>WindowsTextInjector</c> uses to decide to skip restoration under
    /// <c>ClipboardPolicy.TextOnly</c>/<c>BestEffort</c>.
    /// </summary>
    [Fact]
    public void Save_reports_HasNonTextFormats_true_when_clipboard_holds_a_synthetic_bitmap()
    {
        var originalBackup = ClipboardManager.Save();
        try
        {
            Assert.True(PutSyntheticBitmap());

            var backup = ClipboardManager.Save();
            Assert.True(backup.HasNonTextFormats);
            Assert.Contains(2u, backup.FormatsPresent); // CF_BITMAP = 2
        }
        finally
        {
            if (originalBackup.HadUnicodeText)
            {
                int seq = ClipboardManager.GetSequenceNumber();
                ClipboardManager.RestoreUnicodeTextWithSequenceGuard(originalBackup.UnicodeText!, seq, attempts: 3, delay: TimeSpan.FromMilliseconds(20));
            }
        }
    }

    [Fact]
    public void Save_does_not_report_HasNonTextFormats_for_a_plain_text_only_clipboard()
    {
        const string text = "plain text, no companions should count as non-text";
        Assert.True(ClipboardManager.SetUnicodeTextWithRetry(text, attempts: 3, delay: TimeSpan.FromMilliseconds(20)));

        var backup = ClipboardManager.Save();
        // Windows auto-synthesizes CF_LOCALE/CF_OEMTEXT alongside CF_UNICODETEXT -- none of
        // those should trip HasNonTextFormats. See ClipboardManager.Save's doc comment.
        Assert.False(backup.HasNonTextFormats);
    }

    /// <summary>
    /// Places a synthetic bitmap on the clipboard via the raw Win32 API (not
    /// <c>System.Windows.Forms.Clipboard.SetImage</c>), mirroring
    /// <c>spikes/s4-inject-win/ClipboardManager.PutSyntheticBitmap</c>. Pure clipboard-API
    /// call on this process -- no <c>SendInput</c>, no keyboard/window interaction.
    /// </summary>
    private static bool PutSyntheticBitmap()
    {
        using var bmp = new System.Drawing.Bitmap(64, 32);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.CornflowerBlue);
        }

        var hBitmap = bmp.GetHbitmap();
        if (!OpenClipboard(IntPtr.Zero))
            return false;
        try
        {
            EmptyClipboard();
            return SetClipboardData(2 /* CF_BITMAP */, hBitmap) != IntPtr.Zero;
        }
        finally
        {
            CloseClipboard();
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
}
