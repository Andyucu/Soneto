using System.Drawing;
using System.Drawing.Imaging;

namespace s4_inject_win;

/// <summary>
/// Captures the screen (or a specific window's rect) to PNG so an agent
/// without a real pair of eyes on the monitor can still visually confirm
/// diacritics rendered plausibly in apps this spike can't script a
/// text-content read-back against (VS Code, Chrome, Teams, Outlook, Word,
/// Windows Terminal). This does not replace a human's final sign-off --
/// see README -- but it is a real, non-fabricated verification step: an
/// actual bitmap of the actual screen at the actual moment after injection.
/// </summary>
internal static class ScreenshotUtil
{
    internal static string CaptureWindow(IntPtr hWnd, string path)
    {
        NativeMethods.GetWindowRect(hWnd, out var r);
        int width = Math.Max(1, r.Right - r.Left);
        int height = Math.Max(1, r.Bottom - r.Top);

        using var bmp = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(width, height));
        }
        bmp.Save(path, ImageFormat.Png);
        return path;
    }

    internal static string CaptureFullScreen(string path)
    {
        var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        using var bmp = new Bitmap(bounds.Width, bounds.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
        }
        bmp.Save(path, ImageFormat.Png);
        return path;
    }
}
