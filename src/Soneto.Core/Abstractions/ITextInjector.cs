namespace Soneto.Core.Abstractions;

/// <summary>
/// Delivers transcribed text into whatever application has focus. Implementations are
/// platform-specific (Windows: clipboard + synthetic paste via SendInput; Linux:
/// wl-copy + ydotool). The atomic clipboard-restore pattern from plan §1.8 (S4
/// correction: check-and-write must be one critical section, not separate open/check
/// then open/write) is an implementation concern, not part of this interface's shape.
/// </summary>
public interface ITextInjector
{
    /// Opaque handle to the window that had focus at key-down.
    object? CaptureTarget();

    Task<InjectionOutcome> InjectAsync(string text, object? target, InjectionOptions opts, CancellationToken ct);

    /// <summary>
    /// Phase 4 item 3 (§4.4): resolves <paramref name="target"/> (an opaque handle as returned
    /// by <see cref="CaptureTarget"/>) to its owning process's executable file name (e.g.
    /// <c>"wt.exe"</c>), using the exact same "keyed by process executable name" shape
    /// <see cref="Soneto.Core.Configuration.PerAppOverride"/>'s injection-side resolution
    /// (Phase 4 item 2) already established -- so <c>Soneto.Core</c>/<see cref="SessionController"/>
    /// can resolve a process name for dictionary-side per-app profile selection
    /// (<see cref="Soneto.Core.Dictionary.PerAppOverride"/>) without ever needing to know what
    /// concrete type <paramref name="target"/> actually boxes (a Win32 <c>HWND</c> on Windows,
    /// always <c>null</c> on Linux -- mirrors <see cref="CaptureTarget"/>'s own platform split).
    /// Default implementation returns <c>null</c> unconditionally, so this is a non-breaking
    /// addition for any <see cref="ITextInjector"/> implementer written before this method
    /// existed (real or test fake) -- only <c>Soneto.Platform.Windows.WindowsTextInjector</c>
    /// overrides it with a real lookup; <c>Soneto.Platform.Linux.LinuxTextInjector</c> also
    /// overrides it explicitly (rather than relying on this default silently) purely for
    /// discoverability, documented the same way its own <see cref="CaptureTarget"/> already
    /// documents its "always null" contract. Callers must treat <c>null</c> the same as "no
    /// per-app match," never as an error.
    /// </summary>
    string? TryResolveProcessExecutableName(object? target) => null;
}

public enum InjectionMethod
{
    ClipboardPaste,
    UnicodeSynth,
}

/// <summary>
/// Runtime-abstraction mirror of <see cref="Soneto.Core.Configuration.ClipboardPolicy"/>,
/// same config-vs-runtime-abstraction split already established for
/// <see cref="Soneto.Core.Configuration.HotkeyConfig"/> vs <see cref="HotkeyBinding"/> (see
/// that config type's doc comment for the reasoning) -- kept separate from the config DTO's
/// enum so config defaults/serialization concerns never leak into the abstraction contracts
/// consumed by the platform injectors.
/// </summary>
public enum ClipboardPolicy
{
    TextOnly,
    Never,
    BestEffort,
}

public sealed record InjectionOptions(
    InjectionMethod Method,
    string PasteChord,               // "ctrl+v" | "ctrl+shift+v" | "cmd+v"
    TimeSpan PreDelay,
    TimeSpan ClipboardRestoreDelay,
    bool RestoreClipboard,
    // Item 7b: plan §1.8 step 6/9 -- suppress physically-held Shift/Alt/Win before the
    // paste chord, restore only what's still held afterward. Full bypass (no
    // suppress/restore at all) when false, per SonetoConfig.InjectionConfig.SanitizeModifiers.
    bool SanitizeModifiers = true,
    // The configured hotkey trigger's config-schema key string (e.g. "LeftShift",
    // "RightControl", null if unknown/not wired). Kept as a plain string here, not a
    // platform-specific virtual-key code, so this stays usable from platform-agnostic
    // Soneto.Core -- each platform's injector is responsible for mapping it onto whatever
    // native key representation it needs. Exists solely so a platform sanitiser can avoid
    // treating the trigger key's own physical-hold state as "the user is additionally
    // holding this modifier for paste purposes" when the trigger itself is one of
    // Shift/Alt/Win (both are valid, explicitly supported trigger aliases) -- see
    // Soneto.Platform.Windows.ModifierSanitizer's doc comment for the full reasoning.
    string? TriggerKey = null,
    // Item 7c: distinguishes textOnly/never/bestEffort, which RestoreClipboard's plain bool
    // (still the fast Never-bypass gate) collapses together -- the Windows injector needs
    // this to tell textOnly and bestEffort apart from each other when deciding whether a
    // non-text original clipboard blocks restoration.
    ClipboardPolicy Policy = ClipboardPolicy.TextOnly);

public enum InjectionOutcome
{
    Injected,
    TargetLost,
    ClipboardFailed,
    SynthFailed,
    PermissionDenied,
}
