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
At pre-activation review, unsent-draft checks of the installed helpers were
still pending. Activation results below distinguish actual draft checks from
private-clipboard and source-availability checks.

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

## Pre-activation inventory

At this inventory, no installed helper had been stopped or replaced. The owner
subsequently explicitly approved updating and relaunching only these helpers.

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

## Activated after explicit approval

Both installations now run the source update from commit
`9cf8a55276eeb3d43316f1c4ea909142fdf86ff2`. No runtime-source changes were
needed during activation. Only the two approved helpers were quit/relaunched.
Discord, VanchiBot, Oracle, other servers and unrelated processes were not
stopped or restarted.

| Installed verification | Mac | Windows |
| --- | --- | --- |
| Current helper worker PID | 95701 | 13640 |
| Previous helper PIDs | 75561 | 15296 and 30392 |
| Active permission/hook | Accessibility approved, active | Session 1, hook installed, ready |
| Installed self-tests | Steam 50; URLs 30; scope 7; health 7 | Regressions 120; URLs 30; scope 6; updates 3 |
| Startup recovery retained | Owned launchd job | Owned login task and supervisor |

Mac gracefully exited using NSRunningApplication.terminate after exact bundle
path validation. The source installer preserved the bundle ID, signer and
owned startup label. The new designated requirement is identical to the old
one, and strict all-architecture signature verification passed. Installed
executable matches the tested built executable, SHA-256
`db1a52ecaed405749576c9f720247f4697d87c6f56c99a915bc2f580716adf00`.

Mac backups:
`/Users/victorliang/Library/Application Support/Discord Link Fixer/backup-steam-20261016T142341Z.wSoNbDp6`
and the installer's previous app/plist under
`/Users/victorliang/Library/Application Support/Discord Link Fixer/install.WGgZOm7M`.

Windows activation revalidated exact baseline and candidate hashes, owned
scheduled tasks and shortcut, then built and self-tested a final candidate.
A new temporary InteractiveToken task invoked only this installation's
`--quit`; its identity was rechecked and the temporary task removed. Both old
helper processes exited without force termination. Source/executable backups
were verified before replacement. The existing transactional installer
activated the exact tested candidate, and all installed source hashes matched.
Installed executable SHA-256:
`740a1059b62cd1c7332f793241d9aea001edd45aab0146821f31cf02d92463fb`.

Windows backups:
`C:/Users/victo/AppData/Local/Discord Link Fixer/backup-steam-20261016T142340Z`
and `C:/Users/victo/AppData/Local/Discord Link Fixer/install-backup-6y4y6xtf`.
Full machine-local receipt: `steam-activation.json` in the installed root.
Activation script retained in its verified staging directory:
`activate-steam-windows-20261016.py`.

An actual Mac unsent draft test used ordinary Command-V. Steam requests from
the Mac timed out during this final check, independently reproduced by curl.
The helper correctly pasted the unmodified Steam URL without waiting. Only
the agent's unsent test text was cleared, and the original clipboard's complete
snapshot was restored from memory under a unique clipboard ownership marker.
No test message was sent. Positive enhanced pasting in the live Mac composer
was therefore not verified in this run. Earlier live source checks and the
Windows pre-install check found Portal 2's MP4. The final installed Windows
executable also passed all self-tests and returned Portal 2's MP4, while
Enshrouded correctly fell back. Its worker stayed ready with no pending
clipboard replacement. The Windows interactive draft was not tested; native
tests used a private clipboard.

Known compatibility remains narrow. A separate current-store sample in the
VanchiBot workspace found legacy MP4s for only four of twenty top-seller app
IDs; none of twenty newest-release app IDs was confirmed, including two
inconclusive timeouts. This activation does not add streaming conversion or
broader modern-trailer support. Failed preparation retains normal Steam links.
