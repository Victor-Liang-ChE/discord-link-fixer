using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

sealed class ClipboardSnapshot {
    public uint Sequence;
    public Dictionary<uint, byte[]> Formats;
    public string Text;
}

enum ClipboardWrite { Unchanged, Written, RestorePending }

interface IClipboardStore {
    uint Sequence { get; }
    bool Owns(uint sequence);
    ClipboardSnapshot Read();
    ClipboardWrite Write(uint expected, Dictionary<uint, byte[]> desired, Dictionary<uint, byte[]> rollback, out uint changed);
}

// The store owns the clipboard lock across each check/write. This class owns the restore lifecycle.
sealed class ClipboardTransaction {
    readonly IClipboardStore store;
    ClipboardSnapshot candidate, original;
    Dictionary<uint, byte[]> replacement;
    uint changed;
    long deadline;
    public bool Pending { get { return original != null; } }
    public uint ReadySequence { get { return original != null ? store.Sequence : candidate == null ? 0 : candidate.Sequence; } }
    public ClipboardTransaction(IClipboardStore store) { this.store = store; }

    public void Prepare() {
        if (original != null && store.Owns(changed)) return;
        original = null; candidate = null; replacement = null;
        ClipboardSnapshot snapshot = store.Read();
        if (snapshot == null) return;
        string converted;
        try { converted = Program.ConvertLink(snapshot.Text); }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { return; }
        if (converted == snapshot.Text) return;
        candidate = snapshot;
        replacement = new Dictionary<uint, byte[]> { { 13, Encoding.Unicode.GetBytes(converted + "\0") } };
    }

    public bool Paste(uint expected, long now, Func<bool> targetValid) {
        if (!targetValid()) return false;
        if (original != null) {
            if (store.Owns(changed)) { deadline = now + 1000; return true; }
            original = null;
        }
        if (candidate == null || candidate.Sequence != expected || store.Sequence != expected) return false;
        uint sequence;
        ClipboardWrite result = store.Write(expected, replacement, candidate.Formats, out sequence);
        if (result == ClipboardWrite.Unchanged) return false;
        original = candidate; candidate = null; replacement = null;
        changed = sequence; deadline = now + 1000;
        return result == ClipboardWrite.Written;
    }

    public void Renew(long now) { if (original != null) deadline = now + 1000; }

    public bool Restore(long now, bool force) {
        if (original == null) return true;
        if (!force && now < deadline) return false;
        if (!store.Owns(changed)) { original = null; return true; }
        uint sequence;
        ClipboardWrite result = store.Write(changed, original.Formats, original.Formats, out sequence);
        if (result == ClipboardWrite.Written) { original = null; return true; }
        if (result == ClipboardWrite.RestorePending) changed = sequence;
        else if (!store.Owns(changed)) { original = null; return true; }
        return false;
    }
}

