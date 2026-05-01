//
//  app.swift
//  JustAScreenSwitcher (JASS)
//
//  Responsibilities:
//    - Domain types (Config, Shortcut, Monitor, WindowInfo, WindowMove)
//    - Pure core logic (computeSwapMoves)
//    - Config loading from disk
//    - Application entry point and orchestration (AppDelegate)
//
//  OS-specific calls live in system.swift.
//

import AppKit
import Carbon.HIToolbox

// MARK: - Domain types

/// A monitor described in AX coordinate space (origin top-left of primary
/// screen, Y axis pointing down).
struct Monitor: Equatable {
    let id: Int
    let frame: CGRect
}

/// Opaque reference to a window, used by the system layer to identify and
/// move it. The core never inspects this.
struct WindowReference {
    let axElement: AXUIElement
    let pid: pid_t
    let index: Int
}

/// Input to the core: what the system layer observed for a window.
struct WindowInfo {
    let reference: WindowReference
    let monitorId: Int
    /// Position and size as fractions of the current monitor's frame (0..1).
    let relativeFrame: CGRect
}

/// Output from the core: what the system layer should apply.
struct WindowMove {
    let reference: WindowReference
    let targetMonitorId: Int
    let targetRelativeFrame: CGRect
}

// MARK: - Configuration

struct Config {
    var shortcut: Shortcut
    var blink: Bool
    /// Milliseconds to hold the overlay in place after window moves are
    /// done, to let apps finish any in-flight animations before revealing.
    var blinkSettleMs: Int

    static let defaultConfig = Config(
        shortcut: Shortcut(modifiers: [.option], keyCode: UInt32(kVK_UpArrow)),
        blink: true,
        blinkSettleMs: 10
    )
}

struct Modifiers: OptionSet, Equatable {
    let rawValue: UInt32
    static let command = Modifiers(rawValue: UInt32(cmdKey))
    static let option  = Modifiers(rawValue: UInt32(optionKey))
    static let control = Modifiers(rawValue: UInt32(controlKey))
    static let shift   = Modifiers(rawValue: UInt32(shiftKey))
}

struct Shortcut: Equatable {
    let modifiers: Modifiers
    let keyCode: UInt32

    /// Human-readable form, e.g. "Cmd+Up" or "Option+F12". Uses Mac
    /// conventions: "Option" (not "Alt"), "Cmd" (not "Command").
    var description: String {
        var parts: [String] = []
        if modifiers.contains(.control) { parts.append("Ctrl") }
        if modifiers.contains(.option)  { parts.append("Option") }
        if modifiers.contains(.shift)   { parts.append("Shift") }
        if modifiers.contains(.command) { parts.append("Cmd") }
        if let name = KeyCodes.name(for: keyCode) {
            parts.append(name.capitalized)
        } else {
            parts.append("Key(\(keyCode))")
        }
        return parts.joined(separator: "+")
    }

    /// Parse a user-facing shortcut string like "cmd+up" or "ctrl+shift+f12".
    /// Case-insensitive, whitespace-insensitive. Returns nil on failure so
    /// callers can fall back to a default without crashing.
    static func parse(_ raw: String) -> Shortcut? {
        let tokens = raw
            .lowercased()
            .replacingOccurrences(of: " ", with: "")
            .split(separator: "+")
            .map(String.init)
        guard !tokens.isEmpty else { return nil }

        var modifiers: Modifiers = []
        var keyCode: UInt32? = nil

        for token in tokens {
            switch token {
            case "cmd", "command", "meta":
                modifiers.insert(.command)
            case "ctrl", "control":
                modifiers.insert(.control)
            case "alt", "option", "opt":
                modifiers.insert(.option)
            case "shift":
                modifiers.insert(.shift)
            default:
                guard let code = KeyCodes.code(for: token) else { return nil }
                keyCode = code
            }
        }

        guard let finalKey = keyCode else { return nil }
        return Shortcut(modifiers: modifiers, keyCode: finalKey)
    }
}

