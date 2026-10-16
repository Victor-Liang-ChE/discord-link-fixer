using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

static class SteamTests {
    static int checks;
    static void Check(bool value) { checks++; if (!value) throw new Exception("Steam regression " + checks); }
    static bool Wait(SteamTrailers cache, int revision) { return SpinWait.SpinUntil(delegate { return cache.Revision > revision; }, 2000); }
    public static int Run() {
        checks = 0;
        const string link = "https://store.steampowered.com/app/620/", video = "https://cdn.fastly.steamstatic.com/steam/apps/5787/movie480.mp4";
        Check(SteamTrailers.AppId(link) == "620");
        Check(SteamTrailers.AppId(" \nHTTPS://STORE.STEAMPOWERED.COM/app/620/Portal_2/?x=1#fragment\n") == "620");
        foreach (string text in new[] { "<" + link + ">", "(" + link + ")", "look " + link, link + " " + link,
            "https://store.steampowered.com.evil/app/620/", "https://user@store.steampowered.com/app/620/",
            "https://store.steampowered.com:443/app/620/", "https://example.com/?x=" + link,
            "http://store.steampowered.com/app/620/", "https://store.steampowered.com/app/0/",
            "https://store.steampowered.com/app/620evil/", "https://store.steampowered.com/app/620\\evil",
            "https://store.steampowered.com/bundle/620/", video + " " + link, new string('x', 1800) }) Check(SteamTrailers.AppId(text) == null);
        string prefix = "{\"620\":{\"success\":true,\"data\":{\"movies\":";
        Check(SteamTrailers.Trailer(Encoding.UTF8.GetBytes(prefix + "[{\"id\":5787},{\"id\":999}]}}}"), "620") == video);
        foreach (string movies in new[] { "[]", "[null]", "[{\"id\":true}]", "[{\"id\":\"5787\"}]", "[{\"id\":-1}]", "[{\"id\":1.5}]", "[{\"id\":10000000000}]", "[{\"other\":5787}]" })
            Check(SteamTrailers.Trailer(Encoding.UTF8.GetBytes(prefix + movies + "}}}"), "620") == null);
        Check(SteamTrailers.Trailer(Encoding.UTF8.GetBytes("bad json"), "620") == null);
        Check(SteamTrailers.Trailer(new byte[512 * 1024 + 1], "620") == null);
        using (var entered = new ManualResetEvent(false)) using (var release = new ManualResetEvent(false)) {
            var success = new SteamTrailers(delegate(string id) { entered.Set(); release.WaitOne(2000); return video; }, delegate { return DateTime.UtcNow; });
            Check(success.Converted(link) == link);
            Check(success.Warm(link)); Check(entered.WaitOne(2000));
            Check(!success.Warm(link)); Check(!success.Warm("https://store.steampowered.com/app/400/"));
            Check(success.Converted(link) == link);
            release.Set(); Check(Wait(success, 0));
            Check(success.Converted(link) == video + " " + link);
            Check(success.Converted(video + " " + link) == video + " " + link);
            Check(!success.Warm(link));
        }
        DateTime now = DateTime.UtcNow;
        var failed = new SteamTrailers(delegate { throw new InvalidOperationException("private error"); }, delegate { return now; });
        Check(!failed.Known(link)); Check(failed.Known("not a Steam URL"));
        Check(failed.Warm(link)); Check(Wait(failed, 0)); Check(failed.Converted(link) == link);
        Check(failed.Known(link));
        now = now.AddSeconds(899); Check(!failed.Warm(link));
        now = now.AddSeconds(2); Check(failed.Warm(link)); Check(Wait(failed, 1));
        Check(SteamTrailers.Lookup("620&key=private") == null);
        var cache = new SteamTrailers(delegate { return video; }, delegate { return DateTime.UtcNow; });
        var store = new SteamStore(link);
        var transaction = new ClipboardTransaction(store, cache.Converted);
        transaction.Prepare(); Check(transaction.ReadySequence == 0);
        Check(cache.Warm(link)); Check(Wait(cache, 0));
        transaction.Prepare(); Check(transaction.ReadySequence == 10);
        Check(transaction.Paste(10, 0, delegate { return true; }));
        Check(store.Value == video + " " + link);
        Check(transaction.Restore(1000, false)); Check(store.Value == link);
        transaction.Prepare(); store.Copy("new user copy");
        Check(!transaction.Paste(12, 2000, delegate { return true; }));
        Check(store.Value == "new user copy");
        return checks;
    }
    sealed class SteamStore : IClipboardStore {
        public uint Sequence { get; private set; }
        Dictionary<uint, byte[]> formats;
        public SteamStore(string text) { Sequence = 9; Copy(text); }
        public string Value { get { return Encoding.Unicode.GetString(formats[13]).TrimEnd('\0'); } }
        public void Copy(string text) { formats = new Dictionary<uint, byte[]> { { 13, Encoding.Unicode.GetBytes(text + "\0") } }; Sequence++; }
        public bool Owns(uint sequence) { return Sequence == sequence; }
        public ClipboardSnapshot Read() { return new ClipboardSnapshot { Sequence = Sequence, Formats = formats, Text = Value }; }
        public ClipboardWrite Write(uint expected, Dictionary<uint, byte[]> data, Dictionary<uint, byte[]> rollback, out uint changed) {
            changed = 0; if (Sequence != expected) return ClipboardWrite.Unchanged;
            formats = data; changed = ++Sequence; return ClipboardWrite.Written;
        }
    }
}
