using Microsoft.Extensions.Logging;

namespace Soneto.Platform.Linux;

public sealed record KeyboardDeviceInfo(string Path, int Fd);

/// <summary>
/// Real <c>/dev/input/event*</c> enumeration per plan §1.9: opens every node, reads its
/// capability bitmasks via <c>EVIOCGBIT</c>, and keeps only the ones
/// <see cref="KeyboardDeviceFilter.IsKeyboardLike"/> (the pure, unit-tested decision logic)
/// says look like a real keyboard. Logs the full candidate list and which were selected,
/// per the plan's explicit "log the full device list ... this is the first thing you'll
/// want when it doesn't work on a machine that isn't yours" instruction.
///
/// <para>
/// <b>Cannot be exercised from this session.</b> There is no <c>/dev/input</c> on this
/// Windows dev machine -- <see cref="EnumerateKeyboards"/> has never actually run against
/// real device nodes. What IS verified (by unit test, see
/// <c>tests/Soneto.Platform.Linux.Tests</c>) is the pure filter decision this class calls
/// into once it has real bitmask bytes in hand.
/// </para>
/// </summary>
public sealed class KeyboardDeviceEnumerator
{
    private readonly ILogger _logger;

    public KeyboardDeviceEnumerator(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Opens every <c>/dev/input/event*</c> node, filters to keyboard-like devices, and
    /// returns them opened non-blocking (caller owns the returned fds and must close them).
    /// Nodes that fail to open (permission denied -- the <c>input</c> group requirement --
    /// or a transient hotplug race) are logged and skipped, not fatal to the whole scan.
    /// </summary>
    public List<KeyboardDeviceInfo> EnumerateKeyboards()
    {
        var candidates = new List<string>();
        try
        {
            candidates.AddRange(Directory.EnumerateFiles("/dev/input", "event*"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate /dev/input -- is this process running on Linux with /dev/input present?");
            return new List<KeyboardDeviceInfo>();
        }

        _logger.LogInformation("evdev enumeration: found {Count} candidate node(s): {Nodes}", candidates.Count, string.Join(", ", candidates));

        var selected = new List<KeyboardDeviceInfo>();
        foreach (var path in candidates.OrderBy(p => p, StringComparer.Ordinal))
        {
            int fd = EvdevInterop.open(path, EvdevInterop.O_RDONLY | EvdevInterop.O_NONBLOCK);
            if (fd < 0)
            {
                _logger.LogWarning(
                    "evdev enumeration: failed to open {Path} (errno-ish result={Fd}) -- skipping. "
                    + "Common cause: current user is not in the 'input' group (see scripts/setup-linux.sh).",
                    path, fd);
                continue;
            }

            bool isKeyboard;
            try
            {
                isKeyboard = ProbeIsKeyboard(fd);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "evdev enumeration: EVIOCGBIT probe failed for {Path} -- skipping", path);
                EvdevInterop.close(fd);
                continue;
            }

            if (isKeyboard)
            {
                selected.Add(new KeyboardDeviceInfo(path, fd));
            }
            else
            {
                EvdevInterop.close(fd);
            }
        }

        _logger.LogInformation(
            "evdev enumeration: selected {SelectedCount}/{CandidateCount} node(s) as keyboard-like: {Selected}",
            selected.Count, candidates.Count, string.Join(", ", selected.Select(s => s.Path)));

        return selected;
    }

    private static bool ProbeIsKeyboard(int fd)
    {
        int evLen = (EvdevConstants.EV_MAX / 8) + 1;
        int keyLen = (EvdevConstants.KEY_MAX / 8) + 1;

        var evBits = new byte[evLen];
        var keyBits = new byte[keyLen];

        var evReq = EvdevInterop.EVIOCGBIT(0, evLen);
        if (EvdevInterop.ioctl_buf(fd, evReq, evBits) < 0)
            throw new InvalidOperationException("EVIOCGBIT(0, ...) ioctl failed.");

        var keyReq = EvdevInterop.EVIOCGBIT(EvdevConstants.EV_KEY, keyLen);
        if (EvdevInterop.ioctl_buf(fd, keyReq, keyBits) < 0)
            throw new InvalidOperationException("EVIOCGBIT(EV_KEY, ...) ioctl failed.");

        return KeyboardDeviceFilter.IsKeyboardLike(evBits, keyBits);
    }
}