/// Carbon virtual key codes for the subset of keys useful as shortcut targets.
/// The kVK_* constants come from HIToolbox/Events.h (imported via Carbon).
enum KeyCodes {
    static let nameToCode: [String: UInt32] = [
        "up":     UInt32(kVK_UpArrow),
        "down":   UInt32(kVK_DownArrow),
        "left":   UInt32(kVK_LeftArrow),
        "right":  UInt32(kVK_RightArrow),
        "space":  UInt32(kVK_Space),
        "return": UInt32(kVK_Return),
        "enter":  UInt32(kVK_Return),
        "tab":    UInt32(kVK_Tab),
        "escape": UInt32(kVK_Escape),
        "esc":    UInt32(kVK_Escape),
        "f1":  UInt32(kVK_F1),  "f2":  UInt32(kVK_F2),
        "f3":  UInt32(kVK_F3),  "f4":  UInt32(kVK_F4),
        "f5":  UInt32(kVK_F5),  "f6":  UInt32(kVK_F6),
        "f7":  UInt32(kVK_F7),  "f8":  UInt32(kVK_F8),
        "f9":  UInt32(kVK_F9),  "f10": UInt32(kVK_F10),
        "f11": UInt32(kVK_F11), "f12": UInt32(kVK_F12),
        "a": UInt32(kVK_ANSI_A), "b": UInt32(kVK_ANSI_B),
        "c": UInt32(kVK_ANSI_C), "d": UInt32(kVK_ANSI_D),
        "e": UInt32(kVK_ANSI_E), "f": UInt32(kVK_ANSI_F),
        "g": UInt32(kVK_ANSI_G), "h": UInt32(kVK_ANSI_H),
        "i": UInt32(kVK_ANSI_I), "j": UInt32(kVK_ANSI_J),
        "k": UInt32(kVK_ANSI_K), "l": UInt32(kVK_ANSI_L),
        "m": UInt32(kVK_ANSI_M), "n": UInt32(kVK_ANSI_N),
        "o": UInt32(kVK_ANSI_O), "p": UInt32(kVK_ANSI_P),
        "q": UInt32(kVK_ANSI_Q), "r": UInt32(kVK_ANSI_R),
        "s": UInt32(kVK_ANSI_S), "t": UInt32(kVK_ANSI_T),
        "u": UInt32(kVK_ANSI_U), "v": UInt32(kVK_ANSI_V),
        "w": UInt32(kVK_ANSI_W), "x": UInt32(kVK_ANSI_X),
        "y": UInt32(kVK_ANSI_Y), "z": UInt32(kVK_ANSI_Z),
        "0": UInt32(kVK_ANSI_0), "1": UInt32(kVK_ANSI_1),
        "2": UInt32(kVK_ANSI_2), "3": UInt32(kVK_ANSI_3),
        "4": UInt32(kVK_ANSI_4), "5": UInt32(kVK_ANSI_5),
        "6": UInt32(kVK_ANSI_6), "7": UInt32(kVK_ANSI_7),
        "8": UInt32(kVK_ANSI_8), "9": UInt32(kVK_ANSI_9),
    ]

    static func code(for name: String) -> UInt32? { nameToCode[name] }

    static func name(for code: UInt32) -> String? {
        nameToCode.first(where: { $0.value == code })?.key
    }
}

// MARK: - Config loading

/// Loads and persists the user's config from a tiny INI file under
/// ~/Library/Application Support/JASS/config.ini.
///
/// Kept minimal on purpose: one key (shortcut) in v1. The format is friendly
/// to future additions (new keys just get added below).
enum ConfigLoader {
    static let supportDirectoryName = "JASS"
    static let fileName = "config.ini"

