using Microsoft.Extensions.Logging.Abstractions;
using Soneto.Platform.Linux;

namespace Soneto.Platform.Linux.Tests;

public class LinuxClipboardManagerTests
{
    [Fact]
    public void ClassifyMimeTypes_FalseForTextOnlyFamily()
    {
        var mimeTypes = new[] { "text/plain", "text/plain;charset=utf-8", "UTF8_STRING", "STRING" };
        Assert.False(LinuxClipboardManager.ClassifyMimeTypes(mimeTypes));
    }

    [Fact]
    public void ClassifyMimeTypes_TrueWhenNonTextFormatPresent()
    {
        var mimeTypes = new[] { "text/plain", "image/png" };
        Assert.True(LinuxClipboardManager.ClassifyMimeTypes(mimeTypes));
    }

    [Fact]
    public void ClassifyMimeTypes_FalseForEmptyList()
    {
        Assert.False(LinuxClipboardManager.ClassifyMimeTypes(Array.Empty<string>()));
    }

    [Fact]
    public async Task RestoreWithHashGuardAsync_RestoresWhenHashUnchanged()
    {
        var runner = new FakeProcessRunner();
        // The pre-restore paste-back read: returns the same text our write produced.
        runner.Results.Enqueue(new ProcessRunResult(0, "original clipboard text", ""));
        // The actual restore write.
        runner.Results.Enqueue(new ProcessRunResult(0, "", ""));

        var manager = new LinuxClipboardManager(NullLogger.Instance, runner, ClipboardBackendKind.X11);
        var backup = new LinuxClipboardTextBackup { Text = "original clipboard text", HadText = true };
        var expectedHash = ClipboardHashGuard.ComputeHash("original clipboard text");

        var outcome = await manager.RestoreWithHashGuardAsync(backup, expectedHash, declineNonText: true, CancellationToken.None);

        Assert.Equal(ClipboardRestoreOutcome.Restored, outcome);
        Assert.Equal(2, runner.Calls.Count);
    }

    [Fact]
    public async Task RestoreWithHashGuardAsync_SkipsWhenClipboardChangedDuringWindow()
    {
        var runner = new FakeProcessRunner();
        // The pre-restore paste-back read: returns DIFFERENT text -- user copied something else.
        runner.Results.Enqueue(new ProcessRunResult(0, "user's new copy", ""));

        var manager = new LinuxClipboardManager(NullLogger.Instance, runner, ClipboardBackendKind.X11);
        var backup = new LinuxClipboardTextBackup { Text = "original clipboard text", HadText = true };
        var expectedHash = ClipboardHashGuard.ComputeHash("original clipboard text");

        var outcome = await manager.RestoreWithHashGuardAsync(backup, expectedHash, declineNonText: true, CancellationToken.None);

        Assert.Equal(ClipboardRestoreOutcome.SkippedSequenceChanged, outcome);
        // Only the read-back call, never a write -- must never overwrite what the user just copied.
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task RestoreWithHashGuardAsync_SkipsNonTextWithoutTouchingClipboard()
    {
        var runner = new FakeProcessRunner();
        var manager = new LinuxClipboardManager(NullLogger.Instance, runner, ClipboardBackendKind.X11);
        var backup = new LinuxClipboardTextBackup { HadText = false, HasNonTextFormats = true, MimeTypesPresent = new[] { "image/png" } };
        var expectedHash = ClipboardHashGuard.ComputeHash("irrelevant");

        var outcome = await manager.RestoreWithHashGuardAsync(backup, expectedHash, declineNonText: true, CancellationToken.None);

        Assert.Equal(ClipboardRestoreOutcome.SkippedNonText, outcome);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task RestoreWithHashGuardAsync_FailsWhenNoBackedUpText()
    {
        var runner = new FakeProcessRunner();
        var manager = new LinuxClipboardManager(NullLogger.Instance, runner, ClipboardBackendKind.X11);
        var backup = new LinuxClipboardTextBackup { HadText = false };
        var expectedHash = ClipboardHashGuard.ComputeHash("irrelevant");

        var outcome = await manager.RestoreWithHashGuardAsync(backup, expectedHash, declineNonText: true, CancellationToken.None);

        Assert.Equal(ClipboardRestoreOutcome.Failed, outcome);
    }
}
