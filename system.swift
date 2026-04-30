//
//  system.swift
//  JustAScreenSwitcher (JASS)
//
//  Everything that actually touches the operating system:
//    - Accessibility permission check and prompt
//    - Monitor enumeration (NSScreen -> our Monitor type, in AX coordinates)
//    - Window enumeration and moving (AXUIElement)
//    - Global hotkey registration (Carbon)
//    - Small OS-facing helpers (alerts, opening System Settings)
//
//  Pure logic and domain types live in app.swift.
//

import AppKit
import ApplicationServices
import Carbon.HIToolbox

// MARK: - Logging

/// Minimal log facility. Writes to stderr (visible when launched from
/// Terminal) and appends to ~/Library/Logs/JASS/jass.log. Intentionally
/// small: the goal is to have a trace of unusual events, not a structured
/// telemetry pipeline.
enum Log {
    static let fileURL: URL = {
        let base = FileManager.default.urls(for: .libraryDirectory, in: .userDomainMask).first!
        return base.appendingPathComponent("Logs/JASS/jass.log")
    }()

    static func info(_ message: String)  { write("INFO",  message) }
    static func warn(_ message: String)  { write("WARN",  message) }
    static func error(_ message: String) { write("ERROR", message) }

    /// Once-per-process flag: if file logging fails, we stop trying and
    /// complain on stderr exactly once. Stderr keeps getting every line,
    /// so no information is lost, just the persistent log is unavailable.
    private static var fileLogDisabled = false

    private static func write(_ level: String, _ message: String) {
        let line = "\(timestamp()) [\(level)] \(message)\n"
        fputs(line, stderr)

        guard !fileLogDisabled else { return }
        do {
            try appendToLogFile(line)
        } catch {
            fileLogDisabled = true
            fputs("[JASS] File logging disabled: \(error.localizedDescription). Further logs go to stderr only.\n", stderr)
        }
    }

    private static func appendToLogFile(_ line: String) throws {
        let url = fileURL
        try FileManager.default.createDirectory(
            at: url.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        guard let data = line.data(using: .utf8) else {
            throw NSError(
                domain: "JASS.Log",
                code: -1,
                userInfo: [NSLocalizedDescriptionKey: "Could not encode log line as UTF-8"]
            )
        }
        if FileManager.default.fileExists(atPath: url.path) {
            let handle = try FileHandle(forWritingTo: url)
            try handle.seekToEnd()
            try handle.write(contentsOf: data)
            try handle.close()
        } else {
            try data.write(to: url)
        }
    }

    private static let dateFormatter: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()

    private static func timestamp() -> String {
        dateFormatter.string(from: Date())
    }
}

/// Human-readable name for the common AXError cases. Unknown values fall
/// back to the raw integer, which is still greppable in the log.
private extension AXError {
    var name: String {
        switch self {
        case .success:                 return "success"
        case .failure:                 return "failure"
        case .illegalArgument:         return "illegalArgument"
        case .invalidUIElement:        return "invalidUIElement"
        case .cannotComplete:          return "cannotComplete"
        case .attributeUnsupported:    return "attributeUnsupported"
        case .actionUnsupported:       return "actionUnsupported"
        case .notImplemented:          return "notImplemented"
        case .apiDisabled:             return "apiDisabled"
        case .noValue:                 return "noValue"
        default:                       return "code=\(rawValue)"
        }
    }
}

// MARK: - Accessibility permission

enum AccessibilityPermission {
    static var isGranted: Bool {
        AXIsProcessTrusted()
    }

    /// Shows the system prompt asking the user to enable Accessibility access
    /// for this app. Needs to be called at least once so the app appears in
    /// the System Settings list.
    @discardableResult
    static func requestWithSystemPrompt() -> Bool {
        let key = kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String
        let options = [key: true] as CFDictionary
        return AXIsProcessTrustedWithOptions(options)
    }
}

// MARK: - Monitor enumeration

enum SystemMonitors {
    /// Returns all connected monitors, each expressed in AX coordinate space
    /// (origin top-left of primary screen, Y axis pointing down). This matches
    /// the coordinates used by AXUIElement position/size attributes, which
    /// lets the core logic compare window frames to monitor frames directly.
    static func enumerateAll() -> [Monitor] {
        guard let primary = NSScreen.screens.first else { return [] }
        let primaryHeight = primary.frame.height

        return NSScreen.screens.enumerated().map { (index, screen) in
            let cocoa = screen.frame
            let axFrame = CGRect(
                x: cocoa.origin.x,
                y: primaryHeight - cocoa.origin.y - cocoa.size.height,
                width: cocoa.size.width,
                height: cocoa.size.height
            )
            return Monitor(id: index, frame: axFrame)
        }
    }
}

// MARK: - Window enumeration and moving

enum SystemWindows {