    static var fileURL: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
        return base.appendingPathComponent(supportDirectoryName).appendingPathComponent(fileName)
    }

    /// Loads the config, creating a default file on disk if none exists.
    /// Invalid values fall back to defaults, but every failure is logged
    /// so the user can see why JASS is using a different value than they
    /// wrote.
    @discardableResult
    static func loadOrCreate() -> Config {
        ensureFileExists()
        let url = fileURL
        do {
            let text = try String(contentsOf: url, encoding: .utf8)
            return parse(text)
        } catch {
            Log.warn("Could not read config at \(url.path): \(error.localizedDescription). Using defaults.")
            return .defaultConfig
        }
    }

    /// Creates the support directory and default config file if they don't
    /// exist. Any failure is logged; the app can still run from defaults
    /// even without a persisted file.
    private static func ensureFileExists() {
        let url = fileURL
        guard !FileManager.default.fileExists(atPath: url.path) else { return }

        let dir = url.deletingLastPathComponent()
        do {
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        } catch {
            Log.warn("Could not create config directory at \(dir.path): \(error.localizedDescription)")
            return
        }

        do {
            try defaultFileContents.write(to: url, atomically: true, encoding: .utf8)
        } catch {
            Log.warn("Could not write default config at \(url.path): \(error.localizedDescription)")
        }
    }

    private static let defaultFileContents = """
    # JustAScreenSwitcher (JASS) configuration
    #
    # Format:  key = value
    # Lines starting with '#' are ignored.
    #
    # shortcut: the global hotkey that swaps windows between your two screens.
    # Use any combination of modifiers (cmd, ctrl, option, shift) with a key.
    # Examples: option+up, ctrl+option+s, cmd+shift+f12
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
    # After editing this file, choose "Reload config" from the JASS menu bar
    # icon (or quit and relaunch JASS).

    shortcut = option+up
    blink = yes
    blink_settle_ms = 10
    """

    /// Parses the INI text. Unknown keys are ignored silently (forward
    /// compatibility), but known keys with invalid values are logged so
    /// the user can tell why their setting didn't take effect.
    static func parse(_ text: String) -> Config {
        var config = Config.defaultConfig
        for rawLine in text.split(separator: "\n", omittingEmptySubsequences: false) {
            let line = rawLine.trimmingCharacters(in: .whitespaces)
            if line.isEmpty || line.hasPrefix("#") || line.hasPrefix(";") || line.hasPrefix("[") {
                continue
            }
            guard let eq = line.firstIndex(of: "=") else {
                Log.warn("Config: ignoring malformed line (no '='): \(line)")
                continue
            }
            let key   = line[..<eq].trimmingCharacters(in: .whitespaces).lowercased()
            let value = line[line.index(after: eq)...].trimmingCharacters(in: .whitespaces)

            switch key {
            case "shortcut":
                if let s = Shortcut.parse(value) {
                    config.shortcut = s
                } else {
                    Log.warn("Config: invalid shortcut '\(value)'; keeping default \(config.shortcut.description)")
                }
            case "blink":
                let v = value.lowercased()
                if ["yes", "true", "on", "1"].contains(v) {
                    config.blink = true
                } else if ["no", "false", "off", "0"].contains(v) {
                    config.blink = false
                } else {
                    Log.warn("Config: invalid blink value '\(value)'; expected yes or no")
                }
            case "blink_settle_ms":
                if let ms = Int(value), ms >= 0, ms <= 2000 {
                    config.blinkSettleMs = ms
                } else {
                    Log.warn("Config: invalid blink_settle_ms '\(value)'; expected an integer between 0 and 2000")
                }
            default:
                Log.info("Config: unknown key '\(key)' ignored")
            }
        }
        return config
    }

    /// Writes the current config to disk. Called when the user changes a
    /// setting via the menu, so their choice survives the next launch.
    ///
    /// Strategy: if the file already exists, we read it and rewrite only the
    /// lines that match known keys, preserving comments and user formatting.
    /// If the file doesn't exist, we write a fresh default-shaped file with
    /// the current values substituted in.
    static func save(_ config: Config) {
        let url = fileURL
        do {
            try FileManager.default.createDirectory(
                at: url.deletingLastPathComponent(),
                withIntermediateDirectories: true
            )
        } catch {
            Log.warn("Config save: could not create directory at \(url.deletingLastPathComponent().path): \(error.localizedDescription)")
            return
        }

        let output: String
        if let existing = try? String(contentsOf: url, encoding: .utf8) {
            output = rewriteKnownKeys(in: existing, with: config)
        } else {
            output = renderFreshFile(with: config)
        }

        do {
            try output.write(to: url, atomically: true, encoding: .utf8)
        } catch {
            Log.warn("Config save: could not write \(url.path): \(error.localizedDescription)")
        }
    }

    /// Rewrites only the value portion of lines that match known keys,
    /// leaving everything else (comments, blank lines, unknown keys,
    /// whitespace) untouched.
    private static func rewriteKnownKeys(in text: String, with config: Config) -> String {
        var seenKeys: Set<String> = []
        var lines: [String] = []

        for rawLine in text.components(separatedBy: "\n") {
            let trimmed = rawLine.trimmingCharacters(in: .whitespaces)
            if trimmed.isEmpty || trimmed.hasPrefix("#") || trimmed.hasPrefix(";") || trimmed.hasPrefix("[") {
                lines.append(rawLine)
                continue
            }
            guard let eq = rawLine.firstIndex(of: "=") else {
                lines.append(rawLine)
                continue
            }
            let key = rawLine[..<eq].trimmingCharacters(in: .whitespaces).lowercased()
            if let replacement = replacementLine(forKey: key, config: config) {
                lines.append(replacement)
                seenKeys.insert(key)
            } else {
                lines.append(rawLine)
            }
        }

        // Append any known keys that weren't present in the existing file.
        var appended: [String] = []
        for key in ["shortcut", "blink", "blink_settle_ms"] where !seenKeys.contains(key) {
            if let line = replacementLine(forKey: key, config: config) {
                appended.append(line)
            }
        }
        if !appended.isEmpty {
            // Add a blank line between the existing content and the newly
            // appended keys, unless the file already ends in a blank line.
            let lastIsBlank = (lines.last?.isEmpty ?? false)
            if !lastIsBlank { lines.append("") }
            lines.append(contentsOf: appended)
        }

        return lines.joined(separator: "\n")
    }

    private static func replacementLine(forKey key: String, config: Config) -> String? {
        switch key {
        case "shortcut":
            return "shortcut = \(renderShortcutForConfig(config.shortcut))"
        case "blink":
            return "blink = \(config.blink ? "yes" : "no")"
        case "blink_settle_ms":
            return "blink_settle_ms = \(config.blinkSettleMs)"
        default:
            return nil
        }
    }

    /// Fresh-file render, used when there is no existing config to preserve.
    /// Built on top of rewriteKnownKeys so the "replace known lines" logic
    /// lives in one place. The default template contains placeholder values
    /// for every known key, and rewriteKnownKeys overwrites them with the
    /// actual values from `config`.
    private static func renderFreshFile(with config: Config) -> String {
        return rewriteKnownKeys(in: defaultFileContents, with: config)
    }

    /// Render a shortcut in the lowercase, plus-separated form the config
    /// parser expects. Uses Mac conventions ("option" not "alt", "cmd" not
    /// "command"). The parser accepts all synonyms on input, but we write
    /// the most native form on output.
    /// E.g. Shortcut([.option], kVK_UpArrow) -> "option+up".
    private static func renderShortcutForConfig(_ s: Shortcut) -> String {
        var parts: [String] = []
        if s.modifiers.contains(.control) { parts.append("ctrl") }
        if s.modifiers.contains(.option)  { parts.append("option") }
        if s.modifiers.contains(.shift)   { parts.append("shift") }
        if s.modifiers.contains(.command) { parts.append("cmd") }
        parts.append(KeyCodes.name(for: s.keyCode) ?? "key\(s.keyCode)")
        return parts.joined(separator: "+")
    }
}

