using Soneto.Core.Audio;

namespace Soneto.Core.Tests.Audio;

/// <summary>
/// Format-selection decision logic (plan §1.5's "Correct sequence"), tested with fake
/// <c>Pa_IsFormatSupported</c> results — no PortAudio / audio hardware needed.
/// </summary>
public class CaptureFormatSelectorTests
{
    [Fact]
    public void Device_supporting_16k_mono_float32_directly_is_used_without_resampling()
    {
        var plan = CaptureFormatSelector.Select(supports16kMonoFloat32Direct: true, deviceDefaultSampleRate: 48000);

        Assert.Equal(CapturePathKind.Direct16k, plan.Kind);
        Assert.Equal(16000, plan.OpenRate);
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void Device_not_supporting_16k_direct_opens_at_native_rate_for_resampling(double nativeRate)
    {
        var plan = CaptureFormatSelector.Select(supports16kMonoFloat32Direct: false, deviceDefaultSampleRate: nativeRate);

        Assert.Equal(CapturePathKind.ResampleFromNative, plan.Kind);
        Assert.Equal((int)nativeRate, plan.OpenRate);
    }

    [Fact]
    public void Invalid_native_rate_throws_when_direct_path_not_supported()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CaptureFormatSelector.Select(supports16kMonoFloat32Direct: false, deviceDefaultSampleRate: 0));
    }
}
