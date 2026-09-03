using Microsoft.Extensions.Logging.Abstractions;
using Soneto.Core.Abstractions;
using Soneto.Core.Audio;

namespace Soneto.Core.Tests.Audio;

/// <summary>
/// Device-resolution fallback logic (plan §1.5 "Device changes"), tested entirely with fakes
/// — no PortAudio / audio hardware needed, per §1.13's hard rule that the default test run
/// must pass with no audio device present.
/// </summary>
public class CaptureDeviceResolverTests
{
    [Fact]
    public void Null_requested_device_resolves_to_system_default()
    {
        int resolved = CaptureDeviceResolver.Resolve(
            requested: null, deviceExists: _ => true, defaultInputDeviceIndex: 3, NullLogger.Instance);

        Assert.Equal(3, resolved);
    }

    [Fact]
    public void Existing_requested_device_is_used_as_is()
    {
        int resolved = CaptureDeviceResolver.Resolve(
            requested: new AudioDeviceId(5, "USB Mic"),
            deviceExists: idx => idx == 5,
            defaultInputDeviceIndex: 0,
            NullLogger.Instance);

        Assert.Equal(5, resolved);
    }

    [Fact]
    public void Gone_requested_device_falls_back_to_system_default_and_logs_a_warning()
    {
        var logger = new RecordingLogger();

        int resolved = CaptureDeviceResolver.Resolve(
            requested: new AudioDeviceId(5, "USB Mic (unplugged)"),
            deviceExists: _ => false,
            defaultInputDeviceIndex: 0,
            logger);

        Assert.Equal(0, resolved);
        Assert.Contains(logger.Warnings, w => w.Contains("USB Mic (unplugged)") && w.Contains("falling back"));
    }

    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }
}
