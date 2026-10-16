import Foundation

func steamTests() throws {
    let link = "https://store.steampowered.com/app/620/"
    let video = "https://cdn.fastly.steamstatic.com/steam/apps/5787/movie480.mp4"
    var checks = 0
    func check(_ value: Bool) { checks += 1; precondition(value, "Steam regression \(checks)") }
    check(SteamTrailers.appID(link) == "620")
    check(SteamTrailers.appID(" \nHTTPS://STORE.STEAMPOWERED.COM/app/620/Portal_2/?x=1#fragment\n") == "620")
    for text in ["<\(link)>", "(\(link))", "look \(link)", link + " " + link,
                 "https://store.steampowered.com.evil/app/620/", "https://user@store.steampowered.com/app/620/",
                 "https://store.steampowered.com:443/app/620/", "https://example.com/?x=\(link)",
                 "http://store.steampowered.com/app/620/", "https://store.steampowered.com/app/0/",
                 "https://store.steampowered.com/app/620evil/", "https://store.steampowered.com/app/620\\evil",
                 "https://store.steampowered.com/bundle/620/", video + " " + link, String(repeating: "x", count: 1800)] {
        check(SteamTrailers.appID(text) == nil)
    }
    func payload(_ movies: Any, success: Bool = true) throws -> Data {
        try JSONSerialization.data(withJSONObject: ["620": ["success": success, "data": ["movies": movies]]])
    }
    check(SteamTrailers.trailer(try payload([["id": 5787], ["id": 999]]), appID: "620") == video)
    for movies: Any in [[], [NSNull()], [["id": true]], [["id": "5787"]], [["id": -1]], [["id": 1.5]], [["id": 10000000000]], [["other": 5787]]] {
        check(SteamTrailers.trailer(try payload(movies), appID: "620") == nil)
    }
    check(SteamTrailers.trailer(try payload([["id": 5787]], success: false), appID: "620") == nil)
    check(SteamTrailers.trailer(Data("bad json".utf8), appID: "620") == nil)
    check(SteamTrailers.trailer(Data(repeating: 32, count: 512 * 1024 + 1), appID: "620") == nil)
    check(SteamTrailers.lookup("620&key=private") == nil)
    let semaphore = DispatchSemaphore(value: 0)
    let entered = DispatchSemaphore(value: 0)
    let release = DispatchSemaphore(value: 0)
    let success = SteamTrailers(fetch: { id in entered.signal(); _ = release.wait(timeout: .now() + 2); return video })
    check(success.converted(link) == link)
    check(!success.known(link))
    check(success.known("not a Steam URL"))
    check(success.warm(link, completion: { semaphore.signal() }))
    check(entered.wait(timeout: .now() + 2) == .success)
    check(!success.warm(link))
    check(!success.warm("https://store.steampowered.com/app/400/"))
    check(success.converted(link) == link)
    release.signal()
    check(semaphore.wait(timeout: .now() + 2) == .success)
    check(success.converted(link) == video + " " + link)
    check(success.known(link))
    check(success.converted(video + " " + link) == video + " " + link)
    check(!success.warm(link))
    var now = Date()
    let failed = SteamTrailers(fetch: { _ in nil }, clock: { now })
    check(failed.warm(link, completion: { semaphore.signal() }))
    check(semaphore.wait(timeout: .now() + 2) == .success)
    check(failed.converted(link) == link)
    check(failed.known(link))
    now = now.addingTimeInterval(899)
    check(!failed.warm(link))
    now = now.addingTimeInterval(2)
    check(failed.warm(link, completion: { semaphore.signal() }))
    check(semaphore.wait(timeout: .now() + 2) == .success)
    print("Passed \(checks) Steam selection, privacy, cache, failure and nonblocking checks.")
}
