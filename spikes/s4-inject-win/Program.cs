using System.Diagnostics;

namespace s4_inject_win;

/// <summary>
/// S4 spike: Windows clipboard-paste injection matrix, per
/// Docs/soneto-implementation-plan-phase0-1.md §"S4 -- Windows injection
/// matrix". Throwaway spike code -- see spikes/s3-hotkey-win/README.md for
/// the error-handling convention this follows ("fail loudly, no investment
/// beyond that").
///
/// Usage:
///   s4-inject-win countdown [--seconds N] [--text "..."]
///   s4-inject-win notepad-selfcheck
///   s4-inject-win adversarial shift
///   s4-inject-win adversarial restore-race
///   s4-inject-win adversarial image
///   s4-inject-win launch <profile>      (notepad|vscode|chrome-textarea|chrome-addressbar|terminal|teams|outlook|word)
///   s4-inject-win launch all
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

        switch (args[0])
        {
            case "countdown":
                return await CountdownMode.RunAsync(args[1..]);

            case "notepad-selfcheck":
                return NotepadSelfCheck.Run();

            case "debug-keys":
                Console.WriteLine("Sending VK_LSHIFT down...");
                ModifierSanitizer.SendKeyDown(NativeMethods.VK_LSHIFT);
                Thread.Sleep(100);
                Console.WriteLine($"VK_LSHIFT down? {NativeMethods.IsDown(NativeMethods.VK_LSHIFT)}");
                Console.WriteLine($"VK_SHIFT (generic) down? {NativeMethods.IsDown(NativeMethods.VK_SHIFT)}");
                ModifierSanitizer.SendKeyUp(NativeMethods.VK_LSHIFT);
                Thread.Sleep(100);
                Console.WriteLine($"After up, VK_LSHIFT down? {NativeMethods.IsDown(NativeMethods.VK_LSHIFT)}");
                Console.WriteLine("Now typing 'abc' into whatever has focus in 3 seconds...");
                Thread.Sleep(3000);
                foreach (char c in "abc")
                {
                    int vk = char.ToUpperInvariant(c);
                    ModifierSanitizer.SendKeyDown(vk);
                    Thread.Sleep(20);
                    ModifierSanitizer.SendKeyUp(vk);
                    Thread.Sleep(20);
                }
                Console.WriteLine("Now sending Ctrl+V in 1 second (make sure something is on the clipboard)...");
                Thread.Sleep(1000);
                ModifierSanitizer.SendKeyDown(NativeMethods.VK_LCONTROL);
                ModifierSanitizer.SendKeyDown(NativeMethods.VK_V);
                Thread.Sleep(20);
                ModifierSanitizer.SendKeyUp(NativeMethods.VK_V);
                ModifierSanitizer.SendKeyUp(NativeMethods.VK_LCONTROL);
                Console.WriteLine("done");
                return 0;

            case "adversarial":
                if (args.Length < 2) { PrintUsage(); return 1; }
                return args[1] switch
                {
                    "shift" => AdversarialTests.RunShiftHold(),
                    "restore-race" => AdversarialTests.RunRestoreRace(),
                    "image" => AdversarialTests.RunImageOnClipboard(),
                    _ => Unknown(args[1])
                };

            case "launch":
                if (args.Length < 2) { PrintUsage(); return 1; }
                if (args[1].Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var key in AppLauncher.Profiles.Keys)
                    {
                        AppMatrix.RunOne(key);
                        Console.WriteLine();
                    }
                    return 0;
                }
                return AppMatrix.RunOne(args[1]);

            default:
                return Unknown(args[0]);
        }
    }

    private static int Unknown(string what)
    {
        Console.Error.WriteLine($"Unknown mode/profile: {what}");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  s4-inject-win countdown [--seconds N] [--text \"...\"]");
        Console.Error.WriteLine("  s4-inject-win notepad-selfcheck");
        Console.Error.WriteLine("  s4-inject-win adversarial shift|restore-race|image");
        Console.Error.WriteLine("  s4-inject-win launch <profile>|all");
        Console.Error.WriteLine($"  profiles: {string.Join(", ", AppLauncher.Profiles.Keys)}");
    }
}

internal static class CountdownMode
{
    internal static async Task<int> RunAsync(string[] args)
    {
        int seconds = 3;
        string text = TestData.TestString;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--seconds" && i + 1 < args.Length) seconds = int.Parse(args[++i]);
            if (args[i] == "--text" && i + 1 < args.Length) text = args[++i];
        }

        Console.WriteLine($"Injecting in {seconds}s -- switch focus to your target app now...");
        for (int i = seconds; i > 0; i--)
        {
            Console.WriteLine(i);
            await Task.Delay(1000);
        }

        var result = Injector.Inject(text, new InjectionOptions());
        Console.WriteLine($"Outcome={result.Outcome} elapsed={result.Elapsed.TotalMilliseconds:F1}ms");
        return result.Outcome == InjectionOutcome.Injected ? 0 : 1;
    }
}