    /// Enumerates every moveable window belonging to a regular (user-facing)
    /// app, in AX coordinate space. Fullscreen windows are skipped because
    /// they live in their own Space and can't be cleanly moved between
    /// displays.
    static func enumerateAll(monitors: [Monitor]) -> [WindowInfo] {
        var results: [WindowInfo] = []

        let apps = NSWorkspace.shared.runningApplications.filter {
            $0.activationPolicy == .regular
        }

        for app in apps {
            let appElement = AXUIElementCreateApplication(app.processIdentifier)
            var raw: AnyObject?
            let err = AXUIElementCopyAttributeValue(appElement,
                                                    kAXWindowsAttribute as CFString,
                                                    &raw)
            guard err == .success, let axWindows = raw as? [AXUIElement] else { continue }

            for (windowIndex, window) in axWindows.enumerated() {
                if let info = buildWindowInfo(window: window,
                                              pid: app.processIdentifier,
                                              windowIndex: windowIndex,
                                              monitors: monitors) {
                    results.append(info)
                }
            }
        }
        return results
    }

    /// Summary of what happened during a call to `apply`. Returned to the
    /// caller so they can log, notify, or react to partial failures.
    struct MoveReport {
        var attempted = 0
        var succeeded = 0
        /// Move failed, original state was fully restored.
        var rolledBack = 0
        /// Move failed and rollback itself failed. The window may now be in
        /// an unexpected state. Rare, but we track it explicitly so it
        /// never hides.
        var stuck = 0
        /// We couldn't even read the original state, so the move was
        /// skipped without any attempt.
        var skipped = 0

        var hasProblems: Bool { rolledBack + stuck + skipped > 0 }
    }

    /// Apply each move. Returns a MoveReport describing the outcome. Each
    /// window is handled independently: a failure on one window does not
    /// stop the others, but does trigger a best-effort rollback of just
    /// that window's changes.
    @discardableResult
    static func apply(_ moves: [WindowMove], monitors: [Monitor]) -> MoveReport {
        // Re-check: the display configuration may have changed between
        // computing moves and applying them (hotplug during the blink, sleep
        // transitions, etc.). If the set of monitor ids is different now,
        // the moves were computed against a stale layout and applying them
        // could place windows on coordinates that no longer correspond to
        // any screen. Abort entirely in that case.
        let currentMonitors = SystemMonitors.enumerateAll()
        let currentIds = Set(currentMonitors.map { $0.id })
        let requestedIds = Set(monitors.map { $0.id })
        guard currentIds == requestedIds else {
            Log.warn("Display configuration changed between compute and apply (was \(requestedIds), now \(currentIds)); aborting swap")
            var aborted = MoveReport()
            aborted.skipped = moves.count
            return aborted
        }

        let monitorsById: [Int: Monitor] = Dictionary(uniqueKeysWithValues:
            currentMonitors.map { ($0.id, $0) }
        )

        var report = MoveReport()
        for move in moves {
            guard let target = monitorsById[move.targetMonitorId] else {
                // Shouldn't happen given the upfront check, but we keep the
                // per-move guard as defense in depth.
                Log.warn("Move refers to unknown monitor id \(move.targetMonitorId); skipping")
                report.skipped += 1
                continue
            }
            let absolute = absoluteFrame(relative: move.targetRelativeFrame, on: target)

            // Sanity check: the computed target frame must overlap at least
            // one current monitor. This prevents any scenario where rounding,
            // a stale monitor rect, or a miscomputation could push a window
            // to coordinates that aren't visible on any screen.
            let onScreen = currentMonitors.contains { $0.frame.intersects(absolute) }
            guard onScreen else {
                Log.warn("Target frame \(absolute) falls outside all current monitors; skipping to avoid losing window")
                report.skipped += 1
                continue
            }

            let outcome = applyMove(to: move.reference, targetFrame: absolute)
            report.attempted += 1
            switch outcome {
            case .success:     report.succeeded  += 1
            case .rolledBack:  report.rolledBack += 1
            case .stuck:       report.stuck      += 1
            case .skipped:     report.skipped    += 1
            }
        }
        return report
    }

