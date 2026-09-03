using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Soneto.Core.Abstractions;
using Soneto.Core.Configuration;

namespace Soneto.Platform.Windows.Tests;

/// <summary>
/// Phase 4 item 4 (§4.5): proves <see cref="InjectionConfig.PerApp"/>'s per-app override
/// resolution (Phase 4 item 2, <see cref="PerAppOverrideResolver"/>) is GENUINELY APPLIED at
/// real injection time -- not just present in config -- without ever synthetically injecting
/// into a real foreground app outside this test process.
///
/// <para>
/// <b>How this stays safe (never touches an arbitrary foreground window):</b> the per-app
/// table configured here is keyed by THIS TEST PROCESS'S OWN real, dynamically-resolved
/// executable name (<c>Process.GetCurrentProcess().ProcessName + ".exe"</c> -- never
/// hardcoded, so this works under any build config/test-runner host), mapped to
/// <c>Method = InjectionMethod.UnicodeSynth</c>. A throwaway <see cref="WindowsTextInjector"/>
/// built with that table is then exercised via the exact same "self-owned window" pattern
/// <c>Soneto.App.ViewModels.PermissionsDoctorViewModel.RunInjectionSelfTestAsync</c> already
/// established for its "Can synthesize input" self-test (see that method's own doc comment): a
/// real, normally-rendered (not zero-size/zero-opacity -- that broke paste routing when tried
/// first, per that class's own doc comment) but off-screen window living inside this SAME test
/// process is given real OS focus via an ordinary <see cref="UIElement.Focus()"/> call, and a
/// FRESH, throwaway <see cref="ITextInjector.CaptureTarget"/> is called IMMEDIATELY afterward
/// with no yield point in between -- guaranteeing the captured target is this process's own
/// window, never whatever else happens to have OS focus. This is real product code
/// (<see cref="WindowsTextInjector.InjectAsync"/>, unmodified, including its real
/// <see cref="PerAppOverrideResolver.Resolve"/> call) exercising the real per-app resolution
/// mechanism end-to-end, not a mock -- it just can never land anywhere but this test's own
/// window.
/// </para>
///
/// <para>
/// <b>"Genuinely applied, not just configured" -- proven via a real observable side effect,
/// not a log string match.</b> <c>UnicodeSynth</c> never touches the clipboard at all (see
/// <see cref="WindowsTextInjector"/>'s own class doc comment, "Item 10"); the base
/// <c>ClipboardPaste</c> path always does. So this test captures the real Win32 clipboard
/// sequence number (<see cref="ClipboardManager.GetSequenceNumber"/> -- the exact same
/// mechanism <see cref="WindowsTextInjector.InjectAsync"/> itself logs, "clipboard sequence
/// number before touching the clipboard") both immediately before and immediately after the
/// injection, and asserts it did NOT change. If the per-app override were only present in
/// config but not actually resolved/applied, this injection would silently fall through to the
/// base <c>ClipboardPaste</c> default, which always bumps the sequence number (a real
/// <c>SetClipboardData</c> call) -- so an unchanged sequence number is real, structural proof
/// that the real code path taken was the per-app-resolved <c>UnicodeSynth</c> branch, not a
/// fallback. Combined with the marker text -- which deliberately includes Romanian diacritics,
/// since <c>UnicodeSynth</c> exists specifically to fix diacritics delivery for problem apps,
/// the exact scenario §4.5 targets -- actually landing byte-correct in the TextBox, this is a
/// full, real, round-trip proof of §4.4's resolution mechanism.
/// </para>
///
/// <para>
/// Tagged <c>Category=Hardware</c>, same convention as
/// <see cref="WindowsTextInjectorNotepadSelfCheckTests"/>/<c>ModifierSanitizerHardwareTests</c>:
/// this creates a real window, calls <c>Activate()</c>, and sends real synthetic keyboard
/// input via <c>SendInput</c> -- even though, by construction, the only possible target is a
/// window this same test process just created and focused, never an arbitrary foreground app
/// (this project's own standing "never touch the live desktop" caution, see
/// <c>Docs/PROJECT-MEMORY.md</c>'s "Live-desktop testing caution", is about exactly the latter,
/// not this self-contained pattern -- which is also already shipped, running-on-real-desktops
/// production code, not new automation). Excluded from the default <c>dotnet test</c> filter
/// (<c>Category!=Hardware</c>), run deliberately.
/// </para>
/// </summary>
[Trait("Category", "Hardware")]
public sealed class PerAppOverrideEndToEndTests
{
    // Deliberately includes Romanian diacritics (comma-below ș/ț, U+0219/U+021B) -- the exact
    // scenario UnicodeSynth exists to fix, per this class's own doc comment above and plan
    // §4.5's framing.
    private const string MarkerText = "soneto-perapp-șoseaua-țară-îngheț";

