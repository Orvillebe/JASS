# JustAScreenSwitcher (JASS) for Mac

Swap all your windows between two monitors with a single shortcut.

## What it does

You have multiple monitors. Press one shortcut, your windows trade places
between two of them. Window positions scale proportionally so a window in
the top-left of monitor A lands in the top-left of monitor B, regardless
of different resolutions.

Behaviour by number of connected displays:

- **One display.** Nothing to swap; JASS shows a brief explanation if you
  trigger it.
- **Two displays.** All windows trade between the two screens.
- **Three or more displays.** JASS swaps your built-in (or primary) screen
  with whichever external screen your cursor is on at the moment you press
  the shortcut. Windows on other displays stay put.

Other behaviour:

- Default shortcut: **Option+Up**
- Handles maximized windows correctly on the new monitor
- Minimized windows are left alone (they stay minimized where they were)
- Tiny menu bar app, no Dock icon, no bundled dependencies
- All settings accessible from the menu bar; no need to edit files

## Build

Requirements: Xcode (or just the Command Line Tools) with Swift 5.5 or newer.

```
./build.sh
```

This produces `build/JASS.app`, a universal binary that runs on both Apple
Silicon and Intel Macs, macOS 11 or newer.

Note on the build host: the compiled app runs on **macOS 11 or newer**, but
building it currently requires **macOS 14 or newer**, because `build.sh`
renders the SVG icon to PNGs via AppKit's native SVG support, which Apple
added in macOS 14. If you need to build on an older macOS, the cleanest
workaround is to pre-render `icon.svg` to a `JASS.iconset/` folder once on
a newer machine, commit those PNGs, and have `build.sh` skip the rendering
step when the PNGs are already present.

## Distribute

To share JASS with others as a ready-to-install package:

```
./build.sh
./package.sh
```

This produces `dist/JASS.dmg`. Share that file by email, cloud drive, or
upload to a website. Recipients double-click the DMG, drag JASS to their
Applications folder, and follow the short `INSTALL.txt` bundled inside.

First launch on the recipient's Mac still requires a one-time right-click >
Open (because the app isn't signed with an Apple Developer ID) and a
one-time Accessibility permission grant. Both are explained in `INSTALL.txt`.

## Run

First launch:

1. `open build/JASS.app`
   (Finder may warn because the app isn't signed. Right-click the app and
   choose "Open", then confirm.)
2. macOS will ask for Accessibility permission. JASS needs this to move
   windows that belong to other apps. Open
   **System Settings > Privacy & Security > Accessibility** and enable JASS.
3. Relaunch JASS.
4. You should see a small swap icon in the menu bar. Press **Option+Up** to swap.

## Privacy

JASS runs entirely on your Mac. It does not connect to the internet, does
not send any telemetry, and has no auto-update mechanism that phones home.

The Accessibility permission JASS asks for is used only to read window
positions and to move windows belonging to other applications. This is the
OS-level mechanism every window manager on macOS has to use; there is no
private API or alternative path. The same permission lets JASS see which
windows exist, but it does not read window contents (no screen capture, no
keystroke logging).

Logs are written to `~/Library/Logs/JASS/jass.log` and stay on your machine.
The config file at `~/Library/Application Support/JASS/config.ini` only
contains your shortcut and blink preferences.

## Using JASS

Click the menu bar icon to get to everything:

- **Shortcut**: current shortcut is shown. Hover to open the submenu and
  click "Change..." to record a new one. Press the new combination and it
  takes effect immediately.
- **Blink effect**: toggle the brief fade-to-black during swaps. With it on,
  the swap looks clean. With it off, you see the windows move around.
- **Settle time**: how long the screen stays black after JASS has issued
  window moves, to let apps finish their own animations. Pick a preset from
  the submenu. Lower feels snappier; higher hides slow apps better.
- **Swap screens now**: manual swap without using the shortcut.
- **Edit config file**: opens the config in your default text editor for
  power-user tweaks.
- **Reload config**: picks up changes made by editing the file directly.

All menu-driven changes are saved to the config file automatically.

## Configuration file

If you prefer editing text, the file lives at
`~/Library/Application Support/JASS/config.ini`. Format:

```
shortcut = option+up
blink = yes
blink_settle_ms = 10
```

Supported modifiers: `cmd`, `ctrl`, `option` (also accepted as `alt` or
`opt`), `shift`. Supported keys: arrow keys, function keys `f1`..`f12`,
letters `a`..`z`, digits `0`..`9`, and `space` / `return` / `tab` / `escape`.

After editing manually, choose "Reload config" from the menu bar.

## Troubleshooting

**Shortcut does nothing.**
Check the menu bar. It shows the currently registered shortcut. If another
app is using the same combination, JASS warns on launch and the shortcut
stays unregistered. Pick a different key via the menu.

**Shortcut partly works, but also triggers something else.**
Some combinations (like Cmd+Up in Finder, which goes to the parent folder)
are intercepted by the focused app before the global hotkey fires. Pick a
shortcut that's less commonly bound, like Option+Up or Cmd+F12.

**Fullscreen windows stay put.**
Expected. Fullscreen windows on macOS live in their own Space and can't be
cleanly moved between displays. Exit fullscreen first (green button or
`Ctrl+Cmd+F`), then swap.

**One window didn't move.**
Some apps (system utilities, certain Electron variants) don't respond to
Accessibility window-move requests. JASS logs these and rolls back the
window to its original position. Check `~/Library/Logs/JASS/jass.log` for
details.

**The permission prompt keeps coming back.**
Unsigned apps get re-prompted when the binary changes. Rebuilds will trigger
this. For daily use of a stable build, it stays granted.

## Uninstall

1. Quit JASS from the menu bar icon.
2. Delete `build/JASS.app` (or wherever you moved it).
3. Delete `~/Library/Application Support/JASS/` and `~/Library/Logs/JASS/`.
4. Remove JASS from **System Settings > Privacy & Security > Accessibility**.

## Code layout

- `app.swift` — domain types, config loading/saving, core swap logic, and
  the `AppDelegate` that wires everything together. No direct OS calls.
- `system.swift` — everything that touches macOS: Accessibility API,
  `NSScreen`, Carbon hotkey, permission prompt, alerts, blink overlay,
  shortcut recorder dialog.
- `config.ini` — default config, mirrored in code for first-run creation.
- `build.sh` — compile and bundle.

The split is by concern, not by framework. `app.swift` describes *what* JASS
does; `system.swift` describes *how* macOS does it.

## Future improvements

These aren't implemented in v1 but are on the list of things that could be
added later if demand is there.

- **Optional swapping of minimized windows.** Currently minimized windows
  stay where they were. If we wanted to swap them too, we'd need to
  temporarily unminimize each one, move it, and re-minimize it. That works
  but triggers macOS's built-in genie/scale animation on every step, which
  makes the swap feel slow and noisy. A config toggle like
  `swap_minimized = yes/no` could expose this as an opt-in.

- **App exclusion rules.** Some users like to keep certain apps always on
  one specific monitor (for example a chat app always on the internal
  screen). A config list like `exclude_apps = Slack, Telegram` would skip
  those apps during the swap so they stay put.