    private enum MoveOutcome {
        case success
        /// Attempted, failed, and we successfully reverted the window to
        /// its original state.
        case rolledBack
        /// Attempted, failed, and rollback also failed. Window is in an
        /// indeterminate state.
        case stuck
        /// Could not even begin the move (missing original state).
        case skipped
    }

    private struct WindowState {
        let position: CGPoint
        let size: CGSize
    }

    /// Capture a window's current position and size so we can roll back if
    /// a later step fails.
    private static func captureState(_ window: AXUIElement) -> WindowState? {
        guard let position = readPoint(window, kAXPositionAttribute),
              let size     = readSize(window, kAXSizeAttribute) else {
            return nil
        }
        return WindowState(position: position, size: size)
    }

    /// Attempt to move a single window to `targetFrame`. The sequence is:
    ///   1. capture the window's current state
    ///   2. set size, then position
    ///   3. re-apply size once more (helps when the window briefly straddled
    ///      two monitors with different sizes and got clamped)
    /// Any failure in step 2 triggers a rollback to the captured state.
    /// A failure in step 3 is logged but not treated as fatal, because the
    /// geometry change itself already succeeded.
    private static func applyMove(to ref: WindowReference, targetFrame: CGRect) -> MoveOutcome {
        let window = ref.axElement

        guard let original = captureState(window) else {
            Log.warn("pid=\(ref.pid) win=\(ref.index): could not read state; skipping move")
            return .skipped
        }

        // Step 2a: set size.
        let sizeErr = setSize(window, size: targetFrame.size)
        if sizeErr != .success {
            Log.warn("pid=\(ref.pid) win=\(ref.index): setSize failed (\(sizeErr.name)); rolling back")
            return rollback(window: window, to: original, partialSteps: .none)
                ? .rolledBack : .stuck
        }

        // Step 2b: set position.
        let posErr = setPosition(window, point: targetFrame.origin)
        if posErr != .success {
            Log.warn("pid=\(ref.pid) win=\(ref.index): setPosition failed (\(posErr.name)); rolling back")
            return rollback(window: window, to: original, partialSteps: .sizeChanged)
                ? .rolledBack : .stuck
        }

        // Step 3: second size pass. Best-effort only; don't roll back if it fails,
        // because the geometry change itself is already in effect.
        let sizeErr2 = setSize(window, size: targetFrame.size)
        if sizeErr2 != .success {
            Log.warn("pid=\(ref.pid) win=\(ref.index): second setSize failed (\(sizeErr2.name)); accepting window as moved")
        }

        return .success
    }

    /// Which steps of `applyMove` had already happened at the time rollback
    /// was invoked. Determines what we need to undo.
    private enum PartialSteps {
        case none          // nothing changed yet
        case sizeChanged   // only setSize succeeded
    }

    /// Best-effort rollback to the original state. Returns true if the
    /// window was fully restored, false if any restore step failed (in
    /// which case the caller should report the window as "stuck").
    private static func rollback(window: AXUIElement,
                                 to original: WindowState,
                                 partialSteps: PartialSteps) -> Bool {
        switch partialSteps {
        case .none:
            // Nothing was changed yet, nothing to roll back.
            return true
        case .sizeChanged:
            let err = setSize(window, size: original.size)
            if err != .success {
                Log.error("rollback: restoring size failed (\(err.name))")
                return false
            }
            return true
        }
    }

    // MARK: Enumeration helpers

