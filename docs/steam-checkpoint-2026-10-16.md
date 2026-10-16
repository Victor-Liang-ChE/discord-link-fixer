# Steam trailer paste update

Owner selected first-trailer URL pasting rather than a bot-backed card or a
hosted preview service. Scope is macOS and Windows only. No Oracle, VanchiBot,
Discord-account token, video storage, paid service or iPhone changes.

The helper prepares only standalone HTTPS Steam app links while Discord is
foreground. The keyboard handler never waits for the network. When ready,
Paste inserts the first compatible trailer URL plus the original store URL.
An early paste or failed lookup keeps the original. No later-trailer
substitution, message sending, user attribution spoofing or arrow UI.

## Verified

| Gate | Result |
| --- | --- |
| macOS universal build | ARM64 and x86_64 compiled; ARM64 executed |
| Mac Steam checks | 50 passed |
| Mac existing URL / scope / health checks | 30 / 7 / 7 passed |
| Mac clipboard restoration and copy-race checks | Passed on private pasteboard |
| Mac native startup / rollback / crash recovery | Passed, owned test processes exited |
| Mac stable signing / startup installer check | Passed; nothing installed |
| Windows URL / scope / update checks | 30 / 6 / 3 passed |
| Windows pure regressions, including Steam | 120 passed |
| Windows private native clipboard checks | 31 passed; user clipboard untouched |
| Windows installer tests, normal / optimized Python | 16 / 16 passed |
| Windows bootstrap / missing-GUI / native task gates | Passed; temporary task removed |
| Windows isolated crash recovery and intentional Quit | Passed; test processes exited |
| Live Steam lookup on Mac and Windows | Portal 2 first trailer found; Enshrouded correctly fell back |

Live Portal 2 URL:
`https://cdn.fastly.steamstatic.com/steam/apps/5787/movie480.mp4`.
The same Steam URL played through Discord during VanchiBot validation earlier
today. That does not prove a new desktop-helper paste was consumed by Discord.
Unsent-draft checks of the updated installed helpers remain pending activation.

Windows initially failed HTTPS negotiation because this Framework-targeted
executable inherited an obsolete TLS default. Steam lookup now uses TLS 1.2,
with normal certificate validation and no insecure protocol fallback. Its
diagnostic command records only error status/type, not clipboard contents or
raw exception messages. Normal lookup failures are silent.

| New source | SHA-256 |
| --- | --- |
| mac/SteamTrailers.swift | 831fa55e76a8593fc73e9a6466bb5cf3cac141fd5643d2a772d27b7215e70f7a |
| mac/SteamTests.swift | a3f54703225239551e9110cf04ecbe7e57232fbf34a45cbf93145da3b5f60820 |
| windows/SteamTrailers.cs | ec22a271de89c180d006bfc645cb73277b7d61a2041c61634a8275e20cde8696 |
| windows/SteamTests.cs | 8970e51550242bbe46bbcdb4b69bc8bd6559f97679fb10af1d59978c04895e2f |

## Activation pending owner approval

No installed helper was stopped or replaced. The owner has been asked whether
to quit/relaunch only these two helpers after testing. Do not treat selection
of the feature option as an answer to that later restart question.

Mac checkout: `/Users/victorliang/discord-link-fixer`.
Installed app: `/Users/victorliang/Applications/Discord Link Fixer.app`.
Startup label: `local.victor.discord-link-fixer`.
Retain existing bundle ID `local.victor.discord-fx-paste` and signing identity
`Discord FX Paste Local Signing` to preserve Accessibility approval.
`DISCORD_LINK_FIXER_SIGNING_IDENTITY='Discord FX Paste Local Signing' bash mac/install.sh --check`
passed with the existing designated requirement. The regular installer backs
up the app, preserves the owned startup label and can roll back failures.

Windows is reachable over SSH as `victo@100.69.119.20`.
Installed root: `C:/Users/victo/AppData/Local/Discord Link Fixer`.
Verified staged sources: `C:/Users/victo/AppData/Local/Temp/discord-steam-validation-p1vfnzhy`.
The staged `candidate/DiscordLinkFixer.exe` predates the final test-only
additions, but the final full verify.py run compiled and tested all final
sources. Rebuild from the verified source hashes before installation.
Existing interactive helper PIDs at read-only inventory: 15296 and 30392.
Revalidate executable paths and task ownership before any activation. Use a
graceful Quit in the interactive session, not force termination. Preserve
source/executable backups. Run the existing transactional installer with
`--rebuild --update-owned` from the installed root only after the helper exits.

Repository had an unrelated deleted `iphone/Discord Link Fixer.shortcut` at
entry. Leave that deletion untouched and unstaged. No tests touched the user's
clipboard, sent messages, switched the Windows desktop or stopped unrelated
processes. No agent-owned persistent process remains running.
