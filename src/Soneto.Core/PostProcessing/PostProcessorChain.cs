using Soneto.Core.Abstractions;
using Soneto.Core.Dictionary;

namespace Soneto.Core.PostProcessing;

/// <summary>
/// Phase 1 stub orchestrator (plan §1.7/§1.14 item 8): runs a transcript through an ordered
/// set of <see cref="IPostProcessor"/> stages, ascending by <see cref="IPostProcessor.Order"/>,
/// threading each stage's <see cref="PostProcessResult"/> into the next. Intentionally simple
/// — this is the shape item 9 (SessionController) is expected to consume; no retries, no
/// per-stage error isolation, no parallelism.
///
/// <para>
/// <b>Phase 4 item 3 (§4.4): per-app profile selection, added without changing this class's
/// pre-existing behavior for any caller that doesn't opt in.</b> The single-argument
/// constructor and both pre-existing <see cref="Process(PostProcessResult)"/>/
/// <see cref="Process(string)"/> overloads are completely unchanged -- byte-for-byte identical
/// behavior to before this item, since they always run the exact same base processor list they
/// always did. The new capability is additive: an optional <see cref="Dictionary.PerAppOverride"/>
/// table can be handed to the two-argument constructor, and a new
/// <see cref="Process(string, string?)"/> overload lets a caller additionally supply the
/// focused app's resolved process executable name, selecting a per-app-widened processor set
/// for that one call.
/// </para>
///
/// <para>
/// <b>Nothing is built per-utterance -- selection, not construction, happens per call.</b> Per
/// this item's plan text ("this is the first time <c>PostProcessorChain</c> construction
/// becomes utterance-scoped rather than session-lifetime-scoped"), the risk being called out is
/// literal per-utterance CONSTRUCTION work. This class avoids that: at most four processor-list
/// variants (base; base+<see cref="AutoCapitalizeProcessor"/>; base+<see cref="TrailingPunctuationProcessor"/>;
/// base+both) are built ONCE, in the constructor, from the same startup snapshot every other
/// processor in the base list is already built from. <see cref="Process(string, string?)"/>
/// only does a dictionary lookup (via <see cref="Dictionary.PerAppOverrideResolver"/>) and picks
/// one of those four already-built lists -- no allocation of a new processor, no re-sorting, no
/// I/O, on the per-utterance path.
/// </para>
///
/// <para>
/// <b>Why this does NOT conflict with the standing "no live rebuild without restart"
/// limitation</b> (<c>Docs/PROJECT-MEMORY.md</c>'s "Locked-in decisions"): that limitation is
/// about the base processor list and the <see cref="Dictionary.PerAppOverride"/> table itself --
/// both are still built exactly once, at composition-root startup, from a snapshot of
/// <c>config.json</c>/<c>dictionary.json</c>. A hot-reloaded dictionary/config file still does
/// NOT rebuild anything here live; the composition root's existing loud warning on a hot-reload
/// (see <c>DaemonComposition.BuildPostProcessors</c>'s own doc comment) still applies unchanged,
/// including to per-app profile edits. What's new is a DIFFERENT, narrower mechanism operating
/// entirely WITHIN that same fixed startup snapshot: which of the four already-built lists runs
/// for a given utterance is selected fresh each call, based on the utterance's own focused-app
/// process name -- exactly analogous to how the injection-side <c>PerAppOverrideResolver</c>
/// (Phase 4 item 2) already re-resolves an override on every injection from a table that was
/// itself still only built once at startup. Neither mechanism reads a file or reconstructs a
/// processor at call time.
/// </para>
/// </summary>
public sealed class PostProcessorChain
{
    private readonly IReadOnlyList<IPostProcessor> _baseProcessors;

    // Phase 4 item 3 (§4.4): the optional per-app profile table -- null/empty for every caller
    // that doesn't pass one (including every pre-Phase-4-item-3 construction site), in which
    // case SelectProcessors always returns _baseProcessors, unchanged from before this item.
    private readonly IReadOnlyDictionary<string, PerAppOverride>? _perApp;

