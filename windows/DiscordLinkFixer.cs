using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

static class Program {
    public static void StartupState(string phase) {
        File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup.json"),
            "{\"pid\":" + Process.GetCurrentProcess().Id + ",\"session\":" + Process.GetCurrentProcess().SessionId
            + ",\"phase\":\"" + phase + "\",\"utc\":\"" + DateTime.UtcNow.ToString("o") + "\"}");
    }
    static readonly string[,] LinkRules = {
        {@"(?i)(?<![A-Za-z0-9_./@-])(https?://)(?:www\.|mobile\.)?(?:x\.com|twitter\.com)(?=/)", "$1fxtwitter.com"},
        {@"(?i)(?<![A-Za-z0-9_./@-])(https?://)(?:www\.)?instagram\.com(?=/)", "$1kkinstagram.com"},
        {@"(?i)(?<![A-Za-z0-9_./@-])(https?://)(?:www\.)?tiktok\.com(?=/)", "$1tnktok.com"},
        {@"(?i)(?<![A-Za-z0-9_./@-])(https?://)(?:www\.)?reddit\.com(?=/)", "$1vxreddit.com"}
    };
    public static string ConvertLink(string text) {
        for (int i = 0; i < LinkRules.GetLength(0); i++) text = Regex.Replace(text, LinkRules[i, 0], LinkRules[i, 1], RegexOptions.CultureInvariant);
        return text;
    }
    public static bool MatchesShortcut(string process, int key, bool control, bool alt, bool windows) {
        return String.Equals(process, "Discord", StringComparison.OrdinalIgnoreCase) && key == 0x56 && control && !alt && !windows;
    }
    public static bool VersionChanged(string previous, string current) {
        return previous != null && !String.IsNullOrEmpty(current) && previous != current;
    }
    [STAThread] static int Main(string[] args) {
        if (Array.IndexOf(args, "--self-test") >= 0) {
            string sample = "https://x.com/eschatolocation/status/2107617817570709682?s=46";
            string[,] cases = {
                {sample, sample.Replace("x.com", "fxtwitter.com")},
                {"Look: <https://twitter.com/user/status/123#fragment>", "Look: <https://fxtwitter.com/user/status/123#fragment>"},
                {"https://www.x.com/user/status/123/photo/1", "https://fxtwitter.com/user/status/123/photo/1"},
                {"http://mobile.twitter.com/user/status/123", "http://fxtwitter.com/user/status/123"},
                {"https://x.com/i/web/status/123?s=46", "https://fxtwitter.com/i/web/status/123?s=46"},
                {sample + "\n" + sample, ConvertLink(sample) + "\n" + ConvertLink(sample)},
                {"https://x.com/user", "https://fxtwitter.com/user"},
                {"https://x.com.evil.example/user/status/123", "https://x.com.evil.example/user/status/123"},
                {"https://evil.example/https://x.com/user/status/123", "https://evil.example/https://x.com/user/status/123"},
                {"https://x.com/user/status/123abc", "https://fxtwitter.com/user/status/123abc"},
                {"https://fxtwitter.com/user/status/123", "https://fxtwitter.com/user/status/123"},
                {"ordinary text", "ordinary text"}
            };
            for (int i = 0; i < cases.GetLength(0); i++) if (ConvertLink(cases[i, 0]) != cases[i, 1]) throw new Exception("URL test " + i);
            var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
            string fixtures = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "link-tests.json");
            if (!File.Exists(fixtures)) fixtures = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "link-tests.json");
            var extraCases = serializer.Deserialize<string[][]>(File.ReadAllText(fixtures));
            foreach (var test in extraCases) if (ConvertLink(test[0]) != test[1] || ConvertLink(test[1]) != test[1]) throw new Exception("Additional URL or idempotency test");
            if (!MatchesShortcut("Discord", 0x56, true, false, false) || MatchesShortcut("Chrome", 0x56, true, false, false)
                || MatchesShortcut("Discord", 0x56, true, true, false) || MatchesShortcut("Discord", 0x56, true, false, true)
                || MatchesShortcut("Discord", 0x56, false, false, false) || MatchesShortcut("Discord", 0x41, true, false, false)) throw new Exception("Shortcut tests");
            if (VersionChanged(null, "1") || VersionChanged("1", "1") || !VersionChanged("1", "2")) throw new Exception("Update notice tests");
            // No access to the user's clipboard during automated tests.
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "self-test.json"), "{\"urlCases\":" + (cases.GetLength(0) + extraCases.Length) + ",\"scopeCases\":6,\"updateCases\":3,\"passed\":true}");
            return 0;
        }
        if (Array.IndexOf(args, "--worker") < 0) {
            string marker = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "quit-until-login");
            if (Array.IndexOf(args, "--login") >= 0) { if (File.Exists(marker)) File.Delete(marker); }
            else if (File.Exists(marker)) return 0;
            return Supervise();
        }
        bool acquired;
        using (Mutex singleton = new Mutex(true, @"Local\DiscordLinkFixer.v1", out acquired)) {
            if (!acquired) return 0;
            StartupState("visual_styles");
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Application.EnableVisualStyles();
            try { using (Helper helper = new Helper()) Application.Run(helper); }
            catch { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "last-error.txt"), "Helper exited unexpectedly. Task Scheduler will retry. No message or clipboard data was recorded."); return 1; }
        }
        return 0;
    }

    static int Supervise() {
        bool acquired;
        using (Mutex singleton = new Mutex(true, @"Local\DiscordLinkFixer.Supervisor.v1", out acquired)) {
            if (!acquired) return 0;
            string root = AppDomain.CurrentDomain.BaseDirectory;
            int failures = 0;
            for (;;) {
                try {
                    ProcessStartInfo start = new ProcessStartInfo(Application.ExecutablePath, "--worker");
                    start.UseShellExecute = false;
                    start.CreateNoWindow = true;
                    using (Process child = Process.Start(start)) {
                        File.WriteAllText(Path.Combine(root, "supervisor.json"), "{\"pid\":" + Process.GetCurrentProcess().Id + ",\"workerPid\":" + child.Id + ",\"restarts\":" + failures + "}");
                        child.WaitForExit();
                        if (child.ExitCode == 0) {
                            File.WriteAllText(Path.Combine(root, "quit-until-login"), "Intentional Quit. Login or the desktop shortcut resumes conversion.");
                            return 0;
                        }
                    }
                } catch { /* Retry app-launch failures without logging private data. */ }
                failures++;
                Thread.Sleep(Math.Min(60000, 5000 * Math.Min(failures, 12)));
            }
        }
    }
}

