using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Soneto.Composition;
using Soneto.Core;
using Soneto.Core.Abstractions;
using Soneto.Core.Asr;
using Soneto.Core.Audio;
using Soneto.Core.Configuration;

namespace Soneto.App.ViewModels;

/// <summary>
/// Phase 3 item 9 (§3.13) — the Permissions Doctor page ViewModel. Hand-rolled
/// <see cref="INotifyPropertyChanged"/>, same style as <see cref="HistoryViewModel"/>/
/// <see cref="DictionaryEditorViewModel"/>/<see cref="SettingsViewModel"/>.
///
/// <para>
/// <b>Every check runs a REAL test against real environment state</b> — this page exists
/// specifically to diagnose why the real dictation pipeline isn't starting, so it is
/// deliberately constructed independent of whether <see cref="PipelineHost"/>'s pipeline
/// ever comes up this session, mirroring items 6/7/8's own "eager, pipeline-independent
/// construction" architecture decision (<c>App.axaml.cs</c>'s own doc comments) — indeed
/// more so than any of those three, since a broken pipeline is exactly this page's reason
/// to exist.
/// </para>
///
/// <para>
/// <b>Checks run once this page is actually shown, not the instant this ViewModel is
/// constructed — a real bug, hit and fixed during this item's own live verification.</b>
/// <c>MainWindow</c> eagerly constructs all four nav pages up front (§3.8's own "built once
/// and cached" shell) so this ViewModel is constructed long before a user ever clicks the
/// "Permissions" nav item. An earlier version of this class ran every check immediately in
/// its own constructor; live verification caught this running the "can synthesize input"
/// self-test's <see cref="Avalonia.Input.InputElement.Focus()"/> call against a
/// <see cref="TextBox"/> that was not yet attached to any window (this page wasn't the
/// visible <c>NavContent</c> yet), making the call a silent no-op and the self-test
/// meaningless. Fixed by moving the first real run to <see cref="RunInitialChecksIfNeededAsync"/>,
/// called from <c>PermissionsDoctorView</c>'s own <c>Loaded</c> event (fires once this page
/// is genuinely part of a live window's visual tree) — see that method's own doc comment.
/// </para>
///
/// <para>
/// <b>Constraint 1 — never construct a second <see cref="IHotkeySource"/>.</b> SharpHook
/// (the library <c>WindowsHotkeySource</c> wraps) cannot run two concurrent hook instances
/// in one process on this machine (confirmed, reproduced twice — see
/// <c>Docs/PROJECT-MEMORY.md</c> item 6's writeup). The "global hook active" check therefore
/// NEVER builds its own <see cref="IHotkeySource"/>. Instead it subscribes to
/// <see cref="PipelineHost.Started"/> (the same one-shot event <c>RecordingHud</c>/the
/// History append hook already subscribe to in <c>App.axaml.cs</c>) to learn about the
/// REAL, shared <see cref="SessionController"/>/<see cref="IHotkeySource"/> pair if/when the
/// pipeline actually starts, and reports the check purely from that instance's own health
/// signals (<see cref="SessionController.State"/>/<see cref="SessionController.StateChanged"/>,
/// <see cref="IHotkeySource.Faulted"/>) — see <see cref="PermissionsDoctorLogic.EvaluateHookCheck"/>
/// for the exact, independently-testable decision logic. If <see cref="PipelineHost.Started"/>
/// never fires this session, the check honestly reports "not tested", not a false red — see
/// that method's own doc comment for why a not-yet-started pipeline is a distinct state from
/// a genuinely faulted one.
/// </para>
///
/// <para>
/// <b>Constraint 2 — the "can synthesize input" self-test never touches a window outside
/// <c>Soneto.App</c>'s own boundaries.</b> <c>PermissionsDoctorView.axaml</c> hosts a hidden
/// <see cref="TextBox"/> living inside this page's own visual tree — normally rendered
/// (not <c>Opacity="0"</c>, which was tried first and found to break native paste routing)
/// but moved off-screen via a large negative <c>Margin</c> so it is never visible to the
/// user while still being a genuine, focusable, paste-routable control; <see cref="RunInjectionSelfTestAsync"/> focuses it via ordinary in-app
/// <see cref="InputElement.Focus()"/> (never <c>GetForegroundWindow()</c>/anything global),
/// then immediately calls a FRESH, throwaway <see cref="ITextInjector"/>'s
/// <see cref="ITextInjector.CaptureTarget"/> — which legitimately captures whatever has OS
/// focus right now, but since the line just above gave OUR OWN control real OS focus via
/// ordinary Avalonia means, the captured target IS this process's own window, never an
/// arbitrary foreground app — then calls <see cref="ITextInjector.InjectAsync"/> with a short
/// marker string and verifies the TextBox's own <see cref="TextBox.Text"/> actually updated
/// to contain it. This never repeats this project's documented live-desktop near-miss
/// pattern (<c>Docs/PROJECT-MEMORY.md</c>'s "live-desktop testing caution" entries) because
/// the target is always something Soneto.App itself just focused, never "whatever happens to
/// have OS focus right now" independent of this process's own actions. The whole method is
/// deliberately never wrapped in <see cref="Task.Run"/> — every step between the
/// <c>Focus()</c> call and the paste chord actually being sent must stay on the UI thread (no
/// yielding to a background thread that could let real user focus drift away from this
/// window mid-test), which falls out naturally here since this method is only ever invoked
/// from a UI-thread button click and every <c>await</c> below resumes on the same captured
/// UI <see cref="Dispatcher"/> context.
/// </para>
///
/// <para>
/// A fresh <see cref="ITextInjector"/> instance is constructed for this one test rather than
/// reusing the real pipeline's own injector, per this item's own design note: unlike
/// <see cref="IHotkeySource"/>, <c>WindowsTextInjector</c>/<c>LinuxTextInjector</c> perform
/// one-shot clipboard+paste operations on demand — no persistent hook, no shared native
/// resource — so a second, independent instance is always safe and can never collide with
/// the real pipeline's own injector.
/// </para>
///
/// <para>
/// <b>Clipboard mechanism chosen: <c>Soneto.Platform.Windows.ClipboardManager</c> on
/// Windows</b>, the exact same raw Win32 mechanism the real <c>WindowsTextInjector</c> uses —
/// more accurate than testing via Avalonia's own higher-level <c>TopLevel.Clipboard</c>
/// abstraction, which the real injector never touches at all. On non-Windows, this falls
/// back to Avalonia's cross-platform <c>IClipboard</c> (supplied lazily by the view, since
/// this ViewModel has no <see cref="Avalonia.Controls.TopLevel"/> reference of its own) —
/// re-deriving <c>Soneto.Platform.Linux</c>'s own wl-copy/xclip backend-selection logic here
/// would risk drifting from <c>LinuxTextInjector</c>'s real implementation for no benefit a
/// lightweight round-trip check actually needs.
/// </para>
///
/// <para>
/// <b>Mic check — gracefully inconclusive, not a false red, when the device is genuinely
/// busy.</b> If the real, shared <see cref="SessionController"/> (learned the same way as the
/// hook check, above) is currently <see cref="SessionState.Recording"/>, this check reports
/// <see cref="CheckStatus.NotTested"/> rather than attempting to open a second capture stream
/// on the same device and reporting whatever native error that collision produces as if it
/// were a real "microphone access is broken" finding.
/// </para>
///
/// <para>
/// <b>A second real bug, hit and fixed during this item's own live verification: the
/// injection self-test's own clipboard-restore delay.</b> The self-test's target lives in
/// THIS SAME process/UI thread (unlike a real dictation's separate target application), so an
/// initial attempt using <see cref="TimeSpan.Zero"/> for both
/// <see cref="InjectionOptions.PreDelay"/> and <see cref="InjectionOptions.ClipboardRestoreDelay"/>
/// (trying to make the test as fast as possible) created a genuine race: the restore step won
/// every time, overwriting the clipboard back to its original content before Avalonia's own
/// internal (asynchronous) Ctrl+V paste handler had a turn on the UI thread's dispatcher queue
/// to actually read the marker off the clipboard — a real run reproduced this exactly
/// (<c>outcome=Injected</c>, the paste chord itself was accepted, but
/// <c>markerLanded=false</c>, the text never arrived). Fixed by using the same real default
/// delays production dictation uses (<c>InjectionConfig</c>'s own
/// <c>PreDelayMs=20</c>/<c>ClipboardRestoreDelayMs=150</c> defaults) plus a short additional
/// wait before reading <see cref="TextBox.Text"/> back — see
/// <see cref="RunInjectionSelfTestAsync"/>'s own inline comment for the full trace. Re-verified
/// green after the fix, and confirmed to still correctly report red when the model directory
/// was deliberately pointed at a nonexistent path (that check's own independent failure mode —
/// see <c>Docs/soneto-implementation-plan-phase3.md</c> §3.16 item 9's row for the full
/// deliberately-broken-check verification).
/// </para>
/// </summary>
public sealed class PermissionsDoctorViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IConfigService _configService;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Action<Action> _postToUiThread;

    private readonly EventHandler<PipelineStartedEventArgs> _onPipelineStarted;
    private readonly EventHandler<PipelineFailedEventArgs> _onPipelineFailed;

    private SessionController? _controller;
    private IHotkeySource? _hotkeySource;
    private string? _pipelineFailureReason;
    private string? _hookFaultDetail;

    private TextBox? _selfTestTextBox;
    private Func<Avalonia.Input.Platform.IClipboard?>? _nonWindowsClipboardAccessor;

    private readonly List<(PermissionCheckViewModel Vm, Func<Task> Run)> _checks = new();
    private PermissionCheckViewModel? _hookCheckVm;

    public ObservableCollection<PermissionCheckViewModel> Checks { get; } = new();

    private bool _isRecheckingAll;
    public bool IsRecheckingAll
    {
        get => _isRecheckingAll;
        private set { _isRecheckingAll = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public PermissionsDoctorViewModel(IConfigService configService, ILoggerFactory loggerFactory)
        : this(configService, loggerFactory, uiThreadPost: null)
    {
    }

    /// <summary>Test-facing constructor (internal, mirroring every other ViewModel in this
    /// project) — <paramref name="uiThreadPost"/> replaces <see cref="Dispatcher.UIThread"/>
    /// marshaling with a synchronous stand-in.</summary>
    internal PermissionsDoctorViewModel(IConfigService configService, ILoggerFactory loggerFactory, Action<Action>? uiThreadPost)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _postToUiThread = uiThreadPost ?? (action => Dispatcher.UIThread.Post(action));

        AddCheck("Mic access", RunMicCheckAsync);
        AddCheck("Global hook active", vm => { RunHookCheck(vm); return Task.CompletedTask; });
        _hookCheckVm = Checks[^1];
        AddCheck("Can synthesize input", RunInjectionSelfTestAsync);
        AddCheck("Clipboard read/write", RunClipboardCheckAsync);
        AddCheck("Model files present & hashed", RunModelFilesCheckAsync);

        // §3.1/§3.13's own explicit scope note: Linux checks are best-effort only, no
        // remediation UX this item — and only shown at all on Linux, since they are
        // meaningless (and unverifiable) elsewhere.
        if (OperatingSystem.IsLinux())
        {
            AddCheck("/dev/uinput writable (Linux, best-effort)", vm => { RunLinuxUinputCheck(vm); return Task.CompletedTask; });
            AddCheck("ydotoold running (Linux, best-effort)", vm => { RunLinuxYdotooldCheck(vm); return Task.CompletedTask; });
        }

        // Constraint 1: subscribe to the REAL, shared pipeline's events — never construct a
        // second IHotkeySource. Handlers are stored so Dispose can unsubscribe cleanly.
        _onPipelineStarted = OnPipelineStarted;
        _onPipelineFailed = OnPipelineFailed;
        PipelineHost.Started += _onPipelineStarted;
        PipelineHost.Failed += _onPipelineFailed;

        // Late-subscription safety net (see PipelineHost.LastStarted/LastFailureReason's own
        // doc comment) — not currently reachable given this session's construction ordering
        // (this ViewModel is always constructed before PipelineHost.StartInBackground is ever
        // called, per App.axaml.cs), but cheap, correct insurance against a future change.
        if (PipelineHost.LastStarted is { } started)
            OnPipelineStarted(null, started);
        else if (PipelineHost.LastFailureReason is { } reason)
            OnPipelineFailed(null, new PipelineFailedEventArgs(reason));

        // Deliberately NOT run here. This ViewModel is constructed eagerly, up front,
        // inside MainWindow's _pages array — BEFORE this page is ever actually attached to
        // the visual tree / made the visible NavContent (a real bug, hit and fixed during
        // this item's own live verification: MainWindow eagerly constructs all four nav
        // pages, so an auto-run here would fire while this page's hidden self-test TextBox
        // is not attached to any window, making Focus() a silent no-op — the injection
        // self-test would then run against whatever control genuinely has focus in the
        // CURRENTLY VISIBLE page instead, which stays inside Soneto.App's own window
        // (constraint 2 still technically holds) but is not the control-under-test and
        // would misreport a real capability as red). The first real run is triggered by
        // PermissionsDoctorView once this page is actually attached (its Loaded event) via
        // RunInitialChecksIfNeededAsync below — see that method's own doc comment.
    }

    private bool _initialChecksStarted;

    /// <summary>
    /// Runs every check exactly once, the first time this page is genuinely attached to a
    /// live window (called from <c>PermissionsDoctorView</c>'s <c>Loaded</c> handler, which
    /// can fire more than once across nav-tab switches — this method itself is idempotent,
    /// so re-visiting the Permissions tab later does not silently re-trigger a real
    /// microphone open/synthetic paste/clipboard round trip the user didn't ask for; the
    /// explicit "Recheck all" button remains the way to intentionally re-run everything).
    /// </summary>
    public Task RunInitialChecksIfNeededAsync()
    {
        if (_initialChecksStarted)
            return Task.CompletedTask;
        _initialChecksStarted = true;
        return RecheckAllAsync();
    }

    private void AddCheck(string name, Func<PermissionCheckViewModel, Task> run)
    {
        var vm = new PermissionCheckViewModel(name);
        Checks.Add(vm);
        _checks.Add((vm, () => run(vm)));
    }

    /// <summary>
    /// Wired by <c>PermissionsDoctorView</c>'s constructor once its hidden self-test
    /// <see cref="TextBox"/> exists — see this class's own doc comment (constraint 2) for
    /// exactly how it's used and why it's safe.
    /// </summary>
    public void AttachSelfTestTextBox(TextBox textBox) => _selfTestTextBox = textBox;

    /// <summary>
    /// Wired by <c>PermissionsDoctorView</c>'s constructor: lazily resolves the real
    /// <see cref="Avalonia.Input.Platform.IClipboard"/> from this page's own
    /// <see cref="Avalonia.Controls.TopLevel"/> — only used on non-Windows platforms, see
    /// this class's own doc comment for why Windows uses
    /// <c>Soneto.Platform.Windows.ClipboardManager</c> directly instead.
    /// </summary>
    public void AttachNonWindowsClipboardAccessor(Func<Avalonia.Input.Platform.IClipboard?> accessor) =>
        _nonWindowsClipboardAccessor = accessor;

    /// <summary>"Recheck all" — re-runs every check in order, sequentially (not in parallel,
    /// so two checks never contend for the same resource, e.g. the clipboard).</summary>
    public async Task RecheckAllAsync()
    {
        IsRecheckingAll = true;
        try
        {
            foreach (var (_, run) in _checks)
                await run();
        }
        finally
        {
            IsRecheckingAll = false;
        }
    }

    /// <summary>Re-runs a single check on demand (§3.16's own "done when" bar requires being
    /// able to re-run a check without restarting the app).</summary>
    public async Task RecheckAsync(PermissionCheckViewModel check)
    {
        var entry = _checks.FirstOrDefault(c => ReferenceEquals(c.Vm, check));
        if (entry.Run is not null)
            await entry.Run();
    }

    // ── Mic access ──────────────────────────────────────────────────────────────────────

    private async Task RunMicCheckAsync(PermissionCheckViewModel check)
    {
        check.Status = CheckStatus.Pending;
        check.Detail = "Testing…";

        if (_controller?.State == SessionState.Recording)
        {
            check.Status = CheckStatus.NotTested;
            check.Detail = "Not tested — a real dictation is currently recording; the microphone is "
                + "legitimately busy. Recheck after it finishes.";
            return;
        }

        try
        {
            await Task.Run(async () =>
            {
                var capture = new PortAudioCapture(_loggerFactory.CreateLogger<PortAudioCapture>());
                try
                {
                    await capture.StartAsync(device: null, CancellationToken.None);
                    await capture.StopAsync();
                }
                finally
                {
                    await capture.DisposeAsync();
                }
            });

            check.Status = CheckStatus.Green;
            check.Detail = "Successfully opened and closed a short capture stream on the default input device.";
        }
        catch (Exception ex)
        {
            check.Status = CheckStatus.Red;
            check.Detail = $"Failed to open the microphone: {ex.Message}";
        }
    }

    // ── Global hook active ──────────────────────────────────────────────────────────────

    private void RunHookCheck(PermissionCheckViewModel check)
    {
        var (status, detail) = PermissionsDoctorLogic.EvaluateHookCheck(
            pipelineEverStarted: _controller is not null,
            controllerCurrentlyFaulted: _controller?.State == SessionState.Faulted,
            pipelineFailureReason: _pipelineFailureReason,
            hookFaultDetail: _hookFaultDetail);
        check.Status = status;
        check.Detail = detail;
    }

    private void OnPipelineStarted(object? sender, PipelineStartedEventArgs e)
    {
        _controller = e.Controller;
        _hotkeySource = e.HotkeySource;
        e.Controller.StateChanged += OnControllerStateChanged;
        e.HotkeySource.Faulted += OnHotkeySourceFaulted;

        _postToUiThread(() => { if (_hookCheckVm is not null) RunHookCheck(_hookCheckVm); });
    }

    private void OnPipelineFailed(object? sender, PipelineFailedEventArgs e)
    {
        _pipelineFailureReason = e.Reason;
        _postToUiThread(() => { if (_hookCheckVm is not null) RunHookCheck(_hookCheckVm); });
    }

    private void OnControllerStateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        _postToUiThread(() => { if (_hookCheckVm is not null) RunHookCheck(_hookCheckVm); });
    }

    private void OnHotkeySourceFaulted(object? sender, HotkeyFaultEventArgs e)
    {
        _hookFaultDetail = e.Exception is null ? e.Reason : $"{e.Reason} ({e.Exception.Message})";
        _postToUiThread(() => { if (_hookCheckVm is not null) RunHookCheck(_hookCheckVm); });
    }

    // ── Can synthesize input ────────────────────────────────────────────────────────────

    private async Task RunInjectionSelfTestAsync(PermissionCheckViewModel check)
    {
        check.Status = CheckStatus.Pending;
        check.Detail = "Testing…";

        var textBox = _selfTestTextBox;
        if (textBox is null)
        {
            check.Status = CheckStatus.Red;
            check.Detail = "Internal error: the view did not wire up the hidden self-test control.";
            return;
        }

        string marker = $"soneto-doctor-{Guid.NewGuid():N}"[..20];
        try
        {
            textBox.Text = string.Empty;
            textBox.Focus();

            ITextInjector injector = CreateThrowawayTextInjector();

            // Legitimately captures whatever has OS focus right now — safe here specifically
            // because Focus() just gave OUR OWN control real OS focus via ordinary in-app
            // means (see this class's own doc comment, constraint 2).
            object? target = injector.CaptureTarget();

            // Real bug, hit and fixed during this item's own live verification: TimeSpan.Zero
            // for both delays (the original attempt, trying to make this test as fast as
            // possible) created a genuine race specific to an IN-PROCESS paste target. The
            // real paste chord and this test's own clipboard-restore step both run inside
            // Soneto.App's own single UI-thread message loop as our TARGET is a control in
            // THIS SAME process/thread (unlike a real dictation's separate target process) --
            // with ClipboardRestoreDelay=0, the restore step won the race and overwrote the
            // clipboard back to its original content before Avalonia's own internal Ctrl+V
            // paste handler (itself asynchronous) had a turn on the UI thread to actually read
            // the marker off the clipboard, so nothing ever landed even though the paste chord
            // itself was accepted (confirmed via outcome=Injected, markerLanded=False in a
            // real run). Fixed by using the same real default delays production dictation uses
            // (PreDelayMs=20/ClipboardRestoreDelayMs=150 -- see InjectionConfig's own
            // defaults), which give the in-process target enough time to actually process the
            // paste before the clipboard is restored.
            var outcome = await injector.InjectAsync(
                marker, target,
                new InjectionOptions(
                    Method: Soneto.Core.Abstractions.InjectionMethod.ClipboardPaste,
                    PasteChord: "ctrl+v",
                    PreDelay: TimeSpan.FromMilliseconds(20),
                    ClipboardRestoreDelay: TimeSpan.FromMilliseconds(150),
                    RestoreClipboard: true,
                    SanitizeModifiers: true,
                    TriggerKey: null,
                    Policy: Soneto.Core.Abstractions.ClipboardPolicy.TextOnly),
                CancellationToken.None);

            // Give Avalonia's own internal (asynchronous) Ctrl+V paste handling a real chance
            // to complete and update textBox.Text before this method reads it back -- the
            // paste chord being ACCEPTED by SendInput (reflected in `outcome`) does not
            // guarantee the target control has already finished processing it by the instant
            // InjectAsync returns.
            await Task.Delay(TimeSpan.FromMilliseconds(100));

            string finalText = textBox.Text ?? string.Empty;
            bool landed = finalText.Contains(marker, StringComparison.Ordinal);

            if (outcome == InjectionOutcome.Injected && landed)
            {
                check.Status = CheckStatus.Green;
                check.Detail = "A synthetic paste into a hidden control inside Soneto.App's own window "
                    + "round-tripped successfully.";
            }
            else
            {
                check.Status = CheckStatus.Red;
                check.Detail = $"Synthetic input did not land as expected (outcome={outcome}, "
                    + $"markerLanded={landed}).";
            }
        }
        catch (Exception ex)
        {
            check.Status = CheckStatus.Red;
            check.Detail = $"Injection self-test threw an exception: {ex.Message}";
        }
        finally
        {
            textBox.Text = string.Empty;
        }
    }

    private ITextInjector CreateThrowawayTextInjector()
    {
#if WINDOWS
        return new Soneto.Platform.Windows.WindowsTextInjector(
            _loggerFactory.CreateLogger<Soneto.Platform.Windows.WindowsTextInjector>());
#else
        return new Soneto.Platform.Linux.LinuxTextInjector(
            _loggerFactory.CreateLogger<Soneto.Platform.Linux.LinuxTextInjector>());
#endif
    }

    // ── Clipboard read/write ────────────────────────────────────────────────────────────

    private async Task RunClipboardCheckAsync(PermissionCheckViewModel check)
    {
        check.Status = CheckStatus.Pending;
        check.Detail = "Testing…";

        string marker = $"soneto-doctor-clip-{Guid.NewGuid():N}";

        try
        {
#if WINDOWS
            var (status, detail) = await Task.Run(() => RunClipboardCheckWindows(marker));
            check.Status = status;
            check.Detail = detail;
#else
            var clipboard = _nonWindowsClipboardAccessor?.Invoke();
            if (clipboard is null)
            {
                check.Status = CheckStatus.NotTested;
                check.Detail = "Not tested — no clipboard is available from this view yet (the window may "
                    + "not be attached to a TopLevel).";
                return;
            }

            string? original = await clipboard.TryGetTextAsync();
            try
            {
                await clipboard.SetTextAsync(marker);
                string? readBack = await clipboard.TryGetTextAsync();
                if (readBack == marker)
                {
                    check.Status = CheckStatus.Green;
                    check.Detail = "Successfully wrote and read back a marker value via Avalonia's "
                        + "cross-platform clipboard.";
                }
                else
                {
                    check.Status = CheckStatus.Red;
                    check.Detail = $"Clipboard round-trip mismatch: wrote \"{marker}\", read back "
                        + $"\"{readBack}\".";
                }
            }
            finally
            {
                if (original is not null)
                    await clipboard.SetTextAsync(original);
            }
#endif
        }
        catch (Exception ex)
        {
            check.Status = CheckStatus.Red;
            check.Detail = $"Clipboard round-trip failed: {ex.Message}";
        }
    }

