using Soneto.Platform.Linux;

namespace Soneto.Platform.Linux.Tests;

/// <summary>
/// Test double for <see cref="IProcessRunner"/> -- lets clipboard/injection decision logic
/// be exercised without ever spawning a real <c>wl-copy</c>/<c>xclip</c>/<c>ydotool</c>
/// process (none of which exist on the machine these tests run on).
/// </summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    public List<(string FileName, IReadOnlyList<string> Args, string? Stdin)> Calls { get; } = new();

    /// <summary>Queue of canned results, consumed in order, one per call to <see cref="RunAsync"/>.
    /// If exhausted, returns a default success-with-empty-output result.</summary>
    public Queue<ProcessRunResult> Results { get; } = new();

    public Task<ProcessRunResult> RunAsync(string fileName, IReadOnlyList<string> args, string? stdin, CancellationToken ct)
    {
        Calls.Add((fileName, args, stdin));
        var result = Results.Count > 0 ? Results.Dequeue() : new ProcessRunResult(0, "", "");
        return Task.FromResult(result);
    }
}
