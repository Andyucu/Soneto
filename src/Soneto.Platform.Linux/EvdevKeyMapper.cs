namespace Soneto.Platform.Linux;

/// <summary>
/// Maps <see cref="Soneto.Core.Abstractions.HotkeyBinding.Key"/> config strings (plan
/// §1.10's schema, e.g. <c>"RightControl"</c>) onto raw evdev <c>KEY_*</c> scancodes
/// (<c>linux/input-event-codes.h</c>). Linux-side twin of
/// <c>Soneto.Platform.Windows.HotkeyKeyMapper</c> -- same alias-table shape/naming,
/// evdev codes instead of SharpHook <c>KeyCode</c>/VK codes.
///
/// <para>
/// The evdev code values below are copied directly from the upstream kernel header
/// (<c>linux/input-event-codes.h</c>), which has been stable/ABI-frozen for decades --
/// this is a well-known, publicly documented numbering, not something that needs a real
/// kernel to confirm. What CANNOT be confirmed from this Windows session is that a real
/// evdev device actually reports these codes for the corresponding physical key (see
/// <see cref="KeyboardDeviceFilter"/> and <c>LinuxHotkeySource</c>'s doc comments for the
/// broader "cannot verify on real hardware" caveat).
/// </para>
/// </summary>
public static class EvdevKeyMapper
{
    /// <summary>Friendly aliases matching the plan's config schema naming style, same
    /// alias names as <c>Soneto.Platform.Windows.HotkeyKeyMapper</c> where they overlap.</summary>
    private static readonly Dictionary<string, ushort> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RightControl"] = EvdevKeyCodes.KEY_RIGHTCTRL,
        ["RightCtrl"] = EvdevKeyCodes.KEY_RIGHTCTRL,
        ["LeftControl"] = EvdevKeyCodes.KEY_LEFTCTRL,
        ["LeftCtrl"] = EvdevKeyCodes.KEY_LEFTCTRL,
        ["RightShift"] = EvdevKeyCodes.KEY_RIGHTSHIFT,
        ["LeftShift"] = EvdevKeyCodes.KEY_LEFTSHIFT,
        ["RightAlt"] = EvdevKeyCodes.KEY_RIGHTALT,
        ["RightMenu"] = EvdevKeyCodes.KEY_RIGHTALT,
        ["LeftAlt"] = EvdevKeyCodes.KEY_LEFTALT,
        ["LeftMenu"] = EvdevKeyCodes.KEY_LEFTALT,
        ["RightWin"] = EvdevKeyCodes.KEY_RIGHTMETA,
        ["RightMeta"] = EvdevKeyCodes.KEY_RIGHTMETA,
        ["LeftWin"] = EvdevKeyCodes.KEY_LEFTMETA,
        ["LeftMeta"] = EvdevKeyCodes.KEY_LEFTMETA,
        ["CapsLock"] = EvdevKeyCodes.KEY_CAPSLOCK,
        ["ContextMenu"] = EvdevKeyCodes.KEY_COMPOSE,

        // Phase 3 item 8 post-review fix -- see the matching comment/aliases in
        // Soneto.Platform.Windows.HotkeyKeyMapper for the full story: Soneto.App's
        // KeyCaptureField Settings-page control captures Avalonia's own Key enum names
        // verbatim ("LWin"/"RWin"/"Apps"), which don't match this project's config-schema
        // naming ("LeftWin"/"RightWin"/"ContextMenu") -- kept in sync with the Windows
        // mapper's alias table so a hotkey captured via Settings resolves identically on
        // both platforms.
        ["LWin"] = EvdevKeyCodes.KEY_LEFTMETA,
        ["RWin"] = EvdevKeyCodes.KEY_RIGHTMETA,
        ["Apps"] = EvdevKeyCodes.KEY_COMPOSE,
    };

    /// <summary>
    /// Resolves a config key string to an evdev scancode. Tries the friendly alias table
    /// first, then falls back to parsing the string directly as a <c>KEY_*</c> constant
    /// name declared on <see cref="EvdevKeyCodes"/> (with or without the "KEY_" prefix,
    /// mirroring <c>HotkeyKeyMapper.ToKeyCode</c>'s "Vc" prefix fallback), so any evdev key
    /// not covered by the alias table still works without a code change.
    /// </summary>
    public static ushort ToKeyCode(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Hotkey key must not be empty.", nameof(key));

        if (Aliases.TryGetValue(key, out var mapped))
            return mapped;

        string normalized = key.StartsWith("KEY_", StringComparison.OrdinalIgnoreCase)
            ? key.ToUpperInvariant()
            : "KEY_" + key.ToUpperInvariant();

        if (EvdevKeyCodes.ByName.TryGetValue(normalized, out var byName))
            return byName;

        throw new ArgumentException(
            $"Unrecognized hotkey key '{key}'. Known aliases: {string.Join(", ", Aliases.Keys)}. "
            + "Also accepts an evdev KEY_* constant name directly, with or without the 'KEY_' "
            + "prefix (e.g. 'A' or 'KEY_A').",
            nameof(key));
    }
}