    // Pre-built once, in the constructor -- see class doc comment's "nothing is built
    // per-utterance" section. Left null when _perApp is null/empty, so a caller that never
    // passes a per-app table pays zero extra allocation for these four fields beyond the base
    // list it already needed.
    private readonly IReadOnlyList<IPostProcessor>? _withAutoCapitalize;
    private readonly IReadOnlyList<IPostProcessor>? _withTrailingPunctuation;
    private readonly IReadOnlyList<IPostProcessor>? _withBoth;

    public PostProcessorChain(IEnumerable<IPostProcessor> processors)
        : this(processors, perApp: null)
    {
    }

    /// <summary>
    /// Phase 4 item 3 (§4.4) overload: <paramref name="perApp"/> is the (composition-root-owned,
    /// already <see cref="StringComparer.OrdinalIgnoreCase"/>-wrapped -- see
    /// <see cref="Dictionary.PerAppOverrideResolver"/>'s own doc comment for that contract)
    /// dictionary-side per-app profile table. Pass <c>null</c> (or an empty table) for the
    /// exact same behavior as the single-argument constructor.
    /// </summary>
    public PostProcessorChain(
        IEnumerable<IPostProcessor> processors,
        IReadOnlyDictionary<string, PerAppOverride>? perApp)
    {
        ArgumentNullException.ThrowIfNull(processors);

        _baseProcessors = processors.OrderBy(p => p.Order).ToList();
        _perApp = perApp;

        if (_perApp is { Count: > 0 })
        {
            _withAutoCapitalize = AppendSorted(_baseProcessors, new AutoCapitalizeProcessor());
            _withTrailingPunctuation = AppendSorted(_baseProcessors, new TrailingPunctuationProcessor());
            _withBoth = AppendSorted(_withAutoCapitalize, new TrailingPunctuationProcessor());
        }
    }

    private static IReadOnlyList<IPostProcessor> AppendSorted(
        IReadOnlyList<IPostProcessor> baseList, IPostProcessor extra) =>
        baseList.Append(extra).OrderBy(p => p.Order).ToList();

    public PostProcessResult Process(PostProcessResult input) => RunChain(input, _baseProcessors);

    public PostProcessResult Process(string text) =>
        Process(new PostProcessResult(text, Array.Empty<AppliedRule>()));

    /// <summary>
    /// Phase 4 item 3 (§4.4): same as <see cref="Process(string)"/>, but additionally resolves
    /// <paramref name="processExecutableName"/> against the per-app table passed to this
    /// instance's constructor (if any) and, on a match, runs the widened processor list that
    /// matching profile's <see cref="Dictionary.PerAppOverride.AutoCapitalize"/>/
    /// <see cref="Dictionary.PerAppOverride.TrailingPunctuation"/> flags select -- see class doc
    /// comment for the full mechanism. Passing <c>null</c> here is exactly equivalent to calling
    /// <see cref="Process(string)"/>.
    /// </summary>
    public PostProcessResult Process(string text, string? processExecutableName) =>
        RunChain(new PostProcessResult(text, Array.Empty<AppliedRule>()), SelectProcessors(processExecutableName));

    private IReadOnlyList<IPostProcessor> SelectProcessors(string? processExecutableName)
    {
        var profile = PerAppOverrideResolver.Resolve(processExecutableName, _perApp);
        if (profile is null)
            return _baseProcessors;

        return (profile.AutoCapitalize, profile.TrailingPunctuation) switch
        {
            (true, true) => _withBoth!,
            (true, false) => _withAutoCapitalize!,
            (false, true) => _withTrailingPunctuation!,
            (false, false) => _baseProcessors,
        };
    }

    private static PostProcessResult RunChain(PostProcessResult input, IReadOnlyList<IPostProcessor> processors)
    {
        var result = input;
        foreach (var processor in processors)
        {
            result = processor.Process(result);
        }
        return result;
    }
}
