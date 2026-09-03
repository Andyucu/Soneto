using SharpHook.Data;
using SharpHook.Simulation;
using Soneto.Platform.Windows.Interop;

namespace Soneto.Platform.Windows.Tests;

/// <summary>
/// Exercises <see cref="ModifierState.Read"/> (a thin <c>GetAsyncKeyState</c> wrapper) using
/// SharpHook's <see cref="EventSimulator"/> to synthesize real OS-level key state changes --
/// proven to work headlessly in CI per <c>spikes/s3-hotkey-win</c>'s own self-test and this
/// item's manual verification. No global hook is created by these tests (no
/// <see cref="SharpHook.SimpleGlobalHook"/> involved at all), so there is no risk of
/// colliding with <see cref="WindowsHotkeySourceTests"/>'s real-hook tests.
/// </summary>
public sealed class ModifierStateTests
{
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(60);

    [Fact]
    public async Task Read_detects_Shift_held_then_released()
    {
        using var sim = EventSimulator.Create("Soneto.Platform.Windows.Tests");
        try
        {
            sim.SimulateKeyPress(KeyCode.VcLeftShift);
            await Task.Delay(SettleDelay);

            var held = ModifierState.Read();
            Assert.True(held.Shift, "Expected Shift to read as held immediately after a synthesized LeftShift down.");
        }
        finally
        {
            sim.SimulateKeyRelease(KeyCode.VcLeftShift);
        }

        await Task.Delay(SettleDelay);
        var released = ModifierState.Read();
        Assert.False(released.Shift, "Expected Shift to read as released after a synthesized LeftShift up.");
    }

    [Fact]
    public async Task Read_distinguishes_VK_LCONTROL_from_generic_VK_CONTROL_when_the_configured_trigger_is_Right_Control()
    {
        // Mirrors S3's own RunTriggerControlAmbiguityTest: when the physically-held key is
        // Right Ctrl (a realistic trigger binding), generic VK_CONTROL cannot distinguish
        // left from right and always reads "held" -- but ModifierState.Control is
        // deliberately wired to VK_LCONTROL specifically and must NOT read "held" in this
        // situation, or the §1.8 modifier sanitiser would falsely believe the user's other
        // hand is holding Ctrl on every single trigger press.
        using var sim = EventSimulator.Create("Soneto.Platform.Windows.Tests");
        try
        {
            sim.SimulateKeyPress(KeyCode.VcRightControl);
            await Task.Delay(SettleDelay);

            var held = ModifierState.Read();
            Assert.True(held.GenericControlHeld, "Generic VK_CONTROL is expected to read 'held' during a Right Ctrl press (known ambiguity, kept as a labeled diagnostic).");
            Assert.False(held.Control, "ModifierState.Control (VK_LCONTROL) must NOT read 'held' merely because the trigger key itself (Right Ctrl) is down.");
        }
        finally
        {
            sim.SimulateKeyRelease(KeyCode.VcRightControl);
        }

        await Task.Delay(SettleDelay);
        var released = ModifierState.Read();
        Assert.False(released.GenericControlHeld);
        Assert.False(released.Control);
    }

    [Fact]
    public void ToString_lists_no_modifiers_as_none_when_all_flags_are_false()
    {
        var state = new ModifierState(Shift: false, Control: false, Alt: false, LeftWin: false, RightWin: false, GenericControlHeld: false);
        Assert.Equal("(none)", state.ToString());
    }

    [Fact]
    public void ToString_joins_held_modifiers_and_labels_Control_as_left_only()
    {
        var state = new ModifierState(Shift: true, Control: true, Alt: false, LeftWin: false, RightWin: false, GenericControlHeld: true);
        Assert.Equal("Shift+Control(L)", state.ToString());
    }
}