// MARK: - Core logic

/// Given a list of windows and a pair of monitors to swap, return the list
/// of moves that swaps windows between those two monitors while preserving
/// each window's relative position and size within its monitor.
///
/// Windows on monitors that are not part of the swap pair are left alone
/// (they don't appear in the returned moves at all). This is what makes
/// three-or-more-monitor setups work: the caller picks two specific
/// monitors to swap, and any other connected display is untouched.
///
/// Pure function. No OS calls. Safe to unit-test.
func computeSwapMoves(windows: [WindowInfo], swapPair: (Monitor, Monitor)) -> [WindowMove] {
    let a = swapPair.0.id
    let b = swapPair.1.id

    var moves: [WindowMove] = []
    for window in windows {
        let target: Int
        switch window.monitorId {
        case a: target = b
        case b: target = a
        default: continue  // window is on a monitor outside the swap pair; leave it
        }
        moves.append(WindowMove(
            reference: window.reference,
            targetMonitorId: target,
            targetRelativeFrame: window.relativeFrame
        ))
    }
    return moves
}

// MARK: - Application entry point

@main
final class JASSApp: NSObject, NSApplicationDelegate {

    static func main() {
        let app = NSApplication.shared
        let delegate = JASSApp()
        app.delegate = delegate
        // Accessory = no Dock icon, no menu bar menu of our own, still has windows/panels.
        app.setActivationPolicy(.accessory)
        app.run()
    }

