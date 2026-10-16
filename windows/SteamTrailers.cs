using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

sealed class SteamTrailers {
    public static Action<string> Diagnostic;
    public static readonly SteamTrailers Shared = new SteamTrailers(Lookup, delegate { return DateTime.UtcNow; });
    static readonly Regex AppLink = new Regex(@"\Ahttps://store\.steampowered\.com/app/([1-9][0-9]{0,9})(?:/[^\s<>""'`\\]*|[?#][^\s<>""'`\\]*)?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    readonly object gate = new object();
    readonly Dictionary<string, Entry> cache = new Dictionary<string, Entry>();
    readonly Func<string, string> fetch;
    readonly Func<DateTime> clock;
    bool busy;
    DateTime nextRequest;
    int revision;
    sealed class Entry { public DateTime Expires; public string Video; }
    public SteamTrailers(Func<string, string> fetch, Func<DateTime> clock) { this.fetch = fetch; this.clock = clock; }
    public int Revision { get { lock (gate) return revision; } }
    public bool Known(string text) {
        string id = AppId(text); if (id == null) return true;
        lock (gate) { Entry entry; return cache.TryGetValue(id, out entry) && entry.Expires > clock(); }
    }
    public static string AppId(string text) {
        if (text == null || text.Length >= 1800) return null;
        Match match = AppLink.Match(text.Trim());
        return match.Success ? match.Groups[1].Value : null;
    }
    public string Converted(string text) {
        string id = AppId(text); if (id == null) return text;
        lock (gate) {
            Entry entry;
            return cache.TryGetValue(id, out entry) && entry.Expires > clock() && entry.Video != null
                ? entry.Video + " " + text.Trim() : text;
        }
    }
    public bool Warm(string text) {
        string id = AppId(text); if (id == null) return false;
        lock (gate) {
            Entry entry; DateTime now = clock();
            if (busy || now < nextRequest || cache.TryGetValue(id, out entry) && entry.Expires > now) return false;
            busy = true; nextRequest = now.AddSeconds(2);
        }
        ThreadPool.QueueUserWorkItem(delegate {
            string video = null;
            try { video = fetch(id); } catch { /* Lookup failures retain the original Steam link. */ }
            lock (gate) {
                var expired = new List<string>(); string oldest = null; DateTime first = DateTime.MaxValue;
                foreach (var item in cache) {
                    if (item.Value.Expires <= clock()) expired.Add(item.Key);
                    if (item.Value.Expires < first) { first = item.Value.Expires; oldest = item.Key; }
                }
                foreach (string key in expired) cache.Remove(key);
                if (cache.Count >= 128 && oldest != null) cache.Remove(oldest);
                cache[id] = new Entry { Expires = clock().AddSeconds(video == null ? 900 : 3600), Video = video };
                busy = false; revision++;
            }
        });
        return true;
    }
    public static string Trailer(byte[] data, string id) {
        if (data.Length > 512 * 1024) return null;
        try {
            var serializer = new JavaScriptSerializer { MaxJsonLength = 512 * 1024, RecursionLimit = 32 };
            var payload = serializer.DeserializeObject(Encoding.UTF8.GetString(data)) as Dictionary<string, object>;
            object value;
            if (payload == null || !payload.TryGetValue(id, out value)) return null;
            var app = value as Dictionary<string, object>;
            if (app == null || !app.TryGetValue("success", out value) || !(value is bool) || !(bool)value
                || !app.TryGetValue("data", out value)) return null;
            var info = value as Dictionary<string, object>;
            if (info == null || !info.TryGetValue("movies", out value)) return null;
            var movies = value as object[];
            if (movies == null || movies.Length == 0) return null;
            var first = movies[0] as Dictionary<string, object>;
            if (first == null || !first.TryGetValue("id", out value) || !(value is int || value is long)) return null;
            long number = Convert.ToInt64(value);
            return number > 0 && number < 10000000000L ? "https://cdn.fastly.steamstatic.com/steam/apps/" + number + "/movie480.mp4" : null;
        } catch { return null; }
    }
    public static string Lookup(string id) {
        if (AppId("https://store.steampowered.com/app/" + id + "/") != id) return null;
        // This helper targets Framework 4.x, whose inherited TLS default can be obsolete.
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        DateTime deadline = DateTime.UtcNow.AddSeconds(4);
        byte[] data = Get("https://store.steampowered.com/api/appdetails?appids=" + id + "&cc=us&l=english&filters=movies", deadline, false);
        string video = data == null ? null : Trailer(data, id);
        if (video == null) return null;
        byte[] prefix = Get(video, deadline, true);
        return prefix != null && prefix.Length == 12 && Encoding.ASCII.GetString(prefix, 4, 4) == "ftyp" ? video : null;
    }
    static byte[] Get(string url, DateTime deadline, bool video) {
        int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
        if (remaining <= 0) return null;
        var request = (HttpWebRequest)WebRequest.Create(url);
        request.AllowAutoRedirect = false; request.Proxy = null; request.Credentials = null; request.KeepAlive = false;
        request.Timeout = remaining; request.ReadWriteTimeout = remaining;
        if (video) request.AddRange(0, 11);
        using (var abort = new Timer(delegate { request.Abort(); }, null, remaining, Timeout.Infinite)) {
            try {
                using (var response = (HttpWebResponse)request.GetResponse()) {
                    int status = (int)response.StatusCode;
                    if (video ? (status != 200 && status != 206 || response.ContentType.Split(';')[0] != "video/mp4") : status != 200) return null;
                    using (Stream stream = response.GetResponseStream()) using (var bytes = new MemoryStream()) {
                        byte[] chunk = new byte[video ? 12 : 8192]; int limit = video ? 12 : 512 * 1024;
                        while (bytes.Length <= limit) {
                            int count = stream.Read(chunk, 0, Math.Min(chunk.Length, limit + 1 - (int)bytes.Length));
                            if (count == 0) return bytes.ToArray();
                            bytes.Write(chunk, 0, count);
                            if (video && bytes.Length == 12) return bytes.ToArray();
                        }
                    }
                }
            } catch (WebException error) {
                if (Diagnostic != null) Diagnostic("network:" + error.Status + ":" + (error.InnerException == null ? "none" : error.InnerException.GetType().Name));
                return null;
            } catch { return null; }
            finally { request.Abort(); }
        }
        return null;
    }
}
