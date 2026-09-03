namespace Soneto.Core.Dictionary;

/// <summary>
/// Phase 4 item 3 (§4.4): pure per-app profile lookup for the dictionary-schema
/// <see cref="PerAppOverride"/> entry type -- the counterpart, on the dictionary/post-processing
/// side, to <c>Soneto.Core.Configuration.PerAppOverrideResolver</c> (Phase 4 item 2, the
/// injection-side resolver). Deliberately much smaller than that class: the injection-side
/// resolver has to MERGE several possibly-null override fields on top of a base
/// <c>InjectionOptions</c> record; this type only has to answer "is there an enabled profile for
/// this process, and if so, what does it say" -- <see cref="PostProcessing.PostProcessorChain"/>
/// itself does the "which pre-built processor set does that answer select" step (see its own
/// doc comment), not this class.
///
/// <para>
/// <b>Same case-sensitivity contract as the injection-side resolver:</b> this class applies no
/// comparer of its own -- it does a plain <see cref="IReadOnlyDictionary{TKey,TValue}.TryGetValue"/>
/// against whatever dictionary it's handed. The composition root
/// (<c>Soneto.Composition.DaemonComposition</c>) is responsible for wrapping the table in a
/// <see cref="StringComparer.OrdinalIgnoreCase"/>-keyed dictionary, exactly once, at startup --
/// mirrors the injection-side precedent exactly (see that class's own construction-site comment
/// in <c>DaemonComposition</c>).
/// </para>
///
/// <para>
/// <b>Disabled entries never match:</b> a <see cref="PerAppOverride"/> with
/// <see cref="DictionaryEntry.Enabled"/> == <c>false</c> must never be handed to this resolver
/// in the first place -- filtering happens once, at table-construction time in the composition
/// root, the same place every other dictionary-backed processor
/// (<c>DictionaryEngineProcessor</c>/<c>RegexRuleProcessor</c>/<c>SpokenCommandsExtensionProcessor</c>)
/// already filters disabled entries at construction rather than re-checking <c>Enabled</c> on
/// every call.
/// </para>
/// </summary>
public static class PerAppOverrideResolver
{
    /// <summary>
    /// Looks up <paramref name="processExecutableName"/> in <paramref name="perApp"/> and
    /// returns the matching <see cref="PerAppOverride"/>, or <c>null</c> whenever:
    /// <paramref name="perApp"/> is null or empty, <paramref name="processExecutableName"/> is
    /// null, or no entry matches. <c>null</c> means exactly "no profile is active for this
    /// utterance" -- callers must fall back to the base (no-profile) behavior, never treat it as
    /// an error.
    /// </summary>
    public static PerAppOverride? Resolve(
        string? processExecutableName,
        IReadOnlyDictionary<string, PerAppOverride>? perApp)
    {
        if (perApp is null || perApp.Count == 0 || processExecutableName is null)
            return null;

        return perApp.TryGetValue(processExecutableName, out var entry) ? entry : null;
    }
}
