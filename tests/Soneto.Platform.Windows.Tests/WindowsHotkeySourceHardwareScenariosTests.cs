namespace Soneto.Platform.Windows.Tests;

/// <summary>
/// Documents the scenarios that are out of scope for automated CI for
/// <see cref="WindowsHotkeySource"/>, per this project's existing convention (see e.g.
/// <c>Soneto.Core.Tests.Audio.PortAudioCaptureHardwareTests</c> and
/// <c>spikes/s3-hotkey-win/README.md</c>'s manual test script, which this list mirrors).
/// Tagged <c>[Trait("Category","Hardware")]</c> so it is excluded from the default
/// `dotnet test` run by this project's <c>VSTestTestCaseFilter</c> default, same as every
/// other Hardware-tagged test in this solution.
///
/// These are genuinely not automatable from a non-interactive agent/CI session:
/// <list type="bullet">
/// <item><description>A real, physical keyboard press/release of the trigger key (as opposed
/// to <see cref="SharpHook.Simulation.EventSimulator"/>'s synthetic, SendInput-based events,
/// which every other test in this project uses and which S3 already validated works
/// headlessly) -- including confirming no character/modifier leak into a real focused GUI
/// app (Notepad/VS Code/Chrome/Windows Terminal) when <c>Suppress=true</c>.</description></item>
/// <item><description>Physically holding Shift (or any modifier) with a human hand while
/// physically pressing the trigger key, to rule out any real-hardware-only interaction not
/// reproducible via synthetic input.</description></item>
/// <item><description>30-minute idle survival with no synthetic or real events at all,
/// confirming the heartbeat doesn't false-positive-fault on a genuinely healthy but quiet
/// hook over real wall-clock time (the deterministic heartbeat tests in
/// <see cref="WindowsHotkeySourceHeartbeatTests"/> only cover the logic path via a rewound
/// timestamp, not real 60-minute-scale wall-clock behavior).</description></item>
/// <item><description>Lock/unlock (Win+L) cycle survival -- whether the hook and heartbeat
/// keep working correctly across a real interactive session lock, which cannot be triggered
/// meaningfully from an automated test.</description></item>
/// </list>
/// See <c>Docs/soneto-implementation-plan-phase0-1.md</c> §1.4/§1.12 and
/// <c>Docs/PROJECT-MEMORY.md</c>'s item 6 section for the authoritative scope statement, and
/// <c>--watch-hotkey</c>'s own doc comment in <c>Soneto.Daemon/Program.cs</c> for the same
/// "what this can and cannot verify" list applied to the live CLI demo.
/// </summary>
[Trait("Category", "Hardware")]
public sealed class WindowsHotkeySourceHardwareScenariosTests
{
    [Fact(Skip = "Documentation-only placeholder: these scenarios require a physical keyboard, a real focused GUI app, and/or real wall-clock time (30+ minutes) or an interactive session lock/unlock, none of which are available in an automated/CI run. See the class doc comment for the specific scenarios and manual verification pointers.")]
    public void Physical_keyboard_and_real_time_scenarios_are_documented_but_not_automated()
    {
    }
}
