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
- `windows/DiscordLinkFixer.cs`: complete Windows helper and crash supervisor.
- `windows/install.py`: compiler, desktop shortcut and startup/recovery task setup.
- `iphone/Discord Link Fixer.json`: editable Apple Shortcut workflow.
- `iphone/Discord Link Fixer.shortcut`: signed Shortcut for importing.
- `link-tests.json`: shared URL test fixtures.

## Mac

Requires the Apple command-line developer tools and macOS. From Bash, build with:

```bash
bash mac/build.sh
```

The output is `mac/build/Discord Link Fixer.app`. Move it to a permanent location, open it, and grant Accessibility permission in System Settings. It shows a Link menu with Pause, permission controls and Quit.

Command-V in Discord converts supported links. Option-Command-V bypasses conversion. The build defaults to ad-hoc signing for a first local trial. Rebuilding can require Accessibility approval again. Set `DISCORD_LINK_FIXER_SIGNING_IDENTITY` to your own stable code-signing identity to avoid changing the signer. Do not borrow someone else's private signing key.

This portable source does not install a Mac startup job. The original machine's launchd setup and signing identity are deliberately not distributed.

## Windows

Requires Windows, the .NET Framework compiler at the path used in `windows/install.py`, Python, pywin32 and psutil. Review the installer before running it. From Bash on Windows, with Windows Python available:

```bash
python -m pip install pywin32 psutil
python windows/install.py
```

Keep the folder at a permanent, writable location before installing. The installer builds the executable, runs its self-tests, creates a desktop shortcut, registers login and recovery tasks, and starts the helper in the signed-in desktop session without opening background consoles. Existing tasks are not silently overwritten.

Ctrl-V in Discord converts links. Ctrl-Alt-V bypasses conversion. The tray menu can pause or quit. Login resumes it after an intentional quit. To uninstall, quit the helper, remove the Discord Link Fixer and Discord Link Fixer Recovery tasks in Task Scheduler, then remove its desktop shortcut and folder.

## iPhone and iPad

Import `iphone/Discord Link Fixer.shortcut` in Apple's Shortcuts app. Share a supported URL to Discord Link Fixer, switch to Discord, and paste. Running it without shared input uses the clipboard. An already installed Shortcut does not update merely because its file changes; import the new file and replace or remove the old entry.

iOS does not allow this to intercept ordinary Discord Paste continuously. Source validation does not prove behavior in an actual phone Share Sheet, which still needs a device check.

## Privacy and limitations

The helper makes no network requests, uses no Discord token, reads no conversations and sends no messages. macOS Accessibility is a broad permission; the app uses it to recognize paste shortcuts and check the foreground app. Windows uses a keyboard hook for the same purpose.

Desktop conversion applies to Discord text fields, not only its message composer. Browser Discord, typed links and right-click/menu Paste are not covered. Images and files are left unchanged. The original clipboard is restored after a short delay unless something new was copied. Clipboard-history software can observe the temporary converted text, and delayed paste handling can miss it.

The embed domains are independent third-party services. Their privacy, availability and playback behavior are not controlled by this helper. The helper is not affiliated with Discord or those services.

## Verification

Mac and Windows include `--self-test` modes. The Mac build runs the tests with `link-tests.json` from this repository. The Windows installer runs its tests before registering tasks. `iphone/validate.swift` checks the Shortcut's conversion rules and action references. None of these tests replaces trying a paste in a real Discord composer.

This repository is an initial source release, not a notarized or generally verified installer. No open-source license has been selected yet; visibility alone does not grant a reuse license.
