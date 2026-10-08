import AppKit
import ApplicationServices
import UserNotifications

func healthWarning(enabled: Bool, trusted: Bool, hookActive: Bool) -> String? {
    if !enabled { return nil }
    if !trusted { return "Accessibility permission is missing. Link conversion is inactive." }
    if !hookActive { return "Keyboard access is unavailable. Link conversion is inactive." }
    return nil
}

func discordVersionChanged(previous: String?, current: String) -> Bool {
    previous != nil && !current.isEmpty && previous != current
}

let linkRules: [(NSRegularExpression, String)] = [
    (#"(?i)(?<![A-Za-z0-9_./@-])(https?://)(?:www\.|mobile\.)?(?:x\.com|twitter\.com)(?=/)"#, "$1fxtwitter.com"),
    (#"(?i)(?<![A-Za-z0-9_./@-])(https?://)(?:www\.)?instagram\.com(?=/)"#, "$1kkinstagram.com"),
    (#"(?i)(?<![A-Za-z0-9_./@-])(https?://)(?:www\.)?tiktok\.com(?=/)"#, "$1tnktok.com"),
    (#"(?i)(?<![A-Za-z0-9_./@-])(https?://)(?:www\.)?reddit\.com(?=/)"#, "$1vxreddit.com"),
].map { (try! NSRegularExpression(pattern: $0.0), $0.1) }

func convert(_ text: String) -> String {
    linkRules.reduce(text) { result, rule in
        rule.0.stringByReplacingMatches(in: result, range: NSRange(result.startIndex..., in: result), withTemplate: rule.1)
    }
}

func shouldConvert(bundle: String?, key: Int64, flags: CGEventFlags) -> Bool {
    bundle == "com.hnc.Discord" && key == 9 && flags.contains(.maskCommand)
        && !flags.contains(.maskAlternate) && !flags.contains(.maskControl)
}

if CommandLine.arguments.contains("--self-test") {
    try Startup.selfTest()
    let sample = "https://x.com/eschatolocation/status/2107617817570709682?s=46"
    let cases: [(String, String)] = [
        (sample, sample.replacingOccurrences(of: "x.com", with: "fxtwitter.com")),
        ("Look: <https://twitter.com/user/status/123#fragment>", "Look: <https://fxtwitter.com/user/status/123#fragment>"),
        ("https://www.x.com/user/status/123/photo/1", "https://fxtwitter.com/user/status/123/photo/1"),
        ("http://mobile.twitter.com/user/status/123", "http://fxtwitter.com/user/status/123"),
        ("https://x.com/i/web/status/123?s=46", "https://fxtwitter.com/i/web/status/123?s=46"),
        (sample + "\n" + sample, convert(sample) + "\n" + convert(sample)),
        ("https://x.com/user", "https://fxtwitter.com/user"),
        ("https://x.com.evil.example/user/status/123", "https://x.com.evil.example/user/status/123"),
        ("https://evil.example/https://x.com/user/status/123", "https://evil.example/https://x.com/user/status/123"),
        ("https://x.com/user/status/123abc", "https://fxtwitter.com/user/status/123abc"),
        ("https://fxtwitter.com/user/status/123", "https://fxtwitter.com/user/status/123"),
        ("ordinary text 🦊", "ordinary text 🦊"),
    ]
    for (input, expected) in cases { precondition(convert(input) == expected, "Conversion mismatch: \(input)") }
    let fixtureURL = ProcessInfo.processInfo.environment["DISCORD_LINK_FIXER_TESTS"].map { URL(fileURLWithPath: $0) }
        ?? Bundle.main.url(forResource: "link-tests", withExtension: "json")
        ?? URL(fileURLWithPath: NSHomeDirectory() + "/Library/Application Support/Discord Link Fixer/link-tests.json")
    let extraCases = try! JSONSerialization.jsonObject(with: Data(contentsOf: fixtureURL)) as! [[String]]
    for test in extraCases {
        precondition(convert(test[0]) == test[1], "Additional URL conversion mismatch")
        precondition(convert(test[1]) == test[1], "Conversion is not idempotent")
    }
    precondition(shouldConvert(bundle: "com.hnc.Discord", key: 9, flags: .maskCommand))
    precondition(shouldConvert(bundle: "com.hnc.Discord", key: 9, flags: [.maskCommand, .maskShift]))
    precondition(!shouldConvert(bundle: "com.apple.Safari", key: 9, flags: .maskCommand))
    precondition(!shouldConvert(bundle: "com.hnc.Discord", key: 9, flags: [.maskCommand, .maskAlternate]))
    precondition(!shouldConvert(bundle: "com.hnc.Discord", key: 9, flags: [.maskCommand, .maskControl]))
    precondition(!shouldConvert(bundle: "com.hnc.Discord", key: 9, flags: []))
    precondition(!shouldConvert(bundle: "com.hnc.Discord", key: 0, flags: .maskCommand))
    precondition(healthWarning(enabled: false, trusted: false, hookActive: false) == nil)
    precondition(healthWarning(enabled: true, trusted: true, hookActive: true) == nil)
    precondition(healthWarning(enabled: true, trusted: false, hookActive: true) != nil)
    precondition(healthWarning(enabled: true, trusted: true, hookActive: false) != nil)
    precondition(!discordVersionChanged(previous: nil, current: "1"))
    precondition(!discordVersionChanged(previous: "1", current: "1"))
    precondition(discordVersionChanged(previous: "1", current: "2"))
    // A private pasteboard tests byte-preserving restoration without touching the user's clipboard.
    let board = NSPasteboard.withUniqueName()
    defer { board.releaseGlobally() }
    let original = NSPasteboardItem()
    original.setString(sample, forType: .string)
    original.setData(Data([0, 1, 255]), forType: NSPasteboard.PasteboardType("test.original"))
    board.writeObjects([original])
    let snapshot = clipboardSnapshot(board)
    board.clearContents()
    board.setString(convert(sample), forType: .string)
    restoreClipboard(snapshot, board: board, expectedCount: board.changeCount)
    precondition(board.string(forType: .string) == sample)
    precondition(board.data(forType: NSPasteboard.PasteboardType("test.original")) == Data([0, 1, 255]))
    let staleCount = board.changeCount
    board.clearContents()
    board.setString("newly copied text", forType: .string)
    restoreClipboard(snapshot, board: board, expectedCount: staleCount)
    precondition(board.string(forType: .string) == "newly copied text")
    print("Passed \(cases.count + extraCases.count) conversion cases, 7 shortcut/scope cases, 7 health/update cases, clipboard restoration and copy-race checks.")
    exit(0)
}

func clipboardSnapshot(_ board: NSPasteboard) -> [NSPasteboardItem] {
    (board.pasteboardItems ?? []).map { item in
        let copy = NSPasteboardItem()
        for type in item.types { if let data = item.data(forType: type) { copy.setData(data, forType: type) } }
        return copy
    }
}

func restoreClipboard(_ items: [NSPasteboardItem], board: NSPasteboard, expectedCount: Int) {
    guard board.changeCount == expectedCount else { return }
    board.clearContents()
    board.writeObjects(items)
}

final class Helper: NSObject, NSApplicationDelegate {
    var status: NSStatusItem!
    var tap: CFMachPort?
    var timer: Timer?
    var enabled = true
    var pending: ([NSPasteboardItem], Int)?
    var lastStatus = Data()
    var notificationsAllowed = false
    var lastWarning: String?
    var healthTicks = 0
    let stateItem = NSMenuItem(title: "Waiting for Accessibility approval", action: nil, keyEquivalent: "")

    func applicationDidFinishLaunching(_ notification: Notification) {
        status = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        status.button?.title = "Link"
        status.button?.toolTip = "Discord Link Fixer"
        let menu = NSMenu()
        menu.addItem(stateItem)
        menu.addItem(.separator())
        for (title, action) in [("Pause / Resume", #selector(toggle)), ("Enable Accessibility…", #selector(permissions)), ("Enable notifications…", #selector(notificationPermission)), ("Test notification", #selector(testNotification)), ("Quit Discord Link Fixer", #selector(quit))] {
            let item = NSMenuItem(title: title, action: action, keyEquivalent: "")
            item.target = self
            menu.addItem(item)
        }
        status.menu = menu
        installTap()
        // Permission is requested only from the user's menu action, never on a
        // startup or recovery loop. A denied permission must not reopen Settings.
        writeStatus()
        if !UserDefaults.standard.bool(forKey: "notificationPermissionRequested") {
            notificationPermission()
        } else {
            UNUserNotificationCenter.current().getNotificationSettings { [weak self] settings in
                DispatchQueue.main.async {
                    self?.notificationsAllowed = settings.authorizationStatus == .authorized
                    self?.lastWarning = nil
                    self?.writeStatus()
                }
            }
        }
        timer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
            guard let self else { return }
            if let tap = self.tap, !CGEvent.tapIsEnabled(tap: tap) { CGEvent.tapEnable(tap: tap, enable: true) }
            if self.tap == nil { self.installTap() }
            self.writeStatus()
            self.healthTicks += 1
            if self.healthTicks % 30 == 0 { self.checkDiscordVersion() }
        }
    }

    func writeStatus() {
        let trusted = AXIsProcessTrusted()
        let active = enabled && trusted && tap.map { CGEvent.tapIsEnabled(tap: $0) } == true
        let warning = healthWarning(enabled: enabled, trusted: trusted, hookActive: tap.map { CGEvent.tapIsEnabled(tap: $0) } == true)
        status.button?.title = warning == nil ? (enabled ? "Link" : "LinkⅡ") : "Link!"
        stateItem.title = warning ?? (enabled ? "Active • ⌘V in Discord • ⌥⌘V bypasses" : "Paused")
        if warning != lastWarning {
            lastWarning = warning
            if let warning { notify("health", warning) }
        }
        let data = try! JSONSerialization.data(withJSONObject: ["pid": ProcessInfo.processInfo.processIdentifier,
            "accessibilityApproved": trusted, "active": active, "enabled": enabled,
            "notificationsAllowed": notificationsAllowed], options: [.sortedKeys])
        guard data != lastStatus else { return }
        lastStatus = data
        let root = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("Discord Link Fixer")
        try? FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        try? data.write(to: root.appendingPathComponent("mac-running.json"), options: .atomic)
    }

    func notify(_ key: String, _ message: String) {
        guard notificationsAllowed else { return }
        let content = UNMutableNotificationContent()
        content.title = "Discord Link Fixer"
        content.body = message
        UNUserNotificationCenter.current().add(UNNotificationRequest(identifier: key, content: content, trigger: nil))
    }

    @objc func notificationPermission() {
        UserDefaults.standard.set(true, forKey: "notificationPermissionRequested")
        UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound]) { [weak self] granted, _ in
            DispatchQueue.main.async {
                self?.notificationsAllowed = granted
                self?.lastWarning = nil
                self?.writeStatus()
            }
        }
    }

    @objc func testNotification() {
        if !notificationsAllowed {
            stateItem.title = "Notifications are disabled. Allow them in System Settings."
            return
        }
        notify("test", "Notification test. This does not test Discord paste compatibility.")
    }

    func checkDiscordVersion() {
        guard let app = NSWorkspace.shared.frontmostApplication,
              app.bundleIdentifier == "com.hnc.Discord", let url = app.bundleURL,
              let version = Bundle(url: url)?.object(forInfoDictionaryKey: "CFBundleVersion") as? String else { return }
        let previous = UserDefaults.standard.string(forKey: "discordVersion")
        UserDefaults.standard.set(version, forKey: "discordVersion")
        if discordVersionChanged(previous: previous, current: version) {
            notify("discord-update", "Discord updated. Paste compatibility has not been verified. Try a supported link in an unsent draft; do not press Send.")
        }
    }

    func installTap() {
        guard AXIsProcessTrusted() else { return }
        let callback: CGEventTapCallBack = { _, type, event, context in
            let helper = Unmanaged<Helper>.fromOpaque(context!).takeUnretainedValue()
            if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
                if let tap = helper.tap { CGEvent.tapEnable(tap: tap, enable: true) }
            } else if type == .keyDown && helper.enabled && shouldConvert(
                bundle: NSWorkspace.shared.frontmostApplication?.bundleIdentifier,
                key: event.getIntegerValueField(.keyboardEventKeycode), flags: event.flags
            ) { helper.preparePaste() }
            return Unmanaged.passUnretained(event)
        }
        guard let created = CGEvent.tapCreate(tap: .cgSessionEventTap, place: .headInsertEventTap,
            options: .defaultTap, eventsOfInterest: 1 << CGEventType.keyDown.rawValue,
            callback: callback, userInfo: Unmanaged.passUnretained(self).toOpaque()) else {
            stateItem.title = "Keyboard access unavailable; reopen after approval"
            return
        }
        tap = created
        let source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, created, 0)
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        CGEvent.tapEnable(tap: created, enable: true)
        stateItem.title = enabled ? "Active • ⌘V in Discord • ⌥⌘V bypasses" : "Paused"
    }

    func preparePaste() {
        let board = NSPasteboard.general
        guard let items = board.pasteboardItems, items.count == 1,
            !items[0].types.contains(.fileURL), !items[0].types.contains(.png), !items[0].types.contains(.tiff),
            let text = board.string(forType: .string), text.utf8.count < 1_000_000 else { return }
        let converted = convert(text)
        guard converted != text else { return }
        let snapshot = clipboardSnapshot(board)
        board.clearContents()
        guard board.setString(converted, forType: .string) else {
            restoreClipboard(snapshot, board: board, expectedCount: board.changeCount)
            return
        }
        let count = board.changeCount
        pending = (snapshot, count)
        DispatchQueue.main.asyncAfter(deadline: .now() + 1) { [weak self] in
            restoreClipboard(snapshot, board: board, expectedCount: count)
            if self?.pending?.1 == count { self?.pending = nil }
        }
    }

    @objc func toggle() {
        enabled.toggle()
        status.button?.title = enabled ? "Link" : "LinkⅡ"
        stateItem.title = enabled ? (tap == nil ? "Waiting for Accessibility approval" : "Active • ⌘V in Discord • ⌥⌘V bypasses") : "Paused"
        writeStatus()
    }

    @objc func permissions() {
        let key = kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String
        _ = AXIsProcessTrustedWithOptions([key: true] as CFDictionary)
        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")!)
    }

    @objc func quit() { NSApplication.shared.terminate(nil) }

    func applicationWillTerminate(_ notification: Notification) {
        if let (snapshot, count) = pending { restoreClipboard(snapshot, board: .general, expectedCount: count) }
        if let tap { CFMachPortInvalidate(tap) }
        timer?.invalidate()
    }
}

let app = NSApplication.shared
if !CommandLine.arguments.contains("--launch-agent"), NSRunningApplication.runningApplications(withBundleIdentifier: Bundle.main.bundleIdentifier ?? "local.discord-link-fixer")
    .contains(where: { $0.processIdentifier != ProcessInfo.processInfo.processIdentifier }) { exit(0) }
app.setActivationPolicy(.accessory)
do {
    if try Startup.register(app: Bundle.main.bundleURL, home: URL(fileURLWithPath: NSHomeDirectory(), isDirectory: true)) { exit(0) }
} catch {
    let alert = NSAlert()
    alert.messageText = "Discord Link Fixer setup"
    alert.informativeText = error.localizedDescription
    alert.addButton(withTitle: "Quit")
    alert.runModal()
    exit(1)
}
let helper = Helper()
app.delegate = helper
app.run()
