using System.Diagnostics;
using System.IO;

namespace s4_inject_win;

internal sealed record AppProfile(
    string Name,
    string Description,
    Func<Process> Launch,
    int SettleMs,
    int TabsBeforeInject,
    string PasteChord,
    int ClipboardRestoreDelayMs = 150);

/// <summary>
/// Per-app launch profiles for the S4 test matrix. These are best-effort
/// automation of "get a real target app focused on a real text field" --
/// documented explicitly where the automation is approximate (e.g. Tab-count
/// navigation into a compose body), per the task's instruction not to
/// fabricate results for apps that can't actually be exercised.
/// </summary>
internal static class AppLauncher
{
    private const string ChromePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    private const string OutlookPath = @"C:\Program Files\Microsoft Office\root\Office16\OUTLOOK.EXE";
    private const string WordPath = @"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE";

    internal static readonly Dictionary<string, AppProfile> Profiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["notepad"] = new AppProfile(
            "Notepad", "baseline",
            () => Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true })!,
            SettleMs: 1000, TabsBeforeInject: 0, PasteChord: "ctrl+v"),

        ["vscode"] = new AppProfile(
            "VS Code", "Electron -- known problem case",
            () => Process.Start(new ProcessStartInfo("code")
            {
                UseShellExecute = true,
                // A bare "--new-window" opens the welcome page (no editable
                // text area). Opening a fresh temp file gives a real editor
                // tab with a caret to paste into.
                Arguments = $"--new-window \"{Path.Combine(Path.GetTempPath(), $"s4-vscode-test-{Guid.NewGuid():N}.txt")}\""
            })!,
            SettleMs: 4500, TabsBeforeInject: 0, PasteChord: "ctrl+v"),

        ["chrome-textarea"] = new AppProfile(
            "Chrome (textarea)", "data: URL with an autofocused <textarea>",
            () => Process.Start(new ProcessStartInfo(ChromePath)
            {
                UseShellExecute = true,
                ArgumentList = { "--new-window", "data:text/html,<textarea autofocus style='width:600px;height:300px;font-size:20px'></textarea>" }
            })!,
            SettleMs: 2500, TabsBeforeInject: 0, PasteChord: "ctrl+v"),

        ["chrome-addressbar"] = new AppProfile(
            "Chrome (address bar)", "Ctrl+L to focus omnibox, then inject",
            () => Process.Start(new ProcessStartInfo(ChromePath)
            {
                UseShellExecute = true,
                ArgumentList = { "--new-window", "about:blank" }
            })!,
            SettleMs: 2500, TabsBeforeInject: 0, PasteChord: "ctrl+v"),

        ["terminal"] = new AppProfile(
            "Windows Terminal", "paste may need Ctrl+Shift+V",
            () => Process.Start(new ProcessStartInfo("wt.exe") { UseShellExecute = true })!,
            SettleMs: 2000, TabsBeforeInject: 0, PasteChord: "ctrl+v"),

        ["teams"] = new AppProfile(
            "Microsoft Teams", "Electron, slow input handling",
            () => Process.Start(new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true,
                Arguments = "shell:AppsFolder\\MSTeams_8wekyb3d8bbwe!MSTeams"
            })!,
            SettleMs: 6000, TabsBeforeInject: 0, PasteChord: "ctrl+v"),

        ["outlook"] = new AppProfile(
            "Outlook (desktop, classic)", "rich text target -- new mail compose window, body field (Tab navigation: To->Cc->Subject->Body, approximate)",
            () => Process.Start(new ProcessStartInfo(OutlookPath) { UseShellExecute = true, Arguments = "/c ipm.note" })!,
            SettleMs: 4000, TabsBeforeInject: 3, PasteChord: "ctrl+v"),

        ["word"] = new AppProfile(
            "Word", "rich text, autocorrect may mangle -- assumes caret starts in document body",
            () => Process.Start(new ProcessStartInfo(WordPath) { UseShellExecute = true })!,
            SettleMs: 5000, TabsBeforeInject: 0, PasteChord: "ctrl+v"),
    };

    internal static void SendTab()
    {
        ModifierSanitizer.SendKeyDown(0x09); // VK_TAB
        ModifierSanitizer.SendKeyUp(0x09);
        Thread.Sleep(150);
    }

    internal static void SendCtrlL()
    {
        ModifierSanitizer.SendKeyDown(NativeMethods.VK_LCONTROL);
        ModifierSanitizer.SendKeyDown(0x4C); // VK_L
        ModifierSanitizer.SendKeyUp(0x4C);
        ModifierSanitizer.SendKeyUp(NativeMethods.VK_LCONTROL);
        Thread.Sleep(150);
    }
}
