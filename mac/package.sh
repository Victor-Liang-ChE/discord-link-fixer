#!/bin/bash
set -euo pipefail
task_root=$(cd "$(dirname "$0")/.." && pwd)
bash "$task_root/mac/build.sh"
task_stage=$(mktemp -d /tmp/discord-link-fixer-dmg.XXXXXXXX)
task_content="$task_stage/content"
mkdir -p "$task_content"
mkdir -p "$task_content/.background"
cp "$task_root/mac/build/install.png" "$task_content/.background/install.png"
ditto "$task_root/mac/build/Discord Link Fixer.app" "$task_content/Discord Link Fixer.app"
ln -s /Applications "$task_content/Applications"
cp "$task_root/mac/INSTALL.txt" "$task_content/.INSTALL.txt"
task_output="$task_root/mac/build/Discord-Link-Fixer-1.1.0-universal.dmg"
if [ -e "$task_output" ]; then mv "$task_output" "$task_stage/previous.dmg"; fi
hdiutil create -quiet -srcfolder "$task_content" -volname 'Discord Link Fixer' -format UDRW -fs HFS+ "$task_stage/layout.dmg"
task_mount="$task_stage/mounted"
mkdir -p "$task_mount"
hdiutil attach -quiet -nobrowse -mountpoint "$task_mount" "$task_stage/layout.dmg"
trap 'hdiutil detach -quiet "$task_mount" 2>/dev/null || true' EXIT
/usr/bin/osascript "$task_root/mac/layout-dmg.applescript" "$task_mount"
hdiutil detach -quiet "$task_mount"
trap - EXIT
hdiutil convert -quiet "$task_stage/layout.dmg" -format UDZO -o "$task_output"
hdiutil verify "$task_output"
(cd "$task_root/mac/build" && shasum -a 256 "$(basename "$task_output")") > "$task_output.sha256"
printf '%s\n' "$task_output"
