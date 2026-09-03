using Microsoft.Extensions.Logging.Abstractions;
using Soneto.Core.Abstractions;
using Soneto.Platform.Linux;

namespace Soneto.Platform.Linux.Tests;

/// <summary>
/// Exercises <see cref="LinuxTextInjector"/>'s save-&gt;set-&gt;chord-&gt;restore
/// orchestration entirely through the <see cref="FakeProcessRunner"/> seam -- no real
/// <c>xclip</c>/<c>wl-copy</c>/<c>ydotool</c> process is ever spawned. In particular,
/// proves the same ordering property the review had to trace by hand (matching item 7c's
/// Windows sequence-guard shape): the restore-guard's clipboard read-back happens BEFORE
/// any write in that same restore attempt, so a user's Ctrl+C during the restore window is
/// never overwritten.
/// </summary>
public class LinuxTextInjectorTests
{
    private static InjectionOptions ClipboardOptions(
        bool restoreClipboard = true, ClipboardPolicy policy = ClipboardPolicy.TextOnly) => new(
        InjectionMethod.ClipboardPaste,
        "ctrl+v",
        PreDelay: TimeSpan.Zero,
        ClipboardRestoreDelay: TimeSpan.Zero,
        RestoreClipboard: restoreClipboard,
        Policy: policy);

    private static bool IsXclipCall((string FileName, IReadOnlyList<string> Args, string? Stdin) call, params string[] argsContains)
    {
        if (call.FileName != "xclip")
            return false;
        return argsContains.All(a => call.Args.Contains(a));
    }

    [Fact]
    public async Task InjectAsync_RestoreGuard_ReadsClipboardBeforeWritingOnRestore()
    {
        var runner = new FakeProcessRunner();
        // 1: SaveAsync -> ListMimeTypesAsync (xclip -o -t TARGETS)
        runner.Results.Enqueue(new ProcessRunResult(0, "text/plain\n", ""));
        // 2: SaveAsync -> RunPasteAsync (xclip -o) -- the original clipboard content.
        runner.Results.Enqueue(new ProcessRunResult(0, "original clipboard text", ""));
        // 3: SetTextAsync -- our own write of the transcript.
        runner.Results.Enqueue(new ProcessRunResult(0, "", ""));
        // 4: SendPasteChordAsync -- ydotool key.
        runner.Results.Enqueue(new ProcessRunResult(0, "", ""));
        // 5: TryRestoreAsync -> read-back BEFORE the restore write.
        runner.Results.Enqueue(new ProcessRunResult(0, "our transcript text", ""));
        // 6: TryRestoreAsync -> the actual restore write (only reached if 5's hash matched).
        runner.Results.Enqueue(new ProcessRunResult(0, "", ""));

        var injector = new LinuxTextInjector(NullLogger.Instance, runner, ClipboardBackendKind.X11);
        var outcome = await injector.InjectAsync("our transcript text", target: null, ClipboardOptions(), CancellationToken.None);

        Assert.Equal(InjectionOutcome.Injected, outcome);
        Assert.Equal(6, runner.Calls.Count);

        // The restore attempt's read-back (call index 4, 0-based) must happen strictly
        // before its own write (call index 5) -- this is the property that matters.
        Assert.True(IsXclipCall(runner.Calls[4], "-o"));
        Assert.DoesNotContain("-t", runner.Calls[4].Args); // plain paste, not the TARGETS listing.
        Assert.True(IsXclipCall(runner.Calls[5], "-selection", "clipboard"));
        Assert.DoesNotContain("-o", runner.Calls[5].Args); // the write call has no -o (read) flag.
        Assert.Equal("original clipboard text", runner.Calls[5].Stdin);
    }

