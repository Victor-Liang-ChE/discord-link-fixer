using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

sealed class PasteJob {
    public IntPtr Window, Focus;
    public uint Sequence;
    public long Created;
}

sealed class PasteService {
    const int JobMessage = 0x8001, StopMessage = 0x8002;
    readonly ConcurrentQueue<PasteJob> jobs = new ConcurrentQueue<PasteJob>();
    readonly ManualResetEvent stopped = new ManualResetEvent(false), clipboardStarted = new ManualResetEvent(false);
    IntPtr clipboardWindow, keyboardWindow;
    int queued;
    volatile int[] discordProcesses = new int[0];
    volatile uint readySequence;
    volatile bool stopping;
    public volatile bool Enabled = true, HookInstalled;
    public volatile bool ClipboardPending;
    public volatile bool Fatal;
    public bool Stopped { get { return stopped.WaitOne(0); } }
    public static long Now { get { return Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency; } }
    public long MaximumHookTicks;
    public int CancelledPastes;

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [StructLayout(LayoutKind.Sequential)] struct GuiThreadInfo {
        public uint Size, Flags;
        public IntPtr Active, Focus, Capture, Menu, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }
    static IntPtr FocusOf(IntPtr window) {
        uint pid;
        uint thread = GetWindowThreadProcessId(window, out pid);
        GuiThreadInfo info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf(typeof(GuiThreadInfo)) };
        return GetGUIThreadInfo(thread, ref info) ? info.Focus : IntPtr.Zero;
    }
    static bool Down(int key) { return (GetAsyncKeyState(key) & 0x8000) != 0; }
    public static bool TargetValid(PasteJob job) { return GetForegroundWindow() == job.Window && FocusOf(job.Window) == job.Focus; }
    public static bool JobFresh(PasteJob job, long now) { return now >= job.Created && now - job.Created <= 250; }

    public void RefreshProcesses() {
        var ids = new System.Collections.Generic.List<int>();
        string root = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Discord")) + Path.DirectorySeparatorChar;
        foreach (Process process in Process.GetProcessesByName("Discord")) {
            using (process) {
                try {
                    string path = Path.GetFullPath(process.MainModule.FileName);
                    if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && String.Equals(Path.GetFileName(path), "Discord.exe", StringComparison.OrdinalIgnoreCase)) ids.Add(process.Id);
                } catch { /* An exiting or higher-integrity process is not an eligible target. */ }
            }
        }
        discordProcesses = ids.ToArray();
    }

    public void Start() {
        RefreshProcesses();
        Thread clipboard = new Thread(delegate() {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException, true);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs args) { Program.Error("clipboard_loop", args.Exception); Fatal = true; Application.ExitThread(); };
            try { using (ClipboardWindow window = new ClipboardWindow(this)) { clipboardWindow = window.Handle; clipboardStarted.Set(); Application.Run(); } }
            catch (Exception error) { Program.Error("clipboard_thread", error); Fatal = true; }
            finally { if (!stopping) Fatal = true; clipboardWindow = IntPtr.Zero; readySequence = 0; clipboardStarted.Set(); stopped.Set(); }
        });
        clipboard.IsBackground = true; clipboard.SetApartmentState(ApartmentState.STA); clipboard.Start();
        if (!clipboardStarted.WaitOne(5000) || clipboardWindow == IntPtr.Zero) throw new InvalidOperationException("Clipboard worker unavailable");
        Thread keyboard = new Thread(delegate() {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException, true);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs args) { Program.Error("keyboard_loop", args.Exception); Fatal = true; Application.ExitThread(); };
            try { using (KeyboardWindow window = new KeyboardWindow(this)) { keyboardWindow = window.Handle; Application.Run(); } }
            catch (Exception error) { Program.Error("keyboard_thread", error); Fatal = true; }
            finally { if (!stopping) Fatal = true; keyboardWindow = IntPtr.Zero; HookInstalled = false; }
        });
        keyboard.IsBackground = true; keyboard.SetApartmentState(ApartmentState.STA); keyboard.Start();
    }

    bool Enqueue() {
        if (stopping || !Enabled || readySequence == 0 || NativeClipboard.GetClipboardSequenceNumber() != readySequence) return false;
        IntPtr foreground = GetForegroundWindow(); uint pid;
        GetWindowThreadProcessId(foreground, out pid);
        if (Array.IndexOf(discordProcesses, (int)pid) < 0) return false;
        if (Interlocked.Increment(ref queued) > 8) { Interlocked.Decrement(ref queued); return false; }
        try {
            jobs.Enqueue(new PasteJob { Window = foreground, Focus = FocusOf(foreground), Sequence = readySequence, Created = Now });
            PostMessage(clipboardWindow, JobMessage, IntPtr.Zero, IntPtr.Zero);
            return true;
        } catch { Interlocked.Decrement(ref queued); return false; }
    }

    public void BeginStop() {
        Enabled = false; stopping = true;
        if (keyboardWindow != IntPtr.Zero) PostMessage(keyboardWindow, StopMessage, IntPtr.Zero, IntPtr.Zero);
        if (clipboardWindow != IntPtr.Zero) PostMessage(clipboardWindow, StopMessage, IntPtr.Zero, IntPtr.Zero);
    }
    public bool Stop() { BeginStop(); return stopped.WaitOne(2000); }

    sealed class KeyboardWindow : NativeWindow, IDisposable {
        delegate IntPtr HookCallback(int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll", SetLastError=true)] static extern IntPtr SetWindowsHookEx(int type, HookCallback callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("kernel32.dll", CharSet=CharSet.Auto)] static extern IntPtr GetModuleHandle(string name);
        readonly PasteService service;
        readonly HookCallback callback;
        readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer();
        IntPtr hook;
        public KeyboardWindow(PasteService service) {
            this.service = service; callback = OnKey;
            CreateHandle(new CreateParams { Parent = (IntPtr)(-3) });
            Install(); refresh.Interval = 60000; refresh.Tick += delegate { Install(); }; refresh.Start();
        }
        void Install() {
            IntPtr created = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
            if (created == IntPtr.Zero) { service.HookInstalled = false; return; }
            IntPtr previous = hook; hook = created;
            if (previous != IntPtr.Zero) UnhookWindowsHookEx(previous);
            service.HookInstalled = true;
        }
        IntPtr OnKey(int code, IntPtr message, IntPtr data) {
            long started = Stopwatch.GetTimestamp();
            try {
                // No clipboard reads, regex, file IO, provider calls or waits in the keyboard callback.
                if (code >= 0 && (message == (IntPtr)0x100 || message == (IntPtr)0x104)
                    && Marshal.ReadInt32(data) == 0x56 && (Marshal.ReadInt32(data, 8) & 0x10) == 0
                    && Down(0x11) && !Down(0x12) && !Down(0x5B) && !Down(0x5C) && service.Enqueue()) return (IntPtr)1;
            } catch { /* Fail open. The original keyboard event continues. */ }
            finally {
                long elapsed = Stopwatch.GetTimestamp() - started;
                if (elapsed > service.MaximumHookTicks) Interlocked.Exchange(ref service.MaximumHookTicks, elapsed);
            }
            return CallNextHookEx(hook, code, message, data);
        }
        protected override void WndProc(ref Message message) {
            if (message.Msg == StopMessage) { refresh.Stop(); Application.ExitThread(); return; }
            base.WndProc(ref message);
        }
        public void Dispose() { refresh.Dispose(); if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook); DestroyHandle(); service.HookInstalled = false; }
    }

    sealed class ClipboardWindow : NativeWindow, IDisposable {
        [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr window);
        [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr window);
        readonly PasteService service;
        readonly ClipboardTransaction transaction;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        bool dirty = true;
        public ClipboardWindow(PasteService service) {
            this.service = service;
            CreateHandle(new CreateParams { Parent = (IntPtr)(-3) });
            transaction = new ClipboardTransaction(new NativeClipboard(Handle));
            if (!AddClipboardFormatListener(Handle)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            timer.Interval = 25; timer.Tick += delegate { Pump(); }; timer.Start();
        }
        void Pump() {
            try {
                if (service.stopping) {
                    PasteJob cancelled; while (service.jobs.TryDequeue(out cancelled)) Interlocked.Decrement(ref service.queued);
                    if (transaction.Restore(Now, true)) { timer.Stop(); Application.ExitThread(); }
                    return;
                }
                PasteJob job;
                while (service.jobs.TryDequeue(out job)) {
                    Interlocked.Decrement(ref service.queued);
                    if (!JobFresh(job, Now) || !TargetValid(job)) { Interlocked.Increment(ref service.CancelledPastes); continue; }
                    transaction.Paste(job.Sequence, Now, delegate { return TargetValid(job); });
                    service.readySequence = transaction.ReadySequence;
                    if (TargetValid(job) && JobFresh(job, Now) && !Down(0x12) && !Down(0x5B) && !Down(0x5C)) {
                        if (!ReplayPaste()) { transaction.Restore(Now, true); Interlocked.Increment(ref service.CancelledPastes); }
                    } else { transaction.Restore(Now, true); Interlocked.Increment(ref service.CancelledPastes); }
                }
                bool wasPending = transaction.Pending;
                transaction.Restore(Now, false);
                if (wasPending && !transaction.Pending) dirty = true;
                if (dirty && !transaction.Pending) { dirty = false; transaction.Prepare(); }
                service.readySequence = transaction.ReadySequence;
                service.ClipboardPending = transaction.Pending;
                service.ClipboardPending = transaction.Pending;
            } catch (Exception error) { service.readySequence = 0; Program.Error("clipboard_operation", error); }
        }
        protected override void WndProc(ref Message message) {
            if (message.Msg == 0x31D) { if (NativeClipboard.GetClipboardSequenceNumber() != transaction.ReadySequence) { dirty = true; service.readySequence = 0; } return; }
            if (message.Msg == JobMessage || message.Msg == StopMessage) { Pump(); return; }
            base.WndProc(ref message);
        }
        public void Dispose() { timer.Dispose(); transaction.Restore(Now, true); RemoveClipboardFormatListener(Handle); DestroyHandle(); }
    }

    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Explicit)] internal struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] internal struct Input { public uint Type; public InputUnion Value; }
    [DllImport("user32.dll", SetLastError=true)] static extern uint SendInput(uint count, Input[] inputs, int size);
    static Input Key(ushort key, bool up) { return new Input { Type = 1, Value = new InputUnion { Keyboard = new KeyboardInput { Key = key, Flags = up ? 2u : 0u, Extra = (UIntPtr)0xD15C0F1 } } }; }
    static bool ReplayPaste() {
        bool control = Down(0x11);
        Input[] inputs = PasteInputs(control);
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Input)));
        if (sent > 0 && sent < inputs.Length) {
            Input[] release = control ? new[] { Key(0x56, true) } : new[] { Key(0x56, true), Key(0x11, true) };
            SendInput((uint)release.Length, release, Marshal.SizeOf(typeof(Input)));
        }
        return sent == inputs.Length;
    }
    internal static Input[] PasteInputs(bool control) {
        return control ? new[] { Key(0x56, false), Key(0x56, true) }
            : new[] { Key(0x11, false), Key(0x56, false), Key(0x56, true), Key(0x11, true) };
    }
}
