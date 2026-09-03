using System.Diagnostics;

namespace Soneto.Platform.Linux;

public sealed record ProcessRunResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// Thin abstraction over launching an external process (<c>wl-copy</c>, <c>wl-paste</c>,
/// <c>xclip</c>, <c>ydotool</c>), optionally piping text to stdin and reading stdout back.
/// Exists so <c>LinuxClipboardManager</c>/<c>LinuxTextInjector</c>'s decision logic can be
/// unit-tested against a fake implementation (see <c>tests/Soneto.Platform.Linux.Tests</c>)
/// without ever actually spawning a process -- same "separate the native/process call from
/// the decision logic" convention as the rest of this project's platform layers.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(string fileName, IReadOnlyList<string> args, string? stdin, CancellationToken ct);
}

/// <summary>
/// Real implementation, backed by <see cref="Process"/>. Never executed from this Windows
/// dev session (no <c>wl-copy</c>/<c>xclip</c>/<c>ydotool</c> binaries exist here) -- this
/// is standard, well-understood <see cref="Process"/> usage, but the actual external tools'
/// real-world behaviour (exit codes, stdout framing, timing) has not been observed by any
/// agent session working on this item.
/// </summary>
public sealed class RealProcessRunner : IProcessRunner
{
    public async Task<ProcessRunResult> RunAsync(string fileName, IReadOnlyList<string> args, string? stdin, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        process.Start();

        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin.AsMemory(), ct).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        // Post-review fix (issue 5): stdout and stderr must be read CONCURRENTLY, not
        // sequentially. Reading one stream fully to completion before starting to read the
        // other is a well-known .NET Process deadlock: if the child writes enough to the
        // stream nobody is draining yet to fill its OS pipe buffer, the child blocks on that
        // write, and this method -- still stuck awaiting the first ReadToEndAsync -- never
        // gets around to draining the second stream to unblock it. Transcripts up to
        // ~20,000 chars get piped through here (stdin) and error paths can produce verbose
        // stderr, so this is a real, reachable hang, not a theoretical one.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync(ct)).ConfigureAwait(false);

        return new ProcessRunResult(process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }
}
