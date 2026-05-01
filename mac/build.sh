#!/bin/bash
#
# build.sh
# Compiles JASS and bundles it into a runnable JASS.app.
#
# Usage:   ./build.sh
# Output:  build/JASS.app

set -euo pipefail

APP_NAME="JASS"
# If you fork JASS and distribute your own builds, change BUNDLE_ID to your
# own reverse-DNS (e.g. com.github.<yourname>.jass-fork) so macOS does not
# confuse your build with the upstream Orville one in System Settings >
# Privacy & Security > Accessibility.
BUNDLE_ID="be.orville.jass"
VERSION="1.0"
MIN_MACOS="11.0"

BUILD_DIR="build"
APP_BUNDLE="$BUILD_DIR/$APP_NAME.app"
MACOS_DIR="$APP_BUNDLE/Contents/MacOS"
RES_DIR="$APP_BUNDLE/Contents/Resources"

echo "Cleaning previous build..."
rm -rf "$BUILD_DIR"
mkdir -p "$MACOS_DIR" "$RES_DIR"

echo "Compiling Swift sources (universal binary: arm64 + x86_64)..."
# -swift-version 5 keeps the compiler in Swift 5 language mode, which skips
# Swift 6's strict concurrency checks. The code is written to be correct, but
# we don't need the extra annotations for a tool this size.
#
# To produce a "universal" binary that runs natively on both Apple Silicon
# and Intel Macs, we compile twice (once per architecture) and then use
# `lipo` to merge the two into a single file. This is the most reliable
# command-line recipe; passing two -target flags to swiftc does not work.
# Cost is a slightly larger executable; benefit is that JASS works on every
# Mac running macOS 11 or newer, regardless of what chip is inside.
BIN_ARM64="$BUILD_DIR/JASS-arm64"
BIN_X86_64="$BUILD_DIR/JASS-x86_64"

swiftc \
    -O \
    -swift-version 5 \
    -target "arm64-apple-macos$MIN_MACOS" \
    -framework AppKit \
    -framework ApplicationServices \
    -framework Carbon \
    app.swift system.swift \
    -o "$BIN_ARM64"

swiftc \
    -O \
    -swift-version 5 \
    -target "x86_64-apple-macos$MIN_MACOS" \
    -framework AppKit \
    -framework ApplicationServices \
    -framework Carbon \
    app.swift system.swift \
    -o "$BIN_X86_64"

lipo -create -output "$MACOS_DIR/$APP_NAME" "$BIN_ARM64" "$BIN_X86_64"
rm -f "$BIN_ARM64" "$BIN_X86_64"

# Note: we don't ad-hoc sign the raw binary here. The final "codesign" call
# at the end of this script signs the entire .app bundle with the correct
# identifier, which covers the binary too. Signing twice would just have
# the second pass overwrite the first.

echo "Building app icon..."
# Render the SVG master into all the PNG sizes that iconutil needs,
# then ask iconutil to pack them into a single .icns file that macOS uses
# for Dock, Finder, Spotlight, etc. We regenerate from source every build
# so tweaks to icon.svg flow through without manual steps.
ICONSET_DIR="$BUILD_DIR/JASS.iconset"
rm -rf "$ICONSET_DIR"
mkdir -p "$ICONSET_DIR"

if [ ! -f "icon.svg" ]; then
    echo "Warning: icon.svg not found; skipping icon generation."
else
    swift render-icon.swift icon.svg "$ICONSET_DIR"
    iconutil -c icns "$ICONSET_DIR" -o "$RES_DIR/AppIcon.icns"
    rm -rf "$ICONSET_DIR"
fi

echo "Writing Info.plist..."
cat > "$APP_BUNDLE/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>$APP_NAME</string>
    <key>CFBundleDisplayName</key>
    <string>JustAScreenSwitcher</string>
    <key>CFBundleIdentifier</key>
    <string>$BUNDLE_ID</string>
    <key>CFBundleVersion</key>
    <string>$VERSION</string>
    <key>CFBundleShortVersionString</key>
    <string>$VERSION</string>
    <key>CFBundleExecutable</key>
    <string>$APP_NAME</string>
    <key>CFBundleIconFile</key>
    <string>AppIcon</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>LSMinimumSystemVersion</key>
    <string>$MIN_MACOS</string>
    <key>LSUIElement</key>
    <true/>
    <key>NSHighResolutionCapable</key>
    <true/>
</dict>
</plist>
PLIST

echo "Bundle built at: $APP_BUNDLE"

# Ad-hoc code signing: gives the bundle a stable identity that macOS can
# recognize between launches. Without this, Accessibility permission grants
# would not persist: macOS ties the grant to a cryptographic identifier of
# the app, and without a signature that identifier drifts across launches,
# triggering a permission loop. Ad-hoc signing is free, requires no Apple
# Developer account, and does not remove the Gatekeeper first-launch
# warning (because there's still no trusted developer identity), but it
# does solve the relaunch permission issue.
#
# The "-" as identity means ad-hoc. --deep applies it to anything nested
# inside the bundle. --force overwrites any previous signature.
echo "Ad-hoc signing the app bundle..."
codesign --force --deep --sign - --identifier "$BUNDLE_ID" "$APP_BUNDLE"

echo ""
echo "Next steps:"
echo "  1. Open the app the first time with:   open '$APP_BUNDLE'"
echo "     (or right-click > Open in Finder, then confirm 'Open')"
echo "  2. Grant Accessibility permission when prompted."
echo "  3. Relaunch JASS."
echo "  4. Press Option+Up to swap. Configure via the menu bar icon."
