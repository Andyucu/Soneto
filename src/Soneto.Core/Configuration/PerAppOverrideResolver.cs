using Soneto.Core.Abstractions;

namespace Soneto.Core.Configuration;

/// <summary>
/// Phase 4 item 2 (§4.4): pure per-app override resolution/merge logic for
/// <see cref="InjectionConfig.PerApp"/>, pulled out of
/// <c>Soneto.Platform.Windows.WindowsTextInjector</c> so the "given a process name and a
/// PerApp table, which override applies and what's the resulting effective
/// <see cref="InjectionOptions"/>" decision can be unit-tested directly in
/// <c>Soneto.Core.Tests</c> without any real Win32 foreground-window/process-lookup call --
/// mirrors <c>KeyboardDeviceFilter</c>'s (Soneto.Platform.Linux) and
/// <c>InjectionOutcomeMapper</c>'s (Soneto.Platform.Windows) own "pull the pure decision out
/// of the native-call method" precedent.
///
/// <para>
/// <b>Why this lives in Soneto.Core, not Soneto.Platform.Windows:</b> everything this class
/// touches -- <see cref="PerAppOverride"/>, <see cref="InjectionOptions"/> -- is already
/// platform-agnostic, and keeping the merge logic here means it's exercised by the same
/// no-audio-device/no-model-file-required <c>Soneto.Core.Tests</c> suite as the rest of this
/// project's pure decision logic. Only the actual process-name LOOKUP (a real Win32
/// <c>GetWindowThreadProcessId</c>/<see cref="System.Diagnostics.Process"/> call) is
/// Windows-specific and stays in <c>WindowsTextInjector</c> itself -- this class never touches
/// a real window handle or process.
/// </para>
///
/// <para>
/// <b>Case sensitivity is the caller's responsibility, not this class's:</b> this class does a
/// plain <see cref="IReadOnlyDictionary{TKey,TValue}.TryGetValue"/> against whatever dictionary
/// it's handed -- it applies no comparer of its own. Per Windows filename conventions,
/// executable-name matching should be case-insensitive; the composition root
/// (<c>Soneto.Composition.DaemonComposition</c>) is responsible for constructing the
/// dictionary it hands to <c>WindowsTextInjector</c>'s constructor with
/// <see cref="StringComparer.OrdinalIgnoreCase"/>, exactly once, at startup -- see that class's
/// own comment at the construction call site.
/// </para>
/// </summary>
public static class PerAppOverrideResolver
{
    /// <summary>
    /// Looks up <paramref name="processExecutableName"/> in <paramref name="perApp"/> and, if a
    /// match exists, returns a new <see cref="InjectionOptions"/> with that entry's overridden
    /// fields (<see cref="PerAppOverride.PasteChord"/>, <see cref="PerAppOverride.ClipboardRestoreDelayMs"/>,
    /// <see cref="PerAppOverride.Method"/>) merged on top of <paramref name="baseOptions"/>.
    /// <paramref name="baseOptions"/> itself is never mutated -- it's a shared <c>record</c>
    /// that may be reused across many injections; a `with`-derived copy is always returned
    /// instead. Falls back to returning <paramref name="baseOptions"/> itself, completely
    /// unchanged, whenever: <paramref name="perApp"/> is null or empty, <paramref
    /// name="processExecutableName"/> is null, or no entry matches -- this must never regress
    /// the pre-Phase-4 base path (byte-for-byte identical <see cref="InjectionOptions"/> for an
    /// unmatched/unresolvable process).
    /// </summary>
    public static InjectionOptions Resolve(
        InjectionOptions baseOptions,
        string? processExecutableName,
        IReadOnlyDictionary<string, PerAppOverride>? perApp)
    {
        ArgumentNullException.ThrowIfNull(baseOptions);

        if (perApp is null || perApp.Count == 0 || processExecutableName is null)
            return baseOptions;

        if (!perApp.TryGetValue(processExecutableName, out var overrideEntry) || overrideEntry is null)
            return baseOptions;

        return Merge(baseOptions, overrideEntry);
    }

    /// <summary>
    /// Applies <paramref name="overrideEntry"/>'s non-null fields on top of
    /// <paramref name="baseOptions"/>, leaving every field the override leaves null unchanged
    /// from the base value. Internal (not private) so <c>Soneto.Core.Tests</c> can exercise the
    /// merge in isolation from the dictionary-lookup step above.
    /// </summary>
    internal static InjectionOptions Merge(InjectionOptions baseOptions, PerAppOverride overrideEntry) =>
        baseOptions with
        {
            PasteChord = overrideEntry.PasteChord ?? baseOptions.PasteChord,
            ClipboardRestoreDelay = overrideEntry.ClipboardRestoreDelayMs is int ms
                ? TimeSpan.FromMilliseconds(ms)
                : baseOptions.ClipboardRestoreDelay,
            Method = overrideEntry.Method switch
            {
                InjectionMethod.UnicodeSynth => Soneto.Core.Abstractions.InjectionMethod.UnicodeSynth,
                InjectionMethod.ClipboardPaste => Soneto.Core.Abstractions.InjectionMethod.ClipboardPaste,
                null => baseOptions.Method,
                // Unreachable for any value that came from a real config file: the config
                // enums hard-fail deserialization on an unrecognised value (see
                // SonetoConfig.cs's note above InjectionMethod), so only a programmatically
                // cast-in out-of-range value can land here. Throwing keeps that a loud bug
                // rather than a silently-ignored override -- the shape this project's
                // "one value silently serving two gates" bug pattern warns about.
                _ => throw new ArgumentOutOfRangeException(
                    nameof(overrideEntry),
                    overrideEntry.Method,
                    "Unrecognised PerAppOverride.Method value."),
            },
        };
}
