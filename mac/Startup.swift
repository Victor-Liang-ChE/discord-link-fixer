import Foundation

enum StartupError: LocalizedError {
    case moveToApplications
    case conflictingJob
    case launchFailed(Int32)

    var errorDescription: String? {
        switch self {
        case .moveToApplications: return "Drag Discord Link Fixer into Applications, then open the installed copy."
        case .conflictingJob: return "Another startup entry or copy already uses this helper's startup name. Quit and remove the old copy before installing here. No unrelated entry was changed."
        case .launchFailed(let code): return "Startup registration failed with launchctl status \(code). The previous startup file was restored."
        }
    }
}

enum Startup {
    static func configuration(program: String, label: String) -> [String: Any] {
        ["Label": label, "ProgramArguments": [program, "--launch-agent"],
         "RunAtLoad": true, "KeepAlive": ["SuccessfulExit": false],
         "ThrottleInterval": 10, "LimitLoadToSessionType": "Aqua", "ProcessType": "Interactive"]
    }

    static func isInstalled(app: URL, home: URL) -> Bool {
        let parent = app.deletingLastPathComponent().standardizedFileURL
        return parent == URL(fileURLWithPath: "/Applications", isDirectory: true)
            || parent == home.appendingPathComponent("Applications", isDirectory: true).standardizedFileURL
    }

    static func launchctl(_ arguments: [String]) throws -> Int32 {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/launchctl")
        process.arguments = arguments
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        try process.run()
        process.waitUntilExit()
        return process.terminationStatus
    }

    // Returns true when launchd has taken over and this manual instance should exit.
    static func register(app: URL, home: URL, service: String? = ProcessInfo.processInfo.environment["XPC_SERVICE_NAME"],
                         launch: ([String]) throws -> Int32 = launchctl) throws -> Bool {
        guard isInstalled(app: app, home: home) else { throw StartupError.moveToApplications }
        let fm = FileManager.default
        let program = app.appendingPathComponent("Contents/MacOS/DiscordLinkFixer").path
        let agents = home.appendingPathComponent("Library/LaunchAgents", isDirectory: true)
        var label = "local.discord-link-fixer"
        var file = agents.appendingPathComponent(label + ".plist")
        let files = (try? fm.contentsOfDirectory(at: agents, includingPropertiesForKeys: [.isSymbolicLinkKey])) ?? []
        var matches = 0
        for candidate in files where candidate.pathExtension == "plist" {
            guard let data = try? Data(contentsOf: candidate),
                  let config = try? PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any],
                  let arguments = config["ProgramArguments"] as? [String], arguments.first == program else { continue }
            guard (try candidate.resourceValues(forKeys: [.isSymbolicLinkKey])).isSymbolicLink != true,
                  let candidateLabel = config["Label"] as? String,
                  candidateLabel.range(of: "^[A-Za-z0-9._-]+$", options: .regularExpression) != nil else {
                throw StartupError.conflictingJob
            }
            matches += 1
            label = candidateLabel
            file = candidate
        }
        guard matches <= 1, matches == 1 || !fm.fileExists(atPath: file.path) else { throw StartupError.conflictingJob }
        if service == label { return false }
        try fm.createDirectory(at: agents, withIntermediateDirectories: true)
        let previous = try? Data(contentsOf: file)
        let data = try PropertyListSerialization.data(fromPropertyList: configuration(program: program, label: label), format: .xml, options: 0)
        let scope = "gui/\(getuid())"
        let loaded = try launch(["print", "\(scope)/\(label)"]) == 0
        if loaded {
            let code = try launch(["bootout", "\(scope)/\(label)"])
            guard code == 0 else { throw StartupError.launchFailed(code) }
        }
        do {
            try data.write(to: file, options: .atomic)
            let code = try launch(["bootstrap", scope, file.path])
            guard code == 0 else { throw StartupError.launchFailed(code) }
        } catch {
            if let previous {
                try previous.write(to: file, options: .atomic)
                if loaded { _ = try? launch(["bootstrap", scope, file.path]) }
            } else if fm.fileExists(atPath: file.path) {
                try fm.removeItem(at: file)
            }
            throw error
        }
        return true
    }

    static func selfTest() throws {
        let fm = FileManager.default
        let home = fm.temporaryDirectory.appendingPathComponent("link-fixer-startup-" + UUID().uuidString, isDirectory: true)
        try fm.createDirectory(at: home, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: home) }
        let app = home.appendingPathComponent("Applications/Discord Link Fixer.app", isDirectory: true)
        var calls = [[String]]()
        let launch: ([String]) -> Int32 = { arguments in calls.append(arguments); return arguments.first == "print" ? 1 : 0 }
        precondition(isInstalled(app: app, home: home))
        precondition(isInstalled(app: URL(fileURLWithPath: "/Applications/Discord Link Fixer.app"), home: home))
        precondition(!isInstalled(app: home.appendingPathComponent("Disk Image/Discord Link Fixer.app"), home: home))
        let tookOver = try register(app: app, home: home, service: nil, launch: launch)
        precondition(tookOver && calls.count == 2 && calls.last?.first == "bootstrap")
        let file = home.appendingPathComponent("Library/LaunchAgents/local.discord-link-fixer.plist")
        let data = try Data(contentsOf: file)
        let config = try PropertyListSerialization.propertyList(from: data, format: nil) as! [String: Any]
        precondition(config["ProgramArguments"] as? [String] == [app.appendingPathComponent("Contents/MacOS/DiscordLinkFixer").path, "--launch-agent"])
        precondition((config["KeepAlive"] as? [String: Bool])?["SuccessfulExit"] == false)
        let managed = try register(app: app, home: home, service: "local.discord-link-fixer", launch: { _ in preconditionFailure("Managed launch must not register again") })
        let managedData = try Data(contentsOf: file)
        precondition(!managed && managedData == data)
        do {
            _ = try register(app: app, home: home, service: nil, launch: { _ in 1 })
            preconditionFailure("Failed bootstrap must fail setup")
        } catch StartupError.launchFailed {
            let restored = try Data(contentsOf: file)
            precondition(restored == data)
        }
        let foreign = try PropertyListSerialization.data(fromPropertyList: configuration(program: "/other/helper", label: "local.discord-link-fixer"), format: .xml, options: 0)
        try foreign.write(to: file, options: .atomic)
        do {
            _ = try register(app: app, home: home, service: nil, launch: { _ in preconditionFailure("Do not touch a foreign job") })
            preconditionFailure("Foreign job must be preserved")
        } catch StartupError.conflictingJob {
            let preserved = try Data(contentsOf: file)
            precondition(preserved == foreign)
        }
        print("Passed first-launch startup, managed launch, rollback, location and ownership checks.")
    }
}
