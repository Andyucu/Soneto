using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Soneto.Core.Abstractions;
using Soneto.Core.Configuration;
using Soneto.Platform.Windows.Interop;

namespace Soneto.Platform.Windows;

/// <summary>
/// Windows implementation of <see cref="ITextInjector"/>: clipboard + synthetic paste via
/// <c>SendInput</c>, promoting <c>spikes/s4-inject-win</c>'s proven algorithm (see that
/// spike's README for the safety-relevant post-review fixes it already carries: a
/// silently-swallowed clipboard-restore failure, and the atomic open-check-write-close fix
/// for the sequence-number guard's TOCTOU race) to real product code, per plan §1.8's base
/// algorithm (steps 1-5, 8, 10), plus item 7b's step 6/9 modifier sanitising.
///
/// <para>
/// <b>Item 7b update (modifier sanitiser -- now DONE, no longer deferred):</b> this class
/// now suppresses physically-held Shift/Alt/Win (via <see cref="ModifierSanitizer"/>)
/// before sending the paste chord, and re-checks/restores only what's still physically
/// held afterward, gated on <c>InjectionOptions.SanitizeModifiers</c>. Control is
/// deliberately excluded from this sanitiser -- see <see cref="ModifierSanitizer"/>'s doc
/// comment for why. See that class's doc comment for the full step 6/9 algorithm,
/// including how it generalizes item 6/7's Control-specific trigger-key disambiguation to
/// whichever modifier family the configured trigger belongs to.
/// </para>
///
/// <para>
/// <b>Scope for what's still deferred (item 7c):</b>
/// </para>
/// <list type="bullet">
/// <item><description><b>Trigger-key sanitising -- explicit judgment call, CORRECTED after
/// post-review (this class's OWN synthetic paste-chord modifiers can collide with the
/// trigger key -- see below).</b> Plan §1.8 step 6 actually names two separate sub-problems:
/// (a) suppressing the trigger key's own down/up so no orphan key-up leaks and confuses apps
/// that track modifier state themselves, and (b) suppressing modifiers the user is
/// physically holding. Problem (a) is judged to be <b>already fully handled upstream</b> by
/// <c>WindowsHotkeySource</c> (item 6, shipped) via SharpHook's per-event
/// <c>SuppressEvent</c> flag, set independently and symmetrically on both the trigger's
/// <c>KeyPressed</c> and <c>KeyReleased</c> ("suppress both or neither -- never one", already
/// enforced there). By the time <see cref="InjectAsync"/> runs -- after the trigger's key-up
/// and, eventually, a full capture+decode cycle via <c>SessionController</c> (item 9, not yet
/// built) -- the trigger key's own event pair has already been fully handled; this class
/// never reads or suppresses the trigger key's virtual-key code at all.
/// <para>
/// <b>What was previously overclaimed here:</b> this doc comment used to say "there is
/// nothing left for it to sanitise at this layer" -- that is only true for the DEFAULT
/// <c>RightControl</c> binding, not in general. <see cref="SendPasteChord"/> DOES
/// synthesize <c>VK_LCONTROL</c> (always) and <c>VK_LSHIFT</c> (for the shift chord) via
/// <c>SendInput</c>, and <c>WindowsHotkeySource</c>'s <c>WH_KEYBOARD_LL</c> hook observes
/// ALL keyboard events system-wide, including this class's own synthetic ones. If a user
/// configures <c>LeftControl</c> or <c>LeftShift</c> as the trigger key (both are explicit,
/// supported aliases in <c>HotkeyKeyMapper</c>'s alias table, same as the default
/// <c>RightControl</c>), every paste's synthetic modifier would deterministically collide
/// with the trigger, get suppressed before reaching the target app, and post a phantom
/// hotkey event with no real user action behind it -- reproducing on every single paste
/// under that configuration, not a rare race. <b>Fixed at the hook layer, not here:</b>
/// <c>WindowsHotkeySource</c> now checks SharpHook's <c>HookEventArgs.IsEventSimulated</c>
/// (backed by the Windows <c>LLKHF_INJECTED</c> flag, confirmed present via SharpHook
/// 8.0.0's own XML docs) and ignores any trigger-key-coded event that is self-injected,
/// entirely independent of which physical key the trigger is bound to. This class itself
/// still does nothing special for the trigger key -- the fix lives where the collision is
/// actually observed (the hook), not where the synthetic input is generated (here) --
/// but the class-level claim that trigger-key sanitising was a non-issue "in general" was
/// false and is corrected here.
/// Problem (b) above -- suppressing modifiers the user is physically holding -- is now
/// DONE, per the "Item 7b update" paragraph above this bullet list.
/// </para></description></item>
/// <item><description><b>Clipboard sequence guard + policy -- now DONE (item 7c).</b>
/// Restore is no longer unconditional: <see cref="ClipboardManager.RestoreUnicodeTextWithSequenceGuardAsync"/>
/// atomically checks <c>GetClipboardSequenceNumber()</c> against the value captured right
/// after this class's own <c>SetClipboardData</c> write, inside the same open/close section
/// as the write itself, and aborts (without overwriting) if the user copied something new
/// during the restore-delay window. Under <c>clipboardPolicy=textOnly</c> (the default) or
/// <c>bestEffort</c> (Phase 1 treats it identically -- no full OLE/<c>IDataObject</c>
/// round-trip), a non-text original clipboard (image, file selection, etc.) skips
/// restoration entirely rather than risk destroying it; <c>never</c> continues to skip
/// restoration altogether via <c>InjectionOptions.RestoreClipboard</c>. See
/// <see cref="ClipboardManager"/>'s doc comment for the guard/allow-list details.</description></item>
/// </list>
///
/// <para>
/// <b>Item 10: <see cref="InjectionMethod.UnicodeSynth"/>, both as an explicit config choice
/// and as the plan §1.12 automatic fallback.</b> Per plan §1.8's "Other notes": <c>SendInput</c>
/// with <c>KEYEVENTF_UNICODE</c>, one <c>INPUT</c> down/up pair per UTF-16 code unit, batched
/// <see cref="UnicodeSynthBatchSize"/> (~50) per <c>SendInput</c> call with a
/// <see cref="UnicodeSynthBatchGap"/> (~5ms) gap between batches so a very long transcript
/// doesn't flood the input queue -- slow, but touches neither the clipboard nor the modifier
/// state (no <see cref="ClipboardManager"/>/<see cref="ModifierSanitizer"/> involvement at
/// all on this path). Two distinct triggers for this path, both handled in
/// <see cref="InjectAsync"/>:
/// </para>
/// <list type="bullet">
/// <item><description><b>Explicit config choice</b> (<c>opts.Method == UnicodeSynth</c>):
/// used directly, with NO clipboard attempt at all -- this is the user's own explicit
/// choice, not a fallback situation, so steps 3-11 (sequence number, backup, set, restore)
/// never run.</description></item>
/// <item><description><b>Automatic fallback</b> (plan §1.12's "Clipboard set fails after
/// retries" row): when <c>opts.Method == ClipboardPaste</c> (the default) and
/// <see cref="ClipboardManager.SetUnicodeTextWithRetryAsync"/> still fails after all retries,
/// this class automatically retries delivery via <see cref="SendUnicodeSynthAsync"/> for that
/// same call -- the caller never has to have pre-configured <c>UnicodeSynth</c> for this to
/// kick in. A successful fallback still reports <see cref="InjectionOutcome.Injected"/> (the
/// text did land), with a distinct log line noting which method actually delivered it so a
/// human reading logs can tell a fallback happened, not just that injection
/// succeeded. <b>Post-review fix (BLOCKING):</b> a FAILED clipboard set is not guaranteed to
/// be a no-op on the clipboard's actual content -- <c>ClipboardManager.WriteUnicodeTextToOpenClipboard</c>
/// calls <c>EmptyClipboard()</c> (destroying/bumping the sequence number of whatever was
/// there) BEFORE <c>SetClipboardData()</c>, so all retry attempts can end as
/// <c>WriteResult.FailedAfterEmptyClipboard</c> and still leave the clipboard genuinely
/// emptied even though <c>clipboardSet</c> ends up <c>false</c>. Before attempting the
/// UnicodeSynth fallback, <see cref="InjectAsync"/> therefore first attempts the same atomic,
/// sequence-guarded restore used everywhere else in this class (via
/// <see cref="TryRestoreClipboardAsync"/>), checked against a sequence number captured
/// immediately BEFORE the set attempt (not <c>seqAfterOurSet</c>, which is only assigned on
/// the success path) -- so the user's original clipboard content is not silently lost just
/// because our own write attempt failed partway through, while a genuine user copy during the
/// failed retry window still correctly skips the restore via the same guard every other
/// restore call site relies on.</description></item>
/// </list>
///
/// <para>
/// <b>Phase 4 item 2 (§4.4): per-app override resolution lives HERE, not in
/// <c>SessionController</c>/the composition layer.</b> This class's own step 1-2 fresh
/// foreground-window lookup below (<c>currentForeground</c>) is the ONLY place that actually
/// knows which window an injection is really about to land in -- <c>SendInput</c> always
/// targets whatever is foreground at the moment it's called, which can differ from whatever
/// <c>SessionController</c> captured at key-down (see the divergence-logging block below).
/// Right after that lookup, <see cref="InjectAsync"/> resolves the foreground window's owning
/// process's executable name (<see cref="TryGetProcessExecutableName"/>: <c>GetWindowThreadProcessId</c>
/// + <see cref="System.Diagnostics.Process.GetProcessById(int)"/>.<c>ProcessName</c>, chosen over
/// <c>QueryFullProcessImageName</c> for simplicity -- no extra P/Invoke struct marshaling, and
/// the managed API already does the OS-handle lifecycle correctly) and looks it up in
/// <see cref="_perApp"/> via <c>Soneto.Core.Configuration.PerAppOverrideResolver.Resolve</c> (the
/// pure decision logic, unit-tested in <c>Soneto.Core.Tests</c> without any real Win32 call). A
/// match produces a new, merged <see cref="InjectionOptions"/> for THIS injection call only --
/// the caller's <c>opts</c> parameter is never mutated (it's a shared <c>record</c>). No match
/// (including: no <see cref="_perApp"/> table configured, or the process name can't be
/// resolved) falls through byte-for-byte identical to the pre-Phase-4 base path -- and when
/// no table is configured at all, the process-name lookup is skipped entirely, so that path
/// makes no additional native calls either, not just no additional option changes.
/// </para>
/// </summary>
public sealed class WindowsTextInjector : ITextInjector
{
    private const int ClipboardRetryAttempts = 3;
    private static readonly TimeSpan ClipboardRetryDelay = TimeSpan.FromMilliseconds(20);

