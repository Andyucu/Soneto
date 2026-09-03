using Microsoft.Extensions.Logging.Abstractions;
using Soneto.Core.Audio;

namespace Soneto.Core.Tests.Audio;

/// <summary>
/// Real-hardware verification for <see cref="PortAudioCapture"/>: opens the actual default
/// audio input device, captures briefly, and closes it. Tagged
/// <c>[Trait("Category","Hardware")]</c> — same exclusion convention as item 3's
/// <c>Category=Corpus</c> tests (needs a real dependency the default `dotnet test` run must
/// not require: a real audio device instead of a real model) — excluded from the default run
/// via <c>Soneto.Core.Tests.csproj</c>'s <c>VSTestTestCaseFilter</c> default, confirmed
/// empirically (see item 4b's verification notes in Docs/PROJECT-MEMORY.md).
///
/// Run manually with:
///   dotnet test --filter "Category=Hardware"
/// </summary>
[Trait("Category", "Hardware")]
public sealed class PortAudioCaptureHardwareTests
{
    [Fact]
    public async Task Opens_default_device_captures_briefly_and_reports_a_sane_negotiated_rate()
    {
        await using var capture = new PortAudioCapture(NullLogger<PortAudioCapture>.Instance);

        await capture.StartAsync(device: null, CancellationToken.None);
        Assert.True(capture.IsRunning);

        double firstSampleMs = await capture.WaitForFirstSampleAsync(TimeSpan.FromSeconds(3));
        Assert.True(firstSampleMs >= 0);
        Assert.NotNull(capture.NegotiatedSampleRate);
        Assert.True(capture.NegotiatedSampleRate is 16000 or >= 8000);
        Assert.NotNull(capture.CapturePath);

        capture.BeginCapture(TimeSpan.Zero);
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        var samples = capture.EndCapture();

        await capture.StopAsync();
        Assert.False(capture.IsRunning);

        // ~500ms at 16kHz should produce roughly 8000 samples; allow generous slack for
        // scheduler jitter and the resample-path edge taper.
        Assert.True(samples.Length > 1000, $"Expected a meaningful number of captured samples, got {samples.Length}");
    }

    [Fact]
    public async Task Abort_discards_captured_samples()
    {
        await using var capture = new PortAudioCapture(NullLogger<PortAudioCapture>.Instance);

        await capture.StartAsync(device: null, CancellationToken.None);
        await capture.WaitForFirstSampleAsync(TimeSpan.FromSeconds(3));

        capture.BeginCapture(TimeSpan.Zero);
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        capture.AbortCapture();
        var samples = capture.EndCapture();

        await capture.StopAsync();

        Assert.Equal(0, samples.Length);
    }
}
