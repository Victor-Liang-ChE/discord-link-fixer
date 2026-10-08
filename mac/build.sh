#!/bin/bash
set -euo pipefail
task_root=$(cd "$(dirname "$0")/.." && pwd)
task_build="$task_root/mac/build"
mkdir -p "$task_build"
task_stage=$(mktemp -d "$task_build/stage.XXXXXXXX")
task_app="$task_stage/Discord Link Fixer.app"
mkdir -p "$task_app/Contents/MacOS"
cp "$task_root/mac/Info.plist" "$task_app/Contents/Info.plist"
/usr/bin/swiftc "$task_root/mac/main.swift" -o "$task_app/Contents/MacOS/DiscordLinkFixer" -framework AppKit -framework ApplicationServices
DISCORD_LINK_FIXER_TESTS="$task_root/link-tests.json" "$task_app/Contents/MacOS/DiscordLinkFixer" --self-test
/usr/bin/codesign --force --sign "${DISCORD_LINK_FIXER_SIGNING_IDENTITY:--}" "$task_app"
/usr/bin/codesign --verify --strict "$task_app"
if [ -e "$task_build/Discord Link Fixer.app" ]; then
  mv "$task_build/Discord Link Fixer.app" "$task_stage/previous.app"
fi
mv "$task_app" "$task_build/Discord Link Fixer.app"
printf '%s\n' "$task_build/Discord Link Fixer.app"