    // Item 10 / plan §1.8 "Other notes": "batched ~50 with a 5ms gap" -- exact literal numbers.
    internal const int UnicodeSynthBatchSize = 50;
    internal static readonly TimeSpan UnicodeSynthBatchGap = TimeSpan.FromMilliseconds(5);

    private readonly ILogger<WindowsTextInjector> _logger;

    // Phase 4 item 2 (§4.4): resolved once at composition time by
    // Soneto.Composition.DaemonComposition from SonetoConfig.Injection.PerApp -- see that call
    // site's own comment for why the dictionary handed in here must already use
    // StringComparer.OrdinalIgnoreCase. Null/empty is the default (no overrides configured, or
    // a caller -- e.g. a test -- didn't pass one) and is handled identically by
    // PerAppOverrideResolver.Resolve -- always falls through to the base opts unchanged.
    private readonly IReadOnlyDictionary<string, PerAppOverride>? _perApp;

    public WindowsTextInjector(ILogger<WindowsTextInjector> logger, IReadOnlyDictionary<string, PerAppOverride>? perApp = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _perApp = perApp;
    }

    /// <summary>Step 1: capture the foreground window handle (at key-down, per the caller's
    /// contract), boxed as the opaque <see cref="object"/> the interface requires.</summary>
    public object? CaptureTarget()
    {
        var hwnd = InjectionNativeMethods.GetForegroundWindow();
        return hwnd == IntPtr.Zero ? null : hwnd;
    }