    [Fact]
    public async Task InjectAsync_SkipsRestoreWhenClipboardChangedDuringWindow()
    {
        var runner = new FakeProcessRunner();
        runner.Results.Enqueue(new ProcessRunResult(0, "text/plain\n", "")); // list mimetypes
        runner.Results.Enqueue(new ProcessRunResult(0, "original clipboard text", "")); // backup paste
        runner.Results.Enqueue(new ProcessRunResult(0, "", "")); // set our text
        runner.Results.Enqueue(new ProcessRunResult(0, "", "")); // paste chord
        runner.Results.Enqueue(new ProcessRunResult(0, "the user's new copy", "")); // restore read-back: MISMATCH

        var injector = new LinuxTextInjector(NullLogger.Instance, runner, ClipboardBackendKind.X11);
        var outcome = await injector.InjectAsync("our transcript text", target: null, ClipboardOptions(), CancellationToken.None);

        Assert.Equal(InjectionOutcome.Injected, outcome); // the paste itself still succeeded.
        // Only 5 calls: the mismatch must abort BEFORE any restore write.
        Assert.Equal(5, runner.Calls.Count);
    }

    [Fact]
    public async Task InjectAsync_UnicodeSynth_NeverTouchesClipboard()
    {
        var runner = new FakeProcessRunner();
        runner.Results.Enqueue(new ProcessRunResult(0, "", "")); // ydotool type

        var opts = new InjectionOptions(InjectionMethod.UnicodeSynth, "ctrl+v", TimeSpan.Zero, TimeSpan.Zero, RestoreClipboard: true);
        var injector = new LinuxTextInjector(NullLogger.Instance, runner, ClipboardBackendKind.X11);
        var outcome = await injector.InjectAsync("some text", target: null, opts, CancellationToken.None);

        Assert.Equal(InjectionOutcome.Injected, outcome);
        Assert.Single(runner.Calls);
        Assert.Equal("ydotool", runner.Calls[0].FileName);
        Assert.Contains("type", runner.Calls[0].Args);
        Assert.DoesNotContain(runner.Calls, c => c.FileName is "xclip" or "wl-copy" or "wl-paste");
    }

    [Fact]
    public async Task InjectAsync_SkipsRestoreForNonTextClipboardUnderTextOnlyPolicy()
    {
        var runner = new FakeProcessRunner();
        runner.Results.Enqueue(new ProcessRunResult(0, "image/png\n", "")); // list mimetypes: non-text present
        runner.Results.Enqueue(new ProcessRunResult(0, "", "")); // backup paste (no text content)
        runner.Results.Enqueue(new ProcessRunResult(0, "", "")); // set our text
        runner.Results.Enqueue(new ProcessRunResult(0, "", "")); // paste chord

        var injector = new LinuxTextInjector(NullLogger.Instance, runner, ClipboardBackendKind.X11);
        var outcome = await injector.InjectAsync("our transcript text", target: null, ClipboardOptions(policy: ClipboardPolicy.TextOnly), CancellationToken.None);

        Assert.Equal(InjectionOutcome.Injected, outcome);
        // No restore read-back/write attempted at all -- HasNonTextFormats short-circuits
        // RestoreWithHashGuardAsync before any process call.
        Assert.Equal(4, runner.Calls.Count);
    }

    [Fact]
    public async Task InjectAsync_FallsBackToYdotoolTypeWhenClipboardSetFails()
    {
        var runner = new FakeProcessRunner();
        runner.Results.Enqueue(new ProcessRunResult(0, "text/plain\n", "")); // list mimetypes
        runner.Results.Enqueue(new ProcessRunResult(0, "original clipboard text", "")); // backup paste
        runner.Results.Enqueue(new ProcessRunResult(1, "", "some xclip failure")); // set FAILS
        runner.Results.Enqueue(new ProcessRunResult(0, "", "")); // ydotool type fallback succeeds

        var injector = new LinuxTextInjector(NullLogger.Instance, runner, ClipboardBackendKind.X11);
        var outcome = await injector.InjectAsync("our transcript text", target: null, ClipboardOptions(), CancellationToken.None);

        Assert.Equal(InjectionOutcome.Injected, outcome);
        Assert.Equal("ydotool", runner.Calls[^1].FileName);
        Assert.Contains("type", runner.Calls[^1].Args);
    }

    [Fact]
    public async Task CaptureTarget_AlwaysReturnsNull()
    {
        var runner = new FakeProcessRunner();
        var injector = new LinuxTextInjector(NullLogger.Instance, runner, ClipboardBackendKind.X11);
        Assert.Null(injector.CaptureTarget());
    }
}
