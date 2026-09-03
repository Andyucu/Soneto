using System.Runtime.InteropServices;

namespace Soneto.Platform.Windows.Interop;

/// <summary>
/// Win32 P/Invoke surface for the plan §1.8 text-injection algorithm: foreground-window
/// capture/validation, raw clipboard access (not <c>System.Windows.Forms.Clipboard</c> --
/// direct control over format inspection, retry timing and the sequence number is required
/// by the design, and the raw API has no STA-thread requirement, unlike WinForms'
/// wrapper), and <c>SendInput</c> for the paste chord. Ported from
/// <c>spikes/s4-inject-win/NativeMethods.cs</c> (see that spike's README for what was
/// already validated, including the "the union must be sized off MOUSEINPUT, not just
/// KEYBDINPUT, or SendInput rejects every call with ERROR_INVALID_PARAMETER (87)" finding
/// baked into <see cref="InputUnion"/> below).
///
/// <para>
/// Kept in a separate file from <c>Interop/NativeMethods.cs</c> (item 6's
/// <c>GetAsyncKeyState</c>/VK-constant surface for the hotkey modifier reader) rather than
/// merged into one class -- the two surfaces serve different work items (6 vs. 7) and this
/// keeps each file's diff/history focused. Overlapping VK constants both files need
/// (<c>VK_LSHIFT</c>, <c>VK_LCONTROL</c>, etc.) are NOT duplicated here: <c>WindowsTextInjector</c>
/// reuses <c>Interop.NativeMethods</c>'s existing constants for all VK codes it needs,
/// including the one new constant this item required (<c>VK_V</c>, added directly to
/// <c>NativeMethods.cs</c> rather than duplicated here, since that file is already this
/// project's one home for VK_* constants).
/// </para>
/// </summary>
internal static class InjectionNativeMethods
{
    // ---- foreground window ----
    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern bool IsWindow(IntPtr hWnd);

    // Phase 4 item 2 (§4.4): resolves the process ID owning a window handle, the first step of
    // per-app override resolution (WindowsTextInjector.TryGetProcessExecutableName combines
    // this with System.Diagnostics.Process.GetProcessById -- see that method's doc comment for
    // why that combination was chosen over QueryFullProcessImageName).
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // ---- clipboard ----
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll")]
    internal static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll")]
    internal static extern int GetClipboardSequenceNumber();

    // Item 7c: enumerates every format currently on the clipboard (loop starting from 0
    // until it returns 0), used by ClipboardManager.Save's "text family" allow-list check --
    // see that method's doc comment for why a naive "anything other than CF_UNICODETEXT is
    // non-text" check false-positives on Windows' own auto-synthesized CF_LOCALE/CF_OEMTEXT
    // companions.
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint EnumClipboardFormats(uint format);

    internal const uint CF_TEXT = 1;
    internal const uint CF_UNICODETEXT = 13;
    internal const uint CF_LOCALE = 16;
    internal const uint CF_OEMTEXT = 7;

    // ---- global memory (clipboard payloads must live in movable global memory) ----
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalFree(IntPtr hMem);

    internal const uint GMEM_MOVEABLE = 0x0002;
    internal const uint GMEM_ZEROINIT = 0x0040;

    // ---- SendInput ----
    // (VK_V lives in Interop.NativeMethods, see class doc comment.)

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    // See the class doc comment: the union MUST be sized off MOUSEINPUT (32 bytes), not just
    // KEYBDINPUT (24 bytes), or SendInput rejects every call with ERROR_INVALID_PARAMETER
    // (87) -- found by direct reproduction in spikes/s4-inject-win.
    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    internal const uint INPUT_KEYBOARD = 1;
    internal const uint KEYEVENTF_KEYUP = 0x0002;

    // Item 10 (§1.12 "clipboard set fails -> UnicodeSynth fallback" row): KEYEVENTF_UNICODE
    // lets SendInput deliver an arbitrary UTF-16 code unit directly via wScan, without needing
    // a virtual-key code (wVk is set to 0) or a scan-code-to-VK mapping the way the paste
    // chord's real key presses need -- see WindowsTextInjector.BuildUnicodeSynthBatches.
    internal const uint KEYEVENTF_UNICODE = 0x0004;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    internal static extern uint MapVirtualKey(uint uCode, uint uMapType);

    internal const uint MAPVK_VK_TO_VSC = 0;
}
