using Microsoft.Extensions.Logging;

namespace Soneto.Platform.Linux;

public sealed class LinuxClipboardTextBackup
{
    public string? Text { get; init; }
    public bool HadText { get; init; }
    public byte[]? TextHash { get; init; }

    /// <summary>True if the clipboard advertised any non-text-family MIME type at save
    /// time (item 7c parity: <c>Soneto.Platform.Windows.ClipboardTextBackup.HasNonTextFormats</c>).</summary>
    public bool HasNonTextFormats { get; init; }
    public IReadOnlyList<string> MimeTypesPresent { get; init; } = Array.Empty<string>();
}

public enum ClipboardRestoreOutcome
{
    Restored,
    SkippedSequenceChanged, // hash mismatch -- Linux's analogue of the Windows sequence-number mismatch.
    SkippedNonText,
    Failed,
}

/// <summary>
/// Linux clipboard access via <c>wl-copy</c>/<c>wl-paste</c> (Wayland) or <c>xclip</c>
/// (X11), selected per <see cref="ClipboardBackendSelector"/>. Implements plan §1.9's
/// backup/restore algorithm using <see cref="ClipboardHashGuard"/> in place of Windows'
/// clipboard sequence number.
///
/// <para>
/// <b>Cannot be exercised from this session.</b> No <c>wl-copy</c>/<c>wl-paste</c>/<c>xclip</c>
/// binaries exist on this Windows dev machine, so the actual process-invocation behaviour
/// (exit codes, stdout framing for <c>wl-paste -l</c>/<c>xclip -o -t TARGETS</c>, timing) is
/// unverified. What IS verified (by unit test) is <see cref="ClipboardHashGuard"/>'s pure
/// restore-skip-on-mismatch decision and the non-text-MIME-type filtering logic in
/// <see cref="ClassifyMimeTypes"/>.
/// </para>
/// </summary>
public sealed class LinuxClipboardManager
{
    /// <summary>
    /// MIME types Wayland/X11 clipboard tooling always advertises alongside plain text
    /// (analogous to Windows' CF_LOCALE/CF_OEMTEXT auto-synthesized companions -- see
    /// <c>Soneto.Platform.Windows.ClipboardManager.Save</c>'s doc comment for why an
    /// allow-list, not a "anything but the primary text type" check, is required here too).
    /// </summary>
    private static readonly HashSet<string> TextFamilyMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/plain",
        "text/plain;charset=utf-8",
        "UTF8_STRING",
        "STRING",
        "TEXT",
        "COMPOUND_TEXT",
    };

    private readonly ILogger _logger;
    private readonly IProcessRunner _processRunner;
    private readonly ClipboardBackendKind _backend;

    public LinuxClipboardManager(ILogger logger, IProcessRunner processRunner, ClipboardBackendKind backend)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _backend = backend;
    }

    /// <summary>Pure classification, unit-testable without a real clipboard: does the given
    /// MIME type list contain anything outside the text-family allow-list?</summary>
    public static bool ClassifyMimeTypes(IReadOnlyList<string> mimeTypes) =>
        mimeTypes.Any(m => !TextFamilyMimeTypes.Contains(m.Trim()));

    public async Task<LinuxClipboardTextBackup> SaveAsync(CancellationToken ct)
    {
        var mimeTypes = await ListMimeTypesAsync(ct).ConfigureAwait(false);
        bool hasNonText = ClassifyMimeTypes(mimeTypes);

        string? text = null;
        bool hadText = false;
        byte[]? hash = null;
        try
        {
            var (exit, stdout, _) = await RunPasteAsync(ct).ConfigureAwait(false);
            if (exit == 0)
            {
                text = stdout;
                hadText = true;
                hash = ClipboardHashGuard.ComputeHash(text);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "clipboard: paste-for-backup failed (treated as 'nothing to back up')");
        }

        _logger.LogDebug(
            "clipboard: saved backup -- hadText={HadText} hasNonTextFormats={HasNonText} mimeTypes=[{Mimes}]",
            hadText, hasNonText, string.Join(",", mimeTypes));

        return new LinuxClipboardTextBackup
        {
            Text = text,
            HadText = hadText,
            TextHash = hash,
            HasNonTextFormats = hasNonText,
            MimeTypesPresent = mimeTypes,
        };
    }

    public async Task<bool> SetTextAsync(string text, CancellationToken ct)
    {
        try
        {
            var (exit, _, stderr) = await RunCopyAsync(text, ct).ConfigureAwait(false);
            if (exit != 0)
            {
                _logger.LogError("clipboard: set failed (exit={Exit}): {Stderr}", exit, stderr);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "clipboard: set threw (backend={Backend})", _backend);
            return false;
        }
    }

    /// <summary>
    /// Item 7c/plan-§1.9 parity: content-hash-guarded restore. <paramref name="expectedHash"/>
    /// is the hash computed immediately after OUR OWN successful write; this method reads
    /// the clipboard's CURRENT content right before writing, hashes it, and aborts
    /// (<see cref="ClipboardRestoreOutcome.SkippedSequenceChanged"/>) if it doesn't match --
    /// the closest achievable analogue of Windows' atomic open-check-write critical section,
    /// though Linux clipboard tooling gives no way to make the check-then-write itself
    /// atomic the way <c>OpenClipboard</c> does, so a narrow TOCTOU window between the
    /// read-back and the write remains (documented, not fixed -- there is no Linux clipboard
    /// API this project found that closes it).
    /// </summary>
    public async Task<ClipboardRestoreOutcome> RestoreWithHashGuardAsync(
        LinuxClipboardTextBackup backup, byte[] expectedHash, bool declineNonText, CancellationToken ct)
    {
        if (declineNonText && backup.HasNonTextFormats)
        {
            _logger.LogWarning(
                "Skipping clipboard restore: original clipboard held non-text MIME types ({Mimes}) -- "
                + "leaving the injected transcript on the clipboard rather than risk destroying it.",
                string.Join(",", backup.MimeTypesPresent));
            return ClipboardRestoreOutcome.SkippedNonText;
        }

        if (!backup.HadText || backup.Text is null)
            return ClipboardRestoreOutcome.Failed;

        byte[] currentHash;
        try
        {
            var (exit, stdout, _) = await RunPasteAsync(ct).ConfigureAwait(false);
            if (exit != 0)
            {
                _logger.LogWarning("clipboard: restore's pre-check paste failed (exit={Exit}); aborting restore.", exit);
                return ClipboardRestoreOutcome.Failed;
            }
            currentHash = ClipboardHashGuard.ComputeHash(stdout);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "clipboard: restore's pre-check paste threw; aborting restore.");
            return ClipboardRestoreOutcome.Failed;
        }

        if (!ClipboardHashGuard.IsSafeToRestore(expectedHash, currentHash))
        {
            _logger.LogInformation(
                "Skipped clipboard restore: clipboard content hash changed since our write (user likely "
                + "copied something else during the restore window).");
            return ClipboardRestoreOutcome.SkippedSequenceChanged;
        }

        bool ok = await SetTextAsync(backup.Text, ct).ConfigureAwait(false);
        return ok ? ClipboardRestoreOutcome.Restored : ClipboardRestoreOutcome.Failed;
    }

    private Task<ProcessRunResult> RunCopyAsync(string text, CancellationToken ct) => _backend switch
    {
        ClipboardBackendKind.Wayland => _processRunner.RunAsync("wl-copy", Array.Empty<string>(), text, ct),
        _ => _processRunner.RunAsync("xclip", new[] { "-selection", "clipboard" }, text, ct),
    };

    private Task<ProcessRunResult> RunPasteAsync(CancellationToken ct) => _backend switch
    {
        ClipboardBackendKind.Wayland => _processRunner.RunAsync("wl-paste", new[] { "--no-newline" }, null, ct),
        _ => _processRunner.RunAsync("xclip", new[] { "-selection", "clipboard", "-o" }, null, ct),
    };

    private async Task<List<string>> ListMimeTypesAsync(CancellationToken ct)
    {
        try
        {
            var (exit, stdout, _) = _backend == ClipboardBackendKind.Wayland
                ? await _processRunner.RunAsync("wl-paste", new[] { "-l" }, null, ct).ConfigureAwait(false)
                : await _processRunner.RunAsync("xclip", new[] { "-selection", "clipboard", "-o", "-t", "TARGETS" }, null, ct).ConfigureAwait(false);

            if (exit != 0)
                return new List<string>();

            return stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "clipboard: listing MIME types failed (treated as 'nothing on the clipboard')");
            return new List<string>();
        }
    }
}
