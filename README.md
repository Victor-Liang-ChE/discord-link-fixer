# Discord Link Fixer

Replace supported social-media link domains when pasting into the Discord desktop app. You still choose when to send the message. This is a clipboard helper, not a Discord bot or account automation.

| Original domain | Replacement |
| --- | --- |
| x.com, twitter.com | fxtwitter.com |
| instagram.com | kkinstagram.com |
| tiktok.com | tnktok.com |
| reddit.com | vxreddit.com |

The rules support www variants and mobile X/Twitter links. Paths, queries, fragments and surrounding text are preserved. Short-link hosts such as vm.tiktok.com are not supported.

## Source

- `mac/main.swift`: complete native Mac helper.
- `mac/Startup.swift`: first-launch startup registration, ownership checks and rollback.
- `windows/DiscordLinkFixer.cs`: complete Windows helper and crash supervisor.
- `windows/ClipboardTransactions.cs`: bounded native clipboard snapshots and locked restoration.
- `windows/PasteService.cs`: keyboard, clipboard and guarded paste dispatch.
- `windows/install.py`: compiler, desktop shortcut and startup/recovery task setup.
- `iphone/Discord Link Fixer.json`: editable Apple Shortcut workflow.
- `iphone/Discord Link Fixer.shortcut`: signed Shortcut for importing.
- `link-tests.json`: shared URL test fixtures.

## Mac