    private static func buildWindowInfo(
        window: AXUIElement,
        pid: pid_t,
        windowIndex: Int,
        monitors: [Monitor]
    ) -> WindowInfo? {
        // Skip fullscreen windows (they occupy their own Space).
        // "AXFullScreen" is a real runtime attribute on macOS but isn't
        // exposed as a Swift constant in the public headers, so we pass
        // the raw string directly.
        if readBool(window, "AXFullScreen") == true {
            return nil
        }
        // Skip minimized windows. They are not visible on any monitor, so
        // "which monitor do they belong to" is not a meaningful question,
        // and moving them would require an unminimize/minimize dance that
        // triggers noticeable macOS genie/scale animations on every swap.
        // Minimized windows stay where the user left them; when restored,
        // macOS places them wherever it normally would.
        if readBool(window, kAXMinimizedAttribute) == true {
            return nil
        }
        // Skip anything that isn't a standard window (sheets, drawers, etc.).
        if let role = readString(window, kAXRoleAttribute),
           role != (kAXWindowRole as String) {
            return nil
        }

        guard let position = readPoint(window, kAXPositionAttribute),
              let size     = readSize(window,  kAXSizeAttribute) else {
            return nil
        }
        // A few apps report zero-sized placeholders; ignore them.
        if size.width <= 1 || size.height <= 1 { return nil }

        let frame = CGRect(origin: position, size: size)

        guard let monitorId = monitorIdContaining(frame: frame, monitors: monitors) else {
            return nil
        }
        guard let monitor = monitors.first(where: { $0.id == monitorId }) else {
            return nil
        }

        let relative = relativeFrame(absolute: frame, on: monitor)
        return WindowInfo(
            reference: WindowReference(axElement: window, pid: pid, index: windowIndex),
            monitorId: monitorId,
            relativeFrame: relative
        )
    }

    /// Pick the monitor that contains the window's center, falling back to
    /// the monitor with the largest overlap for windows that straddle edges.
    private static func monitorIdContaining(frame: CGRect, monitors: [Monitor]) -> Int? {
        let center = CGPoint(x: frame.midX, y: frame.midY)
        if let m = monitors.first(where: { $0.frame.contains(center) }) {
            return m.id
        }
        var bestId: Int? = nil
        var bestArea: CGFloat = 0
        for m in monitors {
            let inter = m.frame.intersection(frame)
            let area = max(0, inter.width) * max(0, inter.height)
            if area > bestArea { bestArea = area; bestId = m.id }
        }
        return bestId
    }

    // MARK: Coordinate math

    private static func relativeFrame(absolute: CGRect, on monitor: Monitor) -> CGRect {
        CGRect(
            x: (absolute.origin.x - monitor.frame.origin.x) / monitor.frame.width,
            y: (absolute.origin.y - monitor.frame.origin.y) / monitor.frame.height,
            width:  absolute.width  / monitor.frame.width,
            height: absolute.height / monitor.frame.height
        )
    }

    private static func absoluteFrame(relative: CGRect, on monitor: Monitor) -> CGRect {
        CGRect(
            x: monitor.frame.origin.x + relative.origin.x * monitor.frame.width,
            y: monitor.frame.origin.y + relative.origin.y * monitor.frame.height,
            width:  relative.width  * monitor.frame.width,
            height: relative.height * monitor.frame.height
        )
    }

    // MARK: Move application

    // MARK: AX attribute helpers

    private static func readBool(_ element: AXUIElement, _ attr: String) -> Bool? {
        var raw: AnyObject?
        guard AXUIElementCopyAttributeValue(element, attr as CFString, &raw) == .success,
              let value = raw else { return nil }
        if let number = value as? NSNumber { return number.boolValue }
        return nil
    }

    private static func readString(_ element: AXUIElement, _ attr: String) -> String? {
        var raw: AnyObject?
        guard AXUIElementCopyAttributeValue(element, attr as CFString, &raw) == .success,
              let value = raw as? String else { return nil }
        return value
    }

    private static func readPoint(_ element: AXUIElement, _ attr: String) -> CGPoint? {
        var raw: AnyObject?
        guard AXUIElementCopyAttributeValue(element, attr as CFString, &raw) == .success,
              let value = raw,
              CFGetTypeID(value) == AXValueGetTypeID() else { return nil }
        let ax = value as! AXValue
        var point = CGPoint.zero
        return AXValueGetValue(ax, .cgPoint, &point) ? point : nil
    }

