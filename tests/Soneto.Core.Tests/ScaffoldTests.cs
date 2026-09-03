using Soneto.Core.Abstractions;

namespace Soneto.Core.Tests;

/// <summary>
/// Placeholder proving the test project is wired up and can see Soneto.Core's
/// abstractions, with no audio device and no model file present. Real tests land
/// alongside each abstraction's implementation in later work items.
/// </summary>
public class ScaffoldTests
{
    [Fact]
    public void Sanity_check_passes()
    {
        Assert.Equal(2, 1 + 1);
    }

    [Fact]
    public void Core_abstractions_are_visible_from_the_test_project()
    {
        Assert.True(typeof(ITranscriber).IsInterface);
        Assert.True(typeof(IAudioCapture).IsInterface);
        Assert.True(typeof(IHotkeySource).IsInterface);
        Assert.True(typeof(ITextInjector).IsInterface);
        Assert.True(typeof(IPostProcessor).IsInterface);
    }
}
