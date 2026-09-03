using System.Runtime.InteropServices;

namespace Soneto.Platform.Linux;

/// <summary>
/// Raw P/Invoke surface against glibc for evdev device I/O, <c>epoll</c> multiplexing, and
/// <c>inotify</c> hotplug watching -- plan §1.9's specified mechanisms.
///
/// <para>
/// <b>Honest verification gap (read before trusting any of this):</b> every declaration
/// below is written against the publicly documented, ABI-stable Linux/glibc syscall
/// signatures and struct layouts (<c>man 2 open</c>, <c>man 2 ioctl</c>,
/// <c>man 7 epoll</c>, <c>man 7 inotify</c>, <c>linux/input.h</c>). It compiles cleanly on
/// this Windows dev machine (P/Invoke declarations don't need the target library to be
/// present at compile time) but **has never been executed against a real Linux kernel from
/// any agent session working on this item** -- struct packing (especially
/// <see cref="epoll_event"/>, which the kernel/glibc ABI deliberately packs to avoid
/// x86-64's natural 8-byte alignment inserting padding after the leading
/// <c>uint32_t events</c> field) and the <c>input_event</c> 64-bit-time_t layout used by
/// modern (post-2038-safe) kernels are exactly the kind of thing that looks right on paper
/// and only actually proves itself against a real device. Flagged here, not silently
/// assumed correct, per this item's explicit instruction not to fabricate verification of
/// anything hardware-dependent.
/// </para>
/// </summary>
internal static class EvdevInterop
{
    private const string Libc = "libc";

    // ---- open/close/read ---------------------------------------------------------------
    public const int O_RDONLY = 0x0000;
    public const int O_NONBLOCK = 0x0800;

    [DllImport(Libc, SetLastError = true)]
    public static extern int open(string pathname, int flags);

    [DllImport(Libc, SetLastError = true)]
    public static extern int close(int fd);

    [DllImport(Libc, SetLastError = true)]
    public static extern nint read(int fd, byte[] buf, nuint count);

    // ---- ioctl (EVIOCGBIT) -------------------------------------------------------------
    [DllImport(Libc, SetLastError = true, EntryPoint = "ioctl")]
    public static extern int ioctl_buf(int fd, nuint request, byte[] argp);

    private const int _IOC_READ = 2;
    private const int _IOC_NRSHIFT = 0;
    private const int _IOC_TYPESHIFT = 8;
    private const int _IOC_SIZESHIFT = 16;
    private const int _IOC_DIRSHIFT = 30;
    private const int EV_TYPE = 'E';

    /// <summary>
    /// Reconstructs the <c>EVIOCGBIT(ev, len)</c> ioctl request-code macro from
    /// <c>linux/input.h</c>: <c>_IOC(_IOC_READ, 'E', 0x20 + (ev), len)</c>. Pure integer
    /// arithmetic -- unit-testable against the published macro formula without a real
    /// kernel (see <see cref="KeyboardDeviceEnumerator"/>'s doc comment for how this is
    /// used, and this class's own doc comment for what still can't be confirmed: that the
    /// resulting request code is actually accepted correctly by a real kernel).
    /// </summary>
    public static nuint EVIOCGBIT(int ev, int len)
    {
        uint dir = _IOC_READ;
        uint type = EV_TYPE;
        uint nr = (uint)(0x20 + ev);
        uint size = (uint)len;
        uint code = (dir << _IOC_DIRSHIFT) | (type << _IOC_TYPESHIFT) | (nr << _IOC_NRSHIFT) | (size << _IOC_SIZESHIFT);
        return code;
    }

    // ---- epoll ---------------------------------------------------------------------------
    public const int EPOLL_CTL_ADD = 1;
    public const int EPOLL_CTL_DEL = 2;
    public const uint EPOLLIN = 0x001;
    public const uint EPOLLERR = 0x008;
    public const uint EPOLLHUP = 0x010;

    [StructLayout(LayoutKind.Explicit, Size = 12, Pack = 1)]
    public struct epoll_event
    {
        [FieldOffset(0)] public uint events;
        [FieldOffset(4)] public ulong data; // packed layout: data follows events at offset 4, not 8.
    }

    [DllImport(Libc, SetLastError = true)]
    public static extern int epoll_create1(int flags);

    [DllImport(Libc, SetLastError = true)]
    public static extern int epoll_ctl(int epfd, int op, int fd, ref epoll_event ev);

    [DllImport(Libc, SetLastError = true)]
    public static extern int epoll_wait(int epfd, [Out] epoll_event[] events, int maxevents, int timeoutMs);

    // ---- inotify ---------------------------------------------------------------------------
    public const uint IN_CREATE = 0x00000100;
    public const uint IN_DELETE = 0x00000200;
    public const int IN_NONBLOCK = 0x0800;

    [DllImport(Libc, SetLastError = true)]
    public static extern int inotify_init1(int flags);

    [DllImport(Libc, SetLastError = true)]
    public static extern int inotify_add_watch(int fd, string pathname, uint mask);

    /// <summary>
    /// Parses the (possibly multiple, back-to-back) <c>struct inotify_event</c> records in
    /// an inotify read buffer and returns each event's <c>name</c> field.
    /// <c>struct inotify_event { int wd; uint32_t mask; uint32_t cookie; uint32_t len; char
    /// name[]; }</c> -- a fixed 16-byte header followed by <c>len</c> bytes of (NUL-padded)
    /// name. Pure parsing logic over a caller-supplied buffer/length, unit-testable without
    /// a real inotify fd -- used by <c>LinuxHotkeySource.ReaderLoop</c> (post-review fix,
    /// issue 4) to filter hotplug events down to ones that actually look like evdev nodes
    /// (<c>event*</c>) before treating unrelated <c>/dev/input</c> churn as fault-worthy.
    /// </summary>
    public static List<string> ParseInotifyEventNames(ReadOnlySpan<byte> buf, int length)
    {
        var names = new List<string>();
        int offset = 0;
        while (offset + 16 <= length && offset + 16 <= buf.Length)
        {
            uint len = BitConverter.ToUInt32(buf.Slice(offset + 12, 4));
            int nameStart = offset + 16;
            if (len > 0 && nameStart + (int)len <= buf.Length)
            {
                var nameBytes = buf.Slice(nameStart, (int)len);
                int nulIndex = nameBytes.IndexOf((byte)0);
                var trimmed = nulIndex >= 0 ? nameBytes[..nulIndex] : nameBytes;
                names.Add(System.Text.Encoding.UTF8.GetString(trimmed));
            }
            offset = nameStart + (int)len;
        }
        return names;
    }

    // ---- input_event (64-bit time_t layout, modern kernels: 24 bytes) -------------------
    public const int InputEventSize = 24;

    public static (ushort type, ushort code, int value) ParseInputEvent(ReadOnlySpan<byte> buf)
    {
        // struct input_event { struct timeval time; __u16 type; __u16 code; __s32 value; }
        // timeval is 16 bytes (tv_sec: 8, tv_usec: 8) on a 64-bit-time_t kernel/glibc pair.
        ushort type = BitConverter.ToUInt16(buf.Slice(16, 2));
        ushort code = BitConverter.ToUInt16(buf.Slice(18, 2));
        int value = BitConverter.ToInt32(buf.Slice(20, 4));
        return (type, code, value);
    }
}