#if WINDOWS
    private static (CheckStatus Status, string Detail) RunClipboardCheckWindows(string marker)
    {
        var backup = Soneto.Platform.Windows.ClipboardManager.Save();
        try
        {
            bool set = Soneto.Platform.Windows.ClipboardManager.SetUnicodeTextWithRetry(
                marker, attempts: 3, delay: TimeSpan.FromMilliseconds(20));
            if (!set)
                return (CheckStatus.Red, "Failed to write a marker value to the clipboard after retries.");

            int seqAfterOurSet = Soneto.Platform.Windows.ClipboardManager.GetSequenceNumber();

            var readBack = Soneto.Platform.Windows.ClipboardManager.Save();
            if (readBack.UnicodeText != marker)
                return (CheckStatus.Red,
                    $"Clipboard round-trip mismatch: wrote \"{marker}\", read back \"{readBack.UnicodeText}\".");

            if (backup.HadUnicodeText)
            {
                Soneto.Platform.Windows.ClipboardManager.RestoreUnicodeTextWithSequenceGuard(
                    backup.UnicodeText!, seqAfterOurSet, attempts: 3, delay: TimeSpan.FromMilliseconds(20));
            }

            return (CheckStatus.Green,
                "Successfully wrote and read back a marker value via the real clipboard mechanism "
                + "(Soneto.Platform.Windows.ClipboardManager — the same one the real text injector uses).");
        }
        catch (Exception ex)
        {
            return (CheckStatus.Red, $"Clipboard round-trip failed: {ex.Message}");
        }
    }
