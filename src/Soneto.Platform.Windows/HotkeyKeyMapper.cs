using SharpHook.Data;

namespace Soneto.Platform.Windows;

/// <summary>
/// Maps <see cref="Soneto.Core.Abstractions.HotkeyBinding.Key"/> config strings (plan
/// §1.10's schema, e.g. <c>"RightControl"</c>) onto SharpHook's <see cref="KeyCode"/>
/// enum. Kept as its own small static class (rather than inline in
/// <see cref="WindowsHotkeySource"/>) so the mapping table is easy to find and extend.
/// </summary>
public static class HotkeyKeyMapper
{
    /// <summary>Friendly aliases matching the plan's config schema naming style.</summary>
    private static readonly Dictionary<string, KeyCode> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RightControl"] = KeyCode.VcRightControl,
        ["RightCtrl"] = KeyCode.VcRightControl,
        ["LeftControl"] = KeyCode.VcLeftControl,
        ["LeftCtrl"] = KeyCode.VcLeftControl,
        ["RightShift"] = KeyCode.VcRightShift,
        ["LeftShift"] = KeyCode.VcLeftShift,
        ["RightAlt"] = KeyCode.VcRightAlt,
        ["RightMenu"] = KeyCode.VcRightAlt,
        ["LeftAlt"] = KeyCode.VcLeftAlt,
        ["LeftMenu"] = KeyCode.VcLeftAlt,
        ["RightWin"] = KeyCode.VcRightMeta,
        ["RightMeta"] = KeyCode.VcRightMeta,
        ["LeftWin"] = KeyCode.VcLeftMeta,
        ["LeftMeta"] = KeyCode.VcLeftMeta,
        ["CapsLock"] = KeyCode.VcCapsLock,
        ["ContextMenu"] = KeyCode.VcContextMenu,

        // Phase 3 item 8 post-review fix: Soneto.App's KeyCaptureField control (a Settings-page
        // UI element) captures a hotkey by reading Avalonia's own Key enum's .ToString() value
        // directly (e.g. "LWin"/"RWin"/"Apps"), which does NOT match this project's own config
        // schema naming style above (e.g. "LeftWin"/"RightWin"/"ContextMenu") -- a real,
        // live-path bug: a user capturing the Windows key or the context-menu key as their
        // hotkey via Settings would silently save a value that throws ArgumentException the
        // next time SessionController/PipelineHost builds a real HotkeyBinding from it,
        // breaking dictation with no error shown at save time. Rather than change
        // KeyCaptureField's output format (Ctrl/Shift/Alt/CapsLock already happen to work
        // because Avalonia's own names for those overlap with aliases already registered
        // above), the cheaper, more consistent fix is extending this existing alias table --
        // exactly the same pattern already used for "RightCtrl"/"LeftCtrl" as compatibility
        // bridges alongside "RightControl"/"LeftControl".
        ["LWin"] = KeyCode.VcLeftMeta,
        ["RWin"] = KeyCode.VcRightMeta,
        ["Apps"] = KeyCode.VcContextMenu,
    };

    /// <summary>
    /// Resolves a config key string to a SharpHook <see cref="KeyCode"/>. Tries the
    /// friendly alias table first, then falls back to parsing the string directly as a
    /// <see cref="KeyCode"/> name (with or without the "Vc" prefix SharpHook uses, e.g.
    /// "F13" or "VcF13"), so any SharpHook key not covered by the alias table above still
    /// works without a code change.
    /// </summary>
    public static KeyCode ToKeyCode(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Hotkey key must not be empty.", nameof(key));

        if (Aliases.TryGetValue(key, out var mapped))
            return mapped;

        if (Enum.TryParse<KeyCode>(key, ignoreCase: true, out var parsedDirect))
            return parsedDirect;

        if (Enum.TryParse<KeyCode>("Vc" + key, ignoreCase: true, out var parsedWithPrefix))
            return parsedWithPrefix;

        throw new ArgumentException(
            $"Unrecognized hotkey key '{key}'. Known aliases: {string.Join(", ", Aliases.Keys)}. "
            + "Also accepts a SharpHook KeyCode name directly, with or without the 'Vc' "
            + "prefix (e.g. 'F13' or 'VcF13').",
            nameof(key));
    }
}
