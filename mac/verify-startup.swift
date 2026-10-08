import Foundation
import Darwin

@main
struct VerifyStartup {
    static func main() throws {
        let fm = FileManager.default
        if CommandLine.arguments.contains("--launch-agent") {
            var app = URL(fileURLWithPath: CommandLine.arguments[0]).standardizedFileURL
            for _ in 0..<3 { app.deleteLastPathComponent() }
            let home = app.deletingLastPathComponent().deletingLastPathComponent()
            let tookOver = try Startup.register(app: app, home: home)
            precondition(!tookOver)
            let data = try JSONSerialization.data(withJSONObject: ["pid": getpid(), "managed": true])
            try data.write(to: home.appendingPathComponent("worker.json"), options: .atomic)
            RunLoop.main.run()
            return
        }
        let home = fm.temporaryDirectory.appendingPathComponent("link-fixer-native-" + UUID().uuidString, isDirectory: true)
        let app = home.appendingPathComponent("Applications/Verification.app", isDirectory: true)
        let program = app.appendingPathComponent("Contents/MacOS/DiscordLinkFixer")
        let agents = home.appendingPathComponent("Library/LaunchAgents", isDirectory: true)
        let label = "local.discord-link-fixer.verification." + UUID().uuidString.lowercased()
        let scope = "gui/\(getuid())/\(label)"
        defer {
            _ = try? Startup.launchctl(["bootout", scope])
            try? fm.removeItem(at: home)
        }
        try fm.createDirectory(at: program.deletingLastPathComponent(), withIntermediateDirectories: true)
        try fm.createDirectory(at: agents, withIntermediateDirectories: true)
        try fm.copyItem(at: URL(fileURLWithPath: CommandLine.arguments[0]).standardizedFileURL, to: program)
        let config = try PropertyListSerialization.data(fromPropertyList: Startup.configuration(program: program.path, label: label), format: .xml, options: 0)
        try config.write(to: agents.appendingPathComponent(label + ".plist"), options: .atomic)
        let tookOver = try Startup.register(app: app, home: home, service: nil)
        precondition(tookOver)
        func waitForWorker(previous: Int32 = 0) throws -> Int32 {
            let deadline = Date().addingTimeInterval(30)
            while Date() < deadline {
                if let data = try? Data(contentsOf: home.appendingPathComponent("worker.json")),
                   let value = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                   let pid = value["pid"] as? Int32, pid != previous, value["managed"] as? Bool == true {
                    return pid
                }
                Thread.sleep(forTimeInterval: 0.2)
            }
            throw NSError(domain: "StartupVerification", code: 1, userInfo: [NSLocalizedDescriptionKey: "Native worker did not start"])
        }
        let first = try waitForWorker()
        precondition(kill(first, SIGKILL) == 0)
        let recovered = try waitForWorker(previous: first)
        print("Native first-launch takeover and launchd crash recovery passed: \(first) -> \(recovered).")
    }
}
