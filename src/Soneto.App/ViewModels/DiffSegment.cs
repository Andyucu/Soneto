namespace Soneto.App.ViewModels;

/// <summary>
/// One piece of a diff-highlighted rendering (plan §3.10) — either an unchanged run of text, or a
/// span that exactly matches one <see cref="Soneto.Core.Abstractions.AppliedRule"/>'s recorded
/// <c>From</c>/<c>To</c> span. Deliberately NOT the output of a general-purpose text-diff
/// algorithm run over <c>RawText</c>/<c>FinalText</c> — per §3.10's own explicit instruction,
/// <see cref="HistoryViewModel.BuildHighlightedSegments"/> uses the structured span data
/// <see cref="Soneto.Core.Abstractions.AppliedRule"/> already carries directly, which is both
/// simpler and more accurate than trying to reverse-engineer what changed from the two strings
/// alone (a real diff algorithm could easily highlight a different, coincidentally-matching
/// span than the one the dictionary rule actually changed).
/// </summary>
/// <param name="Text">The literal substring this segment covers.</param>
/// <param name="IsHighlighted">True if this segment exactly matches a rule's <c>From</c>/<c>To</c>
/// span and should be rendered with the diff-highlight styling.</param>
public sealed record DiffSegment(string Text, bool IsHighlighted);
