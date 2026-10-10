// Run as a separate process. Its private, non-visible window station has its own clipboard.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class NativeClipboardTests {
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr CreateWindowStation(string name, uint flags, uint access, IntPtr security);
    [DllImport("user32.dll")] static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll", SetLastError=true)] static extern bool SetProcessWindowStation(IntPtr station);
    [DllImport("user32.dll")] static extern bool CloseWindowStation(IntPtr station);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr security);
    [DllImport("user32.dll")] static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError=true)] static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr window);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint format, IntPtr value);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr memory);
    static int count;
    static void Check(bool value, string label) { count++; if (!value) throw new Exception(label); }
    static byte[] Text(string value) { return Encoding.Unicode.GetBytes(value + "\0"); }
    static Dictionary<uint, byte[]> Data(string value) { return new Dictionary<uint, byte[]> { { 13, Text(value) } }; }
    static void Publish(uint format, byte[] value) {
        IntPtr memory = GlobalAlloc(2, (UIntPtr)value.Length);
        if (memory == IntPtr.Zero) throw new OutOfMemoryException();
        IntPtr pointer = GlobalLock(memory);
        if (pointer == IntPtr.Zero) { GlobalFree(memory); throw new Exception("GlobalLock"); }
        try { Marshal.Copy(value, 0, pointer, value.Length); }
        finally { GlobalUnlock(memory); }
        if (SetClipboardData(format, memory) == IntPtr.Zero) { GlobalFree(memory); throw new Exception("SetClipboardData"); }
    }
    sealed class Window : NativeWindow, IDisposable {
        public bool Rendered;
        public Window() { CreateHandle(new CreateParams { Parent = (IntPtr)(-3) }); }
        protected override void WndProc(ref Message message) {
            if (message.Msg == 0x305) { Rendered = true; Publish(13, Text("https://x.com/delayed/status/123")); return; }
            base.WndProc(ref message);
        }
        public void Dispose() { DestroyHandle(); }
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)] static void Run() {
        using (var window = new Window()) {
            var store = new NativeClipboard(window.Handle);
            var original = Data("https://x.com/user/status/123");
            uint changed;
            Check(store.Write(store.Sequence, original, original, out changed) == ClipboardWrite.Written, "initial native write");
            Console.WriteLine("initial sequence returned=" + changed + " current=" + store.Sequence);
            var snapshot = store.Read();
            Check(snapshot != null && snapshot.Text == "https://x.com/user/status/123", "native bounded read");
            var transaction = new ClipboardTransaction(store); transaction.Prepare();
            Check(transaction.Paste(store.Sequence, 0, delegate { return true; }), "native conversion");
            Console.WriteLine("conversion sequence recorded=" + transaction.ReadySequence + " current=" + store.Sequence);
            Check(store.Read().Text == "https://fxtwitter.com/user/status/123", "native converted value");
            uint history = RegisterClipboardFormat("CanIncludeInClipboardHistory"), cloud = RegisterClipboardFormat("CanUploadToCloudClipboard");
            var temporary = store.Read();
            Console.WriteLine("sequence after reader=" + store.Sequence + " pending=" + transaction.Pending);
            Check(temporary.Formats.ContainsKey(history) && BitConverter.ToInt32(temporary.Formats[history], 0) == 0, "temporary history disabled");
            Check(temporary.Formats.ContainsKey(cloud) && BitConverter.ToInt32(temporary.Formats[cloud], 0) == 0, "temporary cloud sync disabled");
            Check(transaction.Restore(1000, false), "native restoration");
            Console.WriteLine("restored sequence=" + store.Sequence + " value=" + store.Read().Text);
            Check(store.Read().Text == "https://x.com/user/status/123", "native original preserved");
            Check(!store.Read().Formats.ContainsKey(RegisterClipboardFormat("DiscordLinkFixer.Transaction.v2")), "temporary marker removed on restoration");

            snapshot = store.Read();
            var newCopy = Data("new concurrent copy");
            Check(store.Write(snapshot.Sequence, newCopy, snapshot.Formats, out changed) == ClipboardWrite.Written, "concurrent copy");
            Check(store.Write(snapshot.Sequence, Data("must not overwrite"), snapshot.Formats, out changed) == ClipboardWrite.Unchanged, "atomic stale comparison");
            Check(store.Read().Text == "new concurrent copy", "native new copy survives");

            var oversized = new Dictionary<uint, byte[]> { { 13, new byte[NativeClipboard.MaxFormatBytes + 1] } };
            uint sequence = store.Sequence;
            Check(store.Write(sequence, oversized, newCopy, out changed) == ClipboardWrite.Unchanged && store.Sequence == sequence, "oversized write before EmptyClipboard");

            using (var locked = new ManualResetEvent(false)) using (var release = new ManualResetEvent(false)) {
                bool opened = false;
                Thread holder = new Thread(delegate() { opened = OpenClipboard(IntPtr.Zero); locked.Set(); release.WaitOne(5000); if (opened) CloseClipboard(); });
                holder.SetApartmentState(ApartmentState.STA); holder.Start();
                Check(locked.WaitOne(5000) && opened, "isolated contention acquired");
                try {
                    Check(store.Write(store.Sequence, original, newCopy, out changed) == ClipboardWrite.Unchanged, "busy native write fails open");
                } finally { release.Set(); holder.Join(5000); }
            }
            Check(store.Read().Text == "new concurrent copy", "contention leaves copy intact");

            uint html = RegisterClipboardFormat("HTML Format"), rtf = RegisterClipboardFormat("Rich Text Format");
            var rich = Data("https://x.com/rich/status/123");
            rich[html] = Encoding.UTF8.GetBytes("Version:1.0\r\n<html>known test data</html>\0");
            rich[rtf] = Encoding.ASCII.GetBytes("{\\rtf1 known test data}\0");
            Check(store.Write(store.Sequence, rich, rich, out changed) == ClipboardWrite.Written, "rich raw write");
            transaction = new ClipboardTransaction(store); transaction.Prepare();
            Check(transaction.Paste(store.Sequence, 0, delegate { return true; }) && transaction.Restore(1000, false), "rich conversion and restoration");
            snapshot = store.Read();
            Check(Convert.ToBase64String(snapshot.Formats[html]) == Convert.ToBase64String(rich[html]), "HTML bytes preserved without deserialization");
            Check(Convert.ToBase64String(snapshot.Formats[rtf]) == Convert.ToBase64String(rich[rtf]), "RTF bytes preserved without deserialization");

            Check(OpenClipboard(window.Handle), "unknown-format clipboard lock");
            try { EmptyClipboard(); Publish(13, Text("https://x.com/user/status/123")); Publish(RegisterClipboardFormat("UnknownSerializedObjectTest"), new byte[] { 1, 2, 3 }); }
            finally { CloseClipboard(); }
            sequence = store.Sequence;
            Check(store.Read() == null && store.Sequence == sequence, "unknown custom formats never materialized");

            Check(OpenClipboard(window.Handle), "file clipboard lock");
            try { EmptyClipboard(); Publish(13, Text("https://x.com/user/status/123")); Publish(15, new byte[32]); }
            finally { CloseClipboard(); }
            Check(store.Read() == null, "file and text clipboard untouched");

            Check(OpenClipboard(window.Handle), "oversized-read clipboard lock");
            try { EmptyClipboard(); Publish(13, new byte[NativeClipboard.MaxFormatBytes + 2]); }
            finally { CloseClipboard(); }
            Check(store.Read() == null, "oversized native read rejected");

            Check(OpenClipboard(window.Handle), "delayed clipboard lock");
            try { EmptyClipboard(); SetClipboardData(13, IntPtr.Zero); }
            finally { CloseClipboard(); }
            snapshot = store.Read();
            Check(window.Rendered && snapshot != null && snapshot.Text.Contains("/delayed/"), "delayed rendering read outside keyboard callback");
        }
    }
    static int Main() {
        IntPtr oldStation = GetProcessWindowStation(), oldDesktop = GetThreadDesktop(GetCurrentThreadId());
        IntPtr station = IntPtr.Zero, desktop = IntPtr.Zero;
        try {
            station = CreateWindowStation("DiscordLinkFixerValidation" + Guid.NewGuid().ToString("N"), 0, 0x37f, IntPtr.Zero);
            Check(station != IntPtr.Zero && SetProcessWindowStation(station), "private window station");
            desktop = CreateDesktop("Default", IntPtr.Zero, IntPtr.Zero, 0, 0x1ff, IntPtr.Zero);
            Check(desktop != IntPtr.Zero, "private non-visible desktop");
            Exception failure = null;
            // A fresh MTA thread avoids CLR/OLE windows already attached to the caller's desktop.
            Thread worker = new Thread(delegate() {
                try {
                    Check(SetThreadDesktop(desktop), "private clipboard thread desktop");
                    Run();
                } catch (Exception error) { failure = error; }
            });
            worker.Start();
            if (!worker.Join(30000)) throw new Exception("Private clipboard test timed out");
            if (failure != null) throw failure;
            Console.WriteLine("{\"passed\":true,\"nativeChecks\":" + count + ",\"userClipboardTouched\":false,\"desktopSwitched\":false}");
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error.GetType().FullName + ": " + error.Message + " Win32=" + Marshal.GetLastWin32Error()); return 1; }
        finally {
            SetThreadDesktop(oldDesktop); SetProcessWindowStation(oldStation);
            if (desktop != IntPtr.Zero) CloseDesktop(desktop);
            if (station != IntPtr.Zero) CloseWindowStation(station);
        }
    }
}