/// <summary>
/// Raw evdev scancodes from <c>linux/input-event-codes.h</c>, restricted to what this
/// project needs: the modifier keys that are valid hotkey trigger aliases, plus the
/// standard QWERTY alphanumeric row used by <see cref="KeyboardDeviceFilter"/> to
/// distinguish a real keyboard node from a power button / consumer-control node (both of
/// which also claim <c>EV_KEY</c>).
/// </summary>
public static class EvdevKeyCodes
{
    public const ushort KEY_ESC = 1;
    public const ushort KEY_1 = 2;
    public const ushort KEY_Q = 16;
    public const ushort KEY_W = 17;
    public const ushort KEY_E = 18;
    public const ushort KEY_R = 19;
    public const ushort KEY_T = 20;
    public const ushort KEY_Y = 21;
    public const ushort KEY_U = 22;
    public const ushort KEY_I = 23;
    public const ushort KEY_O = 24;
    public const ushort KEY_P = 25;
    public const ushort KEY_A = 30;
    public const ushort KEY_S = 31;
    public const ushort KEY_D = 32;
    public const ushort KEY_F = 33;
    public const ushort KEY_G = 34;
    public const ushort KEY_H = 35;
    public const ushort KEY_J = 36;
    public const ushort KEY_K = 37;
    public const ushort KEY_L = 38;
    public const ushort KEY_LEFTSHIFT = 42;
    public const ushort KEY_Z = 44;
    public const ushort KEY_X = 45;
    public const ushort KEY_C = 46;
    public const ushort KEY_V = 47;
    public const ushort KEY_B = 48;
    public const ushort KEY_N = 49;
    public const ushort KEY_M = 50;
    public const ushort KEY_RIGHTSHIFT = 54;
    public const ushort KEY_LEFTCTRL = 29;
    public const ushort KEY_LEFTALT = 56;
    public const ushort KEY_CAPSLOCK = 58;
    public const ushort KEY_RIGHTCTRL = 97;
    public const ushort KEY_RIGHTALT = 100;
    public const ushort KEY_LEFTMETA = 125;
    public const ushort KEY_RIGHTMETA = 126;
    public const ushort KEY_COMPOSE = 127;
    public const ushort KEY_POWER = 116;

    /// <summary>
    /// The 26 standard QWERTY alphanumeric scancodes -- what plan §1.9's multi-keyboard
    /// enumeration spec calls "standard alphanumeric scancodes (<c>KEY_A</c> through
    /// <c>KEY_Z</c>)", used by <see cref="KeyboardDeviceFilter"/> to filter out power
    /// buttons and media-key nodes that also report <c>EV_KEY</c>.
    /// </summary>
    public static readonly IReadOnlyList<ushort> AlphaKeyCodes = new[]
    {
        KEY_Q, KEY_W, KEY_E, KEY_R, KEY_T, KEY_Y, KEY_U, KEY_I, KEY_O, KEY_P,
        KEY_A, KEY_S, KEY_D, KEY_F, KEY_G, KEY_H, KEY_J, KEY_K, KEY_L,
        KEY_Z, KEY_X, KEY_C, KEY_V, KEY_B, KEY_N, KEY_M,
    };

    public static readonly IReadOnlyDictionary<string, ushort> ByName = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
    {
        ["KEY_ESC"] = KEY_ESC,
        ["KEY_Q"] = KEY_Q, ["KEY_W"] = KEY_W, ["KEY_E"] = KEY_E, ["KEY_R"] = KEY_R,
        ["KEY_T"] = KEY_T, ["KEY_Y"] = KEY_Y, ["KEY_U"] = KEY_U, ["KEY_I"] = KEY_I,
        ["KEY_O"] = KEY_O, ["KEY_P"] = KEY_P,
        ["KEY_A"] = KEY_A, ["KEY_S"] = KEY_S, ["KEY_D"] = KEY_D, ["KEY_F"] = KEY_F,
        ["KEY_G"] = KEY_G, ["KEY_H"] = KEY_H, ["KEY_J"] = KEY_J, ["KEY_K"] = KEY_K, ["KEY_L"] = KEY_L,
        ["KEY_Z"] = KEY_Z, ["KEY_X"] = KEY_X, ["KEY_C"] = KEY_C, ["KEY_V"] = KEY_V,
        ["KEY_B"] = KEY_B, ["KEY_N"] = KEY_N, ["KEY_M"] = KEY_M,
        ["KEY_LEFTSHIFT"] = KEY_LEFTSHIFT, ["KEY_RIGHTSHIFT"] = KEY_RIGHTSHIFT,
        ["KEY_LEFTCTRL"] = KEY_LEFTCTRL, ["KEY_RIGHTCTRL"] = KEY_RIGHTCTRL,
        ["KEY_LEFTALT"] = KEY_LEFTALT, ["KEY_RIGHTALT"] = KEY_RIGHTALT,
        ["KEY_LEFTMETA"] = KEY_LEFTMETA, ["KEY_RIGHTMETA"] = KEY_RIGHTMETA,
        ["KEY_CAPSLOCK"] = KEY_CAPSLOCK, ["KEY_COMPOSE"] = KEY_COMPOSE,
        ["KEY_POWER"] = KEY_POWER,
    };
}
