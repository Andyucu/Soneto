using System.Runtime.InteropServices;

namespace s3_hotkey_win;

/// <summary>
/// Minimal Win32 P/Invoke surface for reading physical modifier-key state.
/// This is the exact pattern §1.8 of the implementation plan uses for the
/// modifier sanitiser (step 6): GetAsyncKeyState reads *physical* key state
/// directly from the hardware input queue, independent of what SharpHook's
/// hook reports and independent of which window has focus.
/// </summary>
internal static class NativeMethods
{
    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int vKey);

    // Exactly the set enumerated in the plan's §1.8 pseudocode.
    internal const int VK_SHIFT = 0x10;
    internal const int VK_CONTROL = 0x11;
    internal const int VK_MENU = 0x12; // Alt
    internal const int VK_LWIN = 0x5B;
    internal const int VK_RWIN = 0x5C;

    // Left/right-specific variants, useful for diagnostics beyond what §1.8
    // strictly asks for (helps distinguish "which Ctrl" during self-tests,
    // since our own trigger key is Right Ctrl and would otherwise show up
    // as "Control held" during its own press).
    internal const int VK_LSHIFT = 0xA0;
    internal const int VK_RSHIFT = 0xA1;
    internal const int VK_LCONTROL = 0xA2;
    internal const int VK_RCONTROL = 0xA3;
    internal const int VK_LMENU = 0xA4;
    internal const int VK_RMENU = 0xA5;

    /// <summary>True if the high-order bit is set, i.e. the key is currently physically down.</summary>
    internal static bool IsDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;
}

/// <summary>
/// Snapshot of which modifiers are physically held, read via GetAsyncKeyState.
/// Used both for diagnostic logging (S3) and as the direct precursor to the
/// §1.8 modifier sanitiser that will run before every injected paste chord.
///
/// IMPORTANT finding from code review (see README "Held-modifier reading"
/// section for the full writeup): since our trigger key is Right Ctrl,
/// generic <c>VK_CONTROL</c> (which does not distinguish left/right) will
/// ALWAYS read "held" during and immediately after a trigger press, purely
/// because the trigger itself is a Ctrl key. That is fine for the trigger's
/// own DOWN/UP detection (handled separately by the hook callback's KeyCode
/// check, not by this snapshot), but it is the WRONG signal for §1.8's
/// sanitiser, which needs to know whether the user's *other* hand is
/// genuinely holding Ctrl, independent of the trigger being down. So
/// <see cref="Control"/> here is deliberately read from <c>VK_LCONTROL</c>
/// (left Ctrl only), not generic <c>VK_CONTROL</c> — this is the field
/// §1.8's sanitiser should key off. <see cref="GenericControlHeld"/> is kept
/// alongside, clearly labeled, purely as a diagnostic to demonstrate the
/// ambiguity (see <c>SelfTest.RunModifierReadTest</c>) and should NOT be used
/// by any consumer that needs to distinguish the trigger from a real held
/// Ctrl.
/// </summary>
internal sealed record ModifierSnapshot(
    bool Shift,
    bool Control,
    bool Alt,
    bool LeftWin,
    bool RightWin,
    bool GenericControlHeld)
{
    internal static ModifierSnapshot Read() => new(
        Shift: NativeMethods.IsDown(NativeMethods.VK_SHIFT),
        Control: NativeMethods.IsDown(NativeMethods.VK_LCONTROL), // left-only: see class doc re: VK_CONTROL/trigger ambiguity
        Alt: NativeMethods.IsDown(NativeMethods.VK_MENU),
        LeftWin: NativeMethods.IsDown(NativeMethods.VK_LWIN),
        RightWin: NativeMethods.IsDown(NativeMethods.VK_RWIN),
        GenericControlHeld: NativeMethods.IsDown(NativeMethods.VK_CONTROL)); // diagnostic only, ambiguous with trigger — do not use for §1.8

    public override string ToString()
    {
        var held = new List<string>();
        if (Shift) held.Add("Shift");
        if (Control) held.Add("Control(L)");
        if (Alt) held.Add("Alt");
        if (LeftWin) held.Add("LWin");
        if (RightWin) held.Add("RWin");
        return held.Count == 0 ? "(none)" : string.Join("+", held);
    }
}