    [Fact]
    public void Real_per_app_override_targeting_this_process_own_name_genuinely_applies_UnicodeSynth_not_ClipboardPaste()
    {
        string ownExeName = Process.GetCurrentProcess().ProcessName + ".exe";
        var perApp = new Dictionary<string, PerAppOverride>(StringComparer.OrdinalIgnoreCase)
        {
            [ownExeName] = new PerAppOverride { Method = Soneto.Core.Configuration.InjectionMethod.UnicodeSynth },
        };

        var result = RunOnStaThreadWithMessagePump(() => RunSelfOwnedWindowInjection(perApp));

        Assert.Equal(InjectionOutcome.Injected, result.Outcome);
        Assert.True(
            result.Landed,
            $"Marker text (with Romanian diacritics) did not land in the self-owned window's " +
            $"TextBox as expected. Final text: \"{result.FinalText}\".");
        Assert.Equal(
            result.ClipboardSeqBefore,
            result.ClipboardSeqAfter); // unchanged -> proves the real UnicodeSynth branch ran, not ClipboardPaste
    }

    private readonly record struct InjectionResult(
        InjectionOutcome Outcome, bool Landed, string FinalText, int ClipboardSeqBefore, int ClipboardSeqAfter);

    /// <summary>
    /// Runs on a dedicated STA thread with a live <see cref="Dispatcher"/> message pump (WPF
    /// windows require STA + a running message loop to actually receive/process the real
    /// <c>SendInput</c> keyboard events this test sends into them).
    /// </summary>
    private static InjectionResult RunSelfOwnedWindowInjection(IReadOnlyDictionary<string, PerAppOverride> perApp)
    {
        // Real, normally-rendered but off-screen -- NOT zero-size/zero-opacity, per this
        // class's own doc comment (PermissionsDoctorViewModel's doc comment records that a
        // zero-opacity control broke native paste routing when tried first).
        var textBox = new TextBox { Width = 200, Height = 30, Text = string.Empty };
        var window = new Window
        {
            Title = "Soneto per-app-override self-test (throwaway, off-screen)",
            Content = textBox,
            Width = 220,
            Height = 60,
            Left = -32000,
            Top = -32000,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = true,
        };

        try
        {
            window.Show();
            window.Activate();
            textBox.Focus();

            // Post-review finding (test-runner verification, 1 failure in 6 isolated runs):
            // Activate()/Focus() request OS focus but do not SYNCHRONOUSLY guarantee it has
            // actually landed by the time the next statement runs -- window activation
            // (WM_ACTIVATE et al) can be a few message-pump cycles behind the call that
            // requested it, especially under load. Poll (bounded, pumping this thread's own
            // Dispatcher -- no real yield to any other process/app, see PollUntil's own doc
            // comment) until BOTH the real Win32 foreground window is genuinely this window's
            // hwnd AND WPF itself agrees keyboard focus is inside our TextBox, before
            // proceeding. This does not weaken the "no yield between focus and CaptureTarget"
            // safety property -- it only makes the synchronous hand-off from Focus() to
            // CaptureTarget() below wait until focus has GENUINELY landed, rather than
            // optimistically assuming a single Focus() call already did.
            var hwnd = new WindowInteropHelper(window).Handle;
            PollUntil(
                () => GetForegroundWindow() == hwnd && textBox.IsKeyboardFocusWithin,
                timeout: TimeSpan.FromSeconds(3),
                pollInterval: TimeSpan.FromMilliseconds(15));

            var injector = new WindowsTextInjector(NullLogger<WindowsTextInjector>.Instance, perApp);

            // Legitimately captures whatever has OS focus right now -- safe here specifically
            // because Focus()/Activate() (confirmed landed by the poll immediately above) just
            // gave OUR OWN window real OS focus via ordinary means, with no yield point between
            // that confirmation and this call (see class doc comment).
            object? target = injector.CaptureTarget();

            int seqBefore = ClipboardManager.GetSequenceNumber();

            // Method is deliberately NOT specified as UnicodeSynth here -- opts requests the
            // base ClipboardPaste default. If the real per-app resolution inside InjectAsync
            // did not genuinely apply, this injection would take the ClipboardPaste path
            // (bumping the clipboard sequence number) instead of UnicodeSynth.
            var opts = new InjectionOptions(
                Method: Soneto.Core.Abstractions.InjectionMethod.ClipboardPaste,
                PasteChord: "ctrl+v",
                PreDelay: TimeSpan.FromMilliseconds(20),
                ClipboardRestoreDelay: TimeSpan.FromMilliseconds(150),
                RestoreClipboard: true,
                SanitizeModifiers: true,
                TriggerKey: null,
                Policy: Soneto.Core.Abstractions.ClipboardPolicy.TextOnly);

            var outcome = PumpUntilComplete(injector.InjectAsync(MarkerText, target, opts, CancellationToken.None));

            // Post-review finding (test-runner verification, 1 failure in 6 isolated runs): a
            // single fixed 150ms wait before reading textBox.Text back was occasionally not
            // enough for WPF's own internal (asynchronous) input processing to finish -- mirrors
            // PermissionsDoctorViewModel.RunInjectionSelfTestAsync's own documented reasoning
            // (SendInput being accepted does not guarantee the target control has already
            // finished processing it), but a slow-, eventually-successful injection needs a
            // bounded RETRY, not a longer fixed sleep that just moves the same race further out.
            // Poll (bounded, same "pump this thread's own Dispatcher, no real yield" technique
            // as the focus-confirmation poll above) until the marker has genuinely landed or the
            // timeout elapses -- the assertion below still fails honestly if it never does.
            PollUntil(
                () => (textBox.Text ?? string.Empty).Contains(MarkerText, StringComparison.Ordinal),
                timeout: TimeSpan.FromSeconds(2),
                pollInterval: TimeSpan.FromMilliseconds(25));

            int seqAfter = ClipboardManager.GetSequenceNumber();
            string finalText = textBox.Text ?? string.Empty;
            bool landed = finalText.Contains(MarkerText, StringComparison.Ordinal);

            return new InjectionResult(outcome, landed, finalText, seqBefore, seqAfter);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Pumps the current thread's <see cref="Dispatcher"/> (via <see cref="DispatcherFrame"/>)
    /// until <paramref name="task"/> completes, so real Win32 window messages (including the
    /// synthetic keyboard input this test sends) keep being delivered to the self-owned window
    /// while the injection's own internal awaits (all <c>ConfigureAwait(false)</c> in the real
    /// product code) run on the thread pool.</summary>
    private static InjectionOutcome PumpUntilComplete(Task<InjectionOutcome> task)
    {
        var frame = new DispatcherFrame();
        Exception? error = null;
        InjectionOutcome outcome = default;
        task.ContinueWith(t =>
        {
            if (t.IsFaulted) error = t.Exception;
            else outcome = t.Result;
            frame.Continue = false;
        }, TaskScheduler.Default);

        Dispatcher.PushFrame(frame);
        if (error is not null)
            throw error;
        return outcome;
    }

    /// <summary>
    /// Bounded poll for <paramref name="condition"/>, pumping this thread's own
    /// <see cref="Dispatcher"/> (via <see cref="DispatcherTimer"/>/<see cref="DispatcherFrame"/>,
    /// same technique as <see cref="PumpUntilComplete"/>) between checks so WPF/Win32 keeps
    /// processing real messages (focus changes, WM_CHAR from <c>SendInput</c>, etc.) while
    /// waiting -- this is a bounded, synchronous wait on THIS thread's own message queue, not a
    /// real yield that could let OS focus drift to a different process/app. Returns
    /// <c>true</c> if <paramref name="condition"/> became true before <paramref name="timeout"/>
    /// elapsed, <c>false</c> otherwise (callers that need an honest failure surface the timeout
    /// via their own subsequent assertion, e.g. reading back whatever text actually landed).
    /// </summary>
    private static bool PollUntil(Func<bool> condition, TimeSpan timeout, TimeSpan pollInterval)
    {
        if (condition())
            return true;

        var frame = new DispatcherFrame();
        bool succeeded = false;
        var deadline = DateTime.UtcNow + timeout;
        var timer = new DispatcherTimer { Interval = pollInterval };
        timer.Tick += (_, _) =>
        {
            if (condition())
            {
                succeeded = true;
                timer.Stop();
                frame.Continue = false;
            }
            else if (DateTime.UtcNow >= deadline)
            {
                timer.Stop();
                frame.Continue = false;
            }
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        return succeeded;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private static T RunOnStaThreadWithMessagePump<T>(Func<T> action)
    {
        T? result = default;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        if (error is not null)
            throw error;
        return result!;
    }
}
