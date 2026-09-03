using Soneto.Core.Abstractions;

namespace Soneto.Core.Configuration;

/// <summary>
/// Root configuration model, matching the JSON schema in plan §1.10 exactly (field
/// names and defaults). This is a plain, serializable DTO — deliberately kept separate
/// from <see cref="Soneto.Core.Abstractions.HotkeyBinding"/> and friends, which are the
/// runtime abstractions consumed by the (future) SessionController. Config sections that
/// map onto an existing abstraction expose a small `ToXxx()` conversion instead of
/// reusing the abstraction type directly, so config defaults/serialization concerns
/// never leak into the abstraction contracts consumed by items 3+.
/// </summary>
public sealed class SonetoConfig
{
    public HotkeyConfig Hotkey { get; init; } = new();
    public AudioConfig Audio { get; init; } = new();
    public AsrConfig Asr { get; init; } = new();
    public PostProcessConfig PostProcess { get; init; } = new();
    public InjectionConfig Injection { get; init; } = new();
    public LoggingConfig Logging { get; init; } = new();

    /// <summary>
    /// Phase 3 item 8 (§3.12) — see <see cref="LanguageProfileConfig"/>'s own doc comment for
    /// the full, user-approved scope decision this section exists under.
    /// </summary>
    public LanguageProfileConfig LanguageProfile { get; init; } = new();

    /// <summary>
    /// Phase 3 item 10 (§3.14) — see <see cref="DataPrivacyConfig"/>'s own doc comment for the
    /// data & privacy controls this section gates.
    /// </summary>
    public DataPrivacyConfig DataPrivacy { get; init; } = new();
}

public sealed class HotkeyConfig
{
    public string Key { get; init; } = "RightControl";
    public bool Suppress { get; init; } = true;

    /// Converts to the runtime abstraction consumed by <see cref="IHotkeySource"/>.
    public HotkeyBinding ToBinding() => new(Key, Suppress);
}

public enum CaptureMode { OnDemand, WarmIdle, AlwaysOn }

// NOTE (intentional asymmetry, per plan §1.13): only CaptureMode has a soft per-field
// fallback converter (ConfigService.CaptureModeJsonConverter — unknown value falls back
// to OnDemand + logs a warning, rest of the config still loads). ReadyCue, ResamplerMode,
// InjectionMethod and ClipboardPolicy below (and in InjectionConfig) have no such
// converter, so an invalid value for any of them hard-fails deserialization of the
// *entire* config file, which ConfigService.LoadAsync catches by keeping the previous
// config wholesale. This matches the plan's spec, which only calls out captureMode for
// soft fallback — don't assume the same pattern exists for these when extending them.
public enum ReadyCue { Sound, None }

public enum ResamplerMode { Polyphase, None }

public sealed class VadConfig
{
    public bool Enabled { get; init; } = true;
    public double Threshold { get; init; } = 0.5;
    public int MinSilenceMs { get; init; } = 300;
    public int MinSpeechMs { get; init; } = 250;

    /// <summary>
    /// Whole-utterance discard floor: if the total speech <see cref="SileroVadDetector.Trim"/>
    /// finds (spanning first-segment-start to last-segment-end) is under this many
    /// milliseconds, the entire buffer is discarded rather than sent to the transcriber —
    /// plan §1.5's "if total speech after trim is under 300 ms, discard and log." This is a
    /// deliberately separate knob from <see cref="MinSpeechMs"/> (which instead drives
    /// Silero's own native per-segment filter, <c>SileroVadModelConfig.MinSpeechDuration</c>):
    /// reusing <see cref="MinSpeechMs"/> for both purposes made the discard check
    /// structurally near-unreachable, since <c>TotalSpeechDuration</c> is by construction
    /// always &gt;= the length of any single native segment that produced it, so
    /// "total speech &lt; MinSpeechMs" could basically only ever be true when zero segments
    /// were found at all — a case already covered separately. Defaults to the plan's literal
    /// 300ms.
    /// </summary>
    public int MinUtteranceMs { get; init; } = 300;
}

public sealed class AudioConfig
{
    /// null = system default, resolved per key-down.
    public string? DeviceId { get; init; }
    public CaptureMode CaptureMode { get; init; } = CaptureMode.OnDemand;
    public int IdleCloseMs { get; init; } = 90000;
    public int PreRollMs { get; init; } = 0;
    public ReadyCue ReadyCue { get; init; } = ReadyCue.Sound;
    public int MinDurationMs { get; init; } = 250;
    public int MaxDurationMs { get; init; } = 120000;
    public int LongUtteranceCueMs { get; init; } = 15000;
    public ResamplerMode Resampler { get; init; } = ResamplerMode.Polyphase;
    public VadConfig Vad { get; init; } = new();
}

