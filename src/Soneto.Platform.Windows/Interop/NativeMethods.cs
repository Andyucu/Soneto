using System.Runtime.InteropServices;

namespace Soneto.Platform.Windows.Interop;

/// <summary>
/// Minimal Win32 P/Invoke surface for reading physical modifier-key state, ported from
/// <c>spikes/s3-hotkey-win/NativeMethods.cs</c> (validated there via <c>self-test</c>).
/// This is the exact pattern plan §1.8 uses for the modifier sanitiser (step 6):
/// <c>GetAsyncKeyState</c> reads *physical* key state directly from the hardware input
/// queue, independent of what SharpHook's hook reports and independent of which window
/// has focus.
/// </summary>
internal static class NativeMethods
{
    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int vKey);

    // Exactly the set enumerated in plan §1.8's pseudocode.
    internal const int VK_SHIFT = 0x10;
    internal const int VK_CONTROL = 0x11;
    internal const int VK_MENU = 0x12; // Alt
    internal const int VK_LWIN = 0x5B;
    internal const int VK_RWIN = 0x5C;

    // Left/right-specific variants. VK_LCONTROL in particular is load-bearing: see
    // ModifierState's doc comment for why the generic VK_CONTROL above must not be used
    // to read "is the user holding Ctrl" when the trigger key itself is Right Ctrl.
    internal const int VK_LSHIFT = 0xA0;
    internal const int VK_RSHIFT = 0xA1;
    internal const int VK_LCONTROL = 0xA2;
    internal const int VK_RCONTROL = 0xA3;
    internal const int VK_LMENU = 0xA4;
    internal const int VK_RMENU = 0xA5;

    // Added for item 7 (ITextInjector): the 'V' key for the default ctrl+v paste chord.
    // Not a modifier, kept here rather than in InjectionNativeMethods purely because it's a
    // single VK constant and this file is already the project's one home for VK_* constants.
    internal const int VK_V = 0x56;

    /// <summary>True if the high-order bit is set, i.e. the key is currently physically down.</summary>
    internal static bool IsDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;
}

/// <summary>
/// Snapshot of which modifiers are physically held, read via <c>GetAsyncKeyState</c>.
/// Ported directly from spike S3's <c>ModifierSnapshot</c> (renamed for product use) —
/// used both by item 6's <c>--watch-hotkey</c> demo and, per this work item's explicit
/// ask, meant to be reused as-is by item 7's modifier sanitiser rather than re-derived.
///
/// <para>
/// <b>Why <see cref="Control"/> reads <c>VK_LCONTROL</c>, not generic <c>VK_CONTROL</c>:</b>
/// S3 confirmed (see <c>spikes/s3-hotkey-win/README.md</c>, "VK_CONTROL/trigger-key
/// ambiguity") that when the configured trigger is Right Ctrl, generic <c>VK_CONTROL</c>
/// (which does not distinguish left/right) always reads "held" during and immediately
/// after a trigger press or release — purely because the trigger itself is a Ctrl key,
/// not because the user is holding any other Ctrl key. That is the wrong signal for a
/// modifier sanitiser that needs to know whether the user's *other* hand is genuinely
/// holding Ctrl, independent of the trigger. <see cref="Control"/> is therefore
/// deliberately read from <c>VK_LCONTROL</c> only. <see cref="GenericControlHeld"/> is
/// kept alongside purely as a labeled diagnostic and must NOT be used by any consumer
/// that needs to distinguish the trigger from a genuinely held Ctrl.
/// </para>
/// </summary>
public sealed record ModifierState(
    bool Shift,
    bool Control,
    bool Alt,
    bool LeftWin,
    bool RightWin,
    bool GenericControlHeld)
{
    /// <summary>Reads the current physical modifier state. Safe to call from any thread
    /// (a thin wrapper over <c>GetAsyncKeyState</c>), but must never be called from the
    /// SharpHook hook callback thread — see <see cref="WindowsHotkeySource"/>'s doc
    /// comment on callback-thread discipline.</summary>
    public static ModifierState Read() => new(
        Shift: NativeMethods.IsDown(NativeMethods.VK_SHIFT),
        Control: NativeMethods.IsDown(NativeMethods.VK_LCONTROL), // left-only: see class doc re: VK_CONTROL/trigger ambiguity
        Alt: NativeMethods.IsDown(NativeMethods.VK_MENU),
        LeftWin: NativeMethods.IsDown(NativeMethods.VK_LWIN),
        RightWin: NativeMethods.IsDown(NativeMethods.VK_RWIN),
        GenericControlHeld: NativeMethods.IsDown(NativeMethods.VK_CONTROL)); // diagnostic only, ambiguous with trigger

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
