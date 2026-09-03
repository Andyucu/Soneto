using Soneto.Platform.Linux;

namespace Soneto.Platform.Linux.Tests;

public class ClipboardHashGuardTests
{
    [Fact]
    public void IsSafeToRestore_TrueWhenHashesMatch()
    {
        var hash = ClipboardHashGuard.ComputeHash("hello world");
        var sameHashAgain = ClipboardHashGuard.ComputeHash("hello world");

        Assert.True(ClipboardHashGuard.IsSafeToRestore(hash, sameHashAgain));
    }

    [Fact]
    public void IsSafeToRestore_FalseWhenUserCopiedSomethingElse()
    {
        var hashAfterOurWrite = ClipboardHashGuard.ComputeHash("our transcript");
        var currentHash = ClipboardHashGuard.ComputeHash("something the user just copied");

        Assert.False(ClipboardHashGuard.IsSafeToRestore(hashAfterOurWrite, currentHash));
    }

    [Fact]
    public void ComputeHash_IsDeterministic()
    {
        var a = ClipboardHashGuard.ComputeHash("Ăsta e un test");
        var b = ClipboardHashGuard.ComputeHash("Ăsta e un test");
        Assert.Equal(a, b);
    }

    [Fact]
    public void ComputeHash_DiffersForDifferentText()
    {
        var a = ClipboardHashGuard.ComputeHash("text A");
        var b = ClipboardHashGuard.ComputeHash("text B");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeHash_Is32BytesSha256()
    {
        var hash = ClipboardHashGuard.ComputeHash("anything");
        Assert.Equal(32, hash.Length);
    }
}