    private static func readSize(_ element: AXUIElement, _ attr: String) -> CGSize? {
        var raw: AnyObject?
        guard AXUIElementCopyAttributeValue(element, attr as CFString, &raw) == .success,
              let value = raw,
              CFGetTypeID(value) == AXValueGetTypeID() else { return nil }
        let ax = value as! AXValue
        var size = CGSize.zero
        return AXValueGetValue(ax, .cgSize, &size) ? size : nil
    }

    private static func setPosition(_ element: AXUIElement, point: CGPoint) -> AXError {
        var p = point
        guard let ax = AXValueCreate(.cgPoint, &p) else { return .failure }
        return AXUIElementSetAttributeValue(element, kAXPositionAttribute as CFString, ax)
    }

    private static func setSize(_ element: AXUIElement, size: CGSize) -> AXError {
        var s = size
        guard let ax = AXValueCreate(.cgSize, &s) else { return .failure }
        return AXUIElementSetAttributeValue(element, kAXSizeAttribute as CFString, ax)
    }
}

// MARK: - Global hotkey (Carbon)

/// Registers a single global hotkey via the Carbon HotKey API.
///
/// Why Carbon in 2026? It's the only built-in way to register an app-level
/// global hotkey on macOS without private APIs or third-party frameworks.
/// It's old but stable, and the alternative (NSEvent.addGlobalMonitor) can
/// observe key presses but can't intercept them, which means other apps
/// would also see the shortcut.
final class HotkeyManager {

    private var hotKeyRef: EventHotKeyRef?
    private var id: UInt32 = 0

    /// Install a callback for the given shortcut. Returns true on success.
    /// Only one hotkey is managed per instance; calling register again
    /// replaces the previous one.
    @discardableResult
    func register(shortcut: Shortcut, callback: @escaping () -> Void) -> Bool {
        unregister()
        HotkeyDispatch.installHandlerIfNeeded()

        let newId = HotkeyDispatch.nextId()
        self.id = newId
        HotkeyDispatch.register(id: newId, callback: callback)

        // Signature is a fourCC tag ('JASS'); purely informational.
        let hotKeyID = EventHotKeyID(signature: fourCC("JASS"), id: newId)

        var ref: EventHotKeyRef?
        let status = RegisterEventHotKey(
            shortcut.keyCode,
            shortcut.modifiers.rawValue,
            hotKeyID,
            GetApplicationEventTarget(),
            0,
            &ref
        )
        guard status == noErr, let ref else {
            HotkeyDispatch.unregister(id: newId)
            return false
        }
        self.hotKeyRef = ref
        return true
    }

    func unregister() {
        if let ref = hotKeyRef {
            UnregisterEventHotKey(ref)
            hotKeyRef = nil
        }
        if id != 0 {
            HotkeyDispatch.unregister(id: id)
            id = 0
        }
    }

    deinit { unregister() }
}

/// Internal registry that lets the Carbon C-callback dispatch to the right
/// Swift closure. Carbon's event handler must be a plain C function pointer
/// (`@convention(c)`), so it can't capture Swift state directly. The global
/// dispatch table bridges the gap.
private enum HotkeyDispatch {
    private static var callbacks: [UInt32: () -> Void] = [:]
    private static var nextIdValue: UInt32 = 1
    private static var installed = false

    static func nextId() -> UInt32 {
        defer { nextIdValue &+= 1 }
        return nextIdValue
    }

    static func register(id: UInt32, callback: @escaping () -> Void) {
        callbacks[id] = callback
    }

    static func unregister(id: UInt32) {
        callbacks.removeValue(forKey: id)
    }

    static func fire(id: UInt32) {
        callbacks[id]?()
    }

