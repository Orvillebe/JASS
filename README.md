# JustAScreenSwitcher (JASS)

Swap all your windows between two monitors with a single shortcut.

You have multiple monitors. Press one shortcut, your windows trade places
between two of them. Window positions scale proportionally so a window in
the top-left of monitor A lands in the top-left of monitor B, regardless
of different resolutions.

JASS comes in two implementations sharing the same on-disk config format
and the same overall shape, but written natively for each platform:

- **macOS** see [`mac/README.md`](mac/README.md). Swift, menu bar app,
  uses the Accessibility API. Default shortcut: Option+Up.
- **Windows** see [`windows/README.md`](windows/README.md). C# / .NET 8,
  tray app, uses Win32. Default shortcut: Ctrl+Alt+J.

Each platform README covers build, distribution, configuration, and
troubleshooting in detail. The Windows README also includes a
"Differences from the macOS version" section if you're curious about
behavioral choices.
