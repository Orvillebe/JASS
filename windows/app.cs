//
// app.cs
// JustAScreenSwitcher (JASS) for Windows
//
// Responsibilities:
//   - Domain types (Modifiers, Shortcut, KeyCodes, Config)
//   - Config loading and saving (single source of truth: Config.Default)
//   - Application entry point (Program)
//   - Tray app orchestration (JassApp): wires the tray menu to config edits
//
// OS-specific calls live in system.cs.
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace Jass;

// MARK: - Modifiers

/// <summary>
/// The four modifier flags that RegisterHotKey accepts. Values match the
/// MOD_* constants from the Win32 hotkey API exactly, so we can pass the
/// raw int straight to RegisterHotKey later (stage 3).
/// </summary>
[Flags]
internal enum Modifiers : uint
{
    None    = 0,
    Alt     = 0x0001,  // MOD_ALT
    Control = 0x0002,  // MOD_CONTROL
    Shift   = 0x0004,  // MOD_SHIFT
    Win     = 0x0008,  // MOD_WIN
}

// MARK: - Monitor

/// <summary>
/// A monitor described in physical-pixel virtual-screen coordinates: the
/// origin of the primary monitor sits at (0,0), monitors to the left or
/// above have negative coordinates. Y-axis points down. Same shape as the
/// Mac Monitor type, just sourced from EnumDisplayMonitors instead of
/// NSScreen.
///
/// The Id is the index in the enumeration order returned by SystemMonitors.
/// EnumerateAll. It is stable within one swap cycle (compute -> apply) but
/// not across runs of JASS, which mirrors how the Mac code uses NSScreen
/// indices.
/// </summary>
internal readonly record struct Monitor(int Id, Rectangle Frame);

// MARK: - Window types

/// <summary>
/// Opaque reference to a window, used by the system layer to identify and
/// move it. The core logic never inspects this. Pid and Title are kept
/// only for log readability.
/// </summary>
internal readonly record struct WindowReference(IntPtr HWnd, uint Pid, string Title);

/// <summary>
/// What the system layer observed for a window. WasMaximized records
/// whether the window was maximized at enumerate time, so we can restore
/// that state on the new monitor after the move.
///
/// RelativeFrame uses the *restored* (un-maximized) geometry expressed as
/// fractions (0..1) of the source monitor's full frame. For a non-
/// maximized window that's just its current frame normalized; for a
/// maximized window it's the rect Windows would un-maximize it to.
/// </summary>
internal readonly record struct WindowInfo(
    WindowReference Reference,
    int MonitorId,
    RectangleF RelativeFrame,
    bool WasMaximized);

/// <summary>
/// What the system layer should apply. Same shape as WindowInfo but with
/// a different monitor target.
/// </summary>
internal readonly record struct WindowMove(
    WindowReference Reference,
    int TargetMonitorId,
    RectangleF TargetRelativeFrame,
    bool WasMaximized);

// MARK: - Core swap logic

internal static class Core
{
    /// <summary>
    /// Given a list of windows and a pair of monitors to swap, return the
    /// list of moves that swaps windows between those two monitors while
    /// preserving each window's relative position and size within its
    /// monitor.
    ///
    /// Windows on monitors that are not part of the swap pair are left
    /// alone (they don't appear in the returned moves at all). This is
    /// what makes three-or-more-monitor setups work: the caller picks two
    /// specific monitors to swap, and any other connected display is
    /// untouched.
    ///
    /// Pure function. No OS calls. Safe to unit-test. Mirrors
    /// computeSwapMoves on Mac line-for-line.
    /// </summary>
    public static List<WindowMove> ComputeSwapMoves(
        IList<WindowInfo> windows,
        (Monitor A, Monitor B) swapPair)
    {
        var a = swapPair.A.Id;
        var b = swapPair.B.Id;

        var moves = new List<WindowMove>();
        foreach (var window in windows)
        {
            int target;
            if (window.MonitorId == a) target = b;
            else if (window.MonitorId == b) target = a;
            else continue;  // window is on a monitor outside the swap pair; leave it

            moves.Add(new WindowMove(
                Reference: window.Reference,
                TargetMonitorId: target,
                TargetRelativeFrame: window.RelativeFrame,
                WasMaximized: window.WasMaximized
            ));
        }
        return moves;
    }
}

// MARK: - Shortcut

