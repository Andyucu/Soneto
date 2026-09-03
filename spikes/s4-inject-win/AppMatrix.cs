using System.IO;

namespace s4_inject_win;

/// <summary>
/// Runs one profile from AppLauncher's per-app matrix: launch, focus, inject
/// the S4 test string, screenshot the result. Screenshotting is this spike's
/// way of self-verifying apps it cannot script a text-content read-back
/// against (see README "Verification methodology") -- an actual bitmap of
/// the actual screen, not a fabricated claim.
/// </summary>
internal static class AppMatrix
{
    private static readonly string ScreenshotDir = Environment.GetEnvironmentVariable("S4_SCREENSHOT_DIR")
        ?? Path.Combine(Path.GetTempPath(), "s4-inject-win-screenshots");

    internal static int RunOne(string profileKey)
    {
        if (!AppLauncher.Profiles.TryGetValue(profileKey, out var profile))
        {
            Console.Error.WriteLine($"Unknown profile: {profileKey}");
            return 1;
        }

        Directory.CreateDirectory(ScreenshotDir);
        Console.WriteLine($"=== {profile.Name} ({profile.Description}) ===");

        System.Diagnostics.Process proc;
        try
        {
            proc = profile.Launch();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"LAUNCH FAILED: {ex.Message}");
            return 1;
        }
        using var _ = proc;

        Thread.Sleep(profile.SettleMs);
        var hWnd = NativeMethods.GetForegroundWindow();
        NativeMethods.SetForegroundWindow(hWnd);
        Thread.Sleep(300);

        if (profileKey.Equals("chrome-addressbar", StringComparison.OrdinalIgnoreCase))
        {
            AppLauncher.SendCtrlL();
        }

        for (int i = 0; i < profile.TabsBeforeInject; i++)
        {
            AppLauncher.SendTab();
        }

        var opts = new InjectionOptions(PasteChord: profile.PasteChord, ClipboardRestoreDelayMs: profile.ClipboardRestoreDelayMs);
        var result = Injector.Inject(TestData.TestString, opts, explicitTarget: hWnd);

        Thread.Sleep(400); // let the target app's UI thread actually render the paste before we screenshot
        var shotPath = Path.Combine(ScreenshotDir, $"{profileKey}.png");
        try
        {
            // Full-screen, not window-rect: some apps' foreground HWND right
            // after launch is a small splash/frame window, not the real
            // content window, which produced a near-blank screenshot for
            // VS Code in this spike's first attempt.
            ScreenshotUtil.CaptureFullScreen(shotPath);
            Console.WriteLine($"Screenshot saved: {shotPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Screenshot failed: {ex.Message}");
        }

        Console.WriteLine($"Outcome={result.Outcome} elapsed={result.Elapsed.TotalMilliseconds:F1}ms " +
                           $"(<200ms bar: {(result.Elapsed.TotalMilliseconds < 200 ? "PASS" : "FAIL")})");
        Console.WriteLine("NOTE: text-content correctness for this app must be confirmed from the screenshot " +
                           "(or by a human looking at the live window) -- this spike cannot script a reliable " +
                           "text read-back for every app in the matrix. See README for which apps got a real " +
                           "programmatic read-back (Notepad only) vs. screenshot-only evidence.");

        return result.Outcome == InjectionOutcome.Injected ? 0 : 1;
    }
}
