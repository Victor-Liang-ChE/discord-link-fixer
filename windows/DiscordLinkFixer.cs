using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

static class Program {
    public const int UserQuit = 20, Duplicate = 21, Failed = 1;
    public static bool WriteState(string name, string value) {
        try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name), value); return true; }
        catch { return false; }
    }
    public static void Error(string phase, Exception error) {
        WriteState("last-error.txt", phase + ": " + error.GetType().FullName + " HRESULT=" + error.HResult.ToString("X8")
            + ". No clipboard, message or exception-message data recorded.");
    }
    public static void StartupState(string phase) {
        WriteState("startup.json",
            "{\"pid\":" + Process.GetCurrentProcess().Id + ",\"session\":" + Process.GetCurrentProcess().SessionId
            + ",\"phase\":\"" + phase + "\",\"utc\":\"" + DateTime.UtcNow.ToString("o") + "\"}");
    }
    static readonly Regex UrlToken = new Regex("(?i)(?<![A-Za-z0-9_./@-])https?://[^\\s<>\\\"'`]+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public static string ConvertLink(string text) {
        if (text == null || text.Length > 500000) return text;
        return UrlToken.Replace(text, delegate(Match match) {
            string token = match.Value;
            int start = token.IndexOf("://", StringComparison.Ordinal) + 3;
            int slash = token.IndexOf('/', start);
            if (slash < 0) return token;
            string authority = token.Substring(start, slash - start);
            Uri uri;
            if (!Uri.TryCreate(token, UriKind.Absolute, out uri) || uri.UserInfo.Length != 0
                || !String.Equals(uri.Authority, authority, StringComparison.OrdinalIgnoreCase)) return token;
            string host = authority.ToLowerInvariant();
            if (host.StartsWith("www.", StringComparison.Ordinal)) host = host.Substring(4);
            string replacement = null;
            if (host == "x.com" || host == "twitter.com" || host == "mobile.x.com" || host == "mobile.twitter.com") replacement = "fxtwitter.com";
            else if (host == "instagram.com") replacement = "kkinstagram.com";
            else if (host == "tiktok.com") replacement = "tnktok.com";
            else if (host == "reddit.com") replacement = "vxreddit.com";
            return replacement == null ? token : token.Substring(0, start) + replacement + token.Substring(slash);
        });
    }
    public static bool MatchesShortcut(string process, int key, bool control, bool alt, bool windows) {
        return String.Equals(process, "Discord", StringComparison.OrdinalIgnoreCase) && key == 0x56 && control && !alt && !windows;
    }
    public static bool VersionChanged(string previous, string current) {
        return previous != null && !String.IsNullOrEmpty(current) && previous != current;
    }
    [DllImport("kernel32.dll")] static extern uint SetErrorMode(uint mode);
    [STAThread] static int Main(string[] args) {
        try {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) SetErrorMode(0x0001 | 0x0002 | 0x8000);
            return Start(args);
        } catch (Exception error) { Error("bootstrap", error); return Failed; }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static int Start(string[] args) {
        if (Array.IndexOf(args, "--bootstrap-check") >= 0) {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (assembly.GetName().Name == "System.Windows.Forms" || assembly.GetName().Name == "System.Web.Extensions") return Failed;
            return 0;
        }
        if (Array.IndexOf(args, "--quit") >= 0) {
            EventWaitHandle request;
            if (!EventWaitHandle.TryOpenExisting(QuitEventName(), out request)) return Duplicate;
            using (request) request.Set();
            return 0;
        }
        if (Array.IndexOf(args, "--dependency-check") >= 0) return CheckGuiDependency();
        if (Array.IndexOf(args, "--self-test") >= 0) {
            return SelfTest();
        }
        bool worker = Array.IndexOf(args, "--worker") >= 0;
        bool acquired;
        using (Mutex singleton = new Mutex(true, worker ? @"Local\DiscordLinkFixer.v1" : @"Local\DiscordLinkFixer.Supervisor.v1", out acquired)) {
            if (!acquired) return Duplicate;
            if (worker) return Worker();
            string marker = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "quit-until-login");
            if (Array.IndexOf(args, "--login") >= 0 || Array.IndexOf(args, "--resume") >= 0) {
                if (File.Exists(marker)) File.Delete(marker);
            } else if (File.Exists(marker)) return 0;
            string token = Array.Find(args, delegate(string arg) { return arg.StartsWith("--install-token=", StringComparison.Ordinal); });
            if (token != null && !Regex.IsMatch(token, @"\A--install-token=[a-f0-9]{32}\z")) return Failed;
            return Supervise(token);
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static int SelfTest() {
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
            int regressions = WindowsTests.Run();
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "self-test.json"), "{\"urlCases\":" + (cases.GetLength(0) + extraCases.Length) + ",\"scopeCases\":6,\"updateCases\":3,\"regressions\":" + regressions + ",\"passed\":true}");
            return 0;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static int Worker() {
        StartupState("visual_styles");
        try {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs eventArgs) { Error("ui", eventArgs.Exception); Application.ExitThread(); };
            Application.EnableVisualStyles();
            using (Helper helper = new Helper()) { Application.Run(helper); return helper.QuitRequested ? UserQuit : Failed; }
        } catch (Exception error) { Error("worker", error); return Failed; }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static int CheckGuiDependency() {
        return typeof(Application).Assembly.GetName().Version.Major == 4 ? 0 : Failed;
    }

    public static string QuitEventName() {
        using (var hash = System.Security.Cryptography.SHA256.Create())
            return @"Local\DiscordLinkFixer.Quit." + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).ToUpperInvariant()))).Replace("-", "");
    }
    static int Supervise(string token) {
            string root = AppDomain.CurrentDomain.BaseDirectory;
            int failures = 0;
            for (;;) {
                try {
                    ProcessStartInfo start = new ProcessStartInfo(Path.Combine(root, "DiscordLinkFixer.exe"), "--worker" + (token == null ? "" : " " + token));
                    start.UseShellExecute = false;
                    start.CreateNoWindow = true;
                    using (Process child = Process.Start(start)) {
                        WriteState("supervisor.json", "{\"pid\":" + Process.GetCurrentProcess().Id + ",\"workerPid\":" + child.Id + ",\"restarts\":" + failures + "}");
                        child.WaitForExit();
                        if (child.ExitCode == Duplicate) return 0;
                        if (child.ExitCode == UserQuit) {
                            WriteState("quit-until-login", "Intentional Quit. Login or the desktop shortcut resumes conversion.");
                            return 0;
                        }
                    }
                } catch (Exception error) { Error("supervisor", error); }
                failures++;
                Thread.Sleep(Math.Min(60000, 5000 * Math.Min(failures, 12)));
            }
    }
}