/// <summary>
/// A keyboard shortcut: a set of modifier flags plus one virtual-key code.
/// Mirrors Mac's Shortcut struct. The KeyCode is a Win32 VK_* value (see
/// KeyCodes below).
/// </summary>
internal readonly record struct Shortcut(Modifiers Modifiers, uint KeyCode)
{
    /// <summary>
    /// Human-readable form, e.g. "Ctrl+Alt+J" or "Ctrl+Shift+F12". Order
    /// follows the most common Windows docs convention: Ctrl, Alt, Shift,
    /// Win, then key.
    /// </summary>
    public string Description
    {
        get
        {
            var parts = new List<string>();
            if (Modifiers.HasFlag(Modifiers.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(Modifiers.Alt))     parts.Add("Alt");
            if (Modifiers.HasFlag(Modifiers.Shift))   parts.Add("Shift");
            if (Modifiers.HasFlag(Modifiers.Win))     parts.Add("Win");
            parts.Add(KeyCodes.NameFor(KeyCode) is { } name
                ? Capitalize(name)
                : $"Key({KeyCode})");
            return string.Join("+", parts);
        }
    }

    /// <summary>
    /// Parse a config-file shortcut string like "ctrl+alt+j" or
    /// "ctrl+shift+f12". Case-insensitive, whitespace-insensitive. Returns
    /// null on failure so callers can fall back to a default and log,
    /// rather than crash.
    /// </summary>
    public static Shortcut? Parse(string raw)
    {
        var tokens = raw
            .ToLowerInvariant()
            .Replace(" ", "")
            .Split('+', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return null;

        var mods = Modifiers.None;
        uint? keyCode = null;

        foreach (var token in tokens)
        {
            switch (token)
            {
                case "ctrl":
                case "control":
                    mods |= Modifiers.Control;
                    break;
                case "alt":
                    mods |= Modifiers.Alt;
                    break;
                case "shift":
                    mods |= Modifiers.Shift;
                    break;
                case "win":
                case "super":
                case "meta":
                    mods |= Modifiers.Win;
                    break;
                default:
                    if (KeyCodes.CodeFor(token) is { } code)
                    {
                        keyCode = code;
                    }
                    else
                    {
                        return null;
                    }
                    break;
            }
        }

        return keyCode is { } k ? new Shortcut(mods, k) : null;
    }

    /// <summary>
    /// Render in the lowercase, plus-separated form the parser expects.
    /// E.g. (Ctrl|Alt, VK_J) -> "ctrl+alt+j". The parser accepts synonyms
    /// on input, but we emit one canonical form on output.
    /// </summary>
    public string ToConfigString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(Modifiers.Control)) parts.Add("ctrl");
        if (Modifiers.HasFlag(Modifiers.Alt))     parts.Add("alt");
        if (Modifiers.HasFlag(Modifiers.Shift))   parts.Add("shift");
        if (Modifiers.HasFlag(Modifiers.Win))     parts.Add("win");
        parts.Add(KeyCodes.NameFor(KeyCode) ?? $"key{KeyCode}");
        return string.Join("+", parts);
    }

    private static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

// MARK: - Key codes

/// <summary>
/// Win32 virtual-key codes for the subset of keys useful as hotkey targets.
/// Values come from WinUser.h (VK_* constants); listed inline here so we
/// don't need a separate native-constants file.
/// </summary>
internal static class KeyCodes
{
    private static readonly Dictionary<string, uint> _nameToCode = new()
    {
        ["up"]     = 0x26,  // VK_UP
        ["down"]   = 0x28,  // VK_DOWN
        ["left"]   = 0x25,  // VK_LEFT
        ["right"]  = 0x27,  // VK_RIGHT
        ["space"]  = 0x20,  // VK_SPACE
        ["return"] = 0x0D,  // VK_RETURN
        ["enter"]  = 0x0D,
        ["tab"]    = 0x09,  // VK_TAB
        ["escape"] = 0x1B,  // VK_ESCAPE
        ["esc"]    = 0x1B,
        // F1..F12 are 0x70..0x7B contiguous.
        ["f1"]  = 0x70, ["f2"]  = 0x71, ["f3"]  = 0x72, ["f4"]  = 0x73,
        ["f5"]  = 0x74, ["f6"]  = 0x75, ["f7"]  = 0x76, ["f8"]  = 0x77,
        ["f9"]  = 0x78, ["f10"] = 0x79, ["f11"] = 0x7A, ["f12"] = 0x7B,
        // 'A'..'Z' are 0x41..0x5A; '0'..'9' are 0x30..0x39.
        ["a"] = 0x41, ["b"] = 0x42, ["c"] = 0x43, ["d"] = 0x44, ["e"] = 0x45,
        ["f"] = 0x46, ["g"] = 0x47, ["h"] = 0x48, ["i"] = 0x49, ["j"] = 0x4A,
        ["k"] = 0x4B, ["l"] = 0x4C, ["m"] = 0x4D, ["n"] = 0x4E, ["o"] = 0x4F,
        ["p"] = 0x50, ["q"] = 0x51, ["r"] = 0x52, ["s"] = 0x53, ["t"] = 0x54,
        ["u"] = 0x55, ["v"] = 0x56, ["w"] = 0x57, ["x"] = 0x58, ["y"] = 0x59,
        ["z"] = 0x5A,
        ["0"] = 0x30, ["1"] = 0x31, ["2"] = 0x32, ["3"] = 0x33, ["4"] = 0x34,
        ["5"] = 0x35, ["6"] = 0x36, ["7"] = 0x37, ["8"] = 0x38, ["9"] = 0x39,
    };

    public static uint? CodeFor(string name) =>
        _nameToCode.TryGetValue(name, out var c) ? c : null;

    /// <summary>
    /// Reverse lookup: given a VK_* code, return the canonical lowercase name
    /// ("up", "f1", "j"). When multiple names map to the same code (Return
    /// vs Enter), this returns whichever appears first in the dictionary,
    /// which is fine for display.
    /// </summary>
    public static string? NameFor(uint code) =>
        _nameToCode.FirstOrDefault(kv => kv.Value == code).Key;
}

// MARK: - Config

/// <summary>
/// User-tweakable settings. Single source of truth for default values is
/// Config.Default; the on-disk template is rendered from there, so the
/// values can never drift between code and template.
/// </summary>
internal sealed record Config(
    Shortcut Shortcut,
    bool Blink,
    int BlinkSettleMs)
{
    /// <summary>
    /// Canonical defaults. The shortcut was picked through actual ergonomic
    /// constraints (left-hand single-hand reachable, no Windows-shell
    /// collisions, no AltGr special-character collision on Belgian
    /// layouts). On layouts with AltGr, AltGr+J is identical to Ctrl+Alt+J
    /// because AltGr emits Ctrl+Alt internally.
    /// </summary>
    public static readonly Config Default = new(
        Shortcut: new Shortcut(Modifiers.Control | Modifiers.Alt, KeyCodes.CodeFor("j")!.Value),
        Blink: true,
        // Settle time of 500ms is the Windows-tuned default. The Mac
        // version uses 10ms because AX window moves are near-instant
        // there; on Windows, SetWindowPlacement on apps that draw their
        // own chrome (Brave, Electron, PrusaSlicer) is synchronous and
        // each call can take 100-300ms, with the last few windows in a
        // batch sometimes finishing well after the apply loop returned.
        // 500ms covers a typical mixed window set without leaving the
        // black overlay up so long that it feels broken; users on
        // lighter setups can drop to 100-200ms via the menu, heavier
        // setups can dial up to 600ms or beyond via the config file.
        BlinkSettleMs: 500
    );
}

// MARK: - Config loading

/// <summary>
/// Loads and persists the user's config from a tiny INI file under
/// %APPDATA%\JASS\config.ini.
///
/// Same shape as the Mac ConfigLoader: minimal INI, unknown keys logged and
/// ignored, invalid known values fall back to defaults with a logged
/// reason. Single source of truth: Config.Default. The default file is
/// rendered from that struct; there is no parallel hand-maintained literal.
/// </summary>
internal static class ConfigLoader
{
    public static string FilePath { get; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JASS",
            "config.ini"
        );

    /// <summary>
    /// Header comment block included at the top of freshly-rendered config
    /// files. Pure documentation, no values; values are filled in by
    /// RewriteKnownKeys on top of placeholder lines added below the header.
    /// </summary>
    private const string FileHeader = """
        # JustAScreenSwitcher (JASS) configuration
        #
        # Format:  key = value
        # Lines starting with '#' are ignored.
        #
        # shortcut: the global hotkey that swaps windows between your two screens.
        # Use any combination of modifiers (ctrl, alt, shift, win) with a key.
        # Examples: ctrl+alt+j, ctrl+shift+f12, alt+f12
        #
        # blink: briefly fade the screen to black during the swap, to hide the
        # motion of many windows moving at once. Values: yes or no.
        #
        # blink_settle_ms: how long (in milliseconds) to hold the black overlay
        # after window moves have been issued, before fading back to visible.
        # Apps often animate their own window movement for a couple hundred ms
        # after JASS has told them where to go; this pause lets those animations
        # finish under the cover. Raise if your screen uncovers too early, lower
        # for a snappier feel. Typical range: 50 to 400.
        #
        # After editing this file, choose "Reload config" from the JASS tray icon
        # (or quit and relaunch JASS).

        """;

    // The set of keys this version of JASS understands. New versions can
    // extend this without breaking older config files (parser ignores
    // unknown keys with a log line, never errors).
    private static readonly string[] _knownKeys =
        { "shortcut", "blink", "blink_settle_ms" };

    /// <summary>
    /// Loads the config, creating a default file on disk if none exists.
    /// Invalid values fall back to defaults, with each fallback logged so
    /// a user can see why their setting did not stick.
    /// </summary>
    public static Config LoadOrCreate()
    {
        EnsureFileExists();
        try
        {
            var text = File.ReadAllText(FilePath, Encoding.UTF8);
            return Parse(text);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read config at {FilePath}: {ex.Message}. Using defaults.");
            return Config.Default;
        }
    }

    private static void EnsureFileExists()
    {
        if (File.Exists(FilePath)) return;

        var dir = Path.GetDirectoryName(FilePath);
        if (string.IsNullOrEmpty(dir)) return;

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not create config directory at {dir}: {ex.Message}");
            return;
        }

        try
        {
            File.WriteAllText(FilePath, RenderFreshFile(Config.Default), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not write default config at {FilePath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses the INI text. Unknown keys are ignored silently for forward
    /// compatibility; known keys with bad values keep the default and log
    /// a warning.
    /// </summary>
    public static Config Parse(string text)
    {
        var shortcut = Config.Default.Shortcut;
        var blink = Config.Default.Blink;
        var blinkSettleMs = Config.Default.BlinkSettleMs;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';') || line.StartsWith('['))
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq < 0)
            {
                Log.Warn($"Config: ignoring malformed line (no '='): {line}");
                continue;
            }

            var key = line[..eq].Trim().ToLowerInvariant();
            var value = line[(eq + 1)..].Trim();

            switch (key)
            {
                case "shortcut":
                    if (Shortcut.Parse(value) is { } s)
                    {
                        shortcut = s;
                    }
                    else
                    {
                        Log.Warn($"Config: invalid shortcut '{value}'; keeping default {shortcut.Description}");
                    }
                    break;

                case "blink":
                    var v = value.ToLowerInvariant();
                    if (v is "yes" or "true" or "on" or "1")
                    {
                        blink = true;
                    }
                    else if (v is "no" or "false" or "off" or "0")
                    {
                        blink = false;
                    }
                    else
                    {
                        Log.Warn($"Config: invalid blink value '{value}'; expected yes or no");
                    }
                    break;

                case "blink_settle_ms":
                    if (int.TryParse(value, out var ms) && ms is >= 0 and <= 2000)
                    {
                        blinkSettleMs = ms;
                    }
                    else
                    {
                        Log.Warn($"Config: invalid blink_settle_ms '{value}'; expected an integer between 0 and 2000");
                    }
                    break;

                default:
                    Log.Info($"Config: unknown key '{key}' ignored");
                    break;
            }
        }

        return new Config(shortcut, blink, blinkSettleMs);
    }

    /// <summary>
    /// Writes the current config to disk. If a file already exists, only
    /// known-key lines are rewritten; comments, blank lines, and unknown
    /// keys are preserved verbatim. If the file does not exist, a fresh
    /// commented file is rendered from Config.Default and then patched
    /// with the requested values.
    /// </summary>
    public static void Save(Config config)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                Log.Warn($"Config save: could not create directory at {dir}: {ex.Message}");
                return;
            }
        }

        string output;
        if (File.Exists(FilePath))
        {
            try
            {
                var existing = File.ReadAllText(FilePath, Encoding.UTF8);
                output = RewriteKnownKeys(existing, config);
            }
            catch (Exception ex)
            {
                Log.Warn($"Config save: could not read existing file at {FilePath}: {ex.Message}. Writing fresh.");
                output = RenderFreshFile(config);
            }
        }
        else
        {
            output = RenderFreshFile(config);
        }

        try
        {
            File.WriteAllText(FilePath, output, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Log.Warn($"Config save: could not write {FilePath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Rewrites only the value portion of lines that match known keys.
    /// Comments, blank lines, unknown keys and whitespace are preserved.
    /// Keys not present in the file get appended at the end.
    /// </summary>
    private static string RewriteKnownKeys(string text, Config config)
    {
        var seen = new HashSet<string>();
        var lines = new List<string>();

        foreach (var rawLine in text.Split('\n'))
        {
            // Preserve trailing \r if the original file had CRLF endings;
            // Notepad on Windows defaults to CRLF and we don't want to flip
            // line endings under the user every time we save.
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith(';') || trimmed.StartsWith('['))
            {
                lines.Add(line);
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq < 0)
            {
                lines.Add(line);
                continue;
            }

            var key = line[..eq].Trim().ToLowerInvariant();
            if (ReplacementLine(key, config) is { } replacement)
            {
                lines.Add(replacement);
                seen.Add(key);
            }
            else
            {
                lines.Add(line);
            }
        }

        // Append any known keys missing from the existing content.
        var appended = new List<string>();
        foreach (var key in _knownKeys)
        {
            if (!seen.Contains(key) && ReplacementLine(key, config) is { } line)
            {
                appended.Add(line);
            }
        }
        if (appended.Count > 0)
        {
            // Separate appended keys from prior content with one blank line,
            // unless the file already ends with a blank line.
            if (lines.Count == 0 || lines[^1].Length != 0)
            {
                lines.Add("");
            }
            lines.AddRange(appended);
        }

        return string.Join("\n", lines);
    }

    private static string? ReplacementLine(string key, Config config) =>
        key switch
        {
            "shortcut"        => $"shortcut = {config.Shortcut.ToConfigString()}",
            "blink"           => $"blink = {(config.Blink ? "yes" : "no")}",
            "blink_settle_ms" => $"blink_settle_ms = {config.BlinkSettleMs}",
            _                 => null,
        };

    /// <summary>
    /// Builds a fresh, commented file containing every known key with
    /// values taken from `config`. The placeholder lines below the header
    /// have empty values that RewriteKnownKeys then overwrites with the
    /// real values, keeping the rendering logic in one place.
    /// </summary>
    private static string RenderFreshFile(Config config)
    {
        var placeholders = string.Join("\n", _knownKeys.Select(k => $"{k} ="));
        var withPlaceholders = FileHeader + placeholders + "\n";
        return RewriteKnownKeys(withPlaceholders, config);
    }
}

// MARK: - Application entry point

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        Application.Run(new JassApp());
    }
}

