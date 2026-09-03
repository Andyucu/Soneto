using System.Runtime.InteropServices;
using System.Text;

namespace s4_inject_win;

/// <summary>
/// Win32 P/Invoke surface for the §1.8 injection algorithm: foreground-window
/// capture, clipboard (raw API, not System.Windows.Forms.Clipboard -- we need
/// direct control over formats, retry timing and the sequence number), and
/// SendInput for the paste chord and modifier sanitiser.
/// </summary>
internal static class NativeMethods
{
    // ---- physical key state (copied from spikes/s3-hotkey-win/NativeMethods.cs;
    // spikes are independent/throwaway, so this is a deliberate copy, not a
    // reference, per the task instructions) ----
    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int vKey);

    internal const int VK_SHIFT = 0x10;
    internal const int VK_CONTROL = 0x11;
    internal const int VK_MENU = 0x12; // Alt
    internal const int VK_LWIN = 0x5B;
    internal const int VK_RWIN = 0x5C;
    internal const int VK_LSHIFT = 0xA0;
    internal const int VK_RSHIFT = 0xA1;
    internal const int VK_LCONTROL = 0xA2;
    internal const int VK_RCONTROL = 0xA3;
    internal const int VK_LMENU = 0xA4;
    internal const int VK_RMENU = 0xA5;
    internal const int VK_V = 0x56;

    internal static bool IsDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    // ---- foreground window ----
    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    internal static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

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

    [DllImport("user32.dll")]
    internal static extern uint EnumClipboardFormats(uint format);

    internal const uint CF_TEXT = 1;
    internal const uint CF_BITMAP = 2;
    internal const uint CF_DIB = 8;
    internal const uint CF_UNICODETEXT = 13;
    internal const uint CF_HDROP = 15;
    internal const uint CF_LOCALE = 16;
    internal const uint CF_DIBV5 = 17;
    internal const uint CF_OEMTEXT = 7;

    // ---- global memory (clipboard payloads must live in movable global memory) ----
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    internal static extern UIntPtr GlobalSize(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalFree(IntPtr hMem);

    internal const uint GMEM_MOVEABLE = 0x0002;
    internal const uint GMEM_ZEROINIT = 0x0040;

    // ---- SendInput ----
    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    // IMPORTANT: the union must include MOUSEINPUT (or equivalent padding), not
    // just KEYBDINPUT. On x64, MOUSEINPUT (32 bytes) is larger than KEYBDINPUT
    // (24 bytes), so the real native INPUT union is sized off MOUSEINPUT --
    // making INPUT 40 bytes total (4 + 4 padding + 32), not 32. A union
    // defined with only KEYBDINPUT marshals to a 32-byte INPUT, and SendInput
    // rejects the mismatched cbSize with ERROR_INVALID_PARAMETER (87). Found
    // by direct reproduction in this spike (SendInput returned 0 with
    // GetLastError()=87 for every call until this fix).
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

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    internal static extern uint MapVirtualKey(uint uCode, uint uMapType);

    internal const uint MAPVK_VK_TO_VSC = 0;
}