    private var statusItem: NSStatusItem!
    private var hotkey: HotkeyManager?
    private var config: Config = .defaultConfig

    func applicationDidFinishLaunching(_ notification: Notification) {
        Log.info("JASS starting up")
        guard ensureAccessibilityOrGuideUser() else { return }
        config = ConfigLoader.loadOrCreate()
        Log.info("Config loaded: shortcut=\(config.shortcut.description) blink=\(config.blink)")
        installMenuBar()
        registerConfiguredHotkey()
    }

    // MARK: Permissions

    /// Returns true if we already have permission (and can proceed).
    /// If not, we trigger macOS's native Accessibility prompt and quit so
    /// the user can relaunch JASS after granting. Returning false stops
    /// further setup.
    ///
    /// We intentionally rely on macOS's own prompt here rather than adding
    /// an extra alert of our own. The native one already says "JASS wants
    /// to control your computer using accessibility features", offers an
    /// "Open System Settings" button, and is what users recognize from
    /// every other Mac app that needs this permission. Duplicating it in
    /// a second dialog just makes the first launch feel cluttered.
    private func ensureAccessibilityOrGuideUser() -> Bool {
        if AccessibilityPermission.isGranted { return true }
        AccessibilityPermission.requestWithSystemPrompt()
        NSApp.terminate(nil)
        return false
    }

    // MARK: Menu bar

    /// Presets for the "Settle time" submenu. Any value outside this list is
    /// shown as "Custom (N ms)" and can still be set via the config file.
    private static let settlePresetsMs = [10, 20, 50, 100, 150, 200]

