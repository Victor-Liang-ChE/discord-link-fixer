using System;
using System.Collections.Generic;
using System.Text;

static class WindowsTests {
    static int count;
    static void Check(bool condition, string label) { count++; if (!condition) throw new Exception("Regression: " + label); }
    static Dictionary<uint, byte[]> Text(string text) { return new Dictionary<uint, byte[]> { { 13, Encoding.Unicode.GetBytes(text + "\0") } }; }
    sealed class Store : IClipboardStore {
        public uint Sequence { get; private set; }
        public Dictionary<uint, byte[]> Data = Text("https://x.com/user/status/123");
        public bool Busy, Unsupported, FailAfterClear;
        public bool TaggedOwnership, Owned;
        public Action BeforeLock;
        public int Reads, Writes;
        public Store() { Sequence = 10; }
        public bool Owns(uint sequence) { return TaggedOwnership ? Owned : Sequence == sequence; }
        public void Copy(string text) { Data = Text(text); Sequence++; Owned = false; }
        public void SynthesizedFormat() { Sequence++; }
        public string Value { get { return Encoding.Unicode.GetString(Data[13]).TrimEnd('\0'); } }
        public ClipboardSnapshot Read() {
            Reads++;
            return Busy || Unsupported ? null : new ClipboardSnapshot { Sequence = Sequence, Formats = Data, Text = Value };
        }
        public ClipboardWrite Write(uint expected, Dictionary<uint, byte[]> desired, Dictionary<uint, byte[]> rollback, out uint changed) {
            changed = 0;
            Action before = BeforeLock; BeforeLock = null; if (before != null) before();
            bool restoringOwned = TaggedOwnership && Owned && Object.ReferenceEquals(desired, rollback);
            if (Busy || Sequence != expected && !restoringOwned) return ClipboardWrite.Unchanged;
            Writes++;
            Sequence++;
            if (FailAfterClear) { FailAfterClear = false; Data = Text(""); changed = Sequence; return ClipboardWrite.RestorePending; }
            Data = desired; Owned = !Object.ReferenceEquals(desired, rollback); changed = Sequence; return ClipboardWrite.Written;
        }
    }
    static ClipboardTransaction Prepared(Store store) { var transaction = new ClipboardTransaction(store); transaction.Prepare(); return transaction; }
    static bool Valid() { return true; }

