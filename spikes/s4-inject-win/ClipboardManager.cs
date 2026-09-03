using System.Runtime.InteropServices;

namespace s4_inject_win;

internal enum ClipboardPolicy { TextOnly, Never, BestEffort }

/// <summary>Snapshot of whatever was on the clipboard before we touched it.</summary>
internal sealed class ClipboardBackup
{
    internal string? UnicodeText;
    internal bool HadUnicodeText;
    internal bool HadNonTextFormats;
    internal List<uint> FormatsPresent = [];
}

/// <summary>
/// §1.8 steps 3-5 and 11: raw Win32 clipboard access (not
/// System.Windows.Forms.Clipboard) so we control format inspection, the
/// retry loop, and the sequence-number guard directly, matching the plan's
/// pseudocode exactly. Deliberately does not require an STA thread, unlike
/// System.Windows.Forms.Clipboard -- OpenClipboard/SetClipboardData have no
/// such requirement at the raw Win32 level.
/// </summary>
internal static class ClipboardManager
{
    internal static int GetSequenceNumber() => NativeMethods.GetClipboardSequenceNumber();

    /// <summary>Step 3-4: capture the sequence number and back up CF_UNICODETEXT, and note whether non-text formats are present.</summary>
    internal static ClipboardBackup Save(Action<string>? log = null)
    {
        var backup = new ClipboardBackup();

        if (!NativeMethods.OpenClipboard(IntPtr.Zero))
        {
            log?.Invoke("clipboard: OpenClipboard failed during save (errno=" + Marshal.GetLastWin32Error() + ")");
            return backup;
        }

        try
        {
            // Windows auto-synthesizes companion formats for any text you set --
            // CF_LOCALE (locale of the text) and CF_OEMTEXT (an ANSI copy) always
            // appear alongside CF_UNICODETEXT, even though nothing but plain text
            // was ever placed on the clipboard. A naive "anything other than
            // CF_UNICODETEXT/CF_TEXT is non-text" check (this spike's first
            // attempt) false-positives on essentially every real clipboard save,
            // permanently disabling restoration under the default textOnly
            // policy. Confirmed by direct reproduction here -- every save of a
            // plain-text-only clipboard showed formats [13, 16, 1, 7], i.e.
            // CF_UNICODETEXT + CF_LOCALE + CF_TEXT + CF_OEMTEXT, none of which
            // are actually a second kind of content. §1.8's real implementation
            // needs this same "text family" allow-list, not the plan's literal
            // wording taken at face value.
            var textFamily = new HashSet<uint>
            {
                NativeMethods.CF_UNICODETEXT,
                NativeMethods.CF_TEXT,
                NativeMethods.CF_OEMTEXT,
                NativeMethods.CF_LOCALE,
            };

            uint fmt = 0;
            while ((fmt = NativeMethods.EnumClipboardFormats(fmt)) != 0)
            {
                backup.FormatsPresent.Add(fmt);
                if (!textFamily.Contains(fmt))
                {
                    backup.HadNonTextFormats = true;
                }
            }

            if (NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_UNICODETEXT))
            {
                var h = NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT);
                if (h != IntPtr.Zero)
                {
                    var ptr = NativeMethods.GlobalLock(h);
                    if (ptr != IntPtr.Zero)
                    {
                        try
                        {
                            backup.UnicodeText = Marshal.PtrToStringUni(ptr);
                            backup.HadUnicodeText = true;
                        }
                        finally
                        {
                            NativeMethods.GlobalUnlock(h);
                        }
                    }
                }
            }
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }

        log?.Invoke($"clipboard: saved backup -- hadUnicodeText={backup.HadUnicodeText} hadNonTextFormats={backup.HadNonTextFormats} formats=[{string.Join(",", backup.FormatsPresent)}]");
        return backup;
    }

    /// <summary>Step 5: set CF_UNICODETEXT with a retry loop -- clipboard managers (Ditto, clipboard history, Flow Launcher) can transiently hold the clipboard.</summary>
    internal static bool SetUnicodeTextWithRetry(string text, int attempts = 3, int delayMs = 20, Action<string>? log = null)
    {
        for (int i = 1; i <= attempts; i++)
        {
            if (TrySetUnicodeText(text, out var error))
            {
                log?.Invoke($"clipboard: SetClipboardData succeeded on attempt {i}/{attempts}");
                return true;
            }
            log?.Invoke($"clipboard: SetClipboardData attempt {i}/{attempts} failed ({error})");
            if (i < attempts) Thread.Sleep(delayMs);
        }
        return false;
    }

    private static bool TrySetUnicodeText(string text, out string error)
    {
        error = "";
        if (!NativeMethods.OpenClipboard(IntPtr.Zero))
        {
            error = "OpenClipboard failed, errno=" + Marshal.GetLastWin32Error();
            return false;
        }

        try
        {
            return WriteUnicodeTextToOpenClipboard(text, out error);
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    /// <summary>
    /// Does the actual EmptyClipboard/GlobalAlloc/SetClipboardData work,
    /// assuming the caller already holds the clipboard open (via
    /// OpenClipboard). Factored out so both the plain "set" path
    /// (TrySetUnicodeText) and the atomic restore path
    /// (TryRestoreIfSequenceUnchanged) share one implementation and one set
    /// of failure-path cleanup rules -- see the GlobalFree calls below: on
    /// any failure after GlobalAlloc, ownership of hMem has NOT transferred
    /// to the system (that only happens on a successful SetClipboardData), so
    /// we must free it ourselves or it leaks.
    /// </summary>
    private static bool WriteUnicodeTextToOpenClipboard(string text, out string error)
    {
        error = "";
        if (!NativeMethods.EmptyClipboard())
        {
            error = "EmptyClipboard failed, errno=" + Marshal.GetLastWin32Error();
            return false;
        }

        int bytes = (text.Length + 1) * 2; // UTF-16 + null terminator
        var hMem = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE | NativeMethods.GMEM_ZEROINIT, (UIntPtr)bytes);
        if (hMem == IntPtr.Zero)
        {
            error = "GlobalAlloc failed";
            return false;
        }

        var ptr = NativeMethods.GlobalLock(hMem);
        if (ptr == IntPtr.Zero)
        {
            error = "GlobalLock failed";
            NativeMethods.GlobalFree(hMem); // ownership never transferred -- we still own this and must free it
            return false;
        }
        try
        {
            Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
            Marshal.WriteInt16(ptr, text.Length * 2, 0); // null terminator
        }
        finally
        {
            NativeMethods.GlobalUnlock(hMem);
        }

        if (NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, hMem) == IntPtr.Zero)
        {
            error = "SetClipboardData failed, errno=" + Marshal.GetLastWin32Error();
            NativeMethods.GlobalFree(hMem); // SetClipboardData failed -- ownership did not transfer, must free ourselves
            return false;
        }

        return true;
    }

    /// <summary>
    /// Step 11's restore, made atomic with the sequence-number guard: opens
    /// the clipboard once, reads GetClipboardSequenceNumber() while still
    /// holding it open, and only proceeds to EmptyClipboard/SetClipboardData
    /// if it still matches <paramref name="expectedSeq"/> -- all inside the
    /// same open/close critical section, so there is no gap between "we
    /// checked the sequence number" and "we wrote" for a user's Ctrl+C to
    /// land in. This replaces the earlier design (a standalone
    /// GetSequenceNumber() call, unguarded by OpenClipboard, followed
    /// sometime later by a completely separate RestoreText open/write) that
    /// had exactly that gap.
    /// </summary>
    internal static bool TryRestoreIfSequenceUnchanged(string text, int expectedSeq, out bool sequenceChanged, Action<string>? log = null, int attempts = 3, int delayMs = 20)
    {
        sequenceChanged = false;
        for (int i = 1; i <= attempts; i++)
        {
            if (!NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                log?.Invoke($"clipboard: OpenClipboard failed during atomic restore attempt {i}/{attempts} (errno={Marshal.GetLastWin32Error()})");
                if (i < attempts) Thread.Sleep(delayMs);
                continue;
            }

            try
            {
                int seqNow = NativeMethods.GetClipboardSequenceNumber();
                if (seqNow != expectedSeq)
                {
                    log?.Invoke($"clipboard: sequence number is {seqNow} (expected {expectedSeq}) checked atomically while clipboard held open -- aborting restore, not writing");
                    sequenceChanged = true;
                    return false;
                }

                if (WriteUnicodeTextToOpenClipboard(text, out var error))
                {
                    log?.Invoke($"clipboard: atomic restore succeeded on attempt {i}/{attempts} (sequence verified unchanged inside the same open/close section as the write)");
                    return true;
                }
                log?.Invoke($"clipboard: atomic restore attempt {i}/{attempts} failed ({error})");
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }

            if (i < attempts) Thread.Sleep(delayMs);
        }
        return false;
    }

    /// <summary>
    /// Puts a synthetic bitmap on the clipboard for the "image on clipboard"
    /// adversarial test. Uses System.Drawing (spike-only convenience, see
    /// csproj comment) to build the bitmap, then hands the raw HBITMAP to our
    /// own SetClipboardData -- not System.Windows.Forms.Clipboard.SetImage,
    /// to keep clipboard access uniformly on the raw Win32 path this spike is
    /// validating.
    /// </summary>
    internal static bool PutSyntheticBitmap(Action<string>? log = null)
    {
        using var bmp = new System.Drawing.Bitmap(200, 100);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.CornflowerBlue);
            g.DrawString("S4 test image", new System.Drawing.Font("Arial", 12), System.Drawing.Brushes.White, 10, 40);
        }

        var hBitmap = bmp.GetHbitmap();
        if (!NativeMethods.OpenClipboard(IntPtr.Zero))
        {
            log?.Invoke("clipboard: OpenClipboard failed while placing test image");
            return false;
        }
        try
        {
            NativeMethods.EmptyClipboard();
            var result = NativeMethods.SetClipboardData(NativeMethods.CF_BITMAP, hBitmap);
            log?.Invoke(result != IntPtr.Zero
                ? "clipboard: synthetic CF_BITMAP placed for image-on-clipboard test"
                : "clipboard: failed to place synthetic CF_BITMAP");
            return result != IntPtr.Zero;
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }
}