// MARK: - Tray app orchestration

/// <summary>
/// The tray-resident application. Owns the tray icon, holds the current
/// config, and is responsible for keeping the menu in sync with config
/// changes. Mirrors JASSApp on Mac.
///
/// All Win32 plumbing (NotifyIcon, MessageBox, Process.Start) is delegated
/// to system.cs; this class only knows about Config and the menu shape.
/// </summary>
internal sealed class JassApp : ApplicationContext
{
    /// <summary>
    /// Presets for the "Settle time" submenu. Windows-tuned: app-driven
    /// re-layout after our move calls regularly takes 200-400ms on
    /// heavy apps, so the presets start at 100ms and go up to 600ms.
    /// Any value outside this list is shown as "Custom: N ms" and can
    /// still be set via the config file directly (max 2000ms there).
    /// </summary>
    private static readonly int[] _settlePresetsMs = { 100, 200, 300, 400, 500, 600 };

    private readonly TrayIcon _tray;
    private readonly HotkeyManager _hotkey;
    private readonly BlinkOverlay _blink;
    private Config _config;

    public JassApp()
    {
        Log.Info("JASS starting up");

        _config = ConfigLoader.LoadOrCreate();
        Log.Info($"Config loaded: shortcut={_config.Shortcut.Description} blink={_config.Blink} settle={_config.BlinkSettleMs}ms");

        _tray = new TrayIcon(BuildMenu);
        _hotkey = new HotkeyManager(SwapNow);
        _blink = new BlinkOverlay();
        RegisterConfiguredHotkey();
    }

