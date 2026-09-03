using Soneto.Core.Abstractions;

namespace Soneto.Core.PostProcessing;

/// <summary>
/// Order 90 stage of the plan §1.7 post-processing chain: appends a single trailing space if
/// the transcript ends in a non-whitespace character, so consecutive push-to-talk dictations
/// flow together without the user having to speak a leading space.
///
/// <para>
/// <b>Renumbered from order 40 (Phase 1) to order 90 (Phase 2 item 0):</b> orders 40-70 are
/// reserved by <c>Docs/soneto-implementation-plan-phase2.md</c> for the new dictionary-engine
/// processors (<c>DictionaryEngineProcessor</c> at 40, <c>RegexRuleProcessor</c> at 50,
/// <c>SpokenCommandsExtensionProcessor</c> at 60, <c>FillerWordStripper</c> at 70). This
/// processor conceptually belongs at the very end of the whole chain regardless of what gets
/// inserted in between -- appending a trailing space for inter-utterance flow is a last-stage
/// concern, not a correction -- so it moved to 90 rather than the new work being numbered
/// around it.
/// </para>
///
/// <para>
/// <b>Deliberate deviation from the plan's literal wording:</b> the plan describes this as
/// firing when the transcript "ends in a word character," but the actual rule implemented
/// here is broader -- any non-whitespace character, including trailing punctuation like
/// <c>.</c>/<c>!</c>/<c>?</c>. This is intentional, not an oversight: Parakeet v3 emits
/// terminal sentence punctuation on nearly every utterance, so a strict word-character-only
/// reading would leave this stage effectively dead code for normal dictation. Treating "ends
/// in any non-whitespace character" as the real rule is what actually delivers the plan's
/// stated intent ("it makes consecutive dictations flow").
/// </para>
///
/// <para>
/// When the text is empty or already ends in whitespace (including <c>\n</c>), this is a
/// no-op — appending a space after an existing trailing space/newline would just add
/// unwanted horizontal whitespace (and, for a trailing newline, incorrectly turn a paragraph
/// break into "break + space").
/// </para>
/// </summary>
public sealed class TrailingSpaceProcessor : IPostProcessor
{
    public int Order => 90;
    public string Name => "TrailingSpace";

    private readonly bool _enabled;

    public TrailingSpaceProcessor(bool enabled = true)
    {
        _enabled = enabled;
    }

    public PostProcessResult Process(PostProcessResult input)
    {
        if (!_enabled || string.IsNullOrEmpty(input.Text))
            return input;

        var last = input.Text[^1];
        if (char.IsWhiteSpace(last))
            return input;

        return input with { Text = input.Text + " " };
    }
}
