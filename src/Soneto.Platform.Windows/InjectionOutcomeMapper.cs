using Soneto.Core.Abstractions;

namespace Soneto.Platform.Windows;

/// <summary>
/// Pure outcome-mapping logic for <see cref="WindowsTextInjector.InjectAsync"/>, pulled out
/// so the "which <see cref="InjectionOutcome"/> for which failure" decision can be
/// unit-tested without any real window/clipboard/<c>SendInput</c> call. The check order
/// mirrors plan §1.8's own step order: target resolution (steps 1-2) happens before the
/// clipboard is ever touched (step 5), which happens before the paste chord is sent
/// (step 8) -- so the first thing that failed, in that order, is what's reported.
///
/// <c>PermissionDenied</c> is deliberately not produced by this mapper: this work item (7)
/// has no concrete scenario that maps to it (see <see cref="WindowsTextInjector"/>'s class
/// doc comment).
/// </summary>
public static class InjectionOutcomeMapper
{
    public static InjectionOutcome Map(bool targetResolved, bool clipboardSet, bool synthSent)
    {
        if (!targetResolved) return InjectionOutcome.TargetLost;
        if (!clipboardSet) return InjectionOutcome.ClipboardFailed;
        if (!synthSent) return InjectionOutcome.SynthFailed;
        return InjectionOutcome.Injected;
    }
}
