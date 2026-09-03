using Microsoft.Extensions.Logging;
using Soneto.Core.Abstractions;

namespace Soneto.Platform.Linux;

/// <summary>
/// Linux implementation of <see cref="ITextInjector"/>: clipboard (<c>wl-copy</c>/<c>xclip</c>,
/// backend chosen per <see cref="ClipboardBackendSelector"/>) + synthetic paste via
/// <c>ydotool</c>, per plan §1.9's algorithm ("same algorithm [as Windows], minus the
/// clipboard sequence number ... use a content hash comparison instead ... <c>ydotool key
/// 29:1 47:1 47:0 29:0</c> for the paste chord").
///
/// <para>
/// <b>No cross-compositor "foreground window" concept.</b> Unlike Windows'
/// <c>GetForegroundWindow()</c>, there is no portable, security-model-respecting way under
/// Wayland to identify or target "the currently focused window" from an unprivileged
/// process. <see cref="CaptureTarget"/> therefore always returns <c>null</c> -- injection
/// always goes to whatever the compositor currently has focused, matching the plan's
/// <c>targetLostPolicy: "current"</c> default philosophy already established on Windows
/// (see <c>Soneto.Platform.Windows.WindowsTextInjector.InjectAsync</c>'s doc comment for
/// that policy's reasoning). No elaborate Wayland-specific window tracking is attempted --
/// deliberately out of scope per this item's spec.
/// </para>
///
/// <para>
/// <b>ydotoold probing.</b> Per plan §1.12's error-handling matrix ("ydotoold not running
/// (Linux) | startup probe | log actionable message pointing at setup-linux.sh"), the first
/// injection attempt checks for the <c>ydotoold</c> Unix domain socket (path from the
/// <c>YDOTOOL_SOCKET</c> env var, falling back to the tool's documented default
/// <c>/tmp/.ydotool_socket</c>) and logs a clear, actionable message if it's missing, rather
/// than only surfacing a cryptic process-launch/exit-code failure later. This is a
/// best-effort existence check, not a real connection attempt (deliberately: sending even a
/// harmless real key event as a "probe," the way the Windows heartbeat does, is not done
/// here since it would be indistinguishable from real synthetic input on the user's actual
/// desktop and this item has no hardware to confirm what's actually harmless).
/// </para>
///
/// <para>
/// <b>Cannot be exercised from this session.</b> No <c>ydotool</c>/<c>ydotoold</c> binaries
/// or Wayland/X11 session exist on this Windows dev machine. What IS verified (by unit
/// test): the hash-guard restore-skip decision (<see cref="ClipboardHashGuard"/>), the
/// non-text MIME classification (<see cref="LinuxClipboardManager.ClassifyMimeTypes"/>),
/// and the session-type backend selection (<see cref="ClipboardBackendSelector"/>).
/// </para>
/// </summary>
public sealed class LinuxTextInjector : ITextInjector
{
    private readonly ILogger _logger;
    private readonly IProcessRunner _processRunner;
    private readonly ClipboardBackendKind _backend;
    private readonly LinuxClipboardManager _clipboard;
    private static readonly TimeSpan PasteChordSettleDelay = TimeSpan.FromMilliseconds(20);

    private int _ydotooldProbeLogged;

