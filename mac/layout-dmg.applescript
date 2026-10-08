on run argv
    set installFolder to POSIX file (item 1 of argv) as alias
    set backgroundFile to POSIX file ((item 1 of argv) & "/.background/install.png") as alias
    tell application "Finder"
        open installFolder
        set installWindow to container window of installFolder
        set current view of installWindow to icon view
        set toolbar visible of installWindow to false
        set statusbar visible of installWindow to false
        set viewOptions to icon view options of installWindow
        set arrangement of viewOptions to not arranged
        set icon size of viewOptions to 96
        set text size of viewOptions to 12
        set background picture of viewOptions to backgroundFile
        set position of item "Discord Link Fixer.app" of installFolder to {160, 170}
        set position of item "Applications" of installFolder to {440, 170}
        update installFolder without registering applications
        set bounds of installWindow to {300, 150, 900, 550}
        delay 1
        close installWindow
        delay 2
    end tell
end run