sealed class NativeClipboard : IClipboardStore {
    public const int MaxFormatBytes = 1048576, MaxTotalBytes = 2097152, MaxFormats = 8;
    readonly IntPtr window;
    readonly uint html, rtf, history, cloud, marker;
    byte[] ownedMarker;
    bool partialWrite;
    [DllImport("user32.dll", SetLastError=true)] static extern bool OpenClipboard(IntPtr window);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError=true)] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError=true)] static extern IntPtr SetClipboardData(uint format, IntPtr value);
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern uint RegisterClipboardFormat(string format);
    [DllImport("kernel32.dll")] static extern UIntPtr GlobalSize(IntPtr value);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr value);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr value);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr size);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr value);
    public uint Sequence { get { return GetClipboardSequenceNumber(); } }

    public NativeClipboard(IntPtr window) {
        this.window = window;
        html = RegisterClipboardFormat("HTML Format"); rtf = RegisterClipboardFormat("Rich Text Format");
        history = RegisterClipboardFormat("CanIncludeInClipboardHistory"); cloud = RegisterClipboardFormat("CanUploadToCloudClipboard");
        marker = RegisterClipboardFormat("DiscordLinkFixer.Transaction.v2");
    }
    bool Allowed(uint format) { return format == 1 || format == 7 || format == 13 || format == 16
        || format != 0 && (format == html || format == rtf || format == history || format == cloud || format == marker); }

    bool OwnsLocked() {
        if (GetClipboardOwner() != window) return false;
        if (partialWrite) return true;
        if (ownedMarker == null) return false;
        IntPtr handle = GetClipboardData(marker);
        if (handle == IntPtr.Zero || GlobalSize(handle).ToUInt64() != 16) return false;
        IntPtr pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero) return false;
        try { for (int i = 0; i < 16; i++) if (Marshal.ReadByte(pointer, i) != ownedMarker[i]) return false; return true; }
        finally { GlobalUnlock(handle); }
    }
    public bool Owns(uint sequence) {
        if (ownedMarker == null && !partialWrite) return false;
        // A reader holding the clipboard must not cause us to discard the saved original.
        if (!OpenClipboard(window)) return GetClipboardOwner() == window;
        try { return OwnsLocked(); }
        finally { CloseClipboard(); }
    }

    public ClipboardSnapshot Read() {
        for (int attempt = 0; attempt < 2; attempt++) {
            ClipboardSnapshot snapshot = ReadLocked();
            if (snapshot == null) return null;
            if (Sequence == snapshot.Sequence) return snapshot;
            // Delayed rendering can finalize the sequence when CloseClipboard runs. Re-read,
            // rather than attributing a concurrent user's copy to our older snapshot.
        }
        return null;
    }
    ClipboardSnapshot ReadLocked() {
        if (!OpenClipboard(window)) return null;
        try {
            List<uint> formats = new List<uint>();
            for (uint format = EnumClipboardFormats(0); format != 0; format = EnumClipboardFormats(format)) {
                if (!Allowed(format) || formats.Count == MaxFormats) return null;
                formats.Add(format);
            }
            if (!formats.Contains(13)) return null;
            Dictionary<uint, byte[]> saved = new Dictionary<uint, byte[]>();
            int total = 0;
            foreach (uint format in formats) {
                IntPtr handle = GetClipboardData(format);
                ulong length = GlobalSize(handle).ToUInt64();
                if (handle == IntPtr.Zero || length == 0 || length > MaxFormatBytes || total + (long)length > MaxTotalBytes) return null;
                IntPtr pointer = GlobalLock(handle);
                if (pointer == IntPtr.Zero) return null;
                try { byte[] bytes = new byte[(int)length]; Marshal.Copy(pointer, bytes, 0, bytes.Length); saved.Add(format, bytes); }
                finally { GlobalUnlock(handle); }
                total += (int)length;
            }
            byte[] unicode = saved[13];
            if ((unicode.Length & 1) != 0) return null;
            int end = 0;
            while (end + 1 < unicode.Length && (unicode[end] != 0 || unicode[end + 1] != 0)) end += 2;
            if (end + 1 >= unicode.Length || end / 2 > 500000) return null;
            return new ClipboardSnapshot { Sequence = Sequence, Formats = saved, Text = Encoding.Unicode.GetString(unicode, 0, end) };
        } finally { CloseClipboard(); }
    }

    Dictionary<uint, IntPtr> Allocate(Dictionary<uint, byte[]> data) {
        Dictionary<uint, IntPtr> result = new Dictionary<uint, IntPtr>();
        int total = 0;
        try {
            if (data.Count == 0 || data.Count > MaxFormats) throw new InvalidOperationException();
            foreach (var item in data) {
                if (!Allowed(item.Key) || item.Value == null || item.Value.Length == 0 || item.Value.Length > MaxFormatBytes
                    || (total += item.Value.Length) > MaxTotalBytes) throw new InvalidOperationException();
                IntPtr handle = GlobalAlloc(0x0002, (UIntPtr)item.Value.Length);
                if (handle == IntPtr.Zero) throw new OutOfMemoryException();
                result.Add(item.Key, handle);
                IntPtr pointer = GlobalLock(handle);
                if (pointer == IntPtr.Zero) throw new InvalidOperationException();
                try { Marshal.Copy(item.Value, 0, pointer, item.Value.Length); }
                finally { GlobalUnlock(handle); }
            }
            return result;
        } catch { Free(result); throw; }
    }
    static void Free(Dictionary<uint, IntPtr> data) {
        if (data == null) return;
        foreach (IntPtr handle in data.Values) if (handle != IntPtr.Zero) GlobalFree(handle);
    }
    static bool Publish(Dictionary<uint, IntPtr> data, List<uint> formats) {
        foreach (uint format in formats) {
            if (SetClipboardData(format, data[format]) == IntPtr.Zero) return false;
            data[format] = IntPtr.Zero; // Ownership passed to Windows, including on partial failure.
        }
        return true;
    }
    public ClipboardWrite Write(uint expected, Dictionary<uint, byte[]> desired, Dictionary<uint, byte[]> rollback, out uint changed) {
        changed = 0;
        Dictionary<uint, IntPtr> next = null, backup = null;
        bool opened = false, modified = false;
        bool restoring = Object.ReferenceEquals(desired, rollback);
        byte[] nonce = null;
        try {
            var publish = new Dictionary<uint, byte[]>(desired);
            // Temporary links should not become new Windows history/cloud clipboard entries.
            if (!restoring && desired.Count == 1 && desired.ContainsKey(13)) {
                if (history != 0) publish[history] = new byte[4];
                if (cloud != 0) publish[cloud] = new byte[4];
                if (marker == 0) return ClipboardWrite.Unchanged;
                nonce = Guid.NewGuid().ToByteArray(); publish[marker] = nonce;
            }
            next = Allocate(publish); backup = Allocate(rollback);
            var nextFormats = new List<uint>(next.Keys); var backupFormats = new List<uint>(backup.Keys);
            if (!(opened = OpenClipboard(window))) return ClipboardWrite.Unchanged;
            if (restoring && (ownedMarker != null || partialWrite)) { if (!OwnsLocked()) return ClipboardWrite.Unchanged; }
            else if (Sequence != expected) return ClipboardWrite.Unchanged;
            if (!EmptyClipboard()) return ClipboardWrite.Unchanged;
            modified = true;
            if (Publish(next, nextFormats)) { ownedMarker = nonce; partialWrite = false; changed = Sequence; return ClipboardWrite.Written; }
            // All backup allocations exist before EmptyClipboard. Never deserialize a custom format.
            if (EmptyClipboard() && Publish(backup, backupFormats)) { ownedMarker = null; partialWrite = false; return ClipboardWrite.Unchanged; }
            partialWrite = true;
            changed = Sequence;
            return ClipboardWrite.RestorePending;
        } catch (Exception error) { Program.Error("clipboard_write", error); if (modified) partialWrite = true; changed = modified ? Sequence : 0; return modified ? ClipboardWrite.RestorePending : ClipboardWrite.Unchanged; }
        finally {
            if (opened) CloseClipboard();
            // Windows finalizes synthesized formats and sequence increments on close.
            // Restoration is guarded by owner + nonce under the next lock, not this racy read.
            if (modified) changed = Sequence;
            Free(next); Free(backup);
        }
    }
}
