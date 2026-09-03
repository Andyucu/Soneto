using System.Diagnostics;
using SharpHook;
using SharpHook.Data;
using SharpHook.Simulation;

namespace s3_hotkey_win;

/// <summary>
/// Fully automated self-test: no human, no physical key press. Uses SharpHook's
/// own EventSimulator to synthesize the trigger key at controlled instants, and
/// measures how long it takes SimpleGlobalHook's callback to observe each one.
///
/// IMPORTANT — what this does and does NOT prove (see README "What was
/// self-verified" section for the full picture):
///   - DOES prove: the hook fires reliably for a real OS-level SendInput
///     keystroke, and measures the send-to-callback latency distribution for
///     that self-injected keystroke, and that GetAsyncKeyState correctly
///     reflects a synthesized modifier key's physical state.
///   - Does NOT prove: latency for a genuine physical keypress (real hardware
///     adds USB HID polling-interval jitter, typically 1-8ms at 125-1000Hz
///     polling rates, that a synthetic SendInput event skips entirely) — so
///     treat this number as a floor / best case, not the final answer.
///   - Does NOT prove: that suppression actually stops the keystroke reaching
///     an arbitrary focused GUI app's keyboard buffer. That requires a human
///     with a real app focused — see README manual test script.
/// </summary>
internal static class SelfTest
{
    private const KeyCode Trigger = KeyCode.VcRightControl;
    private const int Trials = 30;
    private const int InterTrialDelayMs = 40;
    private const int WaitForCallbackMs = 1000;
    private const double JitterBarMs = 20.0;

    internal static async Task<bool> RunAsync()
    {
        Console.WriteLine("=== S3 self-test: automated jitter + modifier-read verification ===");
        Console.WriteLine($"Trigger key: {Trigger}, trials: {Trials}, pass bar: p95 < {JitterBarMs} ms\n");

        bool overallPass = true;

        overallPass &= await RunJitterTestAsync();
        Console.WriteLine();
        overallPass &= RunModifierReadTest();
        Console.WriteLine();
        overallPass &= RunTriggerControlAmbiguityTest();

        Console.WriteLine();
        Console.WriteLine(overallPass
            ? "=== SELF-TEST OVERALL: PASS ==="
            : "=== SELF-TEST OVERALL: FAIL (see above) ===");
        return overallPass;
    }

    private static async Task<bool> RunJitterTestAsync()
    {
        using var hook = new SimpleGlobalHook(globalHookProvider: null);
        var simulator = EventSimulator.Create("s3-hotkey-win");

        var downLatencies = new List<double>();
        var upLatencies = new List<double>();
        int downMisses = 0, upMisses = 0;

        long sendTicksDown = 0;
        long sendTicksUp = 0;
        var downSignal = new SemaphoreSlim(0);
        var upSignal = new SemaphoreSlim(0);
        double lastDownLatencyMs = 0, lastUpLatencyMs = 0;

        hook.KeyPressed += (_, e) =>
        {
            if (e.Data.KeyCode != Trigger) return;
            var now = Stopwatch.GetTimestamp();
            lastDownLatencyMs = TicksToMs(now - Interlocked.Read(ref sendTicksDown));
            e.SuppressEvent = true; // this is the trigger key — always suppressed in normal operation
            downSignal.Release();
        };
        hook.KeyReleased += (_, e) =>
        {
            if (e.Data.KeyCode != Trigger) return;
            var now = Stopwatch.GetTimestamp();
            lastUpLatencyMs = TicksToMs(now - Interlocked.Read(ref sendTicksUp));
            e.SuppressEvent = true;
            upSignal.Release();
        };

        // Note: RunAsync's returned Task represents the hook's entire run
        // lifetime (it only completes on Stop()/Dispose()), regardless of
        // useBackgroundThread — that flag only controls which thread runs
        // the native loop. So we must not await it here; fire it and wait
        // for HookEnabled (or a short delay) before proceeding.
        var hookRunTask = hook.RunAsync(GlobalHookType.Keyboard, useBackgroundThread: true);
        await Task.Delay(200);

        Console.WriteLine($"Running {Trials} synthetic press/release pairs...");
        for (int i = 0; i < Trials; i++)
        {
            Interlocked.Exchange(ref sendTicksDown, Stopwatch.GetTimestamp());
            simulator.SimulateKeyPress(Trigger);
            bool gotDown = await downSignal.WaitAsync(WaitForCallbackMs);
            if (gotDown) downLatencies.Add(lastDownLatencyMs); else downMisses++;

            await Task.Delay(15);

            Interlocked.Exchange(ref sendTicksUp, Stopwatch.GetTimestamp());
            simulator.SimulateKeyRelease(Trigger);
            bool gotUp = await upSignal.WaitAsync(WaitForCallbackMs);
            if (gotUp) upLatencies.Add(lastUpLatencyMs); else upMisses++;

            await Task.Delay(InterTrialDelayMs);
        }

        hook.Stop();
        try { await hookRunTask; } catch { /* Stop() causes the run task to complete/cancel; ignore */ }

        var (p50D, p95D, maxD, nD) = LatencyStats.Summarize(downLatencies);
        var (p50U, p95U, maxU, nU) = LatencyStats.Summarize(upLatencies);

        Console.WriteLine($"DOWN latency (send -> callback), n={nD}, misses={downMisses}: p50={p50D:F2}ms p95={p95D:F2}ms max={maxD:F2}ms");
        Console.WriteLine($"UP   latency (send -> callback), n={nU}, misses={upMisses}: p50={p50U:F2}ms p95={p95U:F2}ms max={maxU:F2}ms");

        bool pass = downMisses == 0 && upMisses == 0 && p95D < JitterBarMs && p95U < JitterBarMs;
        Console.WriteLine(pass
            ? "Jitter test: PASS (p95 under 20ms bar, zero missed events)"
            : "Jitter test: FAIL");
        return pass;
    }

