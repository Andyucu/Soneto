using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Soneto.Core.Audio;
using Soneto.Core.Configuration;
using Soneto.Core.History;

namespace Soneto.App.ViewModels;

/// <summary>
/// Phase 3 item 8 (§3.12) — the Settings page ViewModel. Hand-rolled
/// <see cref="INotifyPropertyChanged"/>, same style as <see cref="HistoryViewModel"/>/
/// <see cref="DictionaryEditorViewModel"/> (this project has no reactive-UI/MVVM package
/// pinned).
///
/// <para>
/// <b>Reads happen at construction, synchronously</b> — by the time this class is constructed,
/// <c>App.axaml.cs</c>'s composition root has ALREADY awaited
/// <c>DaemonComposition.LoadAndStartWatchingConfigAsync</c> (mirroring item 7's
/// <see cref="Soneto.Core.Dictionary.IDictionaryService"/> decoupling — see that file's own doc
/// comment for the "block briefly on the fast config/dictionary load, never on the ASR model
/// load" architecture decision and its real, already-fixed deadlock story), so
/// <see cref="IConfigService.Current"/> already reflects config.json's real, on-disk content the
/// first time this constructor runs — no loading spinner/empty-then-catch-up state is needed.
/// </para>
///
/// <para>
/// <b>Save UX decision (documented per this item's own explicit "your call, document it"
/// allowance):</b> ONE "Save settings" action for the whole page, not a per-section
/// live-write-per-field or per-section Save button. <c>config.json</c> is a single small file
/// with no per-entry validation/rejection concept the way <c>dictionary.json</c> has (a
/// malformed value there hard-fails the WHOLE file's deserialization and the previous config is
/// silently retained — see <see cref="ConfigService.LoadAsync"/>'s own doc comment) — so writing
/// it as one atomic round trip of every field this page exposes is simplest, avoids a
/// "did section A's Save silently drop my still-unsaved edit to section B's field" surprise, and
/// matches how a human editing <c>config.json</c> by hand already thinks about it (one file, one
/// edit, one save). Fields this page does NOT expose (e.g. <c>Injection.PerApp</c>,
/// <c>Logging</c>, <c>Audio.ReadyCue</c>) are carried through verbatim from
/// <see cref="IConfigService.Current"/> at save time, never reset to defaults.
/// </para>
///
/// <para>
/// <b>Write path:</b> build a full <see cref="SonetoConfig"/> from this ViewModel's own
/// Editing* fields (validating/parsing numeric text fields first — a parse failure aborts the
/// save with an error status and writes nothing), serialize via
/// <see cref="ConfigJsonOptions.Create"/> (item 8's own extraction of
/// <c>ConfigService</c>'s former private options builder, so this page writes
/// <c>config.json</c> with the EXACT same options <see cref="ConfigService"/> reads it with —
/// see that class's own doc comment), and write it straight to
/// <see cref="IConfigService.ConfigPath"/>. Nothing here talks to a
/// <see cref="System.IO.FileSystemWatcher"/> or reload timer directly — the already-running
/// <see cref="IConfigService.StartWatching"/> (started once, eagerly, in <c>App.axaml.cs</c>)
/// picks this exact write up automatically via its existing 500ms-debounced watcher, mirroring
/// item 7's dictionary editor's "ZERO new watch-side code" write path exactly.
/// </para>
///
/// <para>
/// <b>Post-review fix — concurrent-edit-during-save race, closed at the VIEW layer, not by
/// per-field dirty-tracking.</b> Unlike <see cref="DictionaryEditorViewModel"/> (whose "current
/// state" list and "pending edit" buffer fields are genuinely separate), this class's single
/// "Save the whole page" design (see above) means <see cref="RefreshFromCurrent"/> writes
/// directly into the SAME Editing* properties every input control on the page binds to. If a
/// user edited a DIFFERENT field while an earlier save was still settling, the settle-triggered
/// <see cref="RefreshFromCurrent"/> call (from <see cref="OnConfigChanged"/> or <see cref="SaveAsync"/>'s
/// own <c>finally</c> block) would silently overwrite that in-progress edit with the stale
/// pre-save value — a real data-loss race, not hypothetical. Per-field dirty-tracking would
/// close this too, but is unnecessary complexity for a single-page, single-save-action model:
/// instead, <c>SettingsView.axaml</c> disables the ENTIRE form (not just the Save button) while
/// <see cref="IsSaving"/> is true, so no conflicting edit is possible in the first place during
/// the one window where it could be silently lost. <see cref="IsSaving"/> is therefore true for
/// the WHOLE write-then-settle window (proven directly by
/// <c>SettingsViewModelTests.IsSaving_StaysTrueForTheEntireWriteThenSettleWindow_AndFalseOnlyAfter</c>),
/// not just around the write itself.
/// </para>
/// </summary>
public sealed class SettingsViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>500ms watcher debounce + generous buffer for the reload itself to complete —
    /// same value/reasoning as <see cref="DictionaryEditorViewModel.DefaultSettleTimeout"/>.</summary>
    public static readonly TimeSpan DefaultSettleTimeout = TimeSpan.FromMilliseconds(1500);

    private static readonly IReadOnlyList<string> CaptureModeOptionsStatic = Enum.GetNames<CaptureMode>();
    private static readonly IReadOnlyList<string> InjectionMethodOptionsStatic = Enum.GetNames<InjectionMethod>();
    private static readonly IReadOnlyList<string> ClipboardPolicyOptionsStatic = Enum.GetNames<ClipboardPolicy>();

    // Exposed as INSTANCE properties (not the static fields directly) — Soneto.App's compiled
    // bindings (AvaloniaUseCompiledBindingsByDefault=true, csproj-wide) can only resolve
    // instance members of the bound x:DataType, not static members, even though the values
    // themselves never vary per instance.
    public IReadOnlyList<string> CaptureModeOptions => CaptureModeOptionsStatic;
    public IReadOnlyList<string> InjectionMethodOptions => InjectionMethodOptionsStatic;
    public IReadOnlyList<string> ClipboardPolicyOptions => ClipboardPolicyOptionsStatic;

    private readonly IConfigService _configService;
    private readonly IHistoryStore _historyStore;
    private readonly Action<Action> _postToUiThread;
    private readonly TimeSpan _settleTimeout;
    private readonly string _debugAudioDir;
    private readonly ILogger _logger;

    private bool _isSaving;
    private string? _statusMessage;
    private bool _statusIsError;

    private string _hotkeyKey = string.Empty;
    private bool _hotkeySuppress;

    private string _asrModelDir = string.Empty;
    private string _asrNumThreadsText = string.Empty;
    private string _asrDecodingMethod = string.Empty;

    private string _audioCaptureMode = string.Empty;
    private string _audioDeviceId = string.Empty;
    private string _vadThresholdText = string.Empty;
    private string _vadMinSilenceMsText = string.Empty;
    private string _vadMinSpeechMsText = string.Empty;

    private string _injectionMethod = string.Empty;
    private string _injectionPasteChord = string.Empty;
    private string _injectionClipboardPolicy = string.Empty;

    private string? _languageProfileSecondaryTriggerKey;

    // --- Data & privacy (item 10, §3.14) ---
    private bool _debugAudioRetentionEnabled;
    private string _debugAudioRetentionMaxClipsText = string.Empty;
    private string _historyAutoDeleteAfterDaysText = string.Empty;
    private bool _isWiping;
    private string? _wipeStatusMessage;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsSaving
    {
        get => _isSaving;
        private set { _isSaving = value; OnPropertyChanged(); }
    }

    /// <summary>Result of the last Save attempt, for the UI to display. Null until the first
    /// save attempt.</summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        private set { _statusMessage = value; OnPropertyChanged(); }
    }

    public bool StatusIsError
    {
        get => _statusIsError;
        private set { _statusIsError = value; OnPropertyChanged(); }
    }

    // --- Hotkey ---

    public string HotkeyKey
    {
        get => _hotkeyKey;
        set { _hotkeyKey = value; OnPropertyChanged(); }
    }

    public bool HotkeySuppress
    {
        get => _hotkeySuppress;
        set { _hotkeySuppress = value; OnPropertyChanged(); }
    }

    // --- ASR ---

    public string AsrModelDir
    {
        get => _asrModelDir;
        set { _asrModelDir = value; OnPropertyChanged(); }
    }

    public string AsrNumThreadsText
    {
        get => _asrNumThreadsText;
        set { _asrNumThreadsText = value; OnPropertyChanged(); }
    }

    public string AsrDecodingMethod
    {
        get => _asrDecodingMethod;
        set { _asrDecodingMethod = value; OnPropertyChanged(); }
    }

    // --- Audio ---

    /// <summary>One of <see cref="CaptureModeOptions"/>.</summary>
    public string AudioCaptureMode
    {
        get => _audioCaptureMode;
        set { _audioCaptureMode = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Plain text field, per this item's own documented scope line: a real device PICKER would
    /// need querying <c>PortAudioCapture</c>'s device enumeration, which is more than this item
    /// needs — a text field round-trips the same nullable string
    /// <see cref="AudioConfig.DeviceId"/> already is, and is a reasonable, honestly-scoped
    /// stand-in until a future item adds real enumeration.
    /// </summary>
    public string AudioDeviceId
    {
        get => _audioDeviceId;
        set { _audioDeviceId = value; OnPropertyChanged(); }
    }

    public string VadThresholdText
    {
        get => _vadThresholdText;
        set { _vadThresholdText = value; OnPropertyChanged(); }
    }

    public string VadMinSilenceMsText
    {
        get => _vadMinSilenceMsText;
        set { _vadMinSilenceMsText = value; OnPropertyChanged(); }
    }

    public string VadMinSpeechMsText
    {
        get => _vadMinSpeechMsText;
        set { _vadMinSpeechMsText = value; OnPropertyChanged(); }
    }

    // --- Injection ---

    /// <summary>One of <see cref="InjectionMethodOptions"/>.</summary>
    public string InjectionMethod
    {
        get => _injectionMethod;
        set { _injectionMethod = value; OnPropertyChanged(); }
    }

    public string InjectionPasteChord
    {
        get => _injectionPasteChord;
        set { _injectionPasteChord = value; OnPropertyChanged(); }
    }

    /// <summary>One of <see cref="ClipboardPolicyOptions"/>.</summary>
    public string InjectionClipboardPolicy
    {
        get => _injectionClipboardPolicy;
        set { _injectionClipboardPolicy = value; OnPropertyChanged(); }
    }

    // --- Language profile binding (§3.12's own scope-reduced deliverable — see
    // LanguageProfileConfig's doc comment for the full, user-approved reasoning) ---

    /// <summary>
    /// The captured secondary-trigger key (schema-string shape, e.g. "LeftShift") or null if
    /// never set. Purely inert/unused metadata today — see <see cref="LanguageProfileConfig"/>'s
    /// own doc comment for exactly why, and <c>SettingsView.axaml</c>'s own "not yet active"
    /// label for this field.
    /// </summary>
    public string? LanguageProfileSecondaryTriggerKey
    {
        get => _languageProfileSecondaryTriggerKey;
        set { _languageProfileSecondaryTriggerKey = value; OnPropertyChanged(); }
    }

    // --- Data & privacy (item 10, §3.14) ---

    /// <summary>
    /// Opt-in "keep last N clips for debugging" toggle -- OFF by default, per plan §8 ("audio is
    /// never written to disk by default"). See <see cref="Soneto.Core.Configuration.DataPrivacyConfig"/>'s
    /// own doc comment.
    /// </summary>
    public bool DebugAudioRetentionEnabled
    {
        get => _debugAudioRetentionEnabled;
        set { _debugAudioRetentionEnabled = value; OnPropertyChanged(); }
    }

    public string DebugAudioRetentionMaxClipsText
    {
        get => _debugAudioRetentionMaxClipsText;
        set { _debugAudioRetentionMaxClipsText = value; OnPropertyChanged(); }
    }

    /// <summary>Blank = never auto-delete history (the default).</summary>
    public string HistoryAutoDeleteAfterDaysText
    {
        get => _historyAutoDeleteAfterDaysText;
        set { _historyAutoDeleteAfterDaysText = value; OnPropertyChanged(); }
    }

    /// <summary>True for the whole write-then-settle window of a panic wipe in progress --
    /// deliberately a SEPARATE flag from <see cref="IsSaving"/> (a panic wipe is not a config
    /// save; the two must never be conflated in the UI's disabled-state binding).</summary>
    public bool IsWiping
    {
        get => _isWiping;
        private set { _isWiping = value; OnPropertyChanged(); }
    }

    /// <summary>Result of the last panic-wipe attempt, for the UI to display. Null until the
    /// first attempt.</summary>
    public string? WipeStatusMessage
    {
        get => _wipeStatusMessage;
        private set { _wipeStatusMessage = value; OnPropertyChanged(); }
    }

    /// <summary>Real, production constructor.</summary>
    public SettingsViewModel(IConfigService configService, IHistoryStore historyStore)
        : this(configService, historyStore, uiThreadPost: null, settleTimeout: null, debugAudioDir: null, logger: null)
    {
    }

    /// <summary>
    /// Test-facing constructor (internal, mirroring <see cref="DictionaryEditorViewModel"/>'s
    /// own established pattern): <paramref name="uiThreadPost"/> replaces
    /// <see cref="Dispatcher.UIThread"/> marshaling with a synchronous stand-in,
    /// <paramref name="settleTimeout"/> lets a test use a short window instead of
    /// <see cref="DefaultSettleTimeout"/>'s real 1.5s, and <paramref name="debugAudioDir"/> lets
    /// a test point the panic-wipe's debug-audio cleanup at a throwaway temp directory instead
    /// of the real <see cref="Soneto.Core.Audio.DebugAudioPaths.Resolve"/> default.
    /// </summary>
    internal SettingsViewModel(
        IConfigService configService, IHistoryStore historyStore, Action<Action>? uiThreadPost,
        TimeSpan? settleTimeout, string? debugAudioDir, ILogger? logger)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        _postToUiThread = uiThreadPost ?? (action => Dispatcher.UIThread.Post(action));
        _settleTimeout = settleTimeout ?? DefaultSettleTimeout;
        _debugAudioDir = debugAudioDir ?? DebugAudioPaths.Resolve();
        _logger = logger ?? NullLogger.Instance;

        RefreshFromCurrent();
        _configService.ConfigChanged += OnConfigChanged;
    }

    /// <summary>
    /// Repopulates every Editing* field from <see cref="IConfigService.Current"/> — called at
    /// construction and again whenever a <see cref="IConfigService.ConfigChanged"/> event fires
    /// (a hand-edit to <c>config.json</c>, or this ViewModel's own write settling), so the page
    /// always reflects the real on-disk state, mirroring <see cref="DictionaryEditorViewModel.RefreshFromCurrent"/>'s
    /// live-refresh precedent exactly.
    /// </summary>
    private void RefreshFromCurrent()
    {
        var current = _configService.Current;

        HotkeyKey = current.Hotkey.Key;
        HotkeySuppress = current.Hotkey.Suppress;

        AsrModelDir = current.Asr.ModelDir ?? string.Empty;
        AsrNumThreadsText = current.Asr.NumThreads.ToString(CultureInfo.InvariantCulture);
        AsrDecodingMethod = current.Asr.DecodingMethod;

        AudioCaptureMode = current.Audio.CaptureMode.ToString();
        AudioDeviceId = current.Audio.DeviceId ?? string.Empty;
        VadThresholdText = current.Audio.Vad.Threshold.ToString(CultureInfo.InvariantCulture);
        VadMinSilenceMsText = current.Audio.Vad.MinSilenceMs.ToString(CultureInfo.InvariantCulture);
        VadMinSpeechMsText = current.Audio.Vad.MinSpeechMs.ToString(CultureInfo.InvariantCulture);

        InjectionMethod = current.Injection.Method.ToString();
        InjectionPasteChord = current.Injection.PasteChord;
        InjectionClipboardPolicy = current.Injection.ClipboardPolicy.ToString();

        LanguageProfileSecondaryTriggerKey = current.LanguageProfile.SecondaryTriggerKey;

        DebugAudioRetentionEnabled = current.DataPrivacy.DebugAudioRetentionEnabled;
        DebugAudioRetentionMaxClipsText = current.DataPrivacy.DebugAudioRetentionMaxClips.ToString(CultureInfo.InvariantCulture);
        HistoryAutoDeleteAfterDaysText = current.DataPrivacy.HistoryAutoDeleteAfterDays?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>
    /// The permanent live-refresh subscription (hand-edits, or this ViewModel's own writes
    /// settling) — marshaled through <see cref="_postToUiThread"/> before touching any
    /// property, the same threading discipline every prior ViewModel in this project
    /// establishes as a hard requirement.
    /// </summary>
    private void OnConfigChanged(object? sender, ConfigChangedEventArgs e) =>
        _postToUiThread(RefreshFromCurrent);

    /// <summary>
    /// Validates every numeric text field, builds a full <see cref="SonetoConfig"/> (carrying
    /// through every field this page does not expose from <see cref="IConfigService.Current"/>
    /// unchanged — see this class's own doc comment), writes it to
    /// <see cref="IConfigService.ConfigPath"/>, then waits for the next
    /// <see cref="IConfigService.ConfigChanged"/> event (or <see cref="_settleTimeout"/>) to
    /// report success/failure.
    /// </summary>
    public async Task SaveAsync()
    {
        if (!int.TryParse(AsrNumThreadsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numThreads) || numThreads <= 0)
        {
            SetStatus($"ASR thread count must be a positive whole number (got \"{AsrNumThreadsText}\").", isError: true);
            return;
        }

        if (!double.TryParse(VadThresholdText, NumberStyles.Float, CultureInfo.InvariantCulture, out var vadThreshold))
        {
            SetStatus($"VAD threshold must be a number (got \"{VadThresholdText}\").", isError: true);
            return;
        }

        if (!int.TryParse(VadMinSilenceMsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vadMinSilenceMs) || vadMinSilenceMs < 0)
        {
            SetStatus($"VAD min silence (ms) must be a non-negative whole number (got \"{VadMinSilenceMsText}\").", isError: true);
            return;
        }

        if (!int.TryParse(VadMinSpeechMsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vadMinSpeechMs) || vadMinSpeechMs < 0)
        {
            SetStatus($"VAD min speech (ms) must be a non-negative whole number (got \"{VadMinSpeechMsText}\").", isError: true);
            return;
        }

        if (!Enum.TryParse<CaptureMode>(AudioCaptureMode, out var captureMode))
        {
            SetStatus($"Unknown capture mode \"{AudioCaptureMode}\".", isError: true);
            return;
        }

        if (!Enum.TryParse<InjectionMethod>(InjectionMethod, out var injectionMethod))
        {
            SetStatus($"Unknown injection method \"{InjectionMethod}\".", isError: true);
            return;
        }

        if (!Enum.TryParse<ClipboardPolicy>(InjectionClipboardPolicy, out var clipboardPolicy))
        {
            SetStatus($"Unknown clipboard policy \"{InjectionClipboardPolicy}\".", isError: true);
            return;
        }

        if (!int.TryParse(DebugAudioRetentionMaxClipsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var debugAudioMaxClips) || debugAudioMaxClips < 0)
        {
            SetStatus($"Debug audio clip count must be a non-negative whole number (got \"{DebugAudioRetentionMaxClipsText}\").", isError: true);
            return;
        }

        int? historyAutoDeleteAfterDays = null;
        if (!string.IsNullOrWhiteSpace(HistoryAutoDeleteAfterDaysText))
        {
            if (!int.TryParse(HistoryAutoDeleteAfterDaysText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedDays) || parsedDays <= 0)
            {
                SetStatus($"History auto-delete (days) must be blank or a positive whole number (got \"{HistoryAutoDeleteAfterDaysText}\").", isError: true);
                return;
            }
            historyAutoDeleteAfterDays = parsedDays;
        }

        IsSaving = true;
        try
        {
            var current = _configService.Current;

            var newConfig = new SonetoConfig
            {
                Hotkey = new HotkeyConfig
                {
                    Key = HotkeyKey,
                    Suppress = HotkeySuppress,
                },
                Audio = new AudioConfig
                {
                    DeviceId = string.IsNullOrWhiteSpace(AudioDeviceId) ? null : AudioDeviceId,
                    CaptureMode = captureMode,
                    IdleCloseMs = current.Audio.IdleCloseMs,
                    PreRollMs = current.Audio.PreRollMs,
                    ReadyCue = current.Audio.ReadyCue,
                    MinDurationMs = current.Audio.MinDurationMs,
                    MaxDurationMs = current.Audio.MaxDurationMs,
                    LongUtteranceCueMs = current.Audio.LongUtteranceCueMs,
                    Resampler = current.Audio.Resampler,
                    Vad = new VadConfig
                    {
                        Enabled = current.Audio.Vad.Enabled,
                        Threshold = vadThreshold,
                        MinSilenceMs = vadMinSilenceMs,
                        MinSpeechMs = vadMinSpeechMs,
                        MinUtteranceMs = current.Audio.Vad.MinUtteranceMs,
                    },
                },
                Asr = new AsrConfig
                {
                    ModelDir = string.IsNullOrWhiteSpace(AsrModelDir) ? null : AsrModelDir,
                    NumThreads = numThreads,
                    DecodingMethod = AsrDecodingMethod,
                    HotwordsEnabled = current.Asr.HotwordsEnabled,
                    TimeoutMs = current.Asr.TimeoutMs,
                },
                PostProcess = current.PostProcess,
                Injection = new InjectionConfig
                {
                    Method = injectionMethod,
                    PasteChord = InjectionPasteChord,
                    PreDelayMs = current.Injection.PreDelayMs,
                    ClipboardRestoreDelayMs = current.Injection.ClipboardRestoreDelayMs,
                    ClipboardPolicy = clipboardPolicy,
                    SanitizeModifiers = current.Injection.SanitizeModifiers,
                    TargetLostPolicy = current.Injection.TargetLostPolicy,
                    PerApp = current.Injection.PerApp,
                },
                Logging = current.Logging,
                LanguageProfile = new LanguageProfileConfig
                {
                    SecondaryTriggerKey = string.IsNullOrWhiteSpace(LanguageProfileSecondaryTriggerKey)
                        ? null
                        : LanguageProfileSecondaryTriggerKey,
                },
                DataPrivacy = new DataPrivacyConfig
                {
                    DebugAudioRetentionEnabled = DebugAudioRetentionEnabled,
                    DebugAudioRetentionMaxClips = debugAudioMaxClips,
                    HistoryAutoDeleteAfterDays = historyAutoDeleteAfterDays,
                },
            };

            var json = JsonSerializer.Serialize(newConfig, ConfigJsonOptions.Create());
            await File.WriteAllTextAsync(_configService.ConfigPath, json);

            var settled = await WaitForNextConfigChangedOrTimeoutAsync();

            if (settled)
            {
                SetStatus("Settings saved.", isError: false);
            }
            else
            {
                SetStatus(
                    "The save did not apply within the expected time — no reload was observed. Check the app log.",
                    isError: true);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to write config.json: {ex.Message}", isError: true);
        }
        finally
        {
            RefreshFromCurrent();
            IsSaving = false;
        }
    }

    /// <summary>
    /// The §3.14/§3.15 "panic wipe" control: empties the history store
    /// (<see cref="IHistoryStore.PanicWipeAsync"/>) AND every debug audio clip in
    /// <see cref="_debugAudioDir"/> (those clips correlate to history rows this call just
    /// deleted -- leaving them behind would defeat the whole point of a privacy "wipe
    /// everything" control, since audio is the more sensitive of the two per plan §3.14's own
    /// framing). Deliberately contains NO confirmation logic itself -- the real confirmation
    /// step (<see cref="Soneto.App.Views.ConfirmDialog"/>, a genuine second click on a distinct
    /// control) lives at the VIEW layer, per this item's own "the confirmation dialog is not
    /// itself unit-testable" framing; this method is exactly what the dialog's own Confirm
    /// button calls once the user has already confirmed.
    /// </summary>
    public async Task PanicWipeAsync()
    {
        IsWiping = true;
        try
        {
            await _historyStore.PanicWipeAsync();
            DebugAudioStore.WipeAll(_debugAudioDir, _logger);
            WipeStatusMessage = "History and debug audio clips wiped.";
        }
        catch (Exception ex)
        {
            WipeStatusMessage = $"Panic wipe failed: {ex.Message}";
        }
        finally
        {
            IsWiping = false;
        }
    }

    /// <summary>Awaits the next <see cref="IConfigService.ConfigChanged"/> event or
    /// <see cref="_settleTimeout"/>, whichever comes first. Returns whether the event fired —
    /// mirrors <see cref="DictionaryEditorViewModel.WaitForNextDictionaryChangedOrTimeoutAsync"/>
    /// exactly.</summary>
    private async Task<bool> WaitForNextConfigChangedOrTimeoutAsync()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? s, ConfigChangedEventArgs e) => tcs.TrySetResult(true);

        _configService.ConfigChanged += Handler;
        try
        {
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(_settleTimeout));
            return completed == tcs.Task;
        }
        finally
        {
            _configService.ConfigChanged -= Handler;
        }
    }

    private void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        StatusIsError = isError;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>Unsubscribes from <see cref="IConfigService.ConfigChanged"/>. Does NOT dispose
    /// <see cref="IConfigService"/> itself — this ViewModel does not own the service's
    /// lifetime (the composition root does).</summary>
    public void Dispose()
    {
        _configService.ConfigChanged -= OnConfigChanged;
    }
}
