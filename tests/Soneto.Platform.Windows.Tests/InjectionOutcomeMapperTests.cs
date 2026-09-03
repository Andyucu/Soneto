using Soneto.Core.Abstractions;

namespace Soneto.Platform.Windows.Tests;

/// <summary>
/// Pure outcome-mapping logic tests for <see cref="InjectionOutcomeMapper"/> -- item 7's
/// "outcome-mapping logic" unit test called for by the work item, with fakes for every input
/// so no real window/clipboard/SendInput call is involved.
/// </summary>
public sealed class InjectionOutcomeMapperTests
{
    [Fact]
    public void All_succeeded_maps_to_Injected()
    {
        Assert.Equal(InjectionOutcome.Injected, InjectionOutcomeMapper.Map(targetResolved: true, clipboardSet: true, synthSent: true));
    }

    [Fact]
    public void Target_not_resolved_maps_to_TargetLost_regardless_of_other_flags()
    {
        Assert.Equal(InjectionOutcome.TargetLost, InjectionOutcomeMapper.Map(targetResolved: false, clipboardSet: true, synthSent: true));
        Assert.Equal(InjectionOutcome.TargetLost, InjectionOutcomeMapper.Map(targetResolved: false, clipboardSet: false, synthSent: false));
    }

    [Fact]
    public void Clipboard_not_set_maps_to_ClipboardFailed_when_target_is_resolved()
    {
        Assert.Equal(InjectionOutcome.ClipboardFailed, InjectionOutcomeMapper.Map(targetResolved: true, clipboardSet: false, synthSent: true));
        Assert.Equal(InjectionOutcome.ClipboardFailed, InjectionOutcomeMapper.Map(targetResolved: true, clipboardSet: false, synthSent: false));
    }

    [Fact]
    public void Synth_not_sent_maps_to_SynthFailed_when_target_resolved_and_clipboard_set()
    {
        Assert.Equal(InjectionOutcome.SynthFailed, InjectionOutcomeMapper.Map(targetResolved: true, clipboardSet: true, synthSent: false));
    }

    [Fact]
    public void Check_order_matches_plan_step_order_target_before_clipboard_before_synth()
    {
        // A target failure must win even if clipboardSet/synthSent are (nonsensically) also
        // true, and a clipboard failure must win over a synth failure -- mirroring plan
        // §1.8's own step order (1-2 before 5 before 8).
        Assert.Equal(InjectionOutcome.TargetLost, InjectionOutcomeMapper.Map(false, true, true));
        Assert.Equal(InjectionOutcome.ClipboardFailed, InjectionOutcomeMapper.Map(true, false, true));
    }
}
