using System.Runtime.InteropServices;
using Soneto.Platform.Windows.Interop;

namespace Soneto.Platform.Windows;

/// <summary>Snapshot of whatever text was on the clipboard before injection touched it.</summary>
public sealed class ClipboardTextBackup
{
    public string? UnicodeText { get; internal set; }
    public bool HadUnicodeText { get; internal set; }

    /// <summary>
    /// Item 7c: true if any clipboard format OUTSIDE the "text family" allow-list
    /// (<c>CF_UNICODETEXT</c>/<c>CF_TEXT</c>/<c>CF_OEMTEXT</c>/<c>CF_LOCALE</c>) was present
    /// at save time -- e.g. an image or a file selection. See <see cref="Save"/>'s doc
    /// comment for why the allow-list exists instead of a naive "anything but
    /// CF_UNICODETEXT" check.
    /// </summary>
    public bool HasNonTextFormats { get; internal set; }

    /// <summary>Every format id observed on the clipboard at save time, purely for
    /// diagnostic logging (e.g. "skipped restore, original held formats [2,8,17]").</summary>
    public List<uint> FormatsPresent { get; internal set; } = new();
}

/// <summary>Distinguishes the three possible outcomes of an atomic, sequence-guarded
/// clipboard restore attempt -- see <see cref="ClipboardManager.RestoreUnicodeTextWithSequenceGuardAsync"/>.</summary>
public enum ClipboardRestoreOutcome
{
    /// <summary>The original text was written back successfully.</summary>
    Restored,

    /// <summary>The restore was correctly ABORTED because the clipboard sequence number no
    /// longer matched what was expected -- i.e. the user copied something new during the
    /// restore window. Not a failure: this is the guard working as designed.</summary>
    SkippedSequenceChanged,

    /// <summary>All retry attempts failed for a reason OTHER than a sequence-number change
    /// (e.g. another process held the clipboard open for the entire retry window). The
    /// caller must not report this as a success -- silently losing the user's original
    /// clipboard content without saying so is the worst failure this app can have.</summary>
    Failed,
}

/// <summary>
/// Raw Win32 clipboard access for plan §1.8 steps 3-5 and 10-11 (not
/// <c>System.Windows.Forms.Clipboard</c>, which would force callers onto an STA thread --
/// <c>OpenClipboard</c>/<c>SetClipboardData</c> have no such requirement at the raw Win32
/// level). Ported from <c>spikes/s4-inject-win/ClipboardManager.cs</c> (including that
/// spike's post-review fix: <c>GlobalAlloc</c>'d memory is freed on every failure path
/// after allocation, since ownership only transfers to the system on a *successful*
/// <c>SetClipboardData</c>).
///
/// <para>
/// <b>Item 7c: sequence-number guard + clipboardPolicy, now implemented.</b> Restore is no
/// longer a plain, unconditional write. <see cref="RestoreUnicodeTextWithSequenceGuard"/>/
/// <see cref="RestoreUnicodeTextWithSequenceGuardAsync"/> open the clipboard once per
/// attempt, read <c>GetClipboardSequenceNumber()</c> WHILE STILL HOLDING IT OPEN, and only
/// proceed to <c>EmptyClipboard</c>/<c>SetClipboardData</c> if it still matches the expected
/// value -- all inside the same open/close critical section, so there is no gap between "we
/// checked the sequence number" and "we wrote" for a user's Ctrl+C to land in. This is the
/// S4 spike's own post-review fix for a genuine TOCTOU race between a standalone
/// sequence-number check and a later, separate write; do not reintroduce that gap by
/// splitting the check and the write into separate <c>OpenClipboard</c> sections.
/// </para>
/// </summary>
public static class ClipboardManager
{
    /// <summary>Step 3/11: current clipboard sequence number, incremented by Windows on
    /// every clipboard content change (including changes made by other processes) -- the
    /// basis of the restore guard.</summary>
    public static int GetSequenceNumber() => InjectionNativeMethods.GetClipboardSequenceNumber();

