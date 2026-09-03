using SharpHook;
using SharpHook.Data;

namespace s3_hotkey_win;

/// <summary>
/// S3 spike: Windows global hold-to-talk hotkey via SharpHook. Throwaway code
/// per Docs/soneto-implementation-plan-phase0-1.md §"S3 — Windows global
/// hold-to-talk" — no error-handling investment beyond "fail loudly with a
/// clear message" (see spikes/s1-asr/README.md for the same convention).
///
/// Usage:
///   s3-hotkey-win listen [--duration SEC] [--leak-keyup] [--trigger KEYCODE] [--verbose]
///   s3-hotkey-win self-test
///   s3-hotkey-win block-test
///
/// See README.md for what each mode does, and for the manual test script
/// covering everything that cannot be automated from a console-mode agent
/// process (real target apps, 30-minute idle, lock/unlock).
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        string mode = args[0];
        switch (mode)
        {
            case "self-test":
                bool pass = await SelfTest.RunAsync();
                return pass ? 0 : 1;

            case "block-test":
                await BlockDemo.RunAsync();
                return 0;

            case "listen":
                return await ListenMode.RunAsync(args[1..]);

            case "simulate-trigger":
                // Internal helper, not a primary mode: fires one synthetic
                // press/release of Right Ctrl from a separate process so a
                // concurrently-running `listen` instance can be end-to-end
                // verified without a physical key. Used only to double-check
                // ListenMode's wiring against SelfTest's already-passing result.
                var sim = SharpHook.Simulation.EventSimulator.Create("s3-hotkey-win");
                sim.SimulateKeyPress(KeyCode.VcRightControl);
                await Task.Delay(120);
                sim.SimulateKeyRelease(KeyCode.VcRightControl);
                return 0;

            default:
                Console.Error.WriteLine($"Unknown mode: {mode}");
                PrintUsage();
                return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  s3-hotkey-win listen [--duration SEC] [--leak-keyup] [--trigger KEYCODE] [--verbose]");
        Console.Error.WriteLine("  s3-hotkey-win self-test");
        Console.Error.WriteLine("  s3-hotkey-win block-test");
    }
}