public sealed class AsrConfig
{
    public string? ModelDir { get; init; }

    /// <summary>
    /// Plan §1.10's literal JSON example uses 8. S1's thread sweep (see
    /// Docs/PROJECT-MEMORY.md, "ASR thread count default") found the actual knee on the
    /// dev machine at 4 threads — 8 was comparable, 16 was worst and occasionally missed
    /// the 400ms bar — and explicitly says "carry NumThreads=4 forward as the default
    /// seed for §1.6". We seed the product default with that finding (4) rather than the
    /// plan's un-updated example literal (8), since the plan's own §1.6 already commits
    /// to "seeded from the S1 sweep result." Re-verify on the actual target laptop before
    /// this is fully locked in (S1's own caveat).
    /// </summary>
    public int NumThreads { get; init; } = 4;

    public string DecodingMethod { get; init; } = "greedy_search";
    public bool HotwordsEnabled { get; init; } = false;
    public int TimeoutMs { get; init; } = 10000;
}

public sealed class PostProcessConfig
{
    public bool NormalizeUnicode { get; init; } = true;
    public bool SpokenCommands { get; init; } = true;
    public bool CleanWhitespace { get; init; } = true;
    public bool TrailingSpace { get; init; } = true;

    /// <summary>
    /// Phase 2 item 10: gates <c>Soneto.Core.Dictionary.DictionaryEngineProcessor</c> (order 40 --
    /// correction pairs + vocabulary-term casing). Kept SEPARATE from <see cref="RegexRules"/>
    /// rather than one shared "dictionary" toggle, deliberately: the two are functionally distinct
    /// passes with different risk profiles (the trie-based workhorse's boundary-safe, no-cascade
    /// guarantee vs. regex's power-user, deliberately-cascading escape hatch -- see
    /// <c>RegexRuleProcessor</c>'s own doc comment), and a user debugging an unexpected regex-rule
    /// interaction shouldn't have to also disable ordinary vocabulary corrections (or vice versa)
    /// to isolate which pass is responsible.
    /// </summary>
    public bool DictionaryEngine { get; init; } = true;

    /// <summary>
    /// Phase 2 item 10: gates <c>Soneto.Core.Dictionary.RegexRuleProcessor</c> (order 50). See
    /// <see cref="DictionaryEngine"/>'s doc comment for why this is a separate toggle rather than
    /// one shared "dictionary" switch.
    /// </summary>
    public bool RegexRules { get; init; } = true;

    /// <summary>
    /// Phase 2 item 10: gates <c>Soneto.Core.Dictionary.FillerWordStripper</c> (order 70). Needs
    /// its own toggle (there is no <c>dictionary.json</c>-backed way to disable it -- per item 9's
    /// design note, <c>FillerWordStripper</c> is deliberately NOT entry-backed) unlike
    /// <see cref="DictionaryEngine"/>/<see cref="RegexRules"/>/<see cref="SpokenCommands"/>, which
    /// also each independently gate whether their own <c>dictionary.json</c> entries apply at all.
    /// </summary>
    public bool FillerWordStripping { get; init; } = true;
}

// See the fallback-asymmetry note above CaptureMode: these two also hard-fail the whole
// config file on an invalid value rather than soft-falling-back per-field.
public enum InjectionMethod { ClipboardPaste, UnicodeSynth }

public enum ClipboardPolicy { TextOnly, Never, BestEffort }

public sealed class PerAppOverride
{
    public string? PasteChord { get; init; }
    public int? ClipboardRestoreDelayMs { get; init; }

    /// <summary>
    /// Phase 4 item 2 (§4.4) -- new field, not previously scoped. Optional per-app override of
    /// <see cref="InjectionConfig.Method"/> (e.g. the build plan's own "for <c>wt.exe</c>, use
    /// char-by-char" framing, expressed as <c>Method = InjectionMethod.UnicodeSynth</c> on that
    /// app's entry). Null (the default) means "no override -- use whatever the base config's
    /// <see cref="InjectionConfig.Method"/> resolves to for this injection." See
    /// <see cref="PerAppOverrideResolver"/> for how this is applied.
    /// </summary>
    public InjectionMethod? Method { get; init; }
}