    /// <summary>Step 4: back up whatever <c>CF_UNICODETEXT</c> is currently on the clipboard,
    /// and note whether any non-text formats are present.</summary>
    public static ClipboardTextBackup Save(Action<string>? log = null)
    {
        var backup = new ClipboardTextBackup();

        if (!InjectionNativeMethods.OpenClipboard(IntPtr.Zero))
        {
            log?.Invoke($"clipboard: OpenClipboard failed during save (error={Marshal.GetLastWin32Error()})");
            return backup;
        }

        try
        {
            // Windows auto-synthesizes companion formats for any text you set --
            // CF_LOCALE (locale of the text) and CF_OEMTEXT (an ANSI copy) always appear
            // alongside CF_UNICODETEXT, even though nothing but plain text was ever placed on
            // the clipboard. A naive "anything other than CF_UNICODETEXT/CF_TEXT is non-text"
            // check (the S4 spike's first attempt) false-positives on essentially every real
            // clipboard save, permanently disabling restoration under the default textOnly
            // policy. Confirmed by direct reproduction in that spike -- every save of a
            // plain-text-only clipboard showed formats [13, 16, 1, 7], i.e. CF_UNICODETEXT +
            // CF_LOCALE + CF_TEXT + CF_OEMTEXT, none of which are actually a second kind of
            // content. This "text family" allow-list is that spike's fix, ported here
            // unchanged.
            var textFamily = new HashSet<uint>
            {
                InjectionNativeMethods.CF_UNICODETEXT,
                InjectionNativeMethods.CF_TEXT,
                InjectionNativeMethods.CF_OEMTEXT,
                InjectionNativeMethods.CF_LOCALE,
            };

            uint fmt = 0;
            while ((fmt = InjectionNativeMethods.EnumClipboardFormats(fmt)) != 0)
            {
                backup.FormatsPresent.Add(fmt);
                if (!textFamily.Contains(fmt))
                    backup.HasNonTextFormats = true;
            }

            if (InjectionNativeMethods.IsClipboardFormatAvailable(InjectionNativeMethods.CF_UNICODETEXT))
            {
                var h = InjectionNativeMethods.GetClipboardData(InjectionNativeMethods.CF_UNICODETEXT);
                if (h != IntPtr.Zero)
                {
                    var ptr = InjectionNativeMethods.GlobalLock(h);
                    if (ptr != IntPtr.Zero)
                    {
                        try
                        {
                            backup.UnicodeText = Marshal.PtrToStringUni(ptr);
                            backup.HadUnicodeText = true;
                        }
                        finally
                        {
                            InjectionNativeMethods.GlobalUnlock(h);
                        }
                    }
                }
            }
        }
        finally
        {
            InjectionNativeMethods.CloseClipboard();
        }

        log?.Invoke(
            $"clipboard: saved backup -- hadUnicodeText={backup.HadUnicodeText} "
            + $"hasNonTextFormats={backup.HasNonTextFormats} formats=[{string.Join(",", backup.FormatsPresent)}]");
        return backup;
    }

    /// <summary>Step 5: set <c>CF_UNICODETEXT</c>, retrying per <see cref="RetryHelper"/> --
    /// clipboard managers (Ditto, Windows clipboard history, Flow Launcher, Copilot) can
    /// transiently hold the clipboard open and collide with a single-shot write.</summary>
    public static bool SetUnicodeTextWithRetry(string text, int attempts, TimeSpan delay, Action<string>? log = null)
    {
        string lastError = "";
        bool ok = RetryHelper.TryWithRetry(
            () => TrySetUnicodeText(text, out lastError),
            attempts, delay,
            attempt => log?.Invoke($"clipboard: SetClipboardData attempt {attempt}/{attempts} failed ({lastError})"));
        log?.Invoke(ok ? "clipboard: SetClipboardData succeeded" : "clipboard: FAILED to set clipboard data after retries");
        return ok;
    }

    /// <summary>
    /// Post-review fix: the async, <see cref="CancellationToken"/>-aware twin of
    /// <see cref="SetUnicodeTextWithRetry"/>, built on
    /// <see cref="RetryHelper.TryWithRetryAsync"/> so the retry delay doesn't block a
    /// thread-pool thread and cancellation is honored between attempts. This is the variant
    /// <c>WindowsTextInjector.InjectAsync</c> (an otherwise fully-async pipeline) actually
    /// calls.
    /// </summary>
    public static async Task<bool> SetUnicodeTextWithRetryAsync(string text, int attempts, TimeSpan delay, CancellationToken ct, Action<string>? log = null)
    {
        string lastError = "";
        bool ok = await RetryHelper.TryWithRetryAsync(
            () => TrySetUnicodeText(text, out lastError),
            attempts, delay, ct,
            attempt => log?.Invoke($"clipboard: SetClipboardData attempt {attempt}/{attempts} failed ({lastError})")).ConfigureAwait(false);
        log?.Invoke(ok ? "clipboard: SetClipboardData succeeded" : "clipboard: FAILED to set clipboard data after retries");
        return ok;
    }