    /// <summary>
    /// (Re)registers the global hotkey based on the current config. If the
    /// shortcut is already taken by another app or the OS, the registration
    /// fails; we log and warn the user via an alert, then leave the hotkey
    /// unregistered. The user can pick a different combo via the menu (or
    /// edit the config file and reload).
    ///
    /// Called from the constructor and from ReloadConfig. Not called from
    /// ToggleBlink or PickSettlePreset because those don't change the
    /// shortcut.
    /// </summary>
    private void RegisterConfiguredHotkey()
    {
        if (_hotkey.Register(_config.Shortcut))
        {
            Log.Info($"Registered hotkey {_config.Shortcut.Description}");
        }
        else
        {
            Log.Error($"Failed to register hotkey {_config.Shortcut.Description}; likely taken by another app or the OS");
            SystemActions.ShowAlert(
                "Could not register shortcut",
                $"JASS could not register {_config.Shortcut.Description}. " +
                "Another app or Windows itself may already be using it. " +
                "Pick a different combination via the menu, or edit the " +
                "config file and reload."
            );
        }
    }

    /// <summary>
    /// Build the entire menu from scratch, reflecting the current config.
    /// Called every time the user opens the tray menu, so the single
    /// _config field is enough to drive every label and checkmark; no menu
    /// state needs caching.
    /// </summary>
    private void BuildMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();

