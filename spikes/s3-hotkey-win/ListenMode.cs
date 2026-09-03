using System.Diagnostics;
using SharpHook;
using SharpHook.Data;

namespace s3_hotkey_win;

/// <summary>
/// Long-running interactive mode. This is what a human uses for the manual
/// test script in README.md: focus a real app (Notepad, VS Code, Chrome,
/// Windows Terminal), hold/release the trigger, and watch this console for
/// DOWN/UP while confirming nothing leaks into the focused app.
/// </summary>
internal static class ListenMode
{
    internal static async Task<int> RunAsync(string[] args)
    {
        int? durationSec = null;
        bool leakKeyUp = false;
        bool verbose = false;
        KeyCode trigger = KeyCode.VcRightControl;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--duration":
                    durationSec = int.Parse(args[++i]);
                    break;
                case "--leak-keyup":
                    leakKeyUp = true;
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                case "--trigger":
                    trigger = Enum.Parse<KeyCode>(args[++i]);
                    break;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    return 1;
            }
        }

        Console.WriteLine("=== S3 listen mode ===");
        Console.WriteLine($"Trigger key: {trigger}");
        Console.WriteLine(leakKeyUp
            ? "SUPPRESSION MODE: DOWN suppressed, UP LEAKS DELIBERATELY (--leak-keyup active)."
            : "SUPPRESSION MODE: both DOWN and UP suppressed (normal operation).");
        Console.WriteLine("Press Escape (not suppressed) or Ctrl+C in this console to quit.");
        if (durationSec is int d) Console.WriteLine($"Will also auto-exit after {d}s.");
        Console.WriteLine();
        Console.WriteLine("Now focus whatever app you want to test, then press/release the trigger.\n");

        using var hook = new SimpleGlobalHook(globalHookProvider: null);
        var sw = Stopwatch.StartNew();
        var stopSignal = new TaskCompletionSource();
        int downCount = 0, upCount = 0;

        hook.KeyPressed += (_, e) =>
        {
            if (e.Data.KeyCode == KeyCode.VcEscape)
            {
                stopSignal.TrySetResult();
                return;
            }

            if (e.Data.KeyCode == trigger)
            {
                downCount++;
                var mods = ModifierSnapshot.Read();
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{sw.Elapsed.TotalMilliseconds,10:F1}ms] DOWN  #{downCount}  heldModifiers={mods}");
                e.SuppressEvent = true; // trigger DOWN is always suppressed
            }
            else if (verbose)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] key down: {e.Data.KeyCode} (not suppressed, not the trigger)");
            }
        };

        hook.KeyReleased += (_, e) =>
        {
            if (e.Data.KeyCode == trigger)
            {
                upCount++;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{sw.Elapsed.TotalMilliseconds,10:F1}ms] UP    #{upCount}");
                if (leakKeyUp)
                {
                    Console.WriteLine("  (leak-keyup active: NOT suppressing this key-up — it will reach the focused app)");
                    // deliberately leave e.SuppressEvent = false
                }
                else
                {
                    e.SuppressEvent = true;
                }
            }
            else if (verbose)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] key up:   {e.Data.KeyCode} (not suppressed, not the trigger)");
            }
        };

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopSignal.TrySetResult();
        };

        // See SelfTest.cs for why we don't await this task directly here.
        var hookRunTask = hook.RunAsync(GlobalHookType.Keyboard, useBackgroundThread: true);
        await Task.Delay(200);
        Console.WriteLine("Hook registered and running.\n");

        var timeoutTask = durationSec is int secs ? Task.Delay(TimeSpan.FromSeconds(secs)) : Task.Delay(-1);
        await Task.WhenAny(stopSignal.Task, timeoutTask);

        hook.Stop();
        try { await hookRunTask; } catch { /* expected on Stop() */ }
        Console.WriteLine($"\nStopped. Total: {downCount} DOWN, {upCount} UP over {sw.Elapsed.TotalSeconds:F1}s.");
        return 0;
    }
}