    /// <summary>
    /// Phase 4 item 2 (§4.4): resolves <paramref name="hwnd"/>'s owning process's executable
    /// file name (e.g. <c>"WindowsTerminal.exe"</c>), matching the exact shape
    /// <see cref="InjectionConfig.PerApp"/>'s example keys already ship with. Two-step
    /// resolution: <c>GetWindowThreadProcessId</c> to get the PID, then
    /// <see cref="System.Diagnostics.Process.GetProcessById(int)"/>.<c>ProcessName</c> (which
    /// omits the extension) with <c>".exe"</c> appended -- chosen over the alternative
    /// (<c>QueryFullProcessImageName</c>, which would need a process handle opened via
    /// <c>OpenProcess</c> plus a fixed-size buffer marshaled back through another P/Invoke
    /// call) because the managed <see cref="System.Diagnostics.Process"/> API already handles
    /// the native handle's open/close lifecycle correctly and needs no extra interop surface
    /// in <c>InjectionNativeMethods</c> beyond the one new <c>GetWindowThreadProcessId</c>
    /// declaration. <b>Known, honest simplification:</b> not every Windows process's
    /// <c>ProcessName</c> + <c>".exe"</c> is guaranteed to exactly equal its real executable
    /// file name in every case (e.g. some packaged/UWP apps can have a launcher/host process
    /// whose <c>ProcessName</c> diverges from the app's own display/package name) -- this is
    /// the same "keyed by executable name" assumption <see cref="InjectionConfig.PerApp"/>'s
    /// schema itself already makes (its own shipped example keys, <c>"WindowsTerminal.exe"</c>/
    /// <c>"Teams.exe"</c>, are plain Win32 process names), so this method does not attempt to
    /// go further than that existing schema assumption already commits to. <b>Second known,
    /// honest simplification -- a PID-reuse race the catch list below does NOT cover:</b>
    /// between <c>GetWindowThreadProcessId</c> returning a PID and
    /// <see cref="System.Diagnostics.Process.GetProcessById(int)"/> resolving it, Windows could
    /// in principle have recycled that PID onto a different, still-running process -- which
    /// would resolve to the wrong <c>ProcessName</c> silently, with no exception to catch.
    /// Deliberately not defended against: the window is microseconds wide, and the worst case
    /// is one injection using the wrong paste chord/restore delay (never wrong text, never a
    /// security boundary), so a handle-based re-validation would cost more interop surface than
    /// the failure is worth. Do not read the three <c>catch</c> clauses below as an exhaustive
    /// list of what can go wrong here. Returns
    /// <c>null</c> (never throws) if the PID can't be resolved, the process has already exited
    /// by the time <see cref="System.Diagnostics.Process.GetProcessById(int)"/> runs (a real,
    /// expected race -- e.g. the foreground app closes between the foreground-window lookup and
    /// this call), or access to the process is denied -- callers must treat <c>null</c> the same
    /// as "no PerApp match," never as an error worth failing the injection over.
    /// </summary>
    internal static string? TryGetProcessExecutableName(IntPtr hwnd)
    {
        try
        {
            uint threadId = InjectionNativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            // Two independent failure signals from the same call, neither redundant:
            // threadId == 0 is Win32's documented "invalid window handle" return, while
            // pid == 0 is a defensive check on the out-parameter the API doesn't strictly
            // promise to leave alone on failure. Both mean "no usable process."
            if (threadId == 0 || pid == 0)
                return null;

            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.ProcessName + ".exe";
        }
        catch (ArgumentException)
        {
            // Process.GetProcessById throws ArgumentException when no process with that PID
            // is currently running -- a real, expected race (see doc comment above), not a bug.
            return null;
        }
        catch (InvalidOperationException)
        {
            // The process exited between GetProcessById returning and ProcessName being read --
            // same "expected race," different exception shape from the managed API.
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Access denied opening the process handle (e.g. an elevated process while Soneto
            // itself runs unelevated) -- expected/possible, not a bug; fall through to "no match."
            return null;
        }
    }

    /// <summary>
    /// Phase 4 item 3 (§4.4): the <see cref="ITextInjector"/>-interface-level counterpart to
    /// <see cref="TryGetProcessExecutableName(IntPtr)"/> above -- unboxes <paramref name="target"/>
    /// back to the <c>IntPtr</c> <see cref="CaptureTarget"/> boxed it as, then delegates to that
    /// same static helper (which already documents every failure mode/honest simplification;
    /// not repeated here). Returns <c>null</c>, never throws, if <paramref name="target"/> is
    /// <c>null</c> or not an <c>IntPtr</c> at all (e.g. a stale/foreign handle from a caller
    /// that isn't this class's own <see cref="CaptureTarget"/>).
    /// </summary>
    public string? TryResolveProcessExecutableName(object? target) =>
        target is IntPtr hwnd ? TryGetProcessExecutableName(hwnd) : null;

    /// <summary>
    /// Steps 1-2, 4-5, 7-8, 10 of plan §1.8's base algorithm (see the class doc comment for
    /// what's deferred). Never throws for expected failure modes -- those are reported via
    /// the returned <see cref="InjectionOutcome"/>, per the interface's contract.
    /// </summary>
    public async Task<InjectionOutcome> InjectAsync(string text, object? target, InjectionOptions opts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(opts);

        // Steps 1-2: resolve target. This item supports only targetLostPolicy="current"
        // (plan §1.10's default): if the captured target is null or is no longer a valid
        // window, fall back to whatever is foreground right now (plan §1.4 edge case 2's
        // documented default) rather than aborting outright.
        //
        // Post-review fix (plan §1.8: "log both handles so you can tell what happened"):
        // SendInput always delivers to whatever window currently has OS focus, not to a
        // specific HWND -- there is no way to target a specific, possibly-not-foreground
        // window with it. So the handle that actually matters here is the CURRENT foreground
        // window, unconditionally fetched below, not necessarily the one captured at
        // key-down. Not calling SetForegroundWindow is correct per TargetLostPolicy
        // "current" (inject wherever focus is now); what was missing was logging the
        // divergence when the originally-captured target and the current foreground window
        // disagree, so a human debugging a "typed into the wrong window" report can tell
        // what happened.
        IntPtr capturedTarget = target is IntPtr h ? h : IntPtr.Zero;
        IntPtr currentForeground = InjectionNativeMethods.GetForegroundWindow();
        bool capturedTargetStillValid = capturedTarget != IntPtr.Zero && InjectionNativeMethods.IsWindow(capturedTarget);
        if (capturedTargetStillValid && capturedTarget != currentForeground)
        {
            _logger.LogInformation(
                "Injection target diverged from the current foreground window: captured target=0x{CapturedTarget:X}, "
                + "current foreground=0x{CurrentForeground:X}. SendInput delivers to the current foreground window "
                + "regardless of which window was captured at key-down (TargetLostPolicy=\"current\").",
                capturedTarget.ToInt64(), currentForeground.ToInt64());
        }

        // The value actually reported/used from here on is the current foreground window --
        // that's what SendInput will actually hit, regardless of what was captured.
        IntPtr hwnd = currentForeground;
        bool targetResolved = hwnd != IntPtr.Zero;
        if (!targetResolved)
        {
            _logger.LogWarning("Injection target lost: no valid target handle and no foreground window available.");
            return InjectionOutcomeMapper.Map(targetResolved: false, clipboardSet: false, synthSent: false);
        }

        ct.ThrowIfCancellationRequested();

        // Phase 4 item 2 (§4.4): per-app override resolution -- see class doc comment for why
        // this happens HERE, right after the real foreground-window lookup above, rather than
        // one layer up in SessionController/the composition layer. `opts` itself is never
        // mutated (shared record); `effectiveOpts` is the merged value used for the rest of
        // this call only. Falls straight through to `opts` unchanged when `_perApp` is
        // null/empty, the process name can't be resolved, or no entry matches.
        // The `_perApp is { Count: > 0 }` guard is what makes the no-override path
        // call-identical (not merely value-identical) to the pre-Phase-4 base path: with no
        // overrides configured -- the default for anyone who hasn't hand-edited config.json,
        // and for every caller/test that passes no table -- this adds ZERO native calls to the
        // injection hot path, rather than paying for a GetWindowThreadProcessId P/Invoke plus a
        // Process handle open/close per injection just to hand the result to a lookup that was
        // always going to miss.
        string? processExecutableName = _perApp is { Count: > 0 } ? TryGetProcessExecutableName(hwnd) : null;
        InjectionOptions effectiveOpts = PerAppOverrideResolver.Resolve(opts, processExecutableName, _perApp);
        if (!ReferenceEquals(effectiveOpts, opts))
        {
            _logger.LogInformation(
                "Injection: applying PerApp override for process '{Process}' (method={Method}, "
                + "pasteChord={PasteChord}, clipboardRestoreDelayMs={ClipboardRestoreDelayMs}).",
                processExecutableName, effectiveOpts.Method, effectiveOpts.PasteChord,
                (int)effectiveOpts.ClipboardRestoreDelay.TotalMilliseconds);
        }

        // Item 10: explicit UnicodeSynth config choice -- used directly, with NO clipboard
        // attempt at all (this is the user's own explicit choice, not a fallback situation;
        // see class doc comment). Steps 3-11 below (sequence number, backup, set, restore)
        // are all clipboard-path-only and never run on this branch.
        // Fully-qualified: this file has `using` directives for both Soneto.Core.Abstractions
        // (InjectionOptions.Method's own enum) and Soneto.Core.Configuration (PerAppOverride /
        // PerAppOverrideResolver, pulled in by Phase 4 item 2), and both declare an
        // `InjectionMethod`. The runtime options' enum is the Abstractions one.
        if (effectiveOpts.Method == Soneto.Core.Abstractions.InjectionMethod.UnicodeSynth)
        {
            _logger.LogInformation(
                "Injection: using UnicodeSynth (configured method) -- no clipboard touched, "
                + "no modifier suppression needed.");
            bool synthOk = await SendUnicodeSynthAsync(text, ct).ConfigureAwait(false);
            if (!synthOk)
            {
                _logger.LogError("Injection failed: UnicodeSynth SendInput did not deliver the full text.");
                return InjectionOutcome.SynthFailed;
            }
            _logger.LogInformation("Injected {Chars} chars via UnicodeSynth.", text.Length);
            return InjectionOutcome.Injected;
        }

        // Step 3: sequence number before we touch anything, mostly for parity/logging with
        // the plan's literal step numbering (matches spikes/s4-inject-win/Injector.cs).
        int seqBefore = ClipboardManager.GetSequenceNumber();
        _logger.LogDebug("Injection: clipboard sequence number before touching the clipboard: {SeqBefore}", seqBefore);

        // Step 4: save whatever's currently on the clipboard so it can be restored later.
        var backup = effectiveOpts.RestoreClipboard
            ? ClipboardManager.Save(msg => _logger.LogDebug("{Message}", msg))
            : new ClipboardTextBackup();

        // Step 5: set our text, retrying per plan §1.8's "not optional" retry requirement.
        // Async, ct-aware retry (post-review fix): the delay between attempts uses
        // Task.Delay(delay, ct) rather than a thread-pool-blocking Thread.Sleep, and honors
        // cancellation mid-retry -- see RetryHelper.TryWithRetryAsync's doc comment.
        //
        // Captured immediately BEFORE the set attempt (post-review fix, BLOCKING): a FAILED
        // set is NOT guaranteed to be a no-op on the clipboard's actual content --
        // WriteUnicodeTextToOpenClipboard calls EmptyClipboard() (destroying/bumping the
        // sequence number of whatever was there) BEFORE SetClipboardData(), so a failure after
        // EmptyClipboard succeeded but SetClipboardData didn't (WriteResult.FailedAfterEmptyClipboard,
        // a real, already-anticipated scenario -- see that enum's doc comment) can leave the
        // clipboard genuinely emptied even though clipboardSet below ends up false. seqBeforeSet
        // is the baseline the fallback-path restore below checks against -- NOT seqAfterOurSet,
        // which is only ever assigned on the success path a few lines down and would be stale/
        // wrong here.
        int seqBeforeSet = ClipboardManager.GetSequenceNumber();
        bool clipboardSet = await ClipboardManager.SetUnicodeTextWithRetryAsync(
            text, ClipboardRetryAttempts, ClipboardRetryDelay, ct,
            msg => _logger.LogDebug("{Message}", msg)).ConfigureAwait(false);
        if (!clipboardSet)
        {
            // Plan §1.12's "Clipboard set fails after retries" row: automatic fallback to
            // UnicodeSynth for THIS injection, not a return of ClipboardFailed -- the caller
            // never has to have pre-configured UnicodeSynth for this to kick in. See class
            // doc comment's "Automatic fallback" bullet.
            _logger.LogError(
                "Injection: clipboard set failed after {Attempts} attempts; falling back to "
                + "UnicodeSynth for this injection (plan §1.12 auto-recovery).",
                ClipboardRetryAttempts);

            // Post-review fix (BLOCKING): a failed set may have already emptied the clipboard
            // (see seqBeforeSet's doc comment above) -- attempt the same atomic, sequence-guarded
            // restore used elsewhere in this method BEFORE reporting any outcome, so the user's
            // original clipboard content isn't silently, permanently lost just because our own
            // write attempt failed partway through. If the sequence number no longer matches
            // seqBeforeSet, TryRestoreClipboardAsync's own guard correctly treats that as "the
            // user copied something else during the failed retry window" and skips restoring --
            // same behaviour as every other restore call site in this class.
            if (effectiveOpts.RestoreClipboard && backup.HadUnicodeText)
                await TryRestoreClipboardAsync(backup, seqBeforeSet, effectiveOpts.Policy, ct).ConfigureAwait(false);

            bool synthFallbackOk = await SendUnicodeSynthAsync(text, ct).ConfigureAwait(false);
            if (synthFallbackOk)
            {
                _logger.LogInformation(
                    "Injected {Chars} chars via the UnicodeSynth fallback (clipboard set had failed).",
                    text.Length);
                return InjectionOutcome.Injected;
            }

            _logger.LogError("Injection failed: the UnicodeSynth fallback also failed after the clipboard set failure.");
            return InjectionOutcomeMapper.Map(targetResolved, clipboardSet: false, synthSent: false);
        }

        // Item 7c: the sequence number right after OUR OWN successful clipboard write --
        // this is the value the atomic restore guard checks against at restore time, not
        // seqBefore.
        int seqAfterOurSet = ClipboardManager.GetSequenceNumber();
        _logger.LogDebug("Injection: clipboard sequence number after our own set: {SeqAfterOurSet}", seqAfterOurSet);

        // Post-review fix: from here on, the clipboard has already been overwritten with the
        // transcript and the original content is backed up in `backup`. If `ct` is cancelled
        // mid-flight (during either await below), a best-effort restore attempt must still
        // happen before the resulting OperationCanceledException unwinds out of this method --
        // otherwise cancellation permanently clobbers the clipboard with the transcript and
        // never even tries to restore it. `clipboardRestoreAttempted` tracks whether the
        // NORMAL completion path (below) already handled the restore, so the `finally` block
        // doesn't double-restore on the happy path.
        bool clipboardRestoreAttempted = false;
        // Item 7b: modifiers suppressed by the sanitiser (step 6), tracked here (not just
        // inside the try block below) so the `finally` clause can still restore them on a
        // cancellation path that never reaches the normal-completion restore call --
        // mirrors `clipboardRestoreAttempted`'s pattern immediately above.
        List<(string Name, int Vk)>? suppressedModifiers = null;
        bool modifiersRestoreAttempted = false;
        try
        {
            // Step 6: suppress physically-held Shift/Alt/Win before the paste chord.
            // Full bypass when SanitizeModifiers is false, per this item's explicit
            // config on/off switch (SonetoConfig.InjectionConfig.SanitizeModifiers).
            if (effectiveOpts.SanitizeModifiers)
                suppressedModifiers = ModifierSanitizer.Suppress(effectiveOpts.TriggerKey, _logger);

            // Step 7: pre-delay.
            if (effectiveOpts.PreDelay > TimeSpan.Zero)
                await Task.Delay(effectiveOpts.PreDelay, ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            // Step 8: send the paste chord.
            bool synthSent = SendPasteChord(effectiveOpts.PasteChord);

            // Step 9: re-check physical state and restore only what's still held --
            // unconditionally, regardless of whether the chord itself succeeded, so a
            // failed paste never leaves a suppressed modifier permanently stuck.
            if (suppressedModifiers is not null)
            {
                ModifierSanitizer.Restore(suppressedModifiers, _logger);
                modifiersRestoreAttempted = true;
            }

            if (!synthSent)
            {
                _logger.LogError("Injection failed: SendInput did not accept the paste chord '{Chord}'.", effectiveOpts.PasteChord);
                return InjectionOutcomeMapper.Map(targetResolved, clipboardSet, synthSent: false);
            }

            // Steps 10-11: wait the configured delay, then attempt an ATOMIC,
            // sequence-guarded restore (item 7c) -- see ClipboardManager's doc comment for
            // why the check-and-write must be one critical section, not a separate check
            // then a later separate write. A restore failure (for any reason: sequence
            // changed, non-text policy skip, or a genuine write failure after retries) is
            // logged distinctly but does NOT change the returned outcome -- the paste itself
            // already succeeded, and silently claiming success on the restore specifically
            // (the S4 spike's own originally-swallowed bug) is what must never happen here.
            if (effectiveOpts.RestoreClipboard && backup.HadUnicodeText)
            {
                if (effectiveOpts.ClipboardRestoreDelay > TimeSpan.Zero)
                    await Task.Delay(effectiveOpts.ClipboardRestoreDelay, ct).ConfigureAwait(false);

                clipboardRestoreAttempted = true;
                await TryRestoreClipboardAsync(backup, seqAfterOurSet, effectiveOpts.Policy, ct).ConfigureAwait(false);
            }

            return InjectionOutcomeMapper.Map(targetResolved, clipboardSet, synthSent);
        }
        finally
        {
            // Cancellation-triggered modifier restore: only runs if the try block above
            // never reached its own normal-completion restore (step 9), AND cancellation is
            // what caused that -- e.g. `ct` fired during the PreDelay await, before the
            // paste chord (and therefore step 9) ever ran. Without this, a cancelled
            // injection could leave a physically-held Shift/Alt/Win permanently suppressed
            // -- exactly the "stuck modifier" plan §1.8 warns against, just via a different
            // trigger (cancellation) than the one the re-check-before-restore rule already
            // guards against (the user releasing the key mid-injection).
            if (!modifiersRestoreAttempted && suppressedModifiers is not null && ct.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Injection cancelled mid-flight before the modifier sanitiser's restore "
                    + "step ran; attempting a best-effort restore of any still-held modifiers "
                    + "before unwinding.");
                ModifierSanitizer.Restore(suppressedModifiers, _logger);
            }

            // Cancellation-triggered restore: only runs if the try block above never got to
            // (or never finished) its own normal-completion restore, AND cancellation is what
            // caused that -- e.g. `ct` fired during the PreDelay await, before the paste chord
            // was even sent, or during the ClipboardRestoreDelay await. Uses
            // CancellationToken.None deliberately: `ct` is already cancelled at this point, so
            // passing it through would make the recovery attempt itself abort immediately via
            // Task.Delay(delay, ct) -- defeating the entire point of a best-effort restore.
            if (!clipboardRestoreAttempted && effectiveOpts.RestoreClipboard && backup.HadUnicodeText && ct.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Injection cancelled mid-flight; attempting a best-effort clipboard restore "
                    + "before unwinding (cancellation-triggered restore, distinct from the "
                    + "normal-completion restore above).");
                // CancellationToken.None deliberately: `ct` is already cancelled at this point,
                // so passing it through would make the recovery attempt itself abort
                // immediately via Task.Delay(delay, ct) -- defeating the entire point of a
                // best-effort restore.
                await TryRestoreClipboardAsync(backup, seqAfterOurSet, effectiveOpts.Policy, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Item 7c: shared restore logic for both the normal-completion restore point and the
    /// cancellation-triggered best-effort restore in <c>InjectAsync</c>'s <c>finally</c>
    /// block. Applies the <c>clipboardPolicy</c> non-text skip first (textOnly/bestEffort
    /// both decline to restore over a non-text original in Phase 1 -- no full OLE round-trip
    /// -- per the class doc comment), then the atomic sequence-guarded restore. Logs each of
    /// the three distinct outcomes (restored / skipped-non-text / skipped-sequence-changed /
    /// failed) but never changes the caller's returned <see cref="InjectionOutcome"/> -- the
    /// paste itself has already succeeded by the time this runs.
    /// </summary>
    private async Task TryRestoreClipboardAsync(ClipboardTextBackup backup, int expectedSeq, Soneto.Core.Abstractions.ClipboardPolicy policy, CancellationToken ct)
    {
        bool declinesNonText = policy is Soneto.Core.Abstractions.ClipboardPolicy.TextOnly or Soneto.Core.Abstractions.ClipboardPolicy.BestEffort;
        if (declinesNonText && backup.HasNonTextFormats)
        {
            _logger.LogWarning(
                "Skipping clipboard restore: the original clipboard held non-text formats "
                + "({Formats}) and policy={Policy} does not attempt a full-fidelity round trip "
                + "in Phase 1; leaving the injected transcript on the clipboard rather than "
                + "risk destroying the original non-text content.",
                string.Join(",", backup.FormatsPresent), policy);
            return;
        }

        var outcome = await ClipboardManager.RestoreUnicodeTextWithSequenceGuardAsync(
            backup.UnicodeText!, expectedSeq, ClipboardRetryAttempts, ClipboardRetryDelay, ct,
            msg => _logger.LogDebug("{Message}", msg)).ConfigureAwait(false);

        switch (outcome)
        {
            case ClipboardRestoreOutcome.Restored:
                break; // already logged by ClipboardManager
            case ClipboardRestoreOutcome.SkippedSequenceChanged:
                _logger.LogInformation(
                    "Skipped clipboard restore: clipboard sequence number changed during the "
                    + "restore window (user copied during window).");
                break;
            case ClipboardRestoreOutcome.Failed:
                _logger.LogWarning(
                    "Failed to restore the original clipboard content after retries "
                    + "(not a sequence change -- see prior log entries for the cause).");
                break;
        }
    }

    /// <summary>
    /// Item 10: sends <paramref name="text"/> via <c>KEYEVENTF_UNICODE</c> <c>SendInput</c>
    /// batches (see <see cref="BuildUnicodeSynthBatches"/>), one batch per
    /// <see cref="UnicodeSynthBatchSize"/> code units, with a <see cref="UnicodeSynthBatchGap"/>
    /// delay between (not after) batches. Returns <c>false</c> on the first batch that
    /// <c>SendInput</c> doesn't accept atomically -- mirrors <see cref="SendPasteChord"/>'s
    /// "log and report false" contract, never throws for this expected failure mode.
    /// </summary>
    private async Task<bool> SendUnicodeSynthAsync(string text, CancellationToken ct)
    {
        var batches = BuildUnicodeSynthBatches(text, UnicodeSynthBatchSize);
        for (int i = 0; i < batches.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var batch = batches[i];
            uint sent = InjectionNativeMethods.SendInput(
                (uint)batch.Length, batch, Marshal.SizeOf<InjectionNativeMethods.INPUT>());
            if (sent != batch.Length)
            {
                _logger.LogError(
                    "UnicodeSynth: SendInput failed to submit batch {BatchIndex}/{BatchCount} atomically: "
                    + "sent={Sent}/{Expected}, error={Error}",
                    i + 1, batches.Count, sent, batch.Length, Marshal.GetLastWin32Error());
                return false;
            }

            if (i < batches.Count - 1)
                await Task.Delay(UnicodeSynthBatchGap, ct).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>
    /// Pure batch-building logic for <see cref="SendUnicodeSynthAsync"/>, pulled out so it can
    /// be unit-tested without any real <c>SendInput</c> call (mirrors
    /// <see cref="InjectionOutcomeMapper"/>'s "pull the pure decision out of the native-call
    /// method" precedent). Each UTF-16 code unit becomes one down/up <c>INPUT</c> pair
    /// (<c>wVk=0</c>, <c>wScan=</c>the code unit, <c>KEYEVENTF_UNICODE</c> set on both, plus
    /// <c>KEYEVENTF_KEYUP</c> on the up event) -- per plan §1.8's "Other notes": this does NOT
    /// need a virtual-key code or scan code the way the paste chord's real key presses do,
    /// <c>KEYEVENTF_UNICODE</c> lets Windows synthesize an arbitrary UTF-16 code unit directly.
    /// Batched at <paramref name="batchSize"/> code units per returned array so a very long
    /// transcript doesn't flood the input queue in one giant <c>SendInput</c> call.
    /// </summary>
    internal static List<InjectionNativeMethods.INPUT[]> BuildUnicodeSynthBatches(string text, int batchSize)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (batchSize < 1)
            throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "batchSize must be at least 1.");

        var batches = new List<InjectionNativeMethods.INPUT[]>();
        for (int offset = 0; offset < text.Length; offset += batchSize)
        {
            int count = Math.Min(batchSize, text.Length - offset);
            var inputs = new InjectionNativeMethods.INPUT[count * 2]; // down + up per code unit
            for (int i = 0; i < count; i++)
            {
                char c = text[offset + i];
                inputs[i * 2] = BuildUnicodeKeyInput(c, keyUp: false);
                inputs[i * 2 + 1] = BuildUnicodeKeyInput(c, keyUp: true);
            }
            batches.Add(inputs);
        }
        return batches;
    }

    private static InjectionNativeMethods.INPUT BuildUnicodeKeyInput(char c, bool keyUp)
    {
        uint flags = InjectionNativeMethods.KEYEVENTF_UNICODE
            | (keyUp ? InjectionNativeMethods.KEYEVENTF_KEYUP : 0);
        return new InjectionNativeMethods.INPUT
        {
            type = InjectionNativeMethods.INPUT_KEYBOARD,
            U = new InjectionNativeMethods.InputUnion
            {
                ki = new InjectionNativeMethods.KEYBDINPUT
                {
                    wVk = 0,
                    wScan = c,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                }
            }
        };
    }

    /// <summary>
    /// Supported chords: "ctrl+v" (default) and "ctrl+shift+v". Post-review fix: the whole
    /// chord is assembled as ONE <c>INPUT[]</c> array and submitted via a SINGLE
    /// <c>SendInput</c> call. Win32's non-interleaving guarantee for <c>SendInput</c> only
    /// applies to events submitted together in one call -- splitting the chord across
    /// separate calls (as this used to do, one call per key event) reopens the interleaving
    /// risk the API exists to close: a real or synthetic keystroke could land between the
    /// chord's own key events.
    /// </summary>
    private bool SendPasteChord(string chord)
    {
        bool shift = chord?.Contains("shift", StringComparison.OrdinalIgnoreCase) == true;

        var chordKeys = shift
            ? new (int vk, bool keyUp)[]
            {
                (NativeMethods.VK_LCONTROL, false),
                (NativeMethods.VK_LSHIFT, false),
                (NativeMethods.VK_V, false),
                (NativeMethods.VK_V, true),
                (NativeMethods.VK_LSHIFT, true),
                (NativeMethods.VK_LCONTROL, true),
            }
            : new (int vk, bool keyUp)[]
            {
                (NativeMethods.VK_LCONTROL, false),
                (NativeMethods.VK_V, false),
                (NativeMethods.VK_V, true),
                (NativeMethods.VK_LCONTROL, true),
            };

        var inputs = new InjectionNativeMethods.INPUT[chordKeys.Length];
        for (int i = 0; i < chordKeys.Length; i++)
            inputs[i] = BuildKeyInput(chordKeys[i].vk, chordKeys[i].keyUp);

        uint sent = InjectionNativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<InjectionNativeMethods.INPUT>());
        if (sent != inputs.Length)
        {
            _logger.LogError(
                "SendInput failed to submit the full paste chord '{Chord}' atomically: sent={Sent}/{Expected}, error={Error}",
                chord, sent, inputs.Length, Marshal.GetLastWin32Error());
        }
        return sent == inputs.Length;
    }

    /// <summary>
    /// Sends a single synthetic key event (down or up) via one <c>SendInput</c> call.
    /// Used by <see cref="ModifierSanitizer"/> for step 6/9's individual modifier
    /// release/restore events -- genuinely separate in time from the paste chord itself
    /// (release happens before it, restore happens after it, per plan §1.8), so there is
    /// no atomicity requirement spanning all three phases the way there is for the
    /// chord's own four-to-six key events (see <see cref="SendPasteChord"/>'s doc
    /// comment) -- only the chord itself needs to stay a single non-interleavable
    /// <c>SendInput</c> call.
    /// </summary>
    internal static bool SendSingleKey(int vk, bool keyUp, ILogger logger)
    {
        var input = BuildKeyInput(vk, keyUp);
        uint sent = InjectionNativeMethods.SendInput(1, [input], Marshal.SizeOf<InjectionNativeMethods.INPUT>());
        if (sent != 1)
        {
            logger.LogError(
                "SendInput failed for modifier-sanitiser key vk=0x{Vk:X} keyUp={KeyUp}: sent={Sent}, error={Error}",
                vk, keyUp, sent, Marshal.GetLastWin32Error());
        }
        return sent == 1;
    }

    private static InjectionNativeMethods.INPUT BuildKeyInput(int vk, bool keyUp)
    {
        // Include the hardware scan code (via MapVirtualKey), not just the VK code: some
        // apps (observed by spikes/s4-inject-win: modern Windows 11 Notepad) rely on
        // raw-input / low-level-hook paths that expect a populated scan code and silently
        // ignore a SendInput event carrying wScan=0.
        ushort scan = (ushort)InjectionNativeMethods.MapVirtualKey((uint)vk, InjectionNativeMethods.MAPVK_VK_TO_VSC);
        return new InjectionNativeMethods.INPUT
        {
            type = InjectionNativeMethods.INPUT_KEYBOARD,
            U = new InjectionNativeMethods.InputUnion
            {
                ki = new InjectionNativeMethods.KEYBDINPUT
                {
                    wVk = (ushort)vk,
                    wScan = scan,
                    dwFlags = keyUp ? InjectionNativeMethods.KEYEVENTF_KEYUP : 0,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                }
            }
        };
    }
}
