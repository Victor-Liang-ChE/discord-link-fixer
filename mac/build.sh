#!/bin/bash
set -euo pipefail
task_root=$(cd "$(dirname "$0")/.." && pwd)
task_build="$task_root/mac/build"
mkdir -p "$task_build"
task_stage=$(mktemp -d "$task_build/stage.XXXXXXXX")
task_app="$task_stage/Discord Link Fixer.app"
mkdir -p "$task_app/Contents/MacOS" "$task_app/Contents/Resources"
cp "$task_root/mac/Info.plist" "$task_app/Contents/Info.plist"
cp "$task_root/link-tests.json" "$task_app/Contents/Resources/link-tests.json"
/usr/bin/swift "$task_root/mac/render-assets.swift" "$task_stage/assets"
/usr/bin/iconutil -c icns "$task_stage/assets/AppIcon.iconset" -o "$task_app/Contents/Resources/AppIcon.icns"
if [ -n "${DISCORD_LINK_FIXER_BUNDLE_IDENTIFIER:-}" ]; then
  /usr/bin/plutil -replace CFBundleIdentifier -string "$DISCORD_LINK_FIXER_BUNDLE_IDENTIFIER" "$task_app/Contents/Info.plist"
fi
for task_arch in arm64 x86_64; do
  /usr/bin/swiftc -target "$task_arch-apple-macos13.0" "$task_root/mac/main.swift" "$task_root/mac/Startup.swift" -o "$task_stage/DiscordLinkFixer-$task_arch" -framework AppKit -framework ApplicationServices -framework UserNotifications
done
/usr/bin/lipo -create "$task_stage/DiscordLinkFixer-arm64" "$task_stage/DiscordLinkFixer-x86_64" -output "$task_app/Contents/MacOS/DiscordLinkFixer"
DISCORD_LINK_FIXER_TESTS="$task_root/link-tests.json" "$task_app/Contents/MacOS/DiscordLinkFixer" --self-test
/usr/bin/codesign --force --sign "${DISCORD_LINK_FIXER_SIGNING_IDENTITY:--}" "$task_app"
/usr/bin/codesign --verify --strict --all-architectures "$task_app"
if [ -e "$task_build/Discord Link Fixer.app" ]; then
  mv "$task_build/Discord Link Fixer.app" "$task_stage/previous.app"
fi
cp "$task_stage/assets/install.png" "$task_build/install.png"
mv "$task_app" "$task_build/Discord Link Fixer.app"
printf '%s\n' "$task_build/Discord Link Fixer.app"
