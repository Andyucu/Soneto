using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Soneto.Core.Abstractions;

namespace Soneto.Platform.Windows.Tests;

/// <summary>
/// Fully automated (no human required) Notepad round-trip self-check for the real
/// <see cref="WindowsTextInjector"/>: launches Notepad, injects the exact plan §1.8 / S4 test
/// string via the real clipboard-paste algorithm, reads the content back via UI Automation
/// (see <see cref="NotepadVerifier"/> -- GetWindowText/WM_GETTEXT don't work against modern
/// Notepad's UWP-hosted edit control), and asserts an exact string match plus the
/// byte-level diacritic check (comma-below ș/ț, U+0219/U+021B -- never the cedilla forms
/// U+015F/U+0163). Mirrors <c>spikes/s4-inject-win/NotepadSelfCheck.cs</c>'s already-proven
/// approach, promoted to a real xUnit test against the product implementation.
///
/// <para>
/// Tagged <c>Category=Hardware</c> per this project's convention: this launches and controls
/// a real, visible Notepad window and sends real <c>SendInput</c> keystrokes to whatever has
/// OS focus at the time (the S4 spike's own README documents a real near-miss from running
/// similar automation against a live, in-use desktop) -- not something to run unattended in
/// a default/CI suite, but genuinely valuable and automatable when run deliberately.
/// </para>
/// </summary>
[Trait("Category", "Hardware")]
public sealed class WindowsTextInjectorNotepadSelfCheckTests
{
    private const string TestString =
        "Ăsta e un test: șoseaua Ștefan cel Mare, țară, îngheț — 100% \"quoted\" & <tagged>.\n"
        + "Line two after a newline.";

    [Fact]
    public async Task Inject_into_real_Notepad_round_trips_exact_text_with_correct_diacritics_and_restores_clipboard()
    {
        const string originalClipboardContent = "WindowsTextInjector-selfcheck-original-clipboard-content";
        Assert.True(ClipboardManager.SetUnicodeTextWithRetry(originalClipboardContent, attempts: 3, delay: TimeSpan.FromMilliseconds(20)));

        using var proc = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true })!;
        try
        {
            var hWnd = NotepadVerifier.WaitForForegroundWindowTitled("Notepad");
            // Post-review fix (flaky-test finding, ~2/3 pass rate observed): a fixed
            // Thread.Sleep(800) guess for "give the edit surface time to become interactive"
            // was flaky. Poll for actual readiness instead.
            NotepadVerifier.WaitForEditSurfaceReady(hWnd);
            NotepadVerifier.ClearDocument(hWnd, SendKey);

            var injector = new WindowsTextInjector(NullLogger<WindowsTextInjector>.Instance);
            var opts = new InjectionOptions(
                InjectionMethod.ClipboardPaste, "ctrl+v",
                PreDelay: TimeSpan.FromMilliseconds(20),
                ClipboardRestoreDelay: TimeSpan.FromMilliseconds(150),
                RestoreClipboard: true);

            var outcome = await injector.InjectAsync(TestString, hWnd, opts, CancellationToken.None);
            Assert.Equal(InjectionOutcome.Injected, outcome);

            var readBack = NotepadVerifier.ReadTextStable(hWnd);
            Assert.Equal(Normalize(TestString), Normalize(readBack));

            // Byte-level diacritic check, matching the S4 spike's own rigor: comma-below
            // ș/ț (U+0219/U+021B) must be present in whichever case the test string actually
            // uses (lowercase ț/uppercase Ș, per TestString above -- it has no uppercase Ț);
            // cedilla ş/ţ (U+015F/U+0163) must NOT appear in either case.
            Assert.True(readBack.Contains('ș') || readBack.Contains('Ș'), "Expected comma-below ș/Ș to be present.");
            Assert.True(readBack.Contains('ț') || readBack.Contains('Ț'), "Expected comma-below ț/Ț to be present.");
            Assert.True(readBack.Contains('ă') || readBack.Contains('Ă'), "Expected ă/Ă to be present.");
            Assert.DoesNotContain('ş', readBack);
            Assert.DoesNotContain('Ş', readBack);
            Assert.DoesNotContain('ţ', readBack);
            Assert.DoesNotContain('Ţ', readBack);
            Assert.Equal(0x0219, 'ș');
            Assert.Equal(0x021B, 'ț');

            var afterBackup = ClipboardManager.Save();
            Assert.True(afterBackup.HadUnicodeText);
            Assert.Equal(originalClipboardContent, afterBackup.UnicodeText);
        }
        finally
        {
            try { proc.Kill(); } catch { /* best-effort cleanup */ }
        }
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n', '\r', ' ');

    // ---- minimal, test-local SendInput helper (duplicated from the product's own
    // WindowsTextInjector.SendKey rather than made internal-visible, mirroring this
    // project's precedent of spikes intentionally duplicating small native helpers rather
    // than sharing them with product code) ----
    private static void SendKey(int vk, bool keyUp)
    {
        ushort scan = (ushort)MapVirtualKey((uint)vk, 0);
        var input = new INPUT
        {
            type = 1,
            U = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)vk, wScan = scan, dwFlags = keyUp ? 0x0002u : 0u, time = 0, dwExtraInfo = IntPtr.Zero } }
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
}