    static func installHandlerIfNeeded() {
        guard !installed else { return }

        var eventType = EventTypeSpec(
            eventClass: OSType(kEventClassKeyboard),
            eventKind:  UInt32(kEventHotKeyPressed)
        )
        let status = InstallEventHandler(
            GetApplicationEventTarget(),
            { (_, event, _) -> OSStatus in
                var hotKeyID = EventHotKeyID()
                let err = GetEventParameter(
                    event,
                    EventParamName(kEventParamDirectObject),
                    EventParamType(typeEventHotKeyID),
                    nil,
                    MemoryLayout<EventHotKeyID>.size,
                    nil,
                    &hotKeyID
                )
                if err == noErr {
                    HotkeyDispatch.fire(id: hotKeyID.id)
                }
                return noErr
            },
            1,
            &eventType,
            nil,
            nil
        )
        if status == noErr {
            installed = true
        } else {
            Log.error("InstallEventHandler failed with OSStatus \(status); global hotkeys will not work")
        }
    }
}

/// Build a Carbon fourCC from a 4-character ASCII string.
private func fourCC(_ s: String) -> OSType {
    let bytes = Array(s.utf8.prefix(4))
    var result: UInt32 = 0
    for byte in bytes { result = (result << 8) | UInt32(byte) }
    return OSType(result)
}

// MARK: - Shortcut recorder

/// Small modal window that asks the user to press a new keyboard shortcut
/// and calls back with the result. While the window is up, every key event
/// goes to the recorder; Esc cancels, any other modifier + key combo is
/// captured as the new shortcut.
///
/// Lives in system.swift because it's raw AppKit / event-monitoring code.
enum ShortcutRecorder {

    /// Strong reference to the currently-active recorder, if any. Without
    /// this, the controller gets deallocated the moment prompt(...) returns,
    /// taking its keyDown closure with it and silently dropping every key
    /// press the user makes. Set to nil once the recorder finishes.
    private static var active: ShortcutRecorderWindowController?

    /// Presents the recorder modally and calls `completion` when the user
    /// confirms (with the new shortcut) or cancels (with nil).
    static func prompt(current: Shortcut, completion: @escaping (Shortcut?) -> Void) {
        active = ShortcutRecorderWindowController(current: current) { result in
            completion(result)
            ShortcutRecorder.active = nil
        }
        active?.showModal()
    }
}

/// Window controller for the recorder dialog. Kept private to this file so
/// the public surface is just ShortcutRecorder.prompt(...).
private final class ShortcutRecorderWindowController: NSWindowController, NSWindowDelegate {

    private let completion: (Shortcut?) -> Void
    private let instructionLabel = NSTextField(labelWithString: "")
    private let currentLabel = NSTextField(labelWithString: "")
    private var didCallCompletion = false

    init(current: Shortcut, completion: @escaping (Shortcut?) -> Void) {
        self.completion = completion

        // Use our own NSWindow subclass so we can override keyDown directly.
        // Going through addLocalMonitorForEvents proved unreliable for an
        // accessory (menu-bar-only) app: even after bumping the activation
        // policy to .regular, the local monitor sometimes never fires.
        // Overriding keyDown on the window itself works regardless.
        let window = KeyCapturingWindow(
            contentRect: NSRect(x: 0, y: 0, width: 380, height: 160),
            styleMask: [.titled, .closable],
            backing: .buffered,
            defer: false
        )
        window.title = "Set JASS shortcut"
        window.isReleasedWhenClosed = false
        window.center()

        super.init(window: window)
        window.delegate = self
        window.onKeyDown = { [weak self] event in
            self?.handle(event: event)
        }

        buildContent(currentShortcut: current)
    }

    required init?(coder: NSCoder) { fatalError("not used") }

    private func buildContent(currentShortcut: Shortcut) {
        guard let contentView = window?.contentView else { return }

        instructionLabel.stringValue = "Press the new shortcut.\nPress Esc to cancel."
        instructionLabel.alignment = .center
        instructionLabel.font = .systemFont(ofSize: 13)
        instructionLabel.maximumNumberOfLines = 2
        instructionLabel.translatesAutoresizingMaskIntoConstraints = false

        currentLabel.stringValue = "Current: \(currentShortcut.description)"
        currentLabel.alignment = .center
        currentLabel.font = .systemFont(ofSize: 11)
        currentLabel.textColor = .secondaryLabelColor
        currentLabel.translatesAutoresizingMaskIntoConstraints = false

        contentView.addSubview(instructionLabel)
        contentView.addSubview(currentLabel)

        NSLayoutConstraint.activate([
            instructionLabel.centerXAnchor.constraint(equalTo: contentView.centerXAnchor),
            instructionLabel.centerYAnchor.constraint(equalTo: contentView.centerYAnchor, constant: -10),
            currentLabel.centerXAnchor.constraint(equalTo: contentView.centerXAnchor),
            currentLabel.topAnchor.constraint(equalTo: instructionLabel.bottomAnchor, constant: 16),
        ])
    }