sealed class Helper : ApplicationContext {
    readonly NotifyIcon icon;
    readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer();
    readonly PasteService service;
    readonly EventWaitHandle quit = new EventWaitHandle(false, EventResetMode.AutoReset, Program.QuitEventName());
    bool enabled = true;
    bool quitPending;
    string lastWarning;
    public bool QuitRequested { get; private set; }

    public Helper() {
        Program.StartupState("tray_icon");
        icon = new NotifyIcon { Icon = SystemIcons.Application, Text = "Discord Link Fixer", Visible = true };
        ContextMenuStrip menu = new ContextMenuStrip();
        ToolStripMenuItem pause = new ToolStripMenuItem("Pause conversion");
        pause.Click += delegate { enabled = !enabled; service.Enabled = enabled; pause.Text = enabled ? "Pause conversion" : "Resume conversion"; icon.Text = enabled ? "Discord Link Fixer" : "Discord Link Fixer (paused)"; };
        menu.Items.Add(pause);
        menu.Items.Add("Pause to paste original links").Enabled = false;
        menu.Items.Add("Test notification", null, delegate { Notify("Notification test. This does not test Discord paste compatibility.", ToolTipIcon.Info); });
        menu.Items.Add("Quit until next login", null, delegate {
            quitPending = true; service.BeginStop();
        });
        icon.ContextMenuStrip = menu;
        service = new PasteService();
        Program.StartupState("install_hook");
        service.Start();
        refresh.Interval = 1000;
        refresh.Tick += delegate {
            if (quit.WaitOne(0)) { quitPending = true; service.BeginStop(); }
            if (quitPending && service.Stopped) { QuitRequested = true; ExitThread(); return; }
            if (!quitPending && service.Fatal) { service.BeginStop(); ExitThread(); return; }
            if (Interlocked.Exchange(ref service.CancelledPastes, 0) > 0) Notify("Paste cancelled after focus changed or processing timed out. Try Ctrl-V again.", ToolTipIcon.Warning);
            bool ready = service.HookInstalled;
            if (!ready && lastWarning != "hook") { lastWarning = "hook"; Notify("Keyboard hook unavailable. Ordinary paste is unchanged; recovery is being attempted.", ToolTipIcon.Warning); }
            else if (ready) lastWarning = null;
            service.RefreshProcesses();
            if (DateTime.UtcNow.Second % 15 == 0) WriteStatus();
            if (DateTime.UtcNow.Second == 0) CheckDiscordVersion();
        };
        refresh.Start();
        WriteStatus();
        Program.StartupState("ready");
    }

    void Notify(string message, ToolTipIcon kind) {
        try { icon.ShowBalloonTip(5000, "Discord Link Fixer", message, kind); }
        catch (Exception error) { Program.Error("notification", error); }
    }

    void WriteStatus() {
        Program.WriteState("running.json", "{\"pid\":" + Process.GetCurrentProcess().Id + ",\"session\":" + Process.GetCurrentProcess().SessionId
            + ",\"hookInstalled\":" + (service.HookInstalled ? "true" : "false") + ",\"clipboardPending\":" + (service.ClipboardPending ? "true" : "false")
            + ",\"maximumHookMs\":" + (Interlocked.Read(ref service.MaximumHookTicks) * 1000.0 / Stopwatch.Frequency).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)
            + ",\"pasteVerified\":false}");
    }

    void CheckDiscordVersion() {
        try {
            uint pid;
            PasteService.GetWindowThreadProcessId(PasteService.GetForegroundWindow(), out pid);
            using (Process foreground = Process.GetProcessById((int)pid)) {
                if (!String.Equals(foreground.ProcessName, "Discord", StringComparison.OrdinalIgnoreCase)) return;
                string version = FileVersionInfo.GetVersionInfo(foreground.MainModule.FileName).ProductVersion;
                if (String.IsNullOrEmpty(version)) return;
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "discord-version.txt");
                string previous = File.Exists(path) ? File.ReadAllText(path) : null;
                if (previous == version) return;
                Program.WriteState("discord-version.txt", version);
                if (Program.VersionChanged(previous, version)) Notify("Discord updated. Try a supported link in an unsent draft; do not press Send.", ToolTipIcon.Info);
            }
        } catch { /* Optional metadata never interrupts paste. */ }
    }

    protected override void ExitThreadCore() {
        refresh.Stop();
        service.Stop();
        icon.Visible = false;
        icon.Dispose();
        refresh.Dispose();
        quit.Dispose();
        Program.WriteState("running.json", "{\"stoppedNormally\":" + (QuitRequested ? "true" : "false") + "}");
        base.ExitThreadCore();
    }
}