public sealed class InjectionConfig
{
    public InjectionMethod Method { get; init; } = InjectionMethod.ClipboardPaste;
    public string PasteChord { get; init; } = "ctrl+v";
    public int PreDelayMs { get; init; } = 20;
    public int ClipboardRestoreDelayMs { get; init; } = 150;
    public ClipboardPolicy ClipboardPolicy { get; init; } = ClipboardPolicy.TextOnly;
    public bool SanitizeModifiers { get; init; } = true;
    public string TargetLostPolicy { get; init; } = "current";

    public Dictionary<string, PerAppOverride> PerApp { get; init; } = new()
    {
        ["WindowsTerminal.exe"] = new PerAppOverride { PasteChord = "ctrl+shift+v" },
        ["Teams.exe"] = new PerAppOverride { ClipboardRestoreDelayMs = 300 },
    };

    /// <summary>
    /// Converts to the runtime <see cref="InjectionOptions"/> consumed by
    /// <see cref="ITextInjector"/>. <c>RestoreClipboard</c> is derived from
    /// <see cref="ClipboardPolicy"/> as a simple boolean gate (post-review fix -- this used
    /// to hardcode <c>true</c> unconditionally, silently ignoring
    /// <see cref="ClipboardPolicy.Never"/>): <c>Never</c> means "don't restore at all". Item
    /// 7c added the <see cref="Soneto.Core.Abstractions.InjectionOptions.Policy"/> field
    /// alongside it, explicitly mapped below, so the Windows injector can now tell
    /// <c>TextOnly</c> and <c>BestEffort</c> apart from each other (both still gate
    /// <c>RestoreClipboard</c> to <c>true</c> here, but <c>WindowsTextInjector</c> uses
    /// <c>Policy</c> at restore time to decide whether a non-text original clipboard blocks
    /// restoration -- see that class and <c>ClipboardManager</c>'s doc comments in
    /// Soneto.Platform.Windows for the guard/policy logic itself). <see cref="PerApp"/>
    /// overrides are still NOT applied here (Phase 4 item 2, §4.4): this conversion only ever
    /// reads the base config values, producing the same <see cref="InjectionOptions"/> for
    /// every target app. Per-app resolution happens one layer down, inside
    /// <c>Soneto.Platform.Windows.WindowsTextInjector.InjectAsync</c> itself (via
    /// <see cref="PerAppOverrideResolver"/>) -- NOT here and NOT in
    /// <c>SessionController</c>/the composition layer -- because only that method, at the
    /// point it does its own fresh foreground-window lookup, actually knows which process an
    /// injection is really about to land in (see that class's doc comment for the full
    /// reasoning: <c>SendInput</c> always targets whatever is foreground AT INJECTION TIME,
    /// which can differ from whatever was captured at key-down).
    /// </summary>
    /// <param name="triggerKey">
    /// The configured hotkey trigger's config-schema key string (<c>HotkeyConfig.Key</c>,
    /// e.g. "LeftShift"), or null if not known/wired at the call site. Forwarded unchanged
    /// into <see cref="InjectionOptions.TriggerKey"/> -- item 7b's modifier sanitiser needs
    /// it to avoid mistaking the trigger key's own physical-hold state for a user-held
    /// paste modifier when the trigger itself is Shift/Alt/Win. This method itself does not
    /// interpret the string; it's an opaque pass-through, same as every other value here.
    /// </param>
    public InjectionOptions ToOptions(string? triggerKey = null) => new(
        Method switch
        {
            InjectionMethod.UnicodeSynth => Soneto.Core.Abstractions.InjectionMethod.UnicodeSynth,
            _ => Soneto.Core.Abstractions.InjectionMethod.ClipboardPaste,
        },
        PasteChord,
        TimeSpan.FromMilliseconds(PreDelayMs),
        TimeSpan.FromMilliseconds(ClipboardRestoreDelayMs),
        RestoreClipboard: ClipboardPolicy != ClipboardPolicy.Never,
        SanitizeModifiers: SanitizeModifiers,
        TriggerKey: triggerKey,
        Policy: ClipboardPolicy switch
        {
            ClipboardPolicy.Never => Soneto.Core.Abstractions.ClipboardPolicy.Never,
            ClipboardPolicy.BestEffort => Soneto.Core.Abstractions.ClipboardPolicy.BestEffort,
            _ => Soneto.Core.Abstractions.ClipboardPolicy.TextOnly,
        });
}

public sealed class LoggingConfig
{
    public string Level { get; init; } = "Information";
    public int RetainDays { get; init; } = 7;
}