    private func installMenuBar() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        if let button = statusItem.button {
            if let image = NSImage(systemSymbolName: "arrow.up.arrow.down",
                                   accessibilityDescription: "JASS") {
                image.isTemplate = true
                button.image = image
            } else {
                button.title = "JASS"
            }
        }
        rebuildMenu()
    }

    /// Builds the entire menu from scratch, reflecting current `config` state.
    /// Called on launch and after any change that alters what the menu shows
    /// (shortcut change, blink toggle, settle-time pick, config reload).
    private func rebuildMenu() {
        let menu = NSMenu()

        // Shortcut submenu: shows the current shortcut and offers "Change...".
        let shortcutItem = NSMenuItem(
            title: "Shortcut: \(config.shortcut.description)",
            action: nil, keyEquivalent: ""
        )
        let shortcutSub = NSMenu()
        shortcutSub.addItem(withTitle: "Change...",
                            action: #selector(promptForNewShortcut),
                            keyEquivalent: "")
        shortcutItem.submenu = shortcutSub
        menu.addItem(shortcutItem)

        // Blink toggle: simple on/off with a checkmark.
        let blinkItem = NSMenuItem(title: "Blink effect",
                                   action: #selector(toggleBlink),
                                   keyEquivalent: "")
        blinkItem.state = config.blink ? .on : .off
        menu.addItem(blinkItem)

        // Settle-time submenu: presets plus a read-only "Custom" entry for
        // values set via the config file that don't match a preset.
        let settleItem = NSMenuItem(title: "Settle time: \(config.blinkSettleMs) ms",
                                    action: nil, keyEquivalent: "")
        let settleSub = NSMenu()
        for ms in JASSApp.settlePresetsMs {
            let entry = NSMenuItem(title: "\(ms) ms",
                                   action: #selector(pickSettlePreset(_:)),
                                   keyEquivalent: "")
            entry.tag = ms
            entry.state = (config.blinkSettleMs == ms) ? .on : .off
            settleSub.addItem(entry)
        }
        if !JASSApp.settlePresetsMs.contains(config.blinkSettleMs) {
            settleSub.addItem(.separator())
            let custom = NSMenuItem(title: "Custom: \(config.blinkSettleMs) ms",
                                    action: nil, keyEquivalent: "")
            custom.state = .on
            custom.isEnabled = false
            settleSub.addItem(custom)
        }
        settleItem.submenu = settleSub
        menu.addItem(settleItem)

        menu.addItem(.separator())
        menu.addItem(withTitle: "Swap screens now",
                     action: #selector(swapNow), keyEquivalent: "")
        menu.addItem(.separator())
        menu.addItem(withTitle: "Edit config file",
                     action: #selector(openConfigFile), keyEquivalent: "")
        menu.addItem(withTitle: "Reload config",
                     action: #selector(reloadConfig), keyEquivalent: "")
        menu.addItem(.separator())
        menu.addItem(withTitle: "About JASS",
                     action: #selector(showAbout), keyEquivalent: "")
        menu.addItem(withTitle: "Quit JASS",
                     action: #selector(quitApp), keyEquivalent: "q")

        assignTargetRecursively(menu: menu)
        statusItem.menu = menu
    }

    /// NSMenuItems with an action need a target, otherwise clicks don't fire.
    /// Walks the whole menu tree so submenu items also get wired up.
    private func assignTargetRecursively(menu: NSMenu) {
        for item in menu.items {
            if item.action != nil { item.target = self }
            if let sub = item.submenu { assignTargetRecursively(menu: sub) }
        }
    }

    // MARK: Menu actions

    @objc private func toggleBlink() {
        config.blink.toggle()
        saveConfig()
        rebuildMenu()
    }

    @objc private func pickSettlePreset(_ sender: NSMenuItem) {
        let ms = sender.tag
        guard ms >= 0, ms <= 2000 else { return }
        config.blinkSettleMs = ms
        saveConfig()
        rebuildMenu()
    }

    @objc private func promptForNewShortcut() {
        ShortcutRecorder.prompt(current: config.shortcut) { [weak self] newShortcut in
            guard let self, let newShortcut else { return }
            self.config.shortcut = newShortcut
            self.saveConfig()
            self.registerConfiguredHotkey()
            self.rebuildMenu()
        }
    }

    /// Writes the current `config` back to disk so menu-driven changes
    /// persist across launches. Uses the same key=value format as the
    /// hand-editable config file.
    private func saveConfig() {
        ConfigLoader.save(config)
    }

    // MARK: Hotkey

    private func registerConfiguredHotkey() {
        hotkey?.unregister()
        let manager = HotkeyManager()
        let ok = manager.register(shortcut: config.shortcut) { [weak self] in
            self?.swapNow()
        }
        if ok {
            Log.info("Registered hotkey \(config.shortcut.description)")
        } else {
            Log.error("Failed to register hotkey \(config.shortcut.description); likely taken by another app")
            SystemActions.showAlert(
                title: "Could not register shortcut",
                message: """
                JASS could not register \(config.shortcut.description). \
                Another app may already be using it. Edit config to choose \
                a different combination, then reload.
                """
            )
        }
        hotkey = manager
    }

    // MARK: Actions

    @objc private func swapNow() {
        let monitors = SystemMonitors.enumerateAll()

        // Case 1: zero or one monitor. Nothing meaningful to swap.
        guard monitors.count >= 2 else {
            Log.warn("Swap requested with \(monitors.count) monitor(s); need at least 2")
            SystemActions.showAlert(
                title: "JASS needs at least 2 monitors",
                message: "JASS can only swap windows when at least two displays are connected. JASS sees \(monitors.count) right now."
            )
            return
        }

        // Pick the two monitors to swap between. With exactly two monitors
        // we always swap those two, ignoring cursor position, because that
        // matches the original behaviour and there's no ambiguity. With
        // three or more we use the cursor to pick the partner.
        let pair: (Monitor, Monitor)
        if monitors.count == 2 {
            pair = (monitors[0], monitors[1])
        } else {
            guard let resolved = pickSwapPairFromCursor(in: monitors) else {
                return  // pickSwapPairFromCursor already showed user feedback
            }
            pair = resolved
        }

        let windows = SystemWindows.enumerateAll(monitors: monitors)
        let moves = computeSwapMoves(windows: windows, swapPair: pair)

        // Pass only the swap pair to apply(), not all monitors. apply()
        // uses this list to validate target frames; an external monitor
        // we're not touching shouldn't affect that validation.
        let monitorsForApply = [pair.0, pair.1]

        if config.blink {
            let settle = TimeInterval(config.blinkSettleMs) / 1000.0
            BlinkOverlay.run(settleTime: settle) {
                let report = SystemWindows.apply(moves, monitors: monitorsForApply)
                self.handle(report: report)
            }
        } else {
            let report = SystemWindows.apply(moves, monitors: monitorsForApply)
            handle(report: report)
        }
    }

    /// Pair the anchor (built-in/primary) display with whichever display
    /// the cursor is currently on. Shows guidance to the user and returns
    /// nil when the cursor lands on the anchor itself, since "swap with
    /// where my cursor is" has no meaning then.
    ///
    /// Used in setups with three or more monitors. With exactly two monitors
    /// `swapNow` short-circuits and pairs them directly without consulting
    /// the cursor, so this function isn't called there.
    private func pickSwapPairFromCursor(in monitors: [Monitor]) -> (Monitor, Monitor)? {
        guard let anchor = SystemMonitors.anchorMonitor(in: monitors) else {
            Log.warn("Could not determine anchor monitor; aborting swap")
            return nil
        }
        guard let cursorMonitor = SystemMonitors.monitorUnderCursor(in: monitors) else {
            Log.warn("Cursor is not on any known monitor; aborting swap")
            SystemActions.showAlert(
                title: "JASS could not determine target screen",
                message: "Move the cursor onto the external display you want to swap with, then press the shortcut again."
            )
            return nil
        }
        if cursorMonitor.id == anchor.id {
            Log.info("Cursor is on the anchor display; explaining multi-monitor flow to user")
            SystemActions.showAlert(
                title: "Choose the screen to swap with",
                message: "With more than two displays connected, JASS swaps your built-in (or primary) screen with whichever external screen your cursor is on.\n\nMove the cursor onto the external screen you want to swap with, then press the shortcut again."
            )
            return nil
        }
        return (anchor, cursorMonitor)
    }

    /// Log a one-line summary of the swap. Show an alert only when the
    /// outcome is bad enough that the user deserves to know: windows stuck
    /// in an unexpected state, or a majority of moves failed.
    private func handle(report: SystemWindows.MoveReport) {
        Log.info("Swap report: attempted=\(report.attempted) succeeded=\(report.succeeded) rolledBack=\(report.rolledBack) stuck=\(report.stuck) skipped=\(report.skipped)")

        if report.stuck > 0 {
            SystemActions.showAlert(
                title: "Some windows may be in a wrong position",
                message: """
                \(report.stuck) window(s) could not be moved and could not be \
                restored. Check the affected windows manually. See the log at \
                ~/Library/Logs/JASS/jass.log for details.
                """
            )
            return
        }

        // If most moves failed cleanly (i.e. got rolled back), still worth
        // telling the user, but less alarmingly.
        let failed = report.rolledBack + report.skipped
        if report.attempted > 0 && failed * 2 >= report.attempted {
            SystemActions.showAlert(
                title: "Swap partially failed",
                message: """
                \(failed) of \(report.attempted) window(s) could not be moved. \
                They were left in their original position. See \
                ~/Library/Logs/JASS/jass.log for details.
                """
            )
        }
    }

    @objc private func openConfigFile() {
        let url = ConfigLoader.fileURL

        // .ini files have no default app association on most Macs, so a
        // plain NSWorkspace.open(url) fails. We instead explicitly open the
        // file with TextEdit, which is bundled on every macOS install. If
        // the user has a different preferred editor, they can still open
        // the file from Finder. For that case we also offer the reveal
        // fallback below.
        let textEditURL = URL(fileURLWithPath: "/System/Applications/TextEdit.app")

        let openConfig = NSWorkspace.OpenConfiguration()
        openConfig.activates = true
        NSWorkspace.shared.open([url], withApplicationAt: textEditURL,
                                configuration: openConfig) { [weak self] _, error in
            if let error = error {
                Log.warn("Could not open config in TextEdit: \(error.localizedDescription). Falling back to Finder reveal.")
                DispatchQueue.main.async { self?.revealConfigInFinder(url: url) }
            }
        }
    }

    private func revealConfigInFinder(url: URL) {
        NSWorkspace.shared.activateFileViewerSelecting([url])
    }

    @objc private func reloadConfig() {
        config = ConfigLoader.loadOrCreate()
        registerConfiguredHotkey()
        rebuildMenu()
    }

    @objc private func showAbout() {
        SystemActions.showAlert(
            title: "JustAScreenSwitcher",
            message: """
            Swap all your windows between two monitors with a single shortcut.

            Current shortcut: \(config.shortcut.description)

            Built by Orville BV.
            """
        )
    }

    @objc private func quitApp() {
        NSApp.terminate(nil)
    }
}