Download the [Mac disk image](https://github.com/Victor-Liang-ChE/discord-link-fixer/releases/download/v1.1.0/Discord-Link-Fixer-1.1.0-universal.dmg). No Terminal, Python, Swift or developer tools are needed to use the download.

1. Open the DMG.
2. Drag **Discord Link Fixer** onto **Applications**.
3. Eject the disk image and open the installed app from Applications.
4. Grant Accessibility permission in System Settings. Allow Notifications if you want warning banners.

On first launch, the app registers startup at login and hands over to launchd for crash recovery. It shows a **Link** menu in the menu bar, not a normal app window. The menu includes Pause, permission controls and Test notification. A normal Quit stays stopped until the next login; opening the app manually resumes it. The app refuses to configure startup from the mounted image or an unrelated startup entry. `/Applications` and your user Applications folder are supported.

Command-V in Discord converts supported links. Option-Command-V bypasses conversion.

The download contains Apple Silicon and Intel executables and requires macOS 13 or newer. Native testing was performed on Apple Silicon; Intel execution and a clean-machine quarantined download have not been independently tested. This release is locally signed, not Apple Developer ID signed or notarized. macOS may block its first opening. If you trust the source and checksum, follow [Apple's opening guidance](https://support.apple.com/en-us/102445). Do not disable Gatekeeper or remove quarantine as an installation step.

For updates, Quit the helper before replacing the app in the same Applications folder, then open the replacement. Rebuilding with another signer or moving to a different copy can require permission/startup setup again.

To uninstall, Quit the helper, remove its matching LaunchAgent from `~/Library/LaunchAgents`, and move the app to Trash. Revoke its Accessibility permission if desired. The default startup label is `local.discord-link-fixer`; upgraded installations can retain an existing label. Optional Bash removal of the default job is `launchctl bootout "gui/$(id -u)/local.discord-link-fixer"`. Never unload a different target's job.

### Building from source

The source tools remain for developers, not as the primary install flow. Apple command-line developer tools are required:

```bash
bash mac/build.sh          # Build a universal app
bash mac/test-startup.sh   # Test actual first-launch takeover and crash recovery
bash mac/package.sh        # Build the drag-to-Applications DMG
```

Packaging asks Finder to arrange only the generated image's window. Allow Finder automation if requested. `bash mac/install.sh` remains an advanced local source installer with rollback and a `--check` mode. Set `DISCORD_LINK_FIXER_SIGNING_IDENTITY` to your own stable signing identity when rebuilding. The default ad-hoc signer can require Accessibility approval again after changes. Private signing keys are never distributed.

## Windows

Windows uses a tray app, not a drag-to-Applications window. Once installed, copy a supported link and press Ctrl-V in Discord. You still press Send. Look in the hidden-icons area near the taskbar clock for Discord Link Fixer; right-click it for Pause, Quit and Test notification. Its desktop shortcut opens or resumes it. There is no main window.

There is not yet a downloadable Windows Setup.exe release. The installation below is source-based and requires Python; you do not need Python commands for ordinary use after installation.

Requires Windows, the .NET Framework compiler at the path used in `windows/install.py`, Python, pywin32 and psutil. Review the installer before running it. From Bash on Windows, with Windows Python available:

```bash
python -m pip install pywin32 psutil
python windows/install.py
```

Keep the folder at a permanent, writable location before installing. The installer validates ownership of both existing tasks and the desktop shortcut before changing anything. It builds and tests a candidate, keeps a backup, registers the login task and starts the helper in the signed-in desktop session without opening a background console. A guarded supervisor handles worker recovery. Updating removes the obsolete minute-by-minute recovery task only after its ownership is verified. Failed updates roll back the artifacts they changed.

Ctrl-V in the standard Discord desktop installation converts links. Use the tray's Pause command to paste original links. Ctrl-Alt-V is not advertised as an alternate paste command, because Discord does not reliably bind it to Paste. Login or the desktop shortcut resumes the helper after an intentional quit. To uninstall, quit the helper, remove its Discord Link Fixer task in Task Scheduler, then remove its shortcut and folder. Older installations can also have a Discord Link Fixer Recovery task.

For a source update, Quit from the tray first, pull the updated repository, then run `python windows/install.py --rebuild --update-owned`. The ownership check requires the existing tasks to point at this installation. Do not run a second copy from a different folder.

The keyboard callback queues paste work without reading clipboard contents, waiting on providers or doing URL conversion. The clipboard worker accepts bounded Unicode, plain-text, HTML, RTF and Windows clipboard-policy formats as raw bytes. Images, files, unknown custom formats and oversized copies pass through unchanged. Conversion also passes through if the clipboard snapshot is not ready. Queued pastes expire if processing is slow or the foreground window/focused control changes; no focus is forced and no Enter key is injected.

Clipboard replacement and restoration compare ownership while holding the clipboard lock. Repeated pastes renew the restore delay. A busy clipboard postpones normal Quit until the pending original is preserved or a newer user copy supersedes it. An OS shutdown, forced termination or unrecoverable native crash can still prevent restoration; a recovery process cannot reconstruct rich clipboard data that existed only in the terminated process's memory.

From Bash on Windows, run the automated gates without installing or touching the user's clipboard:

```bash
python windows/verify.py
python windows/install.py --check --update-owned
```

The supervisor lifecycle test needs a separate Windows session with no helper already running in it. An SSH session can test a verified candidate while the installed helper remains in the user's desktop session:

```bash
python windows/test_lifecycle.py /path/to/verified/DiscordLinkFixer.exe
```

It launches only a copied candidate on a private, non-visible desktop, verifies duplicate rejection, crash recovery and intentional Quit, then stops its own test processes. It refuses to run in a session already owned by a helper.

Native clipboard tests run in a private, non-visible window station with its own clipboard. They do not switch the user's desktop. Verification covers URL boundaries, clipboard races and ownership, repeated pastes, contention, rich-format preservation, size limits, delayed rendering, missing GUI dependencies and installer conflict/rollback behavior under normal and optimized Python. A real unsent Discord draft still needs a user-facing smoke test. Automated checks do not prove that Discord or a third-party embed service will always work.

## iPhone and iPad

Import `iphone/Discord Link Fixer.shortcut` in Apple's Shortcuts app. Share a supported URL to Discord Link Fixer, switch to Discord, and paste. Running it without shared input uses the clipboard. An already installed Shortcut does not update merely because its file changes; import the new file and replace or remove the old entry.

iOS does not allow this to intercept ordinary Discord Paste continuously. Source validation does not prove behavior in an actual phone Share Sheet, which still needs a device check.

## Privacy and limitations

The helper makes no network requests, uses no Discord token, reads no conversations and sends no messages. macOS Accessibility is a broad permission; the app uses it to recognize paste shortcuts and check the foreground app. Windows uses a keyboard hook for the same purpose.

Desktop conversion applies to Discord text fields, not only its message composer. Browser Discord, typed links and right-click/menu Paste are not covered. Images and files are left unchanged. The original clipboard is restored after a short delay unless something new was copied. Windows temporary conversions opt out of Windows clipboard history and cloud sync. Independent clipboard-history software can still observe them, and delayed paste handling can miss the conversion.

The embed domains are independent third-party services. Their privacy, availability and playback behavior are not controlled by this helper. The helper is not affiliated with Discord or those services.

## Verification

Mac and Windows include `--self-test` modes. The Mac build runs the tests with `link-tests.json` from this repository. The Windows installer runs its tests before registering tasks. `iphone/validate.swift` checks the Shortcut's conversion rules and action references. None of these tests replaces trying a paste in a real Discord composer.

The startup/health update was tested on the maintainer's Mac and Windows PC: the Mac retained Accessibility approval through installation and recovered an abnormal exit; Windows passed repeated fresh interactive-session starts and worker crash recovery. `bash mac/install.sh --check` validates the build, signing identity and generated startup configuration without replacing or restarting the installed helper. Notification-policy tests cover healthy, paused and unavailable states, a silent first Discord version, unchanged versions and version changes. Banner delivery still depends on OS permissions and notification settings.

This repository is an initial source release, not a notarized or generally verified installer. No open-source license has been selected yet; visibility alone does not grant a reuse license.

## Health and update notices

Mac notifications warn when Accessibility permission or the keyboard event tap is unavailable. The menu also shows the fault. Windows tray notifications warn if refreshing the keyboard hook fails. Warning repeats are suppressed while the same fault persists. OS notification settings and Focus/Do Not Disturb can prevent banners from appearing.

Both desktop helpers check the version of foreground, supported Discord periodically. The first observed version is a silent baseline. A later change produces an informational notice asking you to try a supported link in an unsent draft. An update is not reported as a confirmed failure.

These checks cannot prove that Discord consumed a converted paste or that a third-party embed service is working. The helper does not inspect message fields or make network probes. A fully stopped helper cannot display its own warning; startup recovery is a separate safeguard. iPhone/iPad Shortcuts are not an always-on background monitor.

Windows startup progress is recorded in `startup.json` with the worker PID, desktop session, stage and time. Optional diagnostic write failures do not terminate the helper. Errors record phase, exception type and numeric HRESULT, not exception messages or clipboard contents. `running.json` reports hook installation and maximum callback duration; it explicitly does not certify that a paste was consumed by Discord.