    private static bool RunModifierReadTest()
    {
        Console.WriteLine("Modifier-read test: synthesizing Left Shift down, checking GetAsyncKeyState...");
        var simulator = EventSimulator.Create("s3-hotkey-win");
        bool pass = true;

        simulator.SimulateKeyPress(KeyCode.VcLeftShift);
        Thread.Sleep(30); // let the OS input-state table settle
        var held = ModifierSnapshot.Read();
        Console.WriteLine($"  While Shift held: {held}");
        if (!held.Shift)
        {
            Console.WriteLine("  FAIL: GetAsyncKeyState did not report Shift as held.");
            pass = false;
        }

        simulator.SimulateKeyRelease(KeyCode.VcLeftShift);
        Thread.Sleep(30);
        held = ModifierSnapshot.Read();
        Console.WriteLine($"  After Shift released: {held}");
        if (held.Shift)
        {
            Console.WriteLine("  FAIL: GetAsyncKeyState still reports Shift held after release.");
            pass = false;
        }

        Console.WriteLine(pass
            ? "Modifier-read test: PASS (this is the exact GetAsyncKeyState pattern §1.8's sanitiser needs)"
            : "Modifier-read test: FAIL");
        return pass;
    }

    /// <summary>
    /// Demonstrates the VK_CONTROL/trigger-key ambiguity flagged in code
    /// review: while the Right Ctrl trigger is physically held, generic
    /// VK_CONTROL (which does not distinguish left/right) will ALWAYS read
    /// "held", purely because the trigger itself is a Ctrl key — this is not
    /// a genuinely-held left Ctrl and must not be mistaken for one by §1.8's
    /// modifier sanitiser in Phase 1. VK_LCONTROL, in contrast, correctly
    /// reads NOT held during the same press, because only the trigger's
    /// right-hand key is down. This is exactly why ModifierSnapshot.Control
    /// is read from VK_LCONTROL (see NativeMethods.cs), not generic
    /// VK_CONTROL.
    /// </summary>
    private static bool RunTriggerControlAmbiguityTest()
    {
        Console.WriteLine("Trigger/Control ambiguity test: synthesizing Right Ctrl (the trigger) down,");
        Console.WriteLine("comparing generic VK_CONTROL against VK_LCONTROL during that same press...");
        var simulator = EventSimulator.Create("s3-hotkey-win");
        bool pass = true;

        simulator.SimulateKeyPress(Trigger); // KeyCode.VcRightControl
        Thread.Sleep(30); // let the OS input-state table settle
        var held = ModifierSnapshot.Read();
        Console.WriteLine($"  While Right Ctrl (trigger) held: generic VK_CONTROL={held.GenericControlHeld}, VK_LCONTROL={held.Control}");

        if (!held.GenericControlHeld)
        {
            Console.WriteLine("  FAIL: expected generic VK_CONTROL to read held during the trigger press (this is the ambiguity being demonstrated).");
            pass = false;
        }
        if (held.Control)
        {
            Console.WriteLine("  FAIL: expected VK_LCONTROL to read NOT held during a Right-Ctrl-only trigger press.");
            pass = false;
        }

        simulator.SimulateKeyRelease(Trigger);
        Thread.Sleep(30);
        held = ModifierSnapshot.Read();
        Console.WriteLine($"  After Right Ctrl (trigger) released: generic VK_CONTROL={held.GenericControlHeld}, VK_LCONTROL={held.Control}");
        if (held.GenericControlHeld || held.Control)
        {
            Console.WriteLine("  FAIL: expected both to read NOT held after the trigger is released.");
            pass = false;
        }

        Console.WriteLine(pass
            ? "Trigger/Control ambiguity test: PASS (demonstrates generic VK_CONTROL is ambiguous with the trigger; VK_LCONTROL is not — see README)"
            : "Trigger/Control ambiguity test: FAIL");
        return pass;
    }

    private static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