    public LinuxTextInjector(ILogger<LinuxTextInjector> logger)
        : this(logger, new RealProcessRunner(), ClipboardBackendSelector.Select(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")))
    {
    }

    /// <summary>Test/composition-root seam: lets a caller inject a fake process runner and
    /// force a specific backend, without touching the env-var/real-process path.</summary>
    public LinuxTextInjector(ILogger logger, IProcessRunner processRunner, ClipboardBackendKind backend)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _backend = backend;
        _clipboard = new LinuxClipboardManager(_logger, _processRunner, _backend);

        _logger.LogInformation(
            "LinuxTextInjector: XDG_SESSION_TYPE-derived clipboard backend = {Backend}", _backend);
    }

    /// <summary>No portable Wayland/X11-agnostic focused-window handle exists -- see class
    /// doc comment. Always null; injection always targets whatever currently has focus.</summary>
    public object? CaptureTarget() => null;

    /// <summary>
    /// Phase 4 item 3 (§4.4): same structural gap as <see cref="CaptureTarget"/> -- there is no
    /// portable, unprivileged way to resolve a focused window's owning process under Wayland,
    /// so this always returns <c>null</c> (never throws), same as the interface's own default
    /// implementation. Overridden here explicitly, not left to fall through to
    /// <see cref="ITextInjector"/>'s default, purely so this honest gap is documented at the
    /// same call site <see cref="CaptureTarget"/>'s own gap already is, rather than requiring a
    /// reader to go check the interface to learn Linux has no real implementation either.
    /// </summary>
    public string? TryResolveProcessExecutableName(object? target) => null;

    public async Task<InjectionOutcome> InjectAsync(string text, object? target, InjectionOptions opts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(opts);

        ProbeYdotooldOnce();

        if (opts.Method == InjectionMethod.UnicodeSynth)
        {
            _logger.LogInformation("Injection: using UnicodeSynth (configured method) via `ydotool type` -- no clipboard touched.");
            bool typeOk = await RunYdotoolTypeAsync(text, ct).ConfigureAwait(false);
            if (!typeOk)
            {
                _logger.LogError("Injection failed: `ydotool type` did not succeed.");
                return InjectionOutcome.SynthFailed;
            }
            return InjectionOutcome.Injected;
        }

        var backup = opts.RestoreClipboard
            ? await _clipboard.SaveAsync(ct).ConfigureAwait(false)
            : new LinuxClipboardTextBackup();

        bool clipboardSet = await _clipboard.SetTextAsync(text, ct).ConfigureAwait(false);
        if (!clipboardSet)
        {
            _logger.LogError("Injection: clipboard set failed; falling back to `ydotool type` for this injection.");
            bool synthFallbackOk = await RunYdotoolTypeAsync(text, ct).ConfigureAwait(false);
            if (synthFallbackOk)
            {
                _logger.LogInformation("Injected {Chars} chars via the `ydotool type` fallback (clipboard set had failed).", text.Length);
                return InjectionOutcome.Injected;
            }
            _logger.LogError("Injection failed: the `ydotool type` fallback also failed after the clipboard set failure.");
            return InjectionOutcome.ClipboardFailed;
        }

        byte[] hashAfterOurSet = ClipboardHashGuard.ComputeHash(text);

        bool clipboardRestoreAttempted = false;
        try
        {
            if (opts.PreDelay > TimeSpan.Zero)
                await Task.Delay(opts.PreDelay, ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            bool synthSent = await SendPasteChordAsync(opts.PasteChord, ct).ConfigureAwait(false);
            if (!synthSent)
            {
                _logger.LogError("Injection failed: `ydotool key` did not accept the paste chord '{Chord}'.", opts.PasteChord);
                return InjectionOutcome.SynthFailed;
            }

            if (opts.RestoreClipboard && backup.HadText)
            {
                if (opts.ClipboardRestoreDelay > TimeSpan.Zero)
                    await Task.Delay(opts.ClipboardRestoreDelay, ct).ConfigureAwait(false);

                clipboardRestoreAttempted = true;
                await TryRestoreAsync(backup, hashAfterOurSet, opts.Policy, ct).ConfigureAwait(false);
            }

            return InjectionOutcome.Injected;
        }
        finally
        {
            if (!clipboardRestoreAttempted && opts.RestoreClipboard && backup.HadText && ct.IsCancellationRequested)
            {
                _logger.LogWarning("Injection cancelled mid-flight; attempting a best-effort clipboard restore before unwinding.");
                await TryRestoreAsync(backup, hashAfterOurSet, opts.Policy, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task TryRestoreAsync(LinuxClipboardTextBackup backup, byte[] expectedHash, ClipboardPolicy policy, CancellationToken ct)
    {
        bool declineNonText = policy is ClipboardPolicy.TextOnly or ClipboardPolicy.BestEffort;
        var outcome = await _clipboard.RestoreWithHashGuardAsync(backup, expectedHash, declineNonText, ct).ConfigureAwait(false);
        switch (outcome)
        {
            case ClipboardRestoreOutcome.Restored:
                _logger.LogDebug("clipboard: restore succeeded (hash verified unchanged).");
                break;
            case ClipboardRestoreOutcome.SkippedSequenceChanged:
                _logger.LogInformation("Skipped clipboard restore: content hash changed during the restore window (user copied during window).");
                break;
            case ClipboardRestoreOutcome.SkippedNonText:
                break; // already logged by LinuxClipboardManager
            case ClipboardRestoreOutcome.Failed:
                _logger.LogWarning("Failed to restore the original clipboard content (not a hash mismatch -- see prior log entries).");
                break;
        }
    }

    /// <summary>Sends the paste chord via `ydotool key`, using evdev scancodes exactly as
    /// plan §1.9 specifies (`29:1 47:1 47:0 29:0` = KEY_LEFTCTRL down, KEY_V down, KEY_V up,
    /// KEY_LEFTCTRL up). "ctrl+shift+v" additionally wraps KEY_LEFTSHIFT (42) around KEY_V.</summary>
    private async Task<bool> SendPasteChordAsync(string chord, CancellationToken ct)
    {
        bool shift = chord?.Contains("shift", StringComparison.OrdinalIgnoreCase) == true;
        string seq = shift
            ? $"{EvdevKeyCodes.KEY_LEFTCTRL}:1 {EvdevKeyCodes.KEY_LEFTSHIFT}:1 {EvdevKeyCodes.KEY_V}:1 {EvdevKeyCodes.KEY_V}:0 {EvdevKeyCodes.KEY_LEFTSHIFT}:0 {EvdevKeyCodes.KEY_LEFTCTRL}:0"
            : $"{EvdevKeyCodes.KEY_LEFTCTRL}:1 {EvdevKeyCodes.KEY_V}:1 {EvdevKeyCodes.KEY_V}:0 {EvdevKeyCodes.KEY_LEFTCTRL}:0";

        var args = new List<string> { "key" };
        args.AddRange(seq.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        try
        {
            var result = await _processRunner.RunAsync("ydotool", args, null, ct).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                _logger.LogError("`ydotool key` failed (exit={Exit}): {Stderr}", result.ExitCode, result.Stderr);
                return false;
            }
            await Task.Delay(PasteChordSettleDelay, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "`ydotool key` threw");
            return false;
        }
    }

    private async Task<bool> RunYdotoolTypeAsync(string text, CancellationToken ct)
    {
        try
        {
            var result = await _processRunner.RunAsync("ydotool", new[] { "type", "--", text }, null, ct).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                _logger.LogError("`ydotool type` failed (exit={Exit}): {Stderr}", result.ExitCode, result.Stderr);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "`ydotool type` threw");
            return false;
        }
    }

    private void ProbeYdotooldOnce()
    {
        if (Interlocked.CompareExchange(ref _ydotooldProbeLogged, 1, 0) != 0)
            return;

        string socketPath = Environment.GetEnvironmentVariable("YDOTOOL_SOCKET") ?? "/tmp/.ydotool_socket";
        if (!File.Exists(socketPath))
        {
            _logger.LogWarning(
                "ydotoold socket not found at {SocketPath} -- ydotool paste/type commands will likely fail. "
                + "Run scripts/setup-linux.sh to install and enable the ydotoold user systemd service, then "
                + "re-login if group membership just changed.",
                socketPath);
        }
    }
}
