#!/bin/bash
set -euo pipefail
case "${1:-}" in
  ''|--check) ;;
  *) printf '%s\n' 'Usage: bash mac/install.sh [--check]' >&2; exit 2 ;;
esac
task_root=$(cd "$(dirname "$0")/.." && pwd)
task_app="$HOME/Applications/Discord Link Fixer.app"
task_exe="$task_app/Contents/MacOS/DiscordLinkFixer"
task_support="$HOME/Library/Application Support/Discord Link Fixer"
task_label='local.discord-link-fixer'
task_plist="$HOME/Library/LaunchAgents/$task_label.plist"
task_previous_requirement=''
if [ -e "$task_app" ]; then
  test "$(plutil -extract CFBundleName raw "$task_app/Contents/Info.plist")" = 'Discord Link Fixer'
  test "$(plutil -extract CFBundleExecutable raw "$task_app/Contents/Info.plist")" = 'DiscordLinkFixer'
  export DISCORD_LINK_FIXER_BUNDLE_IDENTIFIER
  DISCORD_LINK_FIXER_BUNDLE_IDENTIFIER=$(plutil -extract CFBundleIdentifier raw "$task_app/Contents/Info.plist")
  task_previous_requirement=$(codesign -d -r- "$task_app" 2>&1 | sed -n 's/^designated => //p')
fi
task_matches=0
for task_candidate in "$HOME/Library/LaunchAgents/"*.plist; do
  [ -f "$task_candidate" ] || continue
  if [ "$(plutil -extract ProgramArguments.0 raw "$task_candidate" 2>/dev/null || true)" = "$task_exe" ]; then
    task_matches=$((task_matches + 1))
    task_plist="$task_candidate"
    task_label=$(plutil -extract Label raw "$task_candidate")
  fi
done
if [ "$task_matches" -gt 1 ]; then
  printf '%s\n' 'Multiple startup jobs reference this app. Inspect them before updating.' >&2
  exit 1
fi
if [ -f "$task_plist" ] && [ "$task_matches" -eq 0 ]; then
  printf '%s\n' 'Startup label belongs to another target; nothing was replaced.' >&2
  exit 1
fi
bash "$task_root/mac/build.sh"
task_built="$task_root/mac/build/Discord Link Fixer.app"
if [ -n "$task_previous_requirement" ] && [ -n "${DISCORD_LINK_FIXER_SIGNING_IDENTITY:-}" ]; then
  codesign -v -R "=$task_previous_requirement" "$task_built"
fi
mkdir -p "$HOME/Applications" "$task_support" "$HOME/Library/LaunchAgents"
task_stage=$(mktemp -d "$task_support/install.XXXXXXXX")
ditto "$task_built" "$task_stage/Discord Link Fixer.app"
cp "$task_root/mac/launchagent.plist" "$task_stage/agent.plist"
plutil -replace Label -string "$task_label" "$task_stage/agent.plist"
task_arguments=$(/usr/bin/osascript -l JavaScript -e 'function run(argv) { return JSON.stringify([argv[0]]); }' "$task_exe")
plutil -replace ProgramArguments -json "$task_arguments" "$task_stage/agent.plist"
plutil -lint "$task_stage/agent.plist"
test "$(plutil -extract ProgramArguments.0 raw "$task_stage/agent.plist")" = "$task_exe"
if plutil -extract ProgramArguments.1 raw "$task_stage/agent.plist" >/dev/null 2>&1; then
  printf '%s\n' 'Unexpected extra startup argument; nothing was replaced.' >&2
  exit 1
fi
if [ "${1:-}" = --check ]; then
  printf '%s\n' 'Build, tests, signature and startup configuration passed. Nothing was installed or restarted.'
  exit 0
fi
cp "$task_root/link-tests.json" "$task_support/link-tests.json"
task_was_loaded=false
if launchctl print "gui/$(id -u)/$task_label" >/dev/null 2>&1; then
  task_was_loaded=true
  launchctl bootout "gui/$(id -u)/$task_label"
fi
if [ -e "$task_app" ]; then mv "$task_app" "$task_stage/previous.app"; fi
if [ -e "$task_plist" ]; then cp "$task_plist" "$task_stage/previous.plist"; fi
if ! { mv "$task_stage/Discord Link Fixer.app" "$task_app" && cp "$task_stage/agent.plist" "$task_plist" && launchctl bootstrap "gui/$(id -u)" "$task_plist"; }; then
  if [ -e "$task_app" ]; then mv "$task_app" "$task_stage/failed.app"; fi
  if [ -e "$task_stage/previous.app" ]; then mv "$task_stage/previous.app" "$task_app"; fi
  if [ -e "$task_stage/previous.plist" ]; then
    cp "$task_stage/previous.plist" "$task_plist"
    if [ "$task_was_loaded" = true ]; then launchctl bootstrap "gui/$(id -u)" "$task_plist"; fi
  elif [ -e "$task_plist" ]; then
    mv "$task_plist" "$task_stage/failed.plist"
  fi
  printf '%s\n' "Startup failed; previous files were restored where available. Backup: $task_stage" >&2
  exit 1
fi
launchctl print "gui/$(id -u)/$task_label" | sed -n '1,18p'
printf '%s\n' 'Installed. Starts at login and retries abnormal exits. Normal Quit stays stopped until login.'
printf '%s\n' 'Grant Accessibility and Notifications if requested. Permission status is in the Link menu.'
