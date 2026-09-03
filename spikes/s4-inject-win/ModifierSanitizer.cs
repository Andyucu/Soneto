namespace s4_inject_win;

/// <summary>
/// §1.8 step 6/9: suppress held modifiers before the paste chord, restore only
/// what's still physically held afterward.
///
/// The Control read here is deliberately <c>VK_LCONTROL</c>, not generic
/// <c>VK_CONTROL</c> -- carried over from spikes/s3-hotkey-win's finding (see
/// that spike's README, "Held-modifier reading" / "VK_CONTROL/trigger-key
/// ambiguity"): if the daemon's hotkey trigger is Right Ctrl, generic
/// VK_CONTROL is ambiguous with the trigger key itself (it reads "held" for
/// as long as the trigger is down, regardless of whether the user's other
/// hand is on a real Ctrl key). S4 doesn't have a trigger key of its own
/// (it's invoked directly from the CLI, not from a hotkey), but the
/// sanitiser being built here is the literal §1.8 code this spike is meant
/// to validate for Phase 1's real hotkey-triggered injector, so it must use
/// the same left/right-specific reads Phase 1 will need. Generic VK_CONTROL
/// is intentionally not used anywhere in this class.
/// </summary>
internal static class ModifierSanitizer
{
    // The exact set enumerated in §1.8's pseudocode, keyed off left/right-specific
    // virtual-key codes so a right-side trigger key elsewhere in the app can never
    // be confused with a genuinely-held modifier.
    private static readonly (string Name, int Vk)[] Modifiers =
    [
        ("Shift", NativeMethods.VK_LSHIFT),
        ("Shift(R)", NativeMethods.VK_RSHIFT),
        ("Alt", NativeMethods.VK_LMENU),
        ("Alt(R)", NativeMethods.VK_RMENU),
        ("Control", NativeMethods.VK_LCONTROL),
        ("Control(R)", NativeMethods.VK_RCONTROL),
        ("LWin", NativeMethods.VK_LWIN),
        ("RWin", NativeMethods.VK_RWIN),
    ];

    /// <summary>Step 6: suppress every physically-held modifier, return what was held so step 9 can re-check it.</summary>
    internal static List<(string Name, int Vk)> Suppress(Action<string>? log = null)
    {
        var held = new List<(string Name, int Vk)>();
        foreach (var (name, vk) in Modifiers)
        {
            if (NativeMethods.IsDown(vk))
            {
                held.Add((name, vk));
                SendKeyUp(vk);
                log?.Invoke($"sanitizer: suppressed held modifier {name}");
            }
        }
        return held;
    }

    /// <summary>
    /// Step 9: re-check physical state before restoring -- do NOT restore
    /// blindly. The user may have released the modifier during the ~200ms
    /// paste sequence; restoring unconditionally would leave a stuck
    /// modifier (§1.8's own warning, quoted verbatim in the plan).
    /// </summary>
    internal static void Restore(List<(string Name, int Vk)> held, Action<string>? log = null)
    {
        foreach (var (name, vk) in held)
        {
            if (NativeMethods.IsDown(vk))
            {
                SendKeyDown(vk);
                log?.Invoke($"sanitizer: restored still-held modifier {name}");
            }
            else
            {
                log?.Invoke($"sanitizer: NOT restoring {name} -- released during injection (avoiding stuck modifier)");
            }
        }
    }

    internal static void SendKeyDown(int vk) => SendKey(vk, keyUp: false);
    internal static void SendKeyUp(int vk) => SendKey(vk, keyUp: true);

    private static void SendKey(int vk, bool keyUp)
    {
        // Include the hardware scan code (via MapVirtualKey), not just the VK
        // code -- some apps (observed: modern Windows 11 Notepad) rely on raw
        // input / low-level hook paths that expect a populated scan code and
        // silently ignore a SendInput event that carries wScan=0.
        ushort scan = (ushort)NativeMethods.MapVirtualKey((uint)vk, NativeMethods.MAPVK_VK_TO_VSC);
        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = (ushort)vk,
                    wScan = scan,
                    dwFlags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                }
            }
        };
        uint sent = NativeMethods.SendInput(1, [input], System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent != 1)
        {
            Console.WriteLine($"SendInput FAILED for vk=0x{vk:X} keyUp={keyUp}: sent={sent}, error={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        }
    }
}