    func showModal() {
        guard let window = window else { return }

        // Promote JASS to a regular app while the recorder is up. Accessory
        // apps can have windows but their windows aren't first-class for
        // activation and focus, which makes key input unreliable. A regular
        // app has normal window semantics.
        NSApp.setActivationPolicy(.regular)
        NSApp.activate(ignoringOtherApps: true)

        window.makeKeyAndOrderFront(nil)
        // Make the window itself the first responder so our keyDown override
        // gets called. Without this, AppKit looks for a responder inside the
        // view hierarchy and our text labels don't handle keys.
        window.makeFirstResponder(window)
    }

    private func handle(event: NSEvent) {
        // Escape cancels.
        if event.keyCode == UInt16(kVK_Escape) {
            finish(with: nil)
            return
        }

        // Extract just the modifier flags JASS understands, ignoring
        // device-specific bits like caps lock or function-key flags.
        let nsMods = event.modifierFlags
        var mods: Modifiers = []
        if nsMods.contains(.command) { mods.insert(.command) }
        if nsMods.contains(.option)  { mods.insert(.option) }
        if nsMods.contains(.control) { mods.insert(.control) }
        if nsMods.contains(.shift)   { mods.insert(.shift) }

        // We require at least one modifier; otherwise the shortcut would
        // intercept a normal typing key system-wide, which is almost never
        // what the user wants.
        guard !mods.isEmpty else {
            instructionLabel.stringValue = "That would intercept a plain key.\nPlease include at least one modifier (Cmd, Ctrl, Option, or Shift)."
            return
        }

        let keyCode = UInt32(event.keyCode)
        let new = Shortcut(modifiers: mods, keyCode: keyCode)
        Log.info("Shortcut recorded: \(new.description)")
        finish(with: new)
    }

    private func finish(with result: Shortcut?) {
        guard !didCallCompletion else { return }
        didCallCompletion = true

        window?.close()

        // Restore JASS to accessory mode so it goes back to being a pure
        // menu-bar-only app with no Dock icon and no app-switcher entry.
        NSApp.setActivationPolicy(.accessory)

        completion(result)
    }

    func windowWillClose(_ notification: Notification) {
        // User closed the dialog via the red traffic-light button: treat as cancel.
        finish(with: nil)
    }
}

/// NSWindow subclass that claims first-responder status itself and forwards
/// keyDown events to a closure. This is the most reliable way to capture
/// key presses for an app that normally runs as a menu-bar accessory.
private final class KeyCapturingWindow: NSWindow {
    var onKeyDown: ((NSEvent) -> Void)?

    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }
    override var acceptsFirstResponder: Bool { true }

    override func keyDown(with event: NSEvent) {
        onKeyDown?(event)
        // Do not call super. We're consuming this key press on purpose.
    }
}

// MARK: - Small OS-facing helpers

enum SystemActions {
    /// Opens the Accessibility pane in System Settings. Logs if the OS
    /// refuses to open the URL (very unusual, but we'd want to know).
    static func openAccessibilitySettings() {
        let url = URL(string:
            "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")!
        if !NSWorkspace.shared.open(url) {
            Log.warn("Could not open Accessibility settings URL: \(url)")
        }
    }

    /// Simple modal alert. Intentionally basic: alerts are only used for a
    /// handful of unusual states (missing permission, no shortcut, wrong
    /// monitor count), not as a general notification channel.
    static func showAlert(title: String, message: String) {
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = message
        alert.addButton(withTitle: "OK")
        alert.runModal()
    }
}

// MARK: - Blink overlay

/// Covers every screen with a black overlay that fades in, executes an action
/// while the screen is hidden, then fades out. Used to mask the visual motion
/// of many windows moving simultaneously during a swap.
///
/// The overlay sits at .screenSaver window level, which is above the Dock,
/// menu bar, and normal app windows, but below the cursor. It ignores mouse
/// events so it doesn't eat clicks if the user happens to click during the
/// brief blink.
enum BlinkOverlay {
    private static var inProgress = false

