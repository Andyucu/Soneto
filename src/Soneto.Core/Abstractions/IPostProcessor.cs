namespace Soneto.Core.Abstractions;

/// <summary>
/// One stage in the post-processing chain applied to a transcript before injection
/// (whitespace cleanup, dictionary substitution, etc. — plan §1.14 item 8). Stages run
/// in ascending <see cref="Order"/>.
/// </summary>
public interface IPostProcessor
{
    int Order { get; }
    string Name { get; }
    PostProcessResult Process(PostProcessResult input);
}

public sealed record PostProcessResult(string Text, IReadOnlyList<AppliedRule> Applied);

/// <summary>
/// AppliedRule is unused in Phase 1 but the plumbing exists so Phase 2 drops in
/// without touching the pipeline.
/// </summary>
public sealed record AppliedRule(string Processor, string Rule, string From, string To);