/// <summary>
/// Phase 3 item 8 (§3.12) — the second-hotkey "language profile hint" CAPTURE mechanism,
/// deliberately scoped down from §3.12's literal text by an explicit, user-approved decision
/// (documented in full in <c>Docs/soneto-implementation-plan-phase3.md</c> §3.16 item 8's row
/// and <c>Docs/PROJECT-MEMORY.md</c>'s Phase 3 section):
/// <list type="bullet">
/// <item>Phase 1's own history (see <c>Docs/PROJECT-MEMORY.md</c>) found and reproduced TWICE
/// that SharpHook (the hook library <c>WindowsHotkeySource</c> wraps) cannot run two concurrent
/// hook instances in one process on this machine — a real, confirmed environmental limitation,
/// not a hypothetical one.</item>
/// <item>Wiring a genuinely LIVE second global hotkey would therefore require either two
/// simultaneous hook instances (broken here) or extending <c>IHotkeySource</c>/
/// <c>WindowsHotkeySource</c>/<c>LinuxHotkeySource</c> (Phase 1, already hardened through
/// multiple review rounds) to recognize a second trigger key within ONE hook — a real
/// platform-code change well beyond this item's "Settings page UI work" scope.</item>
/// <item>The user was asked and explicitly chose the conservative option: build only the
/// second-hotkey CAPTURE mechanism (this config field + a real Settings UI control to set it),
/// and do NOT wire it into a live second hook this item. <see cref="SecondaryTriggerKey"/> is
/// therefore always inert/unused today — nothing in <c>SessionController</c>,
/// <c>DictationCompletedEventArgs</c>, or <see cref="Soneto.Core.History.HistoryEntry"/> reads
/// it, and none of those types gained a "language profile hint" field this item, since that
/// plumbing would only matter once something can actually populate it (a future item that
/// extends the hook mechanism).</item>
/// </list>
/// The Settings UI must label the control that sets this field as "not yet active — captured
/// for a future phase" (mirroring item 7's analogous <c>PerAppOverride</c> "not yet active"
/// labeling), so a user setting this never mistakenly believes it does anything today.
/// </summary>
public sealed class LanguageProfileConfig
{
    /// <summary>
    /// The captured secondary-trigger key string (same schema shape as
    /// <see cref="HotkeyConfig.Key"/>, e.g. "LeftShift") — or null if never set. Purely
    /// inert/unused metadata for now; see this class's own doc comment.
    /// </summary>
    public string? SecondaryTriggerKey { get; init; }
}

/// <summary>
/// Phase 3 item 10 (§3.14) — data &amp; privacy controls. Per plan §8, audio is never written
/// to disk by default; this section holds the explicit, OPT-IN "keep last N clips for
/// debugging" toggle (off by default), plus the history text auto-delete-after-N-days
/// setting. The panic-wipe control (also §3.14) has no config field of its own — it's a
/// one-shot action (<see cref="Soneto.Core.History.IHistoryStore.PanicWipeAsync"/>), not a
/// persisted setting.
/// </summary>
public sealed class DataPrivacyConfig
{
    /// <summary>
    /// Opt-in, OFF by default per plan §8. When true, every completed dictation's recorded
    /// audio (the exact samples the transcriber actually heard — see
    /// <see cref="Soneto.Core.DictationCompletedEventArgs.AudioSamples"/>'s own doc comment for
    /// which samples that is and why) is written as a WAV file correlated to its
    /// <see cref="Soneto.Core.History.HistoryEntry.Id"/>, for debugging purposes only.
    /// </summary>
    public bool DebugAudioRetentionEnabled { get; init; } = false;

    /// <summary>
    /// Keep-last-N retention for debug audio clips — deliberately count-bounded, not
    /// time-bounded like <see cref="HistoryAutoDeleteAfterDays"/>, since audio clips are far
    /// larger and more sensitive than text history rows (plan §3.14's own explicit "its own
    /// separate auto-purge" requirement). Enforced immediately after each write (see
    /// <see cref="Soneto.Core.Audio.DebugAudioStore"/>'s doc comment) — oldest clips beyond
    /// this count are deleted, not aged out on a timer.
    /// </summary>
    public int DebugAudioRetentionMaxClips { get; init; } = 20;

    /// <summary>
    /// Auto-delete history rows older than this many days. Null (the default) means "never
    /// auto-delete" — a user must opt in to a retention window. Enforced by a daily background
    /// sweep calling <see cref="Soneto.Core.History.IHistoryStore.PurgeOlderThanAsync"/> (see
    /// <c>Soneto.App.HistoryRetentionSweeper</c>), not by any change to
    /// <see cref="Soneto.Core.History.IHistoryStore"/> itself.
    /// </summary>
    public int? HistoryAutoDeleteAfterDays { get; init; } = null;
}