#endif

    // ── Model files present & hashed ────────────────────────────────────────────────────

    private async Task RunModelFilesCheckAsync(PermissionCheckViewModel check)
    {
        check.Status = CheckStatus.Pending;
        check.Detail = "Checking…";

        string? configOverride = _configService.Current.Asr.ModelDir;
        string standardDir = Path.Combine(ModelManager.ResolveStandardModelsBaseDir(), ModelManager.ModelFolderName);

        var resolution = PermissionsDoctorLogic.ResolveModelDirForCheck(
            configOverride,
            ModelManager.AreRequiredFilesPresent,
            ModelManager.MissingFiles,
            DaemonComposition.FindDevModelDirWalkingUp,
            standardDir);

        if (resolution.Outcome == PermissionsDoctorLogic.ModelResolutionOutcome.ConfigOverrideMissingFiles)
        {
            check.Status = CheckStatus.Red;
            check.Detail = $"Configured asr.modelDir '{resolution.Dir}' is missing required file(s): "
                + $"{string.Join(", ", resolution.MissingFiles!)}.";
            return;
        }

        if (resolution.Outcome == PermissionsDoctorLogic.ModelResolutionOutcome.NotFoundAnywhere)
        {
            check.Status = CheckStatus.Red;
            check.Detail = "No model found at a configured override, the repo-local dev models/ dir, or "
                + $"the standard location ({standardDir}). The real pipeline would attempt a fresh "
                + "download on its next start.";
            return;
        }

        string dir = resolution.Dir!;
        try
        {
            var hashes = await Task.Run(async () =>
            {
                var list = new List<string>();
                foreach (var file in ModelManager.RequiredFiles)
                {
                    string hash = await ModelManager.ComputeSha256Async(Path.Combine(dir, file), CancellationToken.None);
                    list.Add($"{file}={hash[..12]}…");
                }
                return list;
            });

            check.Status = CheckStatus.Green;
            check.Detail = $"All required model files present and hashed at {dir}: {string.Join(", ", hashes)}.";
        }
        catch (Exception ex)
        {
            check.Status = CheckStatus.Red;
            check.Detail = $"Model files were found at {dir} but failed to hash: {ex.Message}";
        }
    }

    // ── Linux best-effort checks (§3.1/§3.13 — no remediation UX this item) ────────────

    private static void RunLinuxUinputCheck(PermissionCheckViewModel check)
    {
        const string path = "/dev/uinput";
        if (!File.Exists(path))
        {
            check.Status = CheckStatus.Red;
            check.Detail = $"{path} does not exist on this system.";
            return;
        }

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Write);
            check.Status = CheckStatus.Green;
            check.Detail = $"{path} exists and is writable by this process.";
        }
        catch (Exception ex)
        {
            check.Status = CheckStatus.Red;
            check.Detail = $"{path} exists but is not writable: {ex.Message}";
        }
    }

    private static void RunLinuxYdotooldCheck(PermissionCheckViewModel check)
    {
        // Mirrors Soneto.Platform.Linux.LinuxTextInjector's own best-effort probe exactly
        // (env var, then the tool's documented default socket path) — existence check only,
        // not a live connection attempt, same reasoning as that class's own doc comment.
        string socketPath = Environment.GetEnvironmentVariable("YDOTOOL_SOCKET") ?? "/tmp/.ydotool_socket";
        if (File.Exists(socketPath))
        {
            check.Status = CheckStatus.Green;
            check.Detail = $"ydotoold socket found at {socketPath} (existence check only, not a live connection attempt).";
        }
        else
        {
            check.Status = CheckStatus.Red;
            check.Detail = $"ydotoold socket not found at {socketPath}. Run scripts/setup-linux.sh to "
                + "install/enable the ydotoold user service.";
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>Unsubscribes from every event this ViewModel attached to the real, shared
    /// pipeline instances and from the static <see cref="PipelineHost"/> events — does not
    /// own the lifetime of any of them.</summary>
    public void Dispose()
    {
        PipelineHost.Started -= _onPipelineStarted;
        PipelineHost.Failed -= _onPipelineFailed;

        if (_controller is not null)
            _controller.StateChanged -= OnControllerStateChanged;
        if (_hotkeySource is not null)
            _hotkeySource.Faulted -= OnHotkeySourceFaulted;
    }
}