    /// <summary>
    /// Step 10-11's restore, made atomic with the sequence-number guard: opens the clipboard
    /// once PER ATTEMPT, reads <c>GetClipboardSequenceNumber()</c> while still holding it
    /// open, and only proceeds to <c>EmptyClipboard</c>/<c>SetClipboardData</c> if it still
    /// matches <paramref name="expectedSeq"/> -- all inside the same open/close critical
    /// section. If the sequence number has changed, this returns immediately (no further
    /// retries -- a changed sequence number will not spontaneously revert) with
    /// <see cref="ClipboardRestoreOutcome.SkippedSequenceChanged"/>; a mismatch is the guard
    /// working correctly, not a transient failure to retry through. Retries are reserved for
    /// genuine transient failures (e.g. another process briefly holding the clipboard open).
    /// </summary>
    public static ClipboardRestoreOutcome RestoreUnicodeTextWithSequenceGuard(
        string text, int expectedSeq, int attempts, TimeSpan delay, Action<string>? log = null)
    {
        if (attempts < 1)
            throw new ArgumentOutOfRangeException(nameof(attempts), attempts, "attempts must be at least 1.");

        for (int i = 1; i <= attempts; i++)
        {
            if (!InjectionNativeMethods.OpenClipboard(IntPtr.Zero))
            {
                log?.Invoke($"clipboard: OpenClipboard failed during atomic restore attempt {i}/{attempts} (error={Marshal.GetLastWin32Error()})");
                if (i < attempts && delay > TimeSpan.Zero) Thread.Sleep(delay);
                continue;
            }

            try
            {
                int seqNow = InjectionNativeMethods.GetClipboardSequenceNumber();
                if (seqNow != expectedSeq)
                {
                    log?.Invoke(
                        $"clipboard: sequence number is {seqNow} (expected {expectedSeq}), checked atomically while "
                        + "the clipboard was held open -- aborting restore, not writing (user copied during window)");
                    return ClipboardRestoreOutcome.SkippedSequenceChanged;
                }

                var writeResult = WriteUnicodeTextToOpenClipboard(text, out var error);
                if (writeResult == WriteResult.Success)
                {
                    log?.Invoke(
                        $"clipboard: atomic restore succeeded on attempt {i}/{attempts} (sequence verified unchanged "
                        + "inside the same open/close section as the write)");
                    return ClipboardRestoreOutcome.Restored;
                }

                if (writeResult == WriteResult.FailedAfterEmptyClipboard)
                {
                    // Post-review fix: EmptyClipboard() itself bumps GetClipboardSequenceNumber(),
                    // independent of whether SetClipboardData ever succeeds afterward. If we
                    // looped and re-checked the sequence number on a later attempt against the
                    // same (now stale) expectedSeq, our OWN EmptyClipboard call from this attempt
                    // would make that check fail -- misreporting a transient write failure as
                    // "user copied during window" (SkippedSequenceChanged) and potentially
                    // leaving the clipboard empty with no further real attempt. Once
                    // EmptyClipboard has succeeded for an attempt, any further sequence-number
                    // check against expectedSeq can no longer be trusted, so this aborts the
                    // whole restore as Failed immediately rather than retrying.
                    log?.Invoke(
                        $"clipboard: atomic restore attempt {i}/{attempts} failed after EmptyClipboard already "
                        + $"succeeded ({error}) -- our own EmptyClipboard call invalidates the sequence number for "
                        + "any further attempt, so aborting the restore now instead of retrying against a "
                        + "self-perturbed sequence number");
                    return ClipboardRestoreOutcome.Failed;
                }

                log?.Invoke($"clipboard: atomic restore attempt {i}/{attempts} failed ({error})");
            }
            finally
            {
                InjectionNativeMethods.CloseClipboard();
            }

            if (i < attempts && delay > TimeSpan.Zero) Thread.Sleep(delay);
        }

        log?.Invoke("clipboard: FAILED to restore original clipboard content after retries (not a sequence change)");
        return ClipboardRestoreOutcome.Failed;
    }

