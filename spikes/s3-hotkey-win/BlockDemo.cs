using System.Diagnostics;
using SharpHook;
using SharpHook.Data;
using SharpHook.Simulation;

namespace s3_hotkey_win;

/// <summary>
/// Deliberately blocks the hook callback for ~2 seconds on the first trigger
/// press, then fires several more synthetic presses to observe whether
/// Windows silently drops the low-level keyboard hook (WH_KEYBOARD_LL hooks
/// that don't return within the "LowLevelHooksTimeout" window — 300ms by
/// default on modern Windows — get silently unregistered by the OS, with no
/// exception surfaced to the hooking process).
///
/// Fully automated via EventSimulator — no human required to observe this
/// failure mode; only needs a human to have separately confirmed (per the
/// README manual script) that this matches what they'd see with a real key.
/// </summary>
internal static class BlockDemo
{
    private const KeyCode Trigger = KeyCode.VcRightControl;

    internal static async Task RunAsync()
    {
        Console.WriteLine("=== S3 block-callback demo ===");
        Console.WriteLine("Plan: synthesize 4 press/release pairs on Right Ctrl.");
        Console.WriteLine("On the FIRST press, the KeyPressed callback deliberately");
        Console.WriteLine("calls Thread.Sleep(2000) before returning, simulating a");
        Console.WriteLine("hung/slow handler. Watch what happens to presses 2-4.\n");

        using var hook = new SimpleGlobalHook(globalHookProvider: null);
        var simulator = EventSimulator.Create("s3-hotkey-win");
        int downCount = 0;
        int upCount = 0;
        var upSeenForDown = new HashSet<int>();
        var sw = Stopwatch.StartNew();

        hook.KeyPressed += (_, e) =>
        {
            if (e.Data.KeyCode != Trigger) return;
            downCount++;
            Console.WriteLine($"  [{sw.Elapsed.TotalMilliseconds,8:F1}ms] DOWN #{downCount} received by callback");
            e.SuppressEvent = true;

            if (downCount == 1)
            {
                Console.WriteLine($"  [{sw.Elapsed.TotalMilliseconds,8:F1}ms] Deliberately blocking callback for 2000ms now...");
                Thread.Sleep(2000);
                Console.WriteLine($"  [{sw.Elapsed.TotalMilliseconds,8:F1}ms] ...callback unblocked, returning.");
            }
        };
        hook.KeyReleased += (_, e) =>
        {
            if (e.Data.KeyCode != Trigger) return;
            upCount++;
            upSeenForDown.Add(downCount);
            Console.WriteLine($"  [{sw.Elapsed.TotalMilliseconds,8:F1}ms] UP   #{downCount} received by callback");
            e.SuppressEvent = true;
        };

        // See SelfTest.cs for why we don't await this task directly here.
        var hookRunTask = hook.RunAsync(GlobalHookType.Keyboard, useBackgroundThread: true);
        await Task.Delay(200);

        for (int i = 1; i <= 4; i++)
        {
            Console.WriteLine($"[{sw.Elapsed.TotalMilliseconds,8:F1}ms] Sending synthetic press/release #{i}...");
            simulator.SimulateKeyPress(Trigger);
            await Task.Delay(150);
            simulator.SimulateKeyRelease(Trigger);
            // Give plenty of time for #1's 2s block to resolve, and for the OS
            // to have (or not have) delivered the later ones, before sending the next.
            await Task.Delay(1500);
        }

        hook.Stop();
        try { await hookRunTask; } catch { /* expected on Stop() */ }

        Console.WriteLine($"\nTotal DOWN events observed: {downCount} / 4 sent. Total UP events observed: {upCount} / 4 sent.");

        bool anyDownDropped = downCount < 4;
        bool anyUpDroppedWhileDownSeen = Enumerable.Range(1, downCount).Any(n => !upSeenForDown.Contains(n));

        if (anyDownDropped)
        {
            Console.WriteLine("OBSERVED FAILURE MODE: at least one DOWN was never delivered to the callback.");
            Console.WriteLine("Windows dropped the hook entirely after the callback blocked past its");
            Console.WriteLine("timeout. This is the scenario §S3's 'also test' section warns about —");
            Console.WriteLine("build the watchdog (heartbeat + re-register per §1.12's error-handling");
            Console.WriteLine("matrix) knowing this is what silent hook death looks like: no exception,");
            Console.WriteLine("no event, just nothing, until the hook is re-registered.");
        }
        else if (anyUpDroppedWhileDownSeen)
        {
            Console.WriteLine("OBSERVED FAILURE MODE (narrower than full unhook): every DOWN arrived,");
            Console.WriteLine("but at least one matching UP that occurred *during* the 2-second block");
            Console.WriteLine("was silently dropped rather than queued for delivery once the callback");
            Console.WriteLine("unblocked. Concretely: the key-up sent 150ms after press #1 (i.e. while");
            Console.WriteLine("the callback was still asleep) never reached hook.KeyReleased at all —");
            Console.WriteLine("it was not delayed, it was lost. Practically, this is *worse* than a full");
            Console.WriteLine("unhook for a hold-to-talk design: you can get an orphan DOWN with no");
            Console.WriteLine("matching UP ever arriving, which is exactly the 'key stuck down' edge");
            Console.WriteLine("case §1.4 calls out (force-finalise on a maxDurationMs timer with no");
            Console.WriteLine("matching key-up is the correct defense, not an assumption that every DOWN");
            Console.WriteLine("gets an UP).");
        }
        else
        {
            Console.WriteLine("All 4 DOWN and all 4 UP were observed — on this run, the 2s block did not");
            Console.WriteLine("visibly drop any event (possible if the synthetic send/callback timing");
            Console.WriteLine("didn't line up with the exact moment another key event would have needed");
            Console.WriteLine("the hook, or if this Windows build's low-level-hook timeout is more");
            Console.WriteLine("forgiving than the commonly-cited 300ms). Re-run a few times, and also");
            Console.WriteLine("verify manually with a physical key per the README — this failure mode is");
            Console.WriteLine("timing-sensitive and not guaranteed to reproduce on every run.");
        }
    }
}
