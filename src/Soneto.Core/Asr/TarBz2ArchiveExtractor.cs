using System.ComponentModel;
using System.Diagnostics;

namespace Soneto.Core.Asr;

/// <summary>
/// Extracts a <c>.tar.bz2</c> archive by shelling out to the system <c>tar</c> binary
/// (bsdtar on Windows 10 1803+, GNU tar on Linux — both ship bzip2 support built in).
/// Deliberately avoids adding a bzip2/archive NuGet dependency not called for by plan
/// §1.2's package table; `tar` is present on every target platform for this project.
/// </summary>
public sealed class TarBz2ArchiveExtractor : IArchiveExtractor
{
    public void Extract(string archivePath, string destinationParentDir)
    {
        Directory.CreateDirectory(destinationParentDir);

        var psi = new ProcessStartInfo("tar")
        {
            RedirectStandardError = true,
            // Nothing consumes stdout, so leave it un-redirected rather than redirecting
            // and never draining it — an unread stdout pipe can fill its OS buffer and
            // deadlock against the stderr drain below.
            RedirectStandardOutput = false,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-xjf");
        psi.ArgumentList.Add(archivePath);
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(destinationParentDir);

        Process proc;
        try
        {
            proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start 'tar' for model archive extraction.");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                "'tar' was not found on PATH; it is required to extract the downloaded model "
                + "archive. Install tar or ensure it's on PATH.", ex);
        }

        using (proc)
        {
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"'tar -xjf' failed extracting {archivePath} to {destinationParentDir} "
                    + $"(exit code {proc.ExitCode}): {stderr}");
            }
        }
    }
}
