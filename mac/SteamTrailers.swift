import Foundation

final class SteamTrailers {
    static let shared = SteamTrailers()
    static let appLink = try! NSRegularExpression(pattern: #"\Ahttps://store\.steampowered\.com/app/([1-9][0-9]{0,9})(?:/[^\s<>"'`\\]*|[?#][^\s<>"'`\\]*)?\z"#, options: .caseInsensitive)
    private let lock = NSLock()
    private var cache: [String: (Date, String?)] = [:]
    private var busy = false
    private var nextRequest = Date.distantPast
    private let fetch: (String) -> String?
    private let clock: () -> Date

    init(fetch: @escaping (String) -> String? = SteamTrailers.lookup, clock: @escaping () -> Date = Date.init) {
        self.fetch = fetch
        self.clock = clock
    }

    static func appID(_ text: String) -> String? {
        guard text.utf8.count < 1800 else { return nil }
        let value = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let match = appLink.firstMatch(in: value, range: NSRange(value.startIndex..., in: value)),
              let range = Range(match.range(at: 1), in: value) else { return nil }
        return String(value[range])
    }

    func converted(_ text: String) -> String {
        guard let id = Self.appID(text) else { return text }
        lock.lock(); defer { lock.unlock() }
        guard let entry = cache[id], entry.0 > clock(), let video = entry.1 else { return text }
        return video + " " + text.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    func known(_ text: String) -> Bool {
        guard let id = Self.appID(text) else { return true }
        lock.lock(); defer { lock.unlock() }
        return cache[id].map { $0.0 > clock() } ?? false
    }

    @discardableResult func warm(_ text: String, completion: @escaping () -> Void = {}) -> Bool {
        guard let id = Self.appID(text) else { return false }
        lock.lock()
        let now = clock()
        if busy || now < nextRequest || (cache[id].map { $0.0 > now } ?? false) { lock.unlock(); return false }
        busy = true
        nextRequest = now.addingTimeInterval(2)
        lock.unlock()
        DispatchQueue.global(qos: .utility).async {
            let result = self.fetch(id)
            self.lock.lock()
            self.cache = self.cache.filter { $0.value.0 > self.clock() }
            if self.cache.count >= 128, let oldest = self.cache.min(by: { $0.value.0 < $1.value.0 }) { self.cache.removeValue(forKey: oldest.key) }
            self.cache[id] = (self.clock().addingTimeInterval(result == nil ? 900 : 3600), result)
            self.busy = false
            self.lock.unlock()
            completion()
        }
        return true
    }

    static func trailer(_ data: Data, appID: String) -> String? {
        guard data.count <= 512 * 1024,
              let payload = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let app = payload[appID] as? [String: Any], app["success"] as? Bool == true,
              let info = app["data"] as? [String: Any], let movies = info["movies"] as? [[String: Any]],
              let number = movies.first?["id"] as? NSNumber,
              CFGetTypeID(number) != CFBooleanGetTypeID(),
              number.doubleValue > 0, number.doubleValue < 10_000_000_000,
              number.doubleValue == number.doubleValue.rounded(.down) else { return nil }
        return "https://cdn.fastly.steamstatic.com/steam/apps/\(number.int64Value)/movie480.mp4"
    }

    static func lookup(_ id: String) -> String? {
        guard appID("https://store.steampowered.com/app/\(id)/") == id else { return nil }
        let deadline = Date().addingTimeInterval(4)
        guard let url = URL(string: "https://store.steampowered.com/api/appdetails?appids=\(id)&cc=us&l=english&filters=movies"),
              let data = SteamRequest.get(url, deadline: deadline), let video = trailer(data, appID: id),
              let prefix = SteamRequest.get(URL(string: video)!, deadline: deadline, video: true),
              prefix.count == 12, String(data: prefix[4..<8], encoding: .ascii) == "ftyp" else { return nil }
        return video
    }
}

// The delegate cancels after the MP4 prefix, even when a server ignores Range.
private final class SteamRequest: NSObject, URLSessionDataDelegate {
    let video: Bool
    var bytes = Data()
    var accepted = false
    let done = DispatchSemaphore(value: 0)
    init(video: Bool) { self.video = video }

    static func get(_ url: URL, deadline: Date, video: Bool = false) -> Data? {
        let remaining = deadline.timeIntervalSinceNow
        guard remaining > 0 else { return nil }
        let delegate = SteamRequest(video: video)
        let config = URLSessionConfiguration.ephemeral
        config.httpCookieStorage = nil
        config.urlCredentialStorage = nil
        config.urlCache = nil
        config.timeoutIntervalForRequest = remaining
        config.timeoutIntervalForResource = remaining
        let session = URLSession(configuration: config, delegate: delegate, delegateQueue: nil)
        defer { session.invalidateAndCancel() }
        var request = URLRequest(url: url)
        if video { request.setValue("bytes=0-11", forHTTPHeaderField: "Range") }
        session.dataTask(with: request).resume()
        guard delegate.done.wait(timeout: .now() + remaining) == .success else { return nil }
        return delegate.accepted ? delegate.bytes : nil
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        guard let http = response as? HTTPURLResponse,
              video ? ([200, 206].contains(http.statusCode) && http.mimeType == "video/mp4") : http.statusCode == 200 else {
            completionHandler(.cancel); return
        }
        accepted = true
        completionHandler(.allow)
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        if video {
            bytes.append(data.prefix(12 - bytes.count))
            if bytes.count == 12 { dataTask.cancel() }
        } else if bytes.count + data.count > 512 * 1024 {
            accepted = false; dataTask.cancel()
        } else { bytes.append(data) }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        if error != nil && !(video && bytes.count == 12) { accepted = false }
        done.signal()
    }
}
