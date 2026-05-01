# JustAScreenSwitcher (JASS) for Windows

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
- **Three or more displays.** JASS swaps your primary screen with whichever
  external screen your cursor is on at the moment you press the shortcut.
  Windows on other displays stay put.

Other behaviour:

- Default shortcut: **Ctrl+Alt+J** (which is **AltGr+J** on a Belgian or
  other AltGr keyboard layout)
- Handles maximized windows correctly on the new monitor
- Minimized windows are left alone (they stay minimized where they were)
- Windows on other virtual desktops are left alone (they stay on their own
  desktop and aren't dragged into the current one)
- Tiny tray app, no Start menu entry by default, no installer
- All settings accessible from the tray menu; no need to edit files

## Build

Requirements: .NET 8 SDK or newer, Windows 10 or newer.

```powershell
.\build.ps1
```

This produces `bin\Release\net8.0-windows\win-x64\publish\JASS.exe`, a
self-contained single-file executable that runs on any Windows 10 or
newer x64 machine. No .NET install required on the target machine.

The published exe is around 70-150 MB because it bundles the .NET runtime
inside. That's the price of zero-install distribution.

## Distribute

To share JASS with others: copy the published `JASS.exe` and send it.
That's the whole package. No installer, no separate runtime, no DLL
soup. Recipients save it somewhere stable on their machine and
double-click.

First launch on the recipient's machine still triggers Windows
SmartScreen ("Windows protected your PC") because the exe isn't
signed with a Microsoft Authenticode certificate. They click
**More info > Run anyway**. This is a one-time prompt per binary;
subsequent launches go straight through.

## Run

First launch:

1. Double-click `JASS.exe` (or run from PowerShell:
   `.\bin\Release\net8.0-windows\win-x64\publish\JASS.exe`).
2. If Windows SmartScreen warns, click **More info > Run anyway**.
3. JASS appears as an icon in the system tray (bottom-right of the
   taskbar; you may need to click the small chevron to see it).
4. Press **Ctrl+Alt+J** to swap.

## Privacy

JASS runs entirely on your machine. It does not connect to the internet,
does not send any telemetry, and has no auto-update mechanism that phones
home.

JASS uses standard Win32 APIs to read window positions and move windows
belonging to other applications. There's no special permission prompt on
Windows for this; the OS allows any non-elevated app to move windows of
other non-elevated apps. JASS does not read window contents (no screen
capture, no keystroke logging).

Logs are written to `%LOCALAPPDATA%\JASS\jass.log` and stay on your
machine. The config file at `%APPDATA%\JASS\config.ini` only contains
your shortcut and blink preferences.

## Using JASS

Right-click the tray icon to get to everything:

- **Shortcut**: shows the currently registered combination. Edit
  `config.ini` to change it (a graphical recorder is on the to-do list).
- **Blink effect**: toggle the brief fade-to-black during swaps. With
  it on, the swap looks clean. With it off, you see the windows move
  around.
- **Settle time**: how long the screen stays black after JASS has issued
  window moves, to let apps finish their own redraw. Pick a preset from
  the submenu. Lower feels snappier; higher hides slow apps better.
  Defaults higher than the Mac version because Windows window moves are
  synchronous and can take a few hundred ms per app.
- **Swap screens now**: manual swap without using the shortcut.
- **Edit config file**: opens the config in Notepad.
- **Reload config**: picks up changes made by editing the file directly.
- **Quit**: shut down JASS and remove the tray icon.

## Configuration file

If you prefer editing text, the file lives at
`%APPDATA%\JASS\config.ini`. Format:

```
shortcut = ctrl+alt+j
blink = yes
blink_settle_ms = 500
```

Supported modifiers: `ctrl`, `alt`, `shift`, `win`. Supported keys:
arrow keys, function keys `f1`..`f24`, letters `a`..`z`, digits
`0`..`9`, and `space` / `return` / `tab` / `escape` / `pageup` /
`pagedown` / `home` / `end` / `insert` / `delete`.

After editing manually, choose **Reload config** from the tray menu.

## Auto-start with Windows

JASS doesn't add itself to startup automatically. To run it at login:

1. Press **Win+R**, type `shell:startup`, press Enter. This opens your
   personal Startup folder.
2. Right-click in that folder, choose **New > Shortcut**, and point it
   at your `JASS.exe` location.
3. Done. JASS will start the next time you log in.

To stop it from auto-starting, just delete the shortcut.

## Troubleshooting

**Shortcut does nothing.**
The tray menu shows the currently registered shortcut. If another app
is using the same combination, JASS warns on launch and the shortcut
stays unregistered. Pick a different combination via `config.ini` and
reload. Common conflicts: Ctrl+Alt+letter combinations claimed by Intel
graphics utilities or remote desktop software.

**Shortcut partly works but also triggers something else.**
Some combinations are intercepted by the focused app before the global
hotkey fires. If so, pick a less commonly bound combination.

**A window didn't move, or moved to the wrong place.**
Check `%LOCALAPPDATA%\JASS\jass.log`. Most common causes:

- The window belongs to an elevated process (Task Manager, an admin
  PowerShell, anything launched with "Run as administrator"). Windows
  blocks non-elevated apps from moving elevated windows; JASS logs
  these as `skipped` and leaves them alone. Run JASS itself elevated to
  move them, but be aware that an elevated JASS can't move
  non-elevated windows of restricted apps in some cases either.
- The window draws its own title bar (some Electron apps, some Chromium
  variants, PrusaSlicer, certain custom-skinned apps). JASS handles
  these via a three-step un-maximize / move / re-maximize sequence;
  works in our testing but is the most fragile path.

**The swap looks slow or visibly flickers.**
Increase the **Settle time** via the tray menu. The blink overlay only
hides what falls inside its window. Large window sets with heavy apps
(browsers, IDEs, slicers) push the total swap above the default
500 ms. Try 600 ms or set a higher value in `config.ini`.

**Windows on other virtual desktops moved unexpectedly.**
This shouldn't happen. JASS uses the DWM cloak attribute to skip
windows on other virtual desktops. If you see one move anyway, please
file a bug with the log and the steps to reproduce.

**SmartScreen warning every launch.**
Only happens on the very first launch of a given binary. After clicking
"Run anyway" once, the SmartScreen reputation cache remembers it. If
you're rebuilding from source frequently, every fresh build is a "new"
binary as far as SmartScreen is concerned.

## Uninstall

1. Quit JASS from the tray icon (right-click > Quit).
2. Delete `JASS.exe` from wherever you put it.
3. (Optional) Delete `%APPDATA%\JASS\` and `%LOCALAPPDATA%\JASS\` to
   remove your config and logs.
4. (Optional) Remove the startup shortcut from `shell:startup` if you
   set one up.

## Code layout

- `app.cs` - domain types, config loading and saving, core swap logic,
  and the `JassApp` orchestration class. No direct Win32 calls.
- `system.cs` - everything that touches Windows: tray icon (NotifyIcon),
  global hotkey (RegisterHotKey), monitor enumeration
  (EnumDisplayMonitors), window enumeration and moves (EnumWindows,
  SetWindowPlacement, SetWindowPos), blink overlay (layered window),
  alerts. Plain Win32 P/Invoke; the only WinForms types used are
  NotifyIcon and ContextMenuStrip.
- `app.manifest` - declares Per-Monitor V2 DPI awareness and asInvoker
  privilege level.
- `jass.csproj` - .NET project file with embedded icon and manifest.
- `icon.svg` - source icon (Orville charcoal + green arrows).
- `jass.ico` - multi-resolution icon rendered from `icon.svg`.
- `build.ps1` - wraps `dotnet publish` with the right flags for a
  self-contained single-file x64 build.

The split is by concern, not by framework. `app.cs` describes *what*
JASS does; `system.cs` describes *how* Windows does it. Same shape as
the macOS version (`app.swift` / `system.swift`).

## Differences from the macOS version

JASS for Windows is a separate implementation rather than a port. The
domain logic and on-disk config format match the macOS version, but the
OS calls are Win32 throughout (`SetWindowPlacement` instead of the
Accessibility API, `RegisterHotKey` instead of Carbon hotkeys,
`EnumDisplayMonitors` instead of `NSScreen`).

A few user-visible differences worth noting:

- **Default settle time is 500 ms instead of 10 ms.** macOS window moves
  via Accessibility are near-instant; Windows `SetWindowPlacement` on
  apps that draw their own chrome can take 100-300 ms each, with the
  last few windows in a batch finishing well after the apply loop
  returns. The longer default hides this.
- **Default shortcut is Ctrl+Alt+J instead of Option+Up.** Option+Up on
  Windows would mean Alt+Up, which is heavily used (back navigation in
  Explorer, line jumps in editors). Ctrl+Alt+J is rarely bound and on
  AltGr keyboards is reachable as AltGr+J. Easy to change in the
  config.
- **No accessibility permission prompt.** Windows doesn't have an
  equivalent gate; standard Win32 calls work without explicit user
  consent.
- **No graphical shortcut recorder yet.** macOS has a recorder dialog;
  on Windows you currently edit `config.ini` directly. Building the
  recorder is on the to-do list.
- **No virtual-desktop swapping.** Windows has its own concept of
  virtual desktops (Win+Tab) that's separate from monitors; JASS
  doesn't touch virtual desktops, only physical monitors.

## Future improvements

These aren't implemented in v1 but are on the list of things that could
be added later if demand is there.

- **Graphical shortcut recorder.** A small dialog where you press a
  combination and JASS records and saves it, matching what the macOS
  version offers. Tray menu currently links to the config file as a
  workaround.
- **Optional swapping of minimized windows.** Currently minimized
  windows stay where they were. Same trade-off as on macOS; a config
  toggle like `swap_minimized = yes/no` could expose this as opt-in.
- **App exclusion rules.** Some users like to keep certain apps always
  on one specific monitor. A config list like
  `exclude_processes = chat.exe, slack.exe` would skip those during the
  swap so they stay put.
- **Code-signing the published exe.** Eliminates the SmartScreen
  warning on first launch. Requires a code-signing certificate, which
  is a recurring annual cost; deferred until there's enough non-Orville
  use to justify it.
