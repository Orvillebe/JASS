#!/bin/bash
#
# package.sh
# Wraps build/JASS.app in a distributable .dmg.
#
# Usage:   ./package.sh
# Output:  dist/JASS.dmg
#
# Workflow:
#   1. Run ./build.sh first so build/JASS.app exists.
#   2. Run ./package.sh to produce dist/JASS.dmg.
#   3. Share the .dmg by email, upload, etc.
#
# The resulting DMG, when opened, shows a window with the JASS icon and an
# "Applications" shortcut. The user drags JASS into Applications and that's
# it. Recipients still need to right-click-Open the first time and grant
# Accessibility permission (see INSTALL.txt that gets bundled).

set -euo pipefail

APP_NAME="JASS"
APP_BUNDLE="build/$APP_NAME.app"
DIST_DIR="dist"
STAGING_DIR="build/dmg-staging"
DMG_PATH="$DIST_DIR/$APP_NAME.dmg"
VOLUME_NAME="JustAScreenSwitcher"

if [ ! -d "$APP_BUNDLE" ]; then
    echo "Error: $APP_BUNDLE does not exist. Run ./build.sh first."
    exit 1
fi

echo "Preparing staging directory..."
rm -rf "$STAGING_DIR" "$DMG_PATH"
mkdir -p "$STAGING_DIR" "$DIST_DIR"

# Copy the app using `ditto` instead of `cp -R`. `ditto` is Apple's own tool
# for app copying: it preserves code signatures, extended attributes, and
# HFS+ metadata that `cp` can strip, and which TCC relies on to recognize
# the app as the same identity it granted permission to. Using plain `cp`
# here can invalidate the ad-hoc signature we carefully applied in build.sh.
ditto "$APP_BUNDLE" "$STAGING_DIR/$APP_NAME.app"
ln -s /Applications "$STAGING_DIR/Applications"

# Bundle a short install guide so users understand the one-time setup steps
# that macOS requires for unsigned apps. Without this, the Gatekeeper warning
# and the Accessibility permission prompt can feel alarming.
cat > "$STAGING_DIR/INSTALL.txt" <<'INSTALL'
JustAScreenSwitcher (JASS) - one-time install

1. Drag JASS into the Applications folder.

2. Open Applications, right-click (or Control-click) JASS, and choose Open.
   macOS will warn that JASS was not downloaded from the App Store. Click
   Open anyway. This only happens the first time.

3. macOS will ask for Accessibility permission. JASS needs this to move
   windows between your monitors. Click "Open System Settings" and enable
   JASS in the list.

4. Launch JASS from Applications (or from Spotlight). A small up/down arrow
   icon appears in the menu bar. Press Option+Up to swap your screens.

   With two screens, all your windows trade places. With three or more
   screens, JASS swaps your built-in (or primary) screen with whichever
   external screen your cursor is on.

Configuration lives in the menu bar icon. Click it for shortcut, blink
effect, and settle time settings.

To remove JASS: quit it from the menu bar, drag JASS.app to the Trash, and
delete ~/Library/Application Support/JASS/ if you want to remove settings.
INSTALL

echo "Creating DMG..."
# hdiutil ships with macOS. The -format UDZO gives a compressed read-only
# disk image, which is the standard format for distributing Mac apps.
hdiutil create \
    -volname "$VOLUME_NAME" \
    -srcfolder "$STAGING_DIR" \
    -ov \
    -format UDZO \
    "$DMG_PATH" \
    >/dev/null

# Clean up staging.
rm -rf "$STAGING_DIR"

echo ""
echo "DMG built at: $DMG_PATH"
echo ""
echo "To distribute: share $DMG_PATH by email, cloud drive, or upload."
echo "Recipients double-click, drag JASS to Applications, and follow INSTALL.txt."
