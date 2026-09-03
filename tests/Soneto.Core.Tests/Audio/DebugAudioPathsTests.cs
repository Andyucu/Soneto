using Soneto.Core.Audio;

namespace Soneto.Core.Tests.Audio;

/// <summary>Unit tests for <see cref="DebugAudioPaths"/>, mirroring <c>HistoryPathsTests</c>-style
/// coverage of <c>ConfigPaths</c>/<c>HistoryPaths</c>'s own override-vs-OS-default resolution.</summary>
public sealed class DebugAudioPathsTests
{
    [Fact]
    public void Resolve_WithOverride_ReturnsFullPathOfOverride()
    {
        var result = DebugAudioPaths.Resolve(@"some\relative\dir");

        Assert.Equal(Path.GetFullPath(@"some\relative\dir"), result);
    }

    [Fact]
    public void Resolve_WithoutOverride_EndsWithDebugAudioDirectoryName()
    {
        var result = DebugAudioPaths.Resolve();

        Assert.EndsWith(DebugAudioPaths.DirectoryName, result);
    }

    [Fact]
    public void Resolve_WithoutOverride_IsStableAcrossCalls()
    {
        Assert.Equal(DebugAudioPaths.Resolve(), DebugAudioPaths.Resolve());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_WithNullOrBlankOverride_FallsBackToTheDefault(string? overridePath)
    {
        Assert.Equal(DebugAudioPaths.Resolve(), DebugAudioPaths.Resolve(overridePath));
    }
}