        // Shortcut: parent item shows current shortcut, with "(or AltGr+J)"
        // hint only while the shortcut still equals the hardcoded default.
        // Once the user records a custom one, we display only what we
        // actually registered (no AltGr translation guessing).
        var shortcutLabel = _config.Shortcut == Config.Default.Shortcut
            ? $"Shortcut: {_config.Shortcut.Description} (or AltGr+J)"
            : $"Shortcut: {_config.Shortcut.Description}";
        var shortcutItem = new ToolStripMenuItem(shortcutLabel);
        var changeItem = new ToolStripMenuItem("Change...");
        changeItem.Click += (_, _) => PromptForNewShortcut();
        shortcutItem.DropDownItems.Add(changeItem);
        menu.Items.Add(shortcutItem);

        // Blink toggle.
        var blinkItem = new ToolStripMenuItem("Blink effect")
        {
            Checked = _config.Blink,
        };
        blinkItem.Click += (_, _) => ToggleBlink();
        menu.Items.Add(blinkItem);

        // Settle time submenu: presets plus a read-only Custom row when
        // the current value doesn't match any preset (set via config file).
        var settleItem = new ToolStripMenuItem($"Settle time: {_config.BlinkSettleMs} ms");
        foreach (var ms in _settlePresetsMs)
        {
            var entry = new ToolStripMenuItem($"{ms} ms")
            {
                Checked = _config.BlinkSettleMs == ms,
                Tag = ms,
            };
            entry.Click += (s, _) =>
            {
                if (s is ToolStripMenuItem mi && mi.Tag is int picked)
                {
                    PickSettlePreset(picked);
                }
            };
            settleItem.DropDownItems.Add(entry);
        }
        if (!_settlePresetsMs.Contains(_config.BlinkSettleMs))
        {
            settleItem.DropDownItems.Add(new ToolStripSeparator());
            var custom = new ToolStripMenuItem($"Custom: {_config.BlinkSettleMs} ms")
            {
                Checked = true,
                Enabled = false,
            };
            settleItem.DropDownItems.Add(custom);
        }
        menu.Items.Add(settleItem);