    /// <summary>
    /// The async, <see cref="CancellationToken"/>-aware twin of
    /// <see cref="RestoreUnicodeTextWithSequenceGuard"/> -- see
    /// <see cref="SetUnicodeTextWithRetryAsync"/>'s doc comment for why this exists alongside
    /// the sync original. <c>WindowsTextInjector.InjectAsync</c> passes
    /// <see cref="CancellationToken.None"/> specifically for its own cancellation-triggered
    /// best-effort restore attempt (so that recovery isn't itself aborted by the very
    /// cancellation it's recovering from), and the caller's live <c>ct</c> for the
    /// normal-completion restore.
    /// </summary>
    public static async Task<ClipboardRestoreOutcome> RestoreUnicodeTextWithSequenceGuardAsync(
        string text, int expectedSeq, int attempts, TimeSpan delay, CancellationToken ct, Action<string>? log = null)
    {
        if (attempts < 1)
            throw new ArgumentOutOfRangeException(nameof(attempts), attempts, "attempts must be at least 1.");

        for (int i = 1; i <= attempts; i++)
        {
            ct.ThrowIfCancellationRequested();

            if (!InjectionNativeMethods.OpenClipboard(IntPtr.Zero))
            {
                log?.Invoke($"clipboard: OpenClipboard failed during atomic restore attempt {i}/{attempts} (error={Marshal.GetLastWin32Error()})");
                if (i < attempts && delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                int seqNow = InjectionNativeMethods.GetClipboardSequenceNumber();
                if (seqNow != expectedSeq)
                {
                    log?.Invoke(
                        $"clipboard: sequence number is {seqNow} (expected {expectedSeq}), checked atomically while "
                        + "the clipboard was held open -- aborting restore, not writing (user copied during window)");
                    return ClipboardRestoreOutcome.SkippedSequenceChanged;
                }

                var writeResult = WriteUnicodeTextToOpenClipboard(text, out var error);
                if (writeResult == WriteResult.Success)
                {
                    log?.Invoke(
                        $"clipboard: atomic restore succeeded on attempt {i}/{attempts} (sequence verified unchanged "
                        + "inside the same open/close section as the write)");
                    return ClipboardRestoreOutcome.Restored;
                }

                if (writeResult == WriteResult.FailedAfterEmptyClipboard)
                {
                    // See the sync twin's identical comment: EmptyClipboard() itself bumps the
                    // sequence number, so once it has succeeded for this attempt, any further
                    // sequence-number check on a later attempt against the same expectedSeq is
                    // no longer trustworthy -- abort the whole restore as Failed now.
                    log?.Invoke(
                        $"clipboard: atomic restore attempt {i}/{attempts} failed after EmptyClipboard already "
                        + $"succeeded ({error}) -- our own EmptyClipboard call invalidates the sequence number for "
                        + "any further attempt, so aborting the restore now instead of retrying against a "
                        + "self-perturbed sequence number");
                    return ClipboardRestoreOutcome.Failed;
                }

                log?.Invoke($"clipboard: atomic restore attempt {i}/{attempts} failed ({error})");
            }
            finally
            {
                InjectionNativeMethods.CloseClipboard();
            }

            if (i < attempts && delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
        }

        log?.Invoke("clipboard: FAILED to restore original clipboard content after retries (not a sequence change)");
        return ClipboardRestoreOutcome.Failed;
    }

    private static bool TrySetUnicodeText(string text, out string error)
    {
        error = "";
        if (!InjectionNativeMethods.OpenClipboard(IntPtr.Zero))
        {
            error = $"OpenClipboard failed, error={Marshal.GetLastWin32Error()}";
            return false;
        }

        try
        {
            return WriteUnicodeTextToOpenClipboard(text, out error) == WriteResult.Success;
        }
        finally
        {
            InjectionNativeMethods.CloseClipboard();
        }
    }

    /// <summary>
    /// Distinguishes, for <see cref="WriteUnicodeTextToOpenClipboard"/>'s caller, whether a
    /// failure happened BEFORE or AFTER <c>EmptyClipboard()</c> succeeded --
    /// <c>EmptyClipboard()</c> itself bumps <c>GetClipboardSequenceNumber()</c>, independent
    /// of whether <c>SetClipboardData</c> ever succeeds afterward, so the guarded-restore
    /// callers need to know this to avoid misinterpreting their own write's side effect as a
    /// user-driven sequence-number change on a later retry attempt. The plain "set" path
    /// (<see cref="TrySetUnicodeText"/>) doesn't check sequence numbers and treats both
    /// failure kinds identically.
    /// </summary>
    private enum WriteResult
    {
        Success,

        /// <summary>Failed before <c>EmptyClipboard()</c> ran or before it succeeded --
        /// nothing has changed on the clipboard yet, safe to retry normally.</summary>
        FailedBeforeEmptyClipboard,

        /// <summary><c>EmptyClipboard()</c> already succeeded (so the sequence number has
        /// already been bumped by OUR OWN call, not a user action) before the failure
        /// occurred.</summary>
        FailedAfterEmptyClipboard,
    }

    /// <summary>
    /// Does the actual <c>GlobalAlloc</c>/<c>EmptyClipboard</c>/<c>SetClipboardData</c> work,
    /// assuming the caller already holds the clipboard open (via <c>OpenClipboard</c>).
    /// Factored out so both the plain "set" path (<see cref="TrySetUnicodeText"/>) and the
    /// atomic restore path (<see cref="RestoreUnicodeTextWithSequenceGuard"/>/
    /// <see cref="RestoreUnicodeTextWithSequenceGuardAsync"/>) share one implementation and
    /// one set of failure-path cleanup rules -- see the <c>GlobalFree</c> calls below: on any
    /// failure after <c>GlobalAlloc</c>, ownership of <c>hMem</c> has NOT transferred to the
    /// system (that only happens on a successful <c>SetClipboardData</c>), so we must free it
    /// ourselves or it leaks.
    ///
    /// <para>
    /// Post-review fix: <c>GlobalAlloc</c>/<c>GlobalLock</c>/marshal-the-text-in now happen
    /// BEFORE <c>EmptyClipboard()</c>, not after. <c>EmptyClipboard()</c> is called ONLY
    /// immediately before <c>SetClipboardData()</c> -- as late as possible -- because
    /// <c>EmptyClipboard()</c> itself bumps <c>GetClipboardSequenceNumber()</c> regardless of
    /// whether the subsequent <c>SetClipboardData</c> ever succeeds. The original ordering
    /// (<c>EmptyClipboard</c> first, then allocate/write) meant a transient
    /// <c>GlobalAlloc</c>/<c>GlobalLock</c>/<c>SetClipboardData</c> failure would still have
    /// already bumped the sequence number, so a later retry attempt's sequence-number check
    /// (in the guarded-restore callers) would see a mismatch caused by OUR OWN prior attempt,
    /// not a user action -- misreporting a transient write failure as
    /// <c>SkippedSequenceChanged</c> ("user copied during window") and potentially leaving the
    /// clipboard empty with no further real attempt. This reordering narrows the
    /// self-perturbing failure window to just <c>SetClipboardData</c> itself, and the
    /// <see cref="WriteResult"/> distinction above lets the guarded-restore loop still catch
    /// that narrowed window and stop retrying rather than trusting a self-perturbed sequence
    /// number on a subsequent attempt.
    /// </para>
    /// </summary>
    private static WriteResult WriteUnicodeTextToOpenClipboard(string text, out string error)
    {
        error = "";

        int bytes = (text.Length + 1) * 2; // UTF-16 + null terminator
        var hMem = InjectionNativeMethods.GlobalAlloc(
            InjectionNativeMethods.GMEM_MOVEABLE | InjectionNativeMethods.GMEM_ZEROINIT, (UIntPtr)bytes);
        if (hMem == IntPtr.Zero)
        {
            error = "GlobalAlloc failed";
            return WriteResult.FailedBeforeEmptyClipboard;
        }

        var ptr = InjectionNativeMethods.GlobalLock(hMem);
        if (ptr == IntPtr.Zero)
        {
            error = "GlobalLock failed";
            InjectionNativeMethods.GlobalFree(hMem); // ownership never transferred -- must free ourselves
            return WriteResult.FailedBeforeEmptyClipboard;
        }
        try
        {
            Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
            Marshal.WriteInt16(ptr, text.Length * 2, 0); // null terminator
        }
        finally
        {
            InjectionNativeMethods.GlobalUnlock(hMem);
        }

        // EmptyClipboard() as LATE as possible -- see class doc comment above. From this
        // point on, any failure has already bumped GetClipboardSequenceNumber() through no
        // fault of the user.
        if (!InjectionNativeMethods.EmptyClipboard())
        {
            error = $"EmptyClipboard failed, error={Marshal.GetLastWin32Error()}";
            InjectionNativeMethods.GlobalFree(hMem); // ownership never transferred -- must free ourselves
            return WriteResult.FailedBeforeEmptyClipboard;
        }

        if (InjectionNativeMethods.SetClipboardData(InjectionNativeMethods.CF_UNICODETEXT, hMem) == IntPtr.Zero)
        {
            error = $"SetClipboardData failed, error={Marshal.GetLastWin32Error()}";
            InjectionNativeMethods.GlobalFree(hMem); // SetClipboardData failed -- ownership did not transfer
            return WriteResult.FailedAfterEmptyClipboard;
        }

        return WriteResult.Success;
    }
}