sealed class Helper : ApplicationContext {
    delegate IntPtr HookCallback(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int type, HookCallback callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)] static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    readonly HookCallback callback;
    readonly NotifyIcon icon;
    readonly System.Windows.Forms.Timer restore = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer();
    IntPtr hook;
    DataObject snapshot;
    uint changedSequence;
    bool enabled = true;
    string lastWarning;
    readonly string statePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "running.json");

    public Helper() {
        callback = OnKey;
        Program.StartupState("tray_icon");
        icon = new NotifyIcon { Icon = SystemIcons.Application, Text = "Discord Link Fixer", Visible = true };
        Program.StartupState("tray_menu");
        ContextMenuStrip menu = new ContextMenuStrip();
        ToolStripMenuItem pause = new ToolStripMenuItem("Pause conversion");
        pause.Click += delegate { enabled = !enabled; pause.Text = enabled ? "Pause conversion" : "Resume conversion"; icon.Text = enabled ? "Discord Link Fixer" : "Discord Link Fixer (paused)"; };
        menu.Items.Add(pause);
        menu.Items.Add("Ctrl+Alt+V bypasses conversion").Enabled = false;
        menu.Items.Add("Test notification", null, delegate { icon.ShowBalloonTip(5000, "Discord Link Fixer", "Notification test. This does not test Discord paste compatibility.", ToolTipIcon.Info); });
        menu.Items.Add("Quit until next login", null, delegate { ExitThread(); });
        icon.ContextMenuStrip = menu;
        restore.Interval = 1000;
        restore.Tick += delegate { RestoreClipboard(); };
        refresh.Interval = 60000;
        refresh.Tick += delegate {
            try {
                InstallHook();
                lastWarning = null;
                WriteStatus(true);
            } catch {
                Warn("Keyboard hook refresh failed. Link conversion may be unavailable; reopen the helper.");
                WriteStatus(false);
            }
            CheckDiscordVersion();
        };
        Program.StartupState("install_hook");
        InstallHook();
        refresh.Start();
        WriteStatus(true);
        Program.StartupState("ready");
    }

    void WriteStatus(bool installed) {
        File.WriteAllText(statePath, "{\"pid\":" + Process.GetCurrentProcess().Id + ",\"session\":" + Process.GetCurrentProcess().SessionId + ",\"hookInstalled\":" + (installed ? "true" : "false") + "}");
    }

    void Warn(string message) {
        if (lastWarning == message) return;
        lastWarning = message;
        icon.ShowBalloonTip(5000, "Discord Link Fixer", message, ToolTipIcon.Warning);
    }

    void CheckDiscordVersion() {
        try {
            uint pid;
            GetWindowThreadProcessId(GetForegroundWindow(), out pid);
            using (Process foreground = Process.GetProcessById((int)pid)) {
                if (!String.Equals(foreground.ProcessName, "Discord", StringComparison.OrdinalIgnoreCase)) return;
                string version = FileVersionInfo.GetVersionInfo(foreground.MainModule.FileName).ProductVersion;
                if (String.IsNullOrEmpty(version)) return;
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "discord-version.txt");
                string previous = File.Exists(path) ? File.ReadAllText(path) : null;
                if (previous == version) return;
                File.WriteAllText(path, version);
                if (Program.VersionChanged(previous, version))
                    icon.ShowBalloonTip(5000, "Discord Link Fixer", "Discord updated. Paste compatibility has not been verified. Try a supported link in an unsent draft; do not press Send.", ToolTipIcon.Info);
            }
        } catch { /* Version metadata is optional; it must not interrupt conversion. */ }
    }

    void InstallHook() {
        IntPtr created = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
        if (created == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        IntPtr old = hook;
        hook = created;
        if (old != IntPtr.Zero) UnhookWindowsHookEx(old);
    }

    static bool Down(int key) { return (GetAsyncKeyState(key) & 0x8000) != 0; }
    IntPtr OnKey(int code, IntPtr message, IntPtr data) {
        if (code >= 0 && enabled && (message == (IntPtr)0x100 || message == (IntPtr)0x104) && Marshal.ReadInt32(data) == 0x56
            && Down(0x11) && !Down(0x12) && !Down(0x5B) && !Down(0x5C)) {
            try {
                uint pid;
                GetWindowThreadProcessId(GetForegroundWindow(), out pid);
                using (Process foreground = Process.GetProcessById((int)pid)) {
                    if (Program.MatchesShortcut(foreground.ProcessName, 0x56, true, false, false)) PreparePaste();
                }
            } catch { /* A busy clipboard or disappearing foreground app leaves the ordinary paste untouched. */ }
        }
        return CallNextHookEx(hook, code, message, data);
    }

    void PreparePaste() {
        if (!Clipboard.ContainsText(TextDataFormat.UnicodeText) || Clipboard.ContainsImage() || Clipboard.ContainsFileDropList()) return;
        string text = Clipboard.GetText(TextDataFormat.UnicodeText);
        if (text.Length > 500000) return;
        string converted = Program.ConvertLink(text);
        if (converted == text) return;
        IDataObject data = Clipboard.GetDataObject();
        DataObject saved = new DataObject();
        foreach (string format in data.GetFormats(false)) {
            object value = data.GetData(format, false);
            MemoryStream stream = value as MemoryStream;
            saved.SetData(format, false, stream == null ? value : new MemoryStream(stream.ToArray()));
        }
        // Restore a prior conversion before starting another, but never overwrite a new copy.
        RestoreClipboard();
        snapshot = saved;
        Clipboard.SetDataObject(converted, true, 1, 10);
        changedSequence = GetClipboardSequenceNumber();
        restore.Start();
    }

    void RestoreClipboard() {
        restore.Stop();
        if (snapshot != null && GetClipboardSequenceNumber() == changedSequence) {
            try { Clipboard.SetDataObject(snapshot, true, 1, 10); }
            catch { restore.Start(); return; }
        }
        snapshot = null;
    }

    protected override void ExitThreadCore() {
        RestoreClipboard();
        refresh.Stop();
        if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
        icon.Visible = false;
        icon.Dispose();
        restore.Dispose();
        refresh.Dispose();
        File.WriteAllText(statePath, "{\"stoppedNormally\":true}");
        base.ExitThreadCore();
    }
}
