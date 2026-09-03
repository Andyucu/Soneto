using Soneto.Platform.Linux;

namespace Soneto.Platform.Linux.Tests;

public class ClipboardBackendSelectorTests
{
    [Theory]
    [InlineData("wayland")]
    [InlineData("Wayland")]
    [InlineData("WAYLAND")]
    public void Select_ChoosesWaylandForWaylandSessionType(string value)
    {
        Assert.Equal(ClipboardBackendKind.Wayland, ClipboardBackendSelector.Select(value));
    }

    [Theory]
    [InlineData("x11")]
    [InlineData("X11")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tty")]
    [InlineData("unknown")]
    public void Select_FallsBackToX11ForEverythingElse(string? value)
    {
        Assert.Equal(ClipboardBackendKind.X11, ClipboardBackendSelector.Select(value));
    }
}