    public static int Run() {
        count = 0;
        string[,] urls = {
            {"https://example.com/?redirect=https://x.com/user/status/123", "https://example.com/?redirect=https://x.com/user/status/123"},
            {"https://example.com/#https://x.com/user/status/123", "https://example.com/#https://x.com/user/status/123"},
            {"https://example.com/?a=1&target=https://instagram.com/p/abc/", "https://example.com/?a=1&target=https://instagram.com/p/abc/"},
            {"https://example.com?target=https://x.com/user/status/123", "https://example.com?target=https://x.com/user/status/123"},
            {"https://x.com@attacker.example/user/status/123", "https://x.com@attacker.example/user/status/123"},
            {"https://user@x.com/user/status/123", "https://user@x.com/user/status/123"},
            {"https://x.com:443/user/status/123", "https://x.com:443/user/status/123"},
            {"https://x.com\\@attacker.example/user/status/123", "https://x.com\\@attacker.example/user/status/123"},
            {"https://x.com.attacker.example/user/status/123", "https://x.com.attacker.example/user/status/123"},
            {"https://x.com/user/status/1 https://instagram.com/p/A/", "https://fxtwitter.com/user/status/1 https://kkinstagram.com/p/A/"},
            {"<HTTPS://WWW.X.COM/user/status/1#part>", "<HTTPS://fxtwitter.com/user/status/1#part>"},
            {"(https://x.com/user/status/1).", "(https://fxtwitter.com/user/status/1)."},
            {"https://x.com", "https://x.com"},
            {"https://mobile.twitter.com/user/status/1", "https://fxtwitter.com/user/status/1"}
        };
        for (int i = 0; i < urls.GetLength(0); i++) {
            Check(Program.ConvertLink(urls[i, 0]) == urls[i, 1], "URL boundary " + i);
            Check(Program.ConvertLink(urls[i, 1]) == urls[i, 1], "idempotency " + i);
        }
        Check(Program.ConvertLink(null) == null, "null text");
        string oversized = new string('a', 500001);
        Check(Object.ReferenceEquals(Program.ConvertLink(oversized), oversized), "oversized text not scanned");
        var store = new Store(); var transaction = Prepared(store);
        Check(transaction.Paste(10, 0, Valid), "ordinary conversion");
        Check(store.Value == "https://fxtwitter.com/user/status/123", "converted value");
        Check(!transaction.Restore(999, false), "delay respected");
        Check(transaction.Restore(1000, false) && store.Value == "https://x.com/user/status/123", "original restored");

        store = new Store(); transaction = Prepared(store); transaction.Paste(10, 0, Valid);
        store.Copy("new copy");
        Check(transaction.Restore(1000, false) && store.Value == "new copy", "new copy after conversion preserved");
        store = new Store(); transaction = Prepared(store);
        store.BeforeLock = delegate { store.Copy("copy during preparation"); };
        Check(!transaction.Paste(10, 0, Valid) && store.Value == "copy during preparation", "preparation race blocked by locked comparison");
        store = new Store(); transaction = Prepared(store); transaction.Paste(10, 0, Valid);
        store.BeforeLock = delegate { store.Copy("copy during restoration"); };
        Check(transaction.Restore(1000, false) && store.Value == "copy during restoration", "restore race blocked by locked comparison");

        store = new Store(); transaction = Prepared(store); transaction.Paste(10, 0, Valid);
        Check(transaction.Paste(store.Sequence, 950, Valid), "repeat paste reuses active conversion");
        Check(!transaction.Restore(1000, false) && store.Value.Contains("fxtwitter"), "repeat renews delay");
        Check(transaction.Restore(1950, false), "repeat eventually restores");
        store = new Store { TaggedOwnership = true }; transaction = Prepared(store); transaction.Paste(10, 0, Valid);
        store.SynthesizedFormat();
        Check(transaction.Restore(1000, false) && store.Value.Contains("https://x.com/"), "Windows synthesized-format sequence still restores owned data");
        store = new Store(); transaction = Prepared(store); store.Busy = true;
        Check(!transaction.Paste(10, 0, Valid) && store.Writes == 0, "busy replacement fails open");
        store = new Store(); transaction = Prepared(store); transaction.Paste(10, 0, Valid); store.Busy = true;
        Check(!transaction.Restore(1000, true) && transaction.Pending, "quit waits for busy clipboard");
        store.Busy = false;
        Check(transaction.Restore(1025, true) && !transaction.Pending, "quit retries safely");
        store = new Store(); transaction = Prepared(store); store.FailAfterClear = true;
        Check(!transaction.Paste(10, 0, Valid) && transaction.Pending, "partial write retains original");
        Check(transaction.Restore(0, true) && store.Value.Contains("https://x.com/"), "partial failure recovered");
        store = new Store { Unsupported = true }; transaction = Prepared(store);
        Check(!transaction.Paste(10, 0, Valid) && store.Writes == 0, "unsupported clipboard untouched");
        store = new Store(); transaction = Prepared(store);
        Check(!transaction.Paste(10, 0, delegate { return false; }) && store.Writes == 0, "focus changed before replacement");
        store = new Store(); transaction = Prepared(store); store.Copy("https://instagram.com/p/B/");
        Check(!transaction.Paste(10, 0, Valid) && store.Value.Contains("instagram.com"), "stale candidate rejected");
        Check(PasteService.JobFresh(new PasteJob { Created = 100 }, 350), "bounded paste replay");
        Check(!PasteService.JobFresh(new PasteJob { Created = 100 }, 351), "expired replay cancelled");
        Check(!PasteService.JobFresh(new PasteJob { Created = 100 }, 99), "invalid clock cancelled");
        var heldControl = PasteService.PasteInputs(true);
        Check(heldControl.Length == 2 && heldControl[0].Value.Keyboard.Key == 0x56 && heldControl[1].Value.Keyboard.Flags == 2, "held Ctrl is not released by replay");
        var releasedControl = PasteService.PasteInputs(false);
        Check(releasedControl.Length == 4 && releasedControl[0].Value.Keyboard.Key == 0x11 && releasedControl[3].Value.Keyboard.Flags == 2, "released Ctrl replay balanced");
        foreach (var input in releasedControl) Check(input.Value.Keyboard.Key != 0x0d && input.Value.Keyboard.Key != 0x5b, "never inject Enter or Windows key");
        Check(System.Runtime.InteropServices.Marshal.SizeOf(typeof(PasteService.Input)) == (IntPtr.Size == 8 ? 40 : 28), "native input ABI");
        Check(Program.UserQuit != 0 && Program.UserQuit != Program.Duplicate && Program.UserQuit != Program.Failed, "distinct exit reasons");
        Check(!Program.WriteState("absent-" + Guid.NewGuid().ToString("N") + "/state.json", "test"), "optional denied diagnostics nonfatal");
        Program.Error("test_phase", new Exception("PRIVATE_SENTINEL"));
        string log = System.IO.File.ReadAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "last-error.txt"));
        Check(!log.Contains("PRIVATE_SENTINEL") && log.Contains("HRESULT=") && log.Contains("test_phase"), "diagnostics omit exception messages");
        return count;
    }
}