        menu.Items.Add(new ToolStripSeparator());

        var swapItem = new ToolStripMenuItem("Swap screens now");
        swapItem.Click += (_, _) => SwapNow();
        menu.Items.Add(swapItem);

        menu.Items.Add(new ToolStripSeparator());

        var editItem = new ToolStripMenuItem("Edit config file");
        editItem.Click += (_, _) => OpenConfigFile();
        menu.Items.Add(editItem);

        var reloadItem = new ToolStripMenuItem("Reload config");
        reloadItem.Click += (_, _) => ReloadConfig();
        menu.Items.Add(reloadItem);

        menu.Items.Add(new ToolStripSeparator());

        var aboutItem = new ToolStripMenuItem("About JASS");
        aboutItem.Click += (_, _) => ShowAbout();
        menu.Items.Add(aboutItem);

        var quitItem = new ToolStripMenuItem("Quit JASS");
        quitItem.Click += (_, _) => Quit();
        menu.Items.Add(quitItem);
    }

    // MARK: Menu actions

    private void ToggleBlink()
    {
        _config = _config with { Blink = !_config.Blink };
        ConfigLoader.Save(_config);
    }

    private void PickSettlePreset(int ms)
    {
        if (ms is < 0 or > 2000) return;
        _config = _config with { BlinkSettleMs = ms };
        ConfigLoader.Save(_config);
    }

    private void PromptForNewShortcut()
    {
        // The shortcut recorder dialog lands in stage 7. Stub for now so
        // the menu item is wired and visible.
        SystemActions.ShowAlert(
            "Not implemented yet",
            "The shortcut recorder will be added in a later step. " +
            "For now, edit %APPDATA%\\JASS\\config.ini directly and use Reload config."
        );
    }

    private bool _swapInProgress;

    private void SwapNow()
    {
        // Re-entrance guard. WM_HOTKEY runs on the UI thread, the same
        // thread that pumps modal alerts. Without this, a user spamming
        // the hotkey while an alert is up would queue up nested SwapNow
        // calls. The guard keeps the swap a strictly serial operation.
        if (_swapInProgress) return;
        _swapInProgress = true;
        try
        {
            var monitors = SystemMonitors.EnumerateAll();

            // DIAGNOSTIC (stage 5b): dump the monitor layout and chosen
            // swap pair into the log every time a swap is requested.
            Log.Info($"swap: {monitors.Count} monitor(s) detected");
            foreach (var m in monitors)
            {
                Log.Info($"  monitor #{m.Id}: {m.Frame.Width}x{m.Frame.Height} at ({m.Frame.X},{m.Frame.Y})");
            }

            // Case 1: zero or one monitor. Nothing meaningful to swap.
            if (monitors.Count < 2)
            {
                Log.Warn($"Swap requested with {monitors.Count} monitor(s); need at least 2");
                SystemActions.ShowAlert(
                    "JASS needs at least 2 monitors",
                    $"JASS can only swap windows when at least two displays are connected. " +
                    $"JASS sees {monitors.Count} right now."
                );
                return;
            }

            // Pick the two monitors to swap between. With exactly two we
            // always pair those two, ignoring cursor position; that
            // matches the Mac behaviour and avoids ambiguity. With three
            // or more we use the cursor to pick the partner.
            (Monitor A, Monitor B) pair;
            if (monitors.Count == 2)
            {
                pair = (monitors[0], monitors[1]);
            }
            else
            {
                if (PickSwapPairFromCursor(monitors) is { } resolved)
                {
                    pair = resolved;
                }
                else
                {
                    return;  // PickSwapPairFromCursor already showed user feedback
                }
            }

            var windows = SystemWindows.EnumerateAll(monitors);
            var moves = Core.ComputeSwapMoves(windows, pair);

            // Pass only the swap pair to Apply, not all monitors. Apply
            // uses this list to validate that the target monitors are
            // still connected; an external monitor we're not touching
            // shouldn't affect that validation.
            var monitorsForApply = new[] { pair.A, pair.B };

            // The apply loop on Windows is slow for apps that draw
            // their own chrome (Brave, PrusaSlicer) because we have to
            // un-maximize, move, re-maximize them — each step waits for
            // the target process to ack. Total per-swap time is
            // commonly a few seconds with 10+ such windows. The blink
            // overlay hides this entirely: the user sees a brief fade
            // to black, and when it lifts, every window is in its new
            // position. The settle time is included in the blink hold
            // so apps' own animations finish under the cover too.
            //
            // For users who prefer to see the motion (or whose apps
            // are all native Win32 and therefore swap fast), the blink
            // can be turned off in the menu; we then run the apply
            // loop directly.
            SystemWindows.MoveReport report = default;
            if (_config.Blink)
            {
                _blink.Run(monitors, _config.BlinkSettleMs, () =>
                {
                    report = SystemWindows.Apply(moves, monitorsForApply);
                });
            }
            else
            {
                report = SystemWindows.Apply(moves, monitorsForApply);
            }
            HandleReport(report);
        }
        finally
        {
            _swapInProgress = false;
        }
    }

    /// <summary>
    /// Pair the anchor (primary) monitor with whichever monitor the cursor
    /// is currently on. Shows guidance and returns null when the cursor
    /// lands on the anchor itself, since "swap with where my cursor is"
    /// has no meaning there. Used in 3+ monitor setups; not called for
    /// exactly two monitors (SwapNow short-circuits that case).
    /// </summary>
    private static (Monitor A, Monitor B)? PickSwapPairFromCursor(IList<Monitor> monitors)
    {
        var anchor = SystemMonitors.AnchorMonitor(monitors);
        if (anchor is not { } a)
        {
            Log.Warn("Could not determine anchor monitor; aborting swap");
            return null;
        }
        var cursorMonitor = SystemMonitors.MonitorUnderCursor(monitors);
        if (cursorMonitor is not { } c)
        {
            Log.Warn("Cursor is not on any known monitor; aborting swap");
            SystemActions.ShowAlert(
                "JASS could not determine target screen",
                "Move the cursor onto the external display you want to " +
                "swap with, then press the shortcut again."
            );
            return null;
        }
        if (c.Id == a.Id)
        {
            Log.Info("Cursor is on the anchor display; explaining multi-monitor flow to user");
            SystemActions.ShowAlert(
                "Choose the screen to swap with",
                "With more than two displays connected, JASS swaps your " +
                "primary screen with whichever external screen your cursor is on.\n\n" +
                "Move the cursor onto the external screen you want to swap " +
                "with, then press the shortcut again."
            );
            return null;
        }
        return (a, c);
    }

    /// <summary>
    /// Log a one-line summary of the swap. Show an alert only when the
    /// outcome is bad enough that the user deserves to know: windows
    /// stuck in an unexpected state, or a majority of moves failed.
    /// </summary>
    private static void HandleReport(SystemWindows.MoveReport report)
    {
        Log.Info($"Swap report: attempted={report.Attempted} succeeded={report.Succeeded} rolledBack={report.RolledBack} stuck={report.Stuck} skipped={report.Skipped}");

        if (report.Stuck > 0)
        {
            SystemActions.ShowAlert(
                "Some windows may be in a wrong position",
                $"{report.Stuck} window(s) could not be moved and could not be " +
                "restored. Check the affected windows manually. See the log at " +
                $"{Log.FilePath} for details."
            );
            return;
        }

        // If most moves failed cleanly (rolled back), tell the user but
        // less alarmingly. This is also where elevated-process failures
        // surface: SetWindowPlacement returning false for an admin window
        // counts as RolledBack.
        var failed = report.RolledBack + report.Skipped;
        if (report.Attempted > 0 && failed * 2 >= report.Attempted)
        {
            SystemActions.ShowAlert(
                "Swap partially failed",
                $"{failed} of {report.Attempted} window(s) could not be moved. " +
                "They were left in their original position. Common causes: " +
                "windows of apps running as administrator (which a non-elevated " +
                $"JASS cannot touch). See {Log.FilePath} for details."
            );
        }
    }

    private void OpenConfigFile()
    {
        // .ini files have no default app association on a stock Windows
        // install (the .ini extension is internally associated with
        // configuration files but is not user-openable through the shell).
        // We launch Notepad explicitly because it ships with every Windows
        // install since forever. If the user has a preferred editor, they
        // can still open the file from Explorer.
        var path = ConfigLoader.FilePath;
        if (!File.Exists(path))
        {
            // Loading creates the file as a side effect, so this is
            // belt-and-braces in case the file was deleted between launches.
            ConfigLoader.LoadOrCreate();
        }

        try
        {
            // UseShellExecute=false so we control the executable directly;
            // shell-execute on a .ini path would either fail (no handler)
            // or open something unexpected.
            Process.Start(new ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open config in Notepad: {ex.Message}. Falling back to Explorer reveal.");
            try
            {
                // /select highlights the file inside its parent folder.
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = false,
                });
            }
            catch (Exception ex2)
            {
                Log.Warn($"Could not reveal config in Explorer either: {ex2.Message}");
            }
        }
    }

    private void ReloadConfig()
    {
        var previousShortcut = _config.Shortcut;
        _config = ConfigLoader.LoadOrCreate();
        Log.Info($"Config reloaded: shortcut={_config.Shortcut.Description} blink={_config.Blink} settle={_config.BlinkSettleMs}ms");

        // Only re-register the hotkey if the shortcut actually changed.
        // Re-registering an unchanged shortcut would briefly leave the
        // hotkey unbound (Unregister + Register), creating a small window
        // where pressing the combo does nothing.
        if (_config.Shortcut != previousShortcut)
        {
            RegisterConfiguredHotkey();
        }
    }

    private void ShowAbout()
    {
        // Default-shortcut display rule: same as the menu (show AltGr hint
        // only while the user is on the canonical default).
        var shortcutLine = _config.Shortcut == Config.Default.Shortcut
            ? $"Current shortcut: {_config.Shortcut.Description} (or AltGr+J)"
            : $"Current shortcut: {_config.Shortcut.Description}";

        SystemActions.ShowAlert(
            "JustAScreenSwitcher",
            "Swap all your windows between two monitors with a single shortcut.\n\n" +
            shortcutLine + "\n\n" +
            "Built by Orville BV."
        );
    }

    private void Quit()
    {
        Log.Info("JASS quitting");
        _hotkey.Dispose();
        _blink.Dispose();
        _tray.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hotkey.Dispose();
            _blink.Dispose();
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
