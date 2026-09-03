// S7 spike harness: drives the REAL Soneto.Platform.Linux.LinuxHotkeySource against a
// genuine kernel-level virtual keyboard (spikes/s7-docker-linux/uinput_kbd.c), inside a
// Docker container. Throwaway spike code, per this project's established convention.
using Microsoft.Extensions.Logging;
using Soneto.Core.Abstractions;
using Soneto.Platform.Linux;

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Debug));
var logger = loggerFactory.CreateLogger<LinuxHotkeySource>();

var source = new LinuxHotkeySource(logger);

var pressedAt = new List<(DateTimeOffset Fired, DateTimeOffset Synthesized)>();
var releasedAt = new List<(DateTimeOffset Fired, DateTimeOffset Synthesized)>();
DateTimeOffset lastDownSentAt = default, lastUpSentAt = default;

source.Pressed += (_, e) => { pressedAt.Add((e.Timestamp, lastDownSentAt)); Console.WriteLine($"[HARNESS] Pressed fired, ts={e.Timestamp:O}"); };
source.Released += (_, e) => { releasedAt.Add((e.Timestamp, lastUpSentAt)); Console.WriteLine($"[HARNESS] Released fired, ts={e.Timestamp:O}"); };
source.Faulted += async (_, e) =>
{
    Console.WriteLine($"[HARNESS] FAULTED: {e.Reason} {e.Exception}");
    if (e.Reason.Contains("re-enumeration required", StringComparison.Ordinal))
    {
        Console.WriteLine("[HARNESS] Hotplug-shaped fault detected -- calling RestartAsync (SessionController's real job in production) to re-enumerate...");
        try
        {
            await source.RestartAsync(CancellationToken.None);
            Console.WriteLine("[HARNESS] RestartAsync completed successfully after hotplug.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HARNESS] RestartAsync THREW: {ex}");
        }
    }
};

Console.WriteLine("[HARNESS] Starting LinuxHotkeySource against RightControl...");
await source.StartAsync(new HotkeyBinding("RightControl", Suppress: false), CancellationToken.None);
Console.WriteLine("[HARNESS] StartAsync returned successfully -- real evdev enumeration + epoll setup succeeded.");

// Command protocol with the C uinput helper, driven over stdin/stdout of THIS process:
// the orchestrating shell script pipes 'd'/'u' lines to this harness's stdin, which relays
// them to the uinput_kbd process's stdin at the moment of actually sending, so the
// synthesized-vs-fired timestamp comparison is measured from as close to the real syscall
// as this two-process design allows.
string? line;
while ((line = Console.In.ReadLine()) != null)
{
    if (line == "d")
    {
        lastDownSentAt = DateTimeOffset.UtcNow;
        Console.WriteLine($"[HARNESS] (relayed) down requested at {lastDownSentAt:O}");
    }
    else if (line == "u")
    {
        lastUpSentAt = DateTimeOffset.UtcNow;
        Console.WriteLine($"[HARNESS] (relayed) up requested at {lastUpSentAt:O}");
    }
    else if (line == "report")
    {
        Console.WriteLine($"[HARNESS] pressedCount={pressedAt.Count} releasedCount={releasedAt.Count}");
        foreach (var (fired, synth) in pressedAt)
            Console.WriteLine($"[HARNESS] Pressed jitter = {(fired - synth).TotalMilliseconds:F2} ms");
        foreach (var (fired, synth) in releasedAt)
            Console.WriteLine($"[HARNESS] Released jitter = {(fired - synth).TotalMilliseconds:F2} ms");
    }
    else if (line == "q")
    {
        break;
    }
}

await source.DisposeAsync();
Console.WriteLine("[HARNESS] Disposed cleanly, exiting.");