    /// Runs `action` while the screen is fully covered by a black overlay.
    /// The overlay fades in, `action` runs (synchronously, so the action's
    /// own duration is naturally awaited), a settle delay gives apps time
    /// to finish any in-flight window animations they may be running in
    /// response, then the overlay fades out.
    ///
    /// - Parameters:
    ///   - fadeDuration: fade time for each of the fade-in and fade-out phases.
    ///   - settleTime:   pause between action completion and fade-out.
    ///                   Needed because AX-calls return before the target
    ///                   app has finished visually animating the move.
    ///   - action:       the work to perform while the screen is covered.
    ///
    /// Safety: a hard timeout of 5 seconds from the start will unconditionally
    /// remove all overlays and reset state, even if every completion handler
    /// silently stops firing. The user will never stare at a black screen for
    /// longer than that.
    static func run(fadeDuration: TimeInterval = 0.12,
                    settleTime: TimeInterval = 0.15,
                    action: @escaping () -> Void) {
        if inProgress { return }
        inProgress = true

        // Defensive: filter out screens with degenerate frames. This can
        // briefly happen during display hotplug events, where an NSScreen
        // instance exists but its frame is zero. Building an overlay for
        // such a screen would produce an invisible no-op window.
        let screens = NSScreen.screens.filter { $0.frame.width > 0 && $0.frame.height > 0 }

        // Defensive: with no usable screens we skip the swap entirely.
        // Reaching this branch means the display configuration changed
        // between swapNow's monitor check and this moment. The moves in
        // `action` were computed from the old layout, so executing them
        // now could push windows to coordinates that no longer exist.
        // Safer to do nothing; the user can press the shortcut again.
        guard !screens.isEmpty else {
            inProgress = false
            return
        }

        let overlays = screens.map(makeOverlay(for:))
        for overlay in overlays { overlay.orderFront(nil) }

        // Hard failsafe: 5 seconds after the blink starts, force-remove the
        // overlays and reset state. This triggers only if something went
        // seriously wrong (completion handler never fired, action() hung,
        // animation framework stuck). Normal swaps finish in well under a
        // second, so this timeout is only reached in failure modes.
        let hardTimeout: TimeInterval = 5.0
        DispatchQueue.main.asyncAfter(deadline: .now() + hardTimeout) {
            if inProgress {
                Log.warn("Blink overlay timeout after \(hardTimeout)s; forcing reset. This indicates the normal animation chain did not finish.")
                for overlay in overlays { overlay.orderOut(nil) }
                inProgress = false
            }
        }

        NSAnimationContext.runAnimationGroup({ ctx in
            ctx.duration = fadeDuration
            for overlay in overlays { overlay.animator().alphaValue = 1.0 }
        }, completionHandler: {
            // action() is synchronous. It only returns once every AX-call
            // has been answered by the target apps, so longer swaps (more
            // windows) naturally keep the overlay up longer.
            action()
            DispatchQueue.main.asyncAfter(deadline: .now() + settleTime) {
                NSAnimationContext.runAnimationGroup({ ctx in
                    ctx.duration = fadeDuration
                    for overlay in overlays { overlay.animator().alphaValue = 0.0 }
                }, completionHandler: {
                    for overlay in overlays { overlay.orderOut(nil) }
                    inProgress = false
                })
            }
        })
    }

    private static func makeOverlay(for screen: NSScreen) -> NSWindow {
        // Note: we intentionally do NOT pass `screen:` to the NSWindow
        // initializer. When that parameter is provided, AppKit reinterprets
        // `contentRect` in the screen's local coordinate system instead of
        // the global one. That works fine on the primary display (local ==
        // global there) but places overlays wrong on secondary displays.
        let window = NSWindow(
            contentRect: screen.frame,
            styleMask: .borderless,
            backing: .buffered,
            defer: false
        )
        window.isOpaque = false
        window.hasShadow = false
        window.backgroundColor = .black
        window.alphaValue = 0.0
        window.level = .screenSaver
        window.ignoresMouseEvents = true
        window.collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle]
        // Belt-and-braces: set the frame again after creation so the window
        // lands exactly where we want regardless of any default placement.
        window.setFrame(screen.frame, display: false)
        return window
    }
}
