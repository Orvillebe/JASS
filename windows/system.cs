//
// system.cs
// JustAScreenSwitcher (JASS) for Windows
//
// Everything that touches Windows directly:
//   - Logging (file + stderr)
//   - Tray icon and its menu (NotifyIcon wrapper)
//   - SystemActions: simple alerts (MessageBox) and other small OS calls
//   - (later stages) hotkey, monitor enumeration, window enumeration and
//     moves, blink overlay, shortcut recorder.
//
// Pure logic and domain types live in app.cs.
//

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Jass;

// MARK: - Logging

/// <summary>
/// Minimal log facility. Writes to stderr (visible if launched from a
/// console) and appends to %LOCALAPPDATA%\JASS\jass.log. Same shape as the
/// Mac Log enum: a small fixed surface (Info/Warn/Error), no structured
/// telemetry, no rotation. The file logs are the one place we keep enough
/// trail to diagnose a misbehaving swap on a user's machine.
/// </summary>
internal static class Log
{
    /// <summary>
    /// Path to the log file. Lives under LocalAppData (not Roaming) because
    /// logs are per-machine debug data, not per-user preferences. Same
    /// reasoning the Mac version uses for ~/Library/Logs vs
    /// ~/Library/Application Support.
    /// </summary>
    public static string FilePath { get; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JASS",
            "jass.log"
        );

    // If the file write fails once we stop trying for the rest of the
    // process. Stderr keeps getting every line so no information is lost,
    // but we don't want to spam the user with repeated open-file errors.
    private static bool _fileLogDisabled;
    private static readonly object _gate = new();

    public static void Info(string message)  => Write("INFO",  message);
    public static void Warn(string message)  => Write("WARN",  message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        var line = $"{Timestamp()} [{level}] {message}{Environment.NewLine}";

        // Stderr first so a broken file path never robs us of the message.
        try { Console.Error.Write(line); } catch { /* console may be closed */ }

        if (_fileLogDisabled) return;

        lock (_gate)
        {
            try
            {
                AppendToFile(line);
            }
            catch (Exception ex)
            {
                _fileLogDisabled = true;
                try
                {
                    Console.Error.WriteLine(
                        $"[JASS] File logging disabled: {ex.Message}. Further logs go to stderr only."
                    );
                }
                catch { /* console may be closed */ }
            }
        }
    }

    private static void AppendToFile(string line)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        // Append; create if missing. UTF-8 without BOM keeps the file plain.
        File.AppendAllText(FilePath, line);
    }

    private static string Timestamp() =>
        DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz");
}

// MARK: - Tray icon

/// <summary>
/// The Windows-tray equivalent of NSStatusItem. Wraps NotifyIcon (a thin
/// shell-API helper that ships with WinForms) and exposes only the shape
/// that JassApp needs: hand me a builder callback, I'll show the icon and
/// invoke your callback every time the user right-clicks to (re)build the
/// menu.
///
/// The "build on every open" pattern is what lets JassApp drive every label
/// and checkmark from a single _config field, mirroring rebuildMenu() on
/// Mac.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly Action<ContextMenuStrip> _builder;

    public TrayIcon(Action<ContextMenuStrip> builder)
    {
        _builder = builder;

        _menu = new ContextMenuStrip();
        // Rebuild from scratch right before each open. JassApp.BuildMenu
        // is responsible for clearing _menu.Items and adding fresh items.
        _menu.Opening += (_, _) => _builder(_menu);

        _icon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "JASS",
            Visible = true,
            ContextMenuStrip = _menu,
        };
    }

    /// <summary>
    /// Pull the icon out of the running exe. csproj sets jass.ico as the
    /// ApplicationIcon, so it ends up as the default group-icon resource
    /// in the published binary. ExtractAssociatedIcon then gives us the
    /// best-fitting size for the tray context (typically 16x16 or 32x32
    /// at 100% / higher-DPI displays).
    ///
    /// Falls back to the generic system Application icon if extraction
    /// fails for any reason (e.g. running unpublished from a debugger
    /// where the resource hasn't been embedded). That keeps the tray
    /// from disappearing on dev runs.
    /// </summary>
    private static Icon LoadAppIcon()
    {
        try
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exePath))
            {
                var icon = Icon.ExtractAssociatedIcon(exePath);
                if (icon != null) return icon;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"TrayIcon: failed to extract icon from exe ({ex.Message}); using system default");
        }
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}

// MARK: - Small OS-facing helpers

/// <summary>
/// Small wrapper around things the rest of the app should not import
/// System.Windows.Forms directly to do. Mirrors SystemActions on Mac.
/// </summary>
internal static class SystemActions
{
    /// <summary>
    /// Simple modal alert. Intentionally basic: alerts are only used for a
    /// handful of unusual states (missing setup, bad monitor count, swap
    /// problems), not as a general notification channel.
    /// </summary>
    public static void ShowAlert(string title, string message)
    {
        // MessageBox is modal but does not require a parent window, which
        // is exactly what we want for a tray-only app with no main form.
        MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}

// MARK: - Monitor enumeration

/// <summary>
/// Enumerate connected monitors and ask which one is "primary" or "under
/// the cursor". Equivalent of SystemMonitors on Mac.
///
/// Coordinate space: physical pixels in the Windows virtual-screen system.
/// The primary monitor's top-left is (0, 0); monitors to the left or above
/// have negative coordinates. Y-axis points down. Per-Monitor V2 DPI
/// awareness (declared in app.manifest) ensures these are real pixel values
/// rather than DPI-virtualized ones, which matters on mixed-DPI multi-
/// monitor setups.
/// </summary>
internal static class SystemMonitors
{
    /// <summary>
    /// Returns all currently connected monitors, ordered as Windows reports
    /// them. The order is stable as long as the monitor configuration does
    /// not change, which is what callers rely on within a single swap cycle.
    /// </summary>
    public static List<Monitor> EnumerateAll()
    {
        var result = new List<Monitor>();
        var index = 0;

        // EnumDisplayMonitors invokes our callback once per connected
        // monitor, synchronously. The callback receives the HMONITOR plus
        // the monitor's rect; we ignore HMONITOR here because the rect
        // alone is enough to build a Monitor record. Anchor lookup
        // re-enumerates separately to read the primary flag.
        bool ok = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMon, IntPtr hdc, ref RECT rect, IntPtr lParam) =>
            {
                result.Add(new Monitor(index++, RectangleFromRECT(rect)));
                return true;  // continue enumeration
            },
            IntPtr.Zero);

        if (!ok)
        {
            Log.Warn("EnumDisplayMonitors returned false; monitor list may be incomplete");
        }
        return result;
    }

    /// <summary>
    /// Returns the "anchor" monitor: on Windows we treat that as the
    /// primary monitor (the one Windows itself flags with
    /// MONITORINFOF_PRIMARY). On laptops this is in practice always the
    /// built-in screen. EDID-based built-in detection was considered but
    /// rejected because EDID data is unreliable in the wild (virtual
    /// displays, weird docks, mislabelled panels).
    ///
    /// Falls back to the first monitor if Windows somehow flags none, which
    /// shouldn't normally happen but we cope rather than crash.
    /// </summary>
    public static Monitor? AnchorMonitor(IList<Monitor> monitors)
    {
        if (monitors.Count == 0) return null;

        Monitor? anchor = null;
        var index = 0;

        // Re-enumerate so we can call GetMonitorInfo on each HMONITOR.
        // The enumeration order is the same as in EnumerateAll, so
        // walking with a parallel index lets us match back to the
        // pre-enumerated `monitors` list.
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMon, IntPtr hdc, ref RECT rect, IntPtr lParam) =>
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(hMon, ref info) &&
                    (info.dwFlags & MONITORINFOF_PRIMARY) != 0)
                {
                    foreach (var m in monitors)
                    {
                        if (m.Id == index) { anchor = m; break; }
                    }
                    return false;  // primary found, stop
                }
                index++;
                return true;
            },
            IntPtr.Zero);

        if (anchor is null)
        {
            Log.Warn("No primary monitor reported by Windows; falling back to first enumerated");
            return monitors[0];
        }
        return anchor;
    }

    /// <summary>
    /// Returns the monitor that currently contains the mouse cursor, or
    /// null if the cursor falls outside every monitor in the passed list
    /// (which can briefly happen during display reconfiguration). Mirrors
    /// monitorUnderCursor on Mac.
    /// </summary>
    public static Monitor? MonitorUnderCursor(IList<Monitor> monitors)
    {
        if (!GetCursorPos(out var cursor))
        {
            Log.Warn("GetCursorPos failed; cannot determine monitor under cursor");
            return null;
        }
        foreach (var m in monitors)
        {
            if (m.Frame.Contains(cursor.X, cursor.Y))
            {
                return m;
            }
        }
        return null;
    }

    private static Rectangle RectangleFromRECT(RECT r) =>
        Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);

    // MARK: P/Invoke

    private delegate bool MonitorEnumProc(
        IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    private const uint MONITORINFOF_PRIMARY = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}

// MARK: - Window enumeration and moving

/// <summary>
/// Enumerate user-facing top-level windows and apply WindowMoves to them.
/// Equivalent of SystemWindows on Mac, but using EnumWindows + GetWindow
/// Placement / SetWindowPlacement instead of the Accessibility API.
///
/// Why GetWindowPlacement / SetWindowPlacement rather than SetWindowPos:
/// SetWindowPlacement preserves and restores the maximize state in one
/// atomic call. Moving a maximized window with SetWindowPos would either
/// drop the maximize state or require a 3-step un-maximize / move /
/// re-maximize dance with visible flicker. SetWindowPlacement just works:
/// the showCmd field tells Windows the desired final state, and
/// rcNormalPosition tells Windows where to un-maximize to (which also
/// determines the destination monitor when SW_SHOWMAXIMIZED is requested).
/// </summary>
internal static class SystemWindows
{
    /// <summary>
    /// Summary of what happened during a call to Apply. Returned to the
    /// caller so they can log, notify, or react to partial failures.
    /// Same fields as the Mac MoveReport.
    /// </summary>
    public struct MoveReport
    {
        public int Attempted;
        public int Succeeded;
        /// <summary>Move failed; window is unchanged.</summary>
        public int RolledBack;
        /// <summary>
        /// Move failed and the window may be in an indeterminate state.
        /// SetWindowPlacement is documented atomic, so on Windows this
        /// stays at zero in practice; we keep the bucket so the report
        /// shape matches Mac's and any future non-atomic apply path can
        /// use it.
        /// </summary>
        public int Stuck;
        /// <summary>
        /// We could not even read the window's original placement, so the
        /// move was not attempted. Or the window changed state (e.g. was
        /// minimized) between enumerate and apply.
        /// </summary>
        public int Skipped;

        public bool HasProblems => RolledBack + Stuck + Skipped > 0;
    }

    /// <summary>
    /// Walk all top-level windows and produce a WindowInfo for each one
    /// that's a candidate for swapping. Filters out anything we can't or
    /// shouldn't move: invisible windows, tool windows without explicit
    /// app-window flag, owned windows (sub-dialogs, popups), windows
    /// cloaked by DWM (on a different virtual desktop), minimized windows,
    /// and the desktop / shell / taskbar windows.
    /// </summary>
    public static List<WindowInfo> EnumerateAll(IList<Monitor> monitors)
    {
        var result = new List<WindowInfo>();
        EnumWindows((hwnd, _) =>
        {
            try
            {
                if (TryBuildWindowInfo(hwnd, monitors) is { } info)
                {
                    result.Add(info);
                }
            }
            catch (Exception ex)
            {
                // A single window's metadata read shouldn't kill the whole
                // enumeration. Log and move on.
                Log.Warn($"hwnd={hwnd}: error reading window info: {ex.Message}");
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static WindowInfo? TryBuildWindowInfo(IntPtr hwnd, IList<Monitor> monitors)
    {
        if (!IsAltTabVisible(hwnd)) return null;
        if (IsCloaked(hwnd)) return null;
        if (IsIconic(hwnd))  return null;  // skip minimized; matches Mac

        // Reject the desktop and the shell's hidden host window. Both have
        // distinctive class names. They'd otherwise pass the alt-tab
        // heuristic on some Windows versions because they're visible, not
        // owned, and not tool windows.
        var className = GetClassNameSafe(hwnd);
        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
        {
            return null;
        }

        if (!GetWindowRect(hwnd, out var visualRect)) return null;
        var visualBounds = RectangleFromRECT(visualRect);
        // Some apps register zero-sized placeholder windows; ignore them,
        // same as the Mac code does for AX-reported zero-size windows.
        if (visualBounds.Width <= 1 || visualBounds.Height <= 1) return null;

        // Determine which monitor the window is on. Use the visible bounds
        // for this; for a maximized window, GetWindowRect gives the full
        // monitor extent, which the center-in-frame check resolves
        // correctly.
        if (FindMonitorFor(visualBounds, monitors) is not { } sourceMonitor)
        {
            return null;
        }

        var pl = NewPlacement();
        if (!GetWindowPlacement(hwnd, ref pl))
        {
            Log.Warn($"hwnd={hwnd}: GetWindowPlacement failed during enumeration");
            return null;
        }

        var wasMaximized = pl.showCmd == SW_SHOWMAXIMIZED;

        // Compute the "relative frame": where this window sits within its
        // source monitor, expressed as fractions of the monitor frame
        // (0..1). On apply, the same fractions are mapped onto the target
        // monitor so a window keeps its proportional position even when
        // the target has a different resolution.
        //
        // Maximized windows are a special case. Windows stores
        // rcNormalPosition for them too (the un-maximize geometry), but
        // it can extend beyond the monitor — Windows is allowed to
        // overshoot because the rect won't actually be displayed while
        // SW_SHOWMAXIMIZED is in effect. Using that overshoot rect as
        // input to the relative-frame math compounds across swaps:
        // each apply rounds the oversized rect into rcNormalPosition on
        // the target, and the next enumerate reads it back even larger.
        // Diagnosed in the field: a maximized window grew ~10% per swap.
        //
        // Resolution: for maximized windows we don't carry a meaningful
        // relative frame at all; on apply, the target monitor's work
        // area is used as rcNormalPosition. The window stays maximized
        // on arrival, and if the user un-maximizes it later it lands at
        // a sane size on the destination monitor.
        var sf = sourceMonitor.Frame;
        RectangleF relative;
        if (wasMaximized)
        {
            // Sentinel value: the apply path checks WasMaximized and
            // ignores TargetRelativeFrame. We pass an obviously-empty
            // rect so any code path that *did* try to use it would land
            // somewhere harmless rather than at oversized coordinates.
            relative = RectangleF.Empty;
        }
        else
        {
            relative = new RectangleF(
                (float)(visualBounds.X - sf.X) / sf.Width,
                (float)(visualBounds.Y - sf.Y) / sf.Height,
                (float)visualBounds.Width  / sf.Width,
                (float)visualBounds.Height / sf.Height
            );
        }

        GetWindowThreadProcessId(hwnd, out var pid);
        var title = GetWindowTextSafe(hwnd);

        // DIAGNOSTIC (stage 5b): log every window we accept, with all the
        // numbers we feed into computeSwapMoves. Lets us see in the log
        // exactly how a window got classified before any move happens.
        Log.Info(
            $"  enum: hwnd={hwnd} pid={pid} title='{TruncateForLog(title)}' " +
            $"monitor={sourceMonitor.Id} max={wasMaximized} " +
            $"visual=({visualBounds.X},{visualBounds.Y} {visualBounds.Width}x{visualBounds.Height}) " +
            $"relative=({relative.X:F3},{relative.Y:F3} {relative.Width:F3}x{relative.Height:F3})"
        );

        return new WindowInfo(
            new WindowReference(hwnd, pid, title),
            sourceMonitor.Id,
            relative,
            wasMaximized
        );
    }

    /// <summary>
    /// "Alt-tab visible" heuristic: a window is a real top-level user
    /// window if it's visible, not a tool window (unless explicitly
    /// flagged as app window), and either has no owner or is flagged as
    /// app window. This catches the typical taskbar/alt-tab list and
    /// excludes popup helpers, tray helpers, etc.
    /// </summary>
    private static bool IsAltTabVisible(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd)) return false;
        var ex = (uint)GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        var hasAppWindow  = (ex & WS_EX_APPWINDOW) != 0;
        var hasToolWindow = (ex & WS_EX_TOOLWINDOW) != 0;
        var owner = GetWindow(hwnd, GW_OWNER);
        if (hasAppWindow) return true;
        if (hasToolWindow) return false;
        if (owner != IntPtr.Zero) return false;
        return true;
    }

    /// <summary>
    /// True if the window is "cloaked" by the Desktop Window Manager.
    /// In modern Windows, cloaking is what's used for:
    ///   - windows on a different virtual desktop than the current one
    ///   - UWP/Store apps that have been suspended off-screen
    /// We skip cloaked windows because moving them would either be a
    /// no-op (different virtual desktop, user doesn't see it) or interfere
    /// with the platform's lifecycle (suspended UWP).
    /// </summary>
    private static bool IsCloaked(IntPtr hwnd)
    {
        int cloaked = 0;
        // DWMWA_CLOAKED is supported from Windows 8 onwards. The hr will
        // be a failure on older OSes; treat that as "not cloaked" so JASS
        // still works there.
        var hr = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out cloaked, sizeof(int));
        if (hr != 0) return false;
        return cloaked != 0;
    }

    /// <summary>
    /// Pick the monitor that contains the window's center, falling back
    /// to the monitor with the largest area-overlap for windows that
    /// straddle edges.
    /// </summary>
    private static Monitor? FindMonitorFor(Rectangle frame, IList<Monitor> monitors)
    {
        var center = new Point(frame.X + frame.Width / 2, frame.Y + frame.Height / 2);
        foreach (var m in monitors)
        {
            if (m.Frame.Contains(center)) return m;
        }
        Monitor? best = null;
        long bestArea = 0;
        foreach (var m in monitors)
        {
            var inter = Rectangle.Intersect(m.Frame, frame);
            long area = (long)Math.Max(0, inter.Width) * Math.Max(0, inter.Height);
            if (area > bestArea) { bestArea = area; best = m; }
        }
        return best;
    }

    /// <summary>
    /// Apply each move to its window. Each window is handled
    /// independently: a failure on one does not stop the others. On
    /// Windows, SetWindowPlacement is atomic, so a failed call leaves the
    /// window in its original state and counts as RolledBack; there's no
    /// partial-state recovery to do.
    ///
    /// `monitors` here is the swap pair, not the full list of connected
    /// displays. We re-check that those two are still present at apply
    /// time; the presence or absence of other monitors does not matter.
    /// </summary>
    public static MoveReport Apply(IList<WindowMove> moves, IList<Monitor> monitors)
    {
        // Re-check: between computing the moves and applying them, a
        // display could have been disconnected (sleep transition, USB-C
        // hub hiccup, hotplug during the blink). If any monitor we're
        // about to move windows TO is no longer connected, abort entirely.
        // Apply would otherwise push windows to coordinates outside any
        // visible display.
        var current = SystemMonitors.EnumerateAll();
        var currentIds = new HashSet<int>();
        foreach (var m in current) currentIds.Add(m.Id);
        foreach (var m in monitors)
        {
            if (!currentIds.Contains(m.Id))
            {
                Log.Warn($"A monitor in the swap pair (id={m.Id}) disappeared between compute and apply; aborting swap");
                return new MoveReport { Skipped = moves.Count };
            }
        }

        // Build the target lookup from the current monitors so we use up-
        // to-date frames in case a display's resolution changed between
        // compute and apply.
        var byId = new Dictionary<int, Monitor>();
        foreach (var m in current) byId[m.Id] = m;

        var report = new MoveReport();
        foreach (var move in moves)
        {
            if (!byId.TryGetValue(move.TargetMonitorId, out var target))
            {
                // Defensive: the upfront check should have caught this,
                // but keeping the per-move guard avoids ever pushing a
                // window to coordinates from a now-gone monitor.
                Log.Warn($"Move refers to unknown monitor id {move.TargetMonitorId}; skipping");
                report.Skipped++;
                continue;
            }

            // Compute the target rect.
            //
            // For maximized windows: ignore the relative frame entirely
            // (it carried no meaningful data — see the enumerate side)
            // and use the target monitor's work area as rcNormalPosition.
            // The window stays maximized on arrival; if the user un-
            // maximizes it later, it lands at a sane size on the new
            // monitor.
            //
            // For normal windows: scale the source-relative frame onto
            // the target monitor.
            Rectangle absolute;
            if (move.WasMaximized)
            {
                // Resolve the target monitor's work area (rcWork). We pull
                // it via GetMonitorInfo on the HMONITOR that contains the
                // target frame's center, so taskbar position and side
                // panels are reflected even when they differ between
                // monitors.
                if (TargetWorkArea(target) is not { } workArea)
                {
                    Log.Warn($"Cannot resolve work area for target monitor {target.Id}; skipping move");
                    report.Skipped++;
                    continue;
                }
                absolute = workArea;
            }
            else
            {
                absolute = AbsoluteFrame(move.TargetRelativeFrame, target);
            }

            // Sanity check: the computed target frame must overlap at
            // least one current monitor. Prevents any scenario where
            // rounding, a stale monitor rect, or a miscomputation could
            // push a window to coordinates that are not visible.
            var onScreen = false;
            foreach (var m in current)
            {
                if (m.Frame.IntersectsWith(absolute)) { onScreen = true; break; }
            }
            if (!onScreen)
            {
                Log.Warn($"Target frame {absolute} falls outside all current monitors; skipping to avoid losing window");
                report.Skipped++;
                continue;
            }

            var outcome = ApplyMove(move, absolute);
            report.Attempted++;
            switch (outcome)
            {
                case MoveOutcome.Success:    report.Succeeded++;  break;
                case MoveOutcome.RolledBack: report.RolledBack++; break;
                case MoveOutcome.Stuck:      report.Stuck++;      break;
                case MoveOutcome.Skipped:    report.Skipped++;    break;
            }
        }
        return report;
    }

    /// <summary>
    /// Returns the target monitor's work area (the part of the screen
    /// excluding the taskbar and other docked appbars), in virtual-
    /// screen coordinates. Used as the destination rectangle for
    /// maximized windows: a maximized window on a monitor has its
    /// rcNormalPosition set to that monitor's work area, so this is
    /// what gives Windows the correct un-maximize geometry.
    /// </summary>
    private static Rectangle? TargetWorkArea(Monitor monitor)
    {
        // Pick a point safely inside the monitor for HMONITOR resolution.
        var centerVS = new Point(
            monitor.Frame.X + monitor.Frame.Width / 2,
            monitor.Frame.Y + monitor.Frame.Height / 2);
        var pt = new POINT(centerVS.X, centerVS.Y);
        var hMon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        if (hMon == IntPtr.Zero) return null;

        var mi = NewMonitorInfo();
        if (!GetMonitorInfo(hMon, ref mi)) return null;

        return Rectangle.FromLTRB(
            mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom);
    }

    private enum MoveOutcome { Success, RolledBack, Stuck, Skipped }

    private static MoveOutcome ApplyMove(WindowMove move, Rectangle targetVS)
    {
        var hwnd = move.Reference.HWnd;

        // Skip if the window was minimized between enumerate and apply.
        // Touching it would un-minimize, which is not what the user
        // expects: minimized windows are explicitly left alone, matching
        // Mac semantics.
        if (IsIconic(hwnd))
        {
            Log.Info($"hwnd={hwnd} pid={move.Reference.Pid}: minimized between enumerate and apply; leaving alone");
            return MoveOutcome.Skipped;
        }

        // DIAGNOSTIC (stage 5b): log the input of every move so we can
        // correlate enum-time observations with apply-time effects when
        // something doesn't behave.
        Log.Info(
            $"  apply: hwnd={hwnd} title='{TruncateForLog(move.Reference.Title)}' " +
            $"target_monitor={move.TargetMonitorId} max={move.WasMaximized} " +
            $"target_vs=({targetVS.X},{targetVS.Y} {targetVS.Width}x{targetVS.Height})"
        );

        // Two apply paths because Windows' two relevant APIs each have
        // limitations the other doesn't:
        //
        //   - SetWindowPlacement is the only way to set/preserve the
        //     SW_SHOWMAXIMIZED state cleanly. But its rcNormalPosition
        //     is in workspace coordinates of the destination monitor,
        //     and the API behaves inconsistently when those coords land
        //     outside the window's current monitor for SW_SHOWNORMAL
        //     (observed: window resizes but does not move). Reliable for
        //     maximized; not for normal.
        //
        //   - SetWindowPos takes raw virtual-screen coords and always
        //     moves the window where you tell it. But it can't change a
        //     maximized window's "maximized" flag — calling it on a
        //     maximized HWND moves the (visible) maximized rect to a
        //     location that no longer matches any monitor's work area,
        //     leaving Windows in an inconsistent state. Reliable for
        //     normal; not for maximized.
        //
        // So: maximized -> SetWindowPlacement, normal -> SetWindowPos.
        return move.WasMaximized
            ? ApplyMaximized(move, targetVS)
            : ApplyNormal(move, targetVS);
    }

    /// <summary>
    /// Move a maximized window to a new monitor, preserving its
    /// maximized state. The targetVS rect is the destination monitor's
    /// work area in virtual-screen coordinates; we convert that to the
    /// workspace coordinates SetWindowPlacement expects.
    /// </summary>
    private static MoveOutcome ApplyMaximized(WindowMove move, Rectangle targetVS)
    {
        var hwnd = move.Reference.HWnd;

        // Resolve the target monitor's HMONITOR from its center point so
        // we get its actual work-area origin (which can differ from the
        // monitor frame when a taskbar or app-bar is docked there).
        var center = new POINT(
            targetVS.X + targetVS.Width / 2,
            targetVS.Y + targetVS.Height / 2);
        var hTargetMon = MonitorFromPoint(center, MONITOR_DEFAULTTONEAREST);
        var targetMi = NewMonitorInfo();
        if (hTargetMon == IntPtr.Zero || !GetMonitorInfo(hTargetMon, ref targetMi))
        {
            Log.Warn($"hwnd={hwnd}: cannot resolve target monitor work area; skipping");
            return MoveOutcome.Skipped;
        }

        // Three-step move for maximized windows:
        //
        //   1. Un-maximize via SetWindowPlacement(SW_SHOWNORMAL).
        //   2. SetWindowPos to the target monitor's work area.
        //   3. Re-maximize via SetWindowPlacement(SW_SHOWMAXIMIZED).
        //
        // Why not just SetWindowPlacement directly: many apps draw their
        // own title-bar and report SW_SHOWMAXIMIZED while actually being
        // a borderless window pinned to the work area. For those
        // (Brave, PrusaSlicer, Electron-based apps in general)
        // SetWindowPlacement returns success but doesn't visibly move
        // anything — the app's own rendering keeps the window pinned to
        // its old monitor. SetWindowPos in between forces a real move
        // through the regular window-position pipeline that even
        // self-rendered windows can't ignore. Re-maximizing afterwards
        // restores the maximized state on the new monitor cleanly.
        //
        // The brief un-maximize + re-maximize causes one extra repaint
        // that's normally hidden behind the blink overlay (stage 6).
        // Without the overlay it can be slightly visible; that's the
        // tradeoff for actually working on apps that do their own chrome.

        // DIAGNOSTIC: target monitor work-area for context.
        Log.Info($"  apply-max: hwnd={hwnd} target_workArea=({targetMi.rcWork.Left},{targetMi.rcWork.Top} -> {targetMi.rcWork.Right},{targetMi.rcWork.Bottom})");

        var unMaxed = NewPlacement();
        if (!GetWindowPlacement(hwnd, ref unMaxed))
        {
            var err = Marshal.GetLastWin32Error();
            Log.Warn($"hwnd={hwnd}: GetWindowPlacement failed (err {err}); skipping");
            return MoveOutcome.Skipped;
        }
        Log.Info($"  apply-max step0: pre showCmd={unMaxed.showCmd} rcNormalPosition=({unMaxed.rcNormalPosition.Left},{unMaxed.rcNormalPosition.Top} -> {unMaxed.rcNormalPosition.Right},{unMaxed.rcNormalPosition.Bottom})");
        var originalShowCmd = unMaxed.showCmd;
        unMaxed.flags = 0;
        unMaxed.showCmd = SW_SHOWNORMAL;
        if (!SetWindowPlacement(hwnd, ref unMaxed))
        {
            var err = Marshal.GetLastWin32Error();
            Log.Warn($"hwnd={hwnd} pid={move.Reference.Pid} title='{TruncateForLog(move.Reference.Title)}': SetWindowPlacement(un-maximize) failed (err {err}); window unchanged");
            return MoveOutcome.RolledBack;
        }
        DiagnosticLogPlacementAndRect(hwnd, "apply-max step1 (un-maximized)");

        // Step 2: physically move to the target monitor.
        //
        // SWP_NOZORDER preserves stacking. SWP_NOACTIVATE prevents focus
        // changes. We deliberately do NOT use SWP_ASYNCWINDOWPOS here —
        // the next call (re-maximize via SetWindowPlacement) reads the
        // window's current monitor to decide which one to maximize on.
        // If the move is still in flight, that decision can land on the
        // wrong monitor (observed: intern->extern swaps that silently
        // re-maximized back on intern). Synchronous SetWindowPos blocks
        // until the target process acks WM_WINDOWPOSCHANGED, which is
        // slower but reliable.
        const uint flags = SWP_NOZORDER | SWP_NOACTIVATE;
        if (!SetWindowPos(
                hwnd, IntPtr.Zero,
                targetVS.X, targetVS.Y, targetVS.Width, targetVS.Height,
                flags))
        {
            var err = Marshal.GetLastWin32Error();
            Log.Warn($"hwnd={hwnd} pid={move.Reference.Pid} title='{TruncateForLog(move.Reference.Title)}': SetWindowPos(move) failed (err {err}); attempting to restore original maximized state");
            unMaxed.showCmd = originalShowCmd;
            if (!SetWindowPlacement(hwnd, ref unMaxed))
            {
                Log.Warn($"hwnd={hwnd}: rollback to original placement also failed; window may be in unmaximized state");
                return MoveOutcome.Stuck;
            }
            return MoveOutcome.RolledBack;
        }
        // Brief settle window. With synchronous SetWindowPos the move
        // is technically complete when the call returns, but apps
        // running their own paint pipeline (Brave, Electron, etc) need
        // a handful of extra ms before the next call sees a stable
        // state. 0ms produces visible jumping at the end of a swap;
        // 15ms removes that on the test setup while only adding ~180ms
        // across a 12-window swap.
        System.Threading.Thread.Sleep(15);
        DiagnosticLogPlacementAndRect(hwnd, "apply-max step2 (after SetWindowPos+settle)");

        // Step 3: re-maximize on the target monitor.
        //
        // rcNormalPosition is in VIRTUAL SCREEN coordinates (absolute,
        // signed, can be negative for monitors above/left of primary),
        // not workspace-relative coordinates as the older Win16 API
        // documentation suggests. With SW_SHOWMAXIMIZED, Windows looks
        // at WHERE rcNormalPosition lies in virtual-screen space and
        // maximizes the window on whichever monitor contains those
        // coordinates. If we feed it (0,0 → 1920,1152) when the target
        // is the upper monitor at virtual Y=-1200, Windows sees a rect
        // on the lower (primary) monitor and maximizes there — exactly
        // the silent regression we hit before this fix.
        //
        // Correct: pass the target monitor's work area in virtual-screen
        // coordinates directly (rcWork is already in those units).
        var workspaceRect = targetMi.rcWork;
        var pl = unMaxed;
        pl.flags = 0;
        pl.showCmd = SW_SHOWMAXIMIZED;
        pl.rcNormalPosition = workspaceRect;
        Log.Info($"  apply-max step3 setting: showCmd={pl.showCmd} rcNormalPosition=({pl.rcNormalPosition.Left},{pl.rcNormalPosition.Top} -> {pl.rcNormalPosition.Right},{pl.rcNormalPosition.Bottom})");

        if (!SetWindowPlacement(hwnd, ref pl))
        {
            var err = Marshal.GetLastWin32Error();
            Log.Warn($"hwnd={hwnd} pid={move.Reference.Pid} title='{TruncateForLog(move.Reference.Title)}': SetWindowPlacement(re-maximize) failed (err {err}); window moved but not re-maximized");
            return MoveOutcome.Stuck;
        }
        DiagnosticLogPlacementAndRect(hwnd, "apply-max step3 (re-maximized)");
        return MoveOutcome.Success;
    }

    /// <summary>
    /// Diagnostic helper: dump a window's current GetWindowRect and
    /// GetWindowPlacement state to the log with a label. Reads back
    /// from the OS, so it shows what Windows actually believes about
    /// the window after our last call (subject to async settling).
    /// Used during the maximized-move investigation.
    /// </summary>
    private static void DiagnosticLogPlacementAndRect(IntPtr hwnd, string label)
    {
        var pl = NewPlacement();
        var havePlacement = GetWindowPlacement(hwnd, ref pl);
        var haveRect = GetWindowRect(hwnd, out var rect);
        var rectStr = haveRect
            ? $"({rect.Left},{rect.Top} -> {rect.Right},{rect.Bottom})"
            : "(unavailable)";
        var plStr = havePlacement
            ? $"showCmd={pl.showCmd} rcNormalPosition=({pl.rcNormalPosition.Left},{pl.rcNormalPosition.Top} -> {pl.rcNormalPosition.Right},{pl.rcNormalPosition.Bottom})"
            : "(unavailable)";
        Log.Info($"  {label}: rect={rectStr} {plStr}");
    }

    /// <summary>
    /// Move a non-maximized window to a new virtual-screen rectangle.
    /// Uses SetWindowPos with absolute coordinates because that's the
    /// only API that reliably moves windows across monitor boundaries
    /// without falling into the "resize but don't move" failure mode of
    /// SetWindowPlacement with SW_SHOWNORMAL when the target rect is on
    /// a different monitor than the window currently lives on.
    /// </summary>
    private static MoveOutcome ApplyNormal(WindowMove move, Rectangle targetVS)
    {
        var hwnd = move.Reference.HWnd;

        // Clamp the target to the destination monitor's work area. A
        // non-maximized window should fit inside one monitor's visible
        // area, full stop. We end up here for two cases:
        //
        //   1. The window's source-relative frame was sane but the
        //      target monitor is smaller in some dimension, so the
        //      naive scale produces a slightly oversized result. Clamp
        //      shrinks it to fit.
        //
        //   2. The window is already too big for either monitor, e.g.
        //      because an earlier (buggy) move stretched it. Clamp pulls
        //      it back to a sane size on its way to the destination, so
        //      JASS self-heals over time rather than perpetuating the
        //      damage.
        //
        // Without this, a window can land partially on the destination
        // and partially over the seam between monitors, which is the
        // visible failure mode users report as "the window is half on
        // each screen".
        var clamped = ClampToTargetWorkArea(targetVS);

        // SWP_NOZORDER preserves the current Z-order (we don't want to
        // raise every moved window to the top, that would scramble the
        // stacking order across two monitors). SWP_NOACTIVATE prevents
        // changing focus. SWP_ASYNCWINDOWPOS lets the call return without
        // waiting for the target process to handle WM_WINDOWPOSCHANGING,
        // which keeps a frozen-app's window from blocking the whole swap.
        const uint flags = SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS;

        if (!SetWindowPos(
                hwnd, IntPtr.Zero,
                clamped.X, clamped.Y, clamped.Width, clamped.Height,
                flags))
        {
            var err = Marshal.GetLastWin32Error();
            Log.Warn($"hwnd={hwnd} pid={move.Reference.Pid} title='{TruncateForLog(move.Reference.Title)}': SetWindowPos failed (err {err}); window unchanged");
            return MoveOutcome.RolledBack;
        }
        return MoveOutcome.Success;
    }

    /// <summary>
    /// Returns `desired` shrunk and shifted so it fits entirely within
    /// the work area of whatever monitor `desired`'s center lies on.
    /// Width and height are capped at the work area's; the position is
    /// then nudged so the resulting rect sits flush against the edge
    /// when it would otherwise stick out.
    /// </summary>
    private static Rectangle ClampToTargetWorkArea(Rectangle desired)
    {
        // Resolve the target monitor's HMONITOR from the desired rect's
        // center. If the rect is wider than the monitor, the center is
        // still the most reliable point to identify which monitor the
        // user expects this window to land on.
        var center = new POINT(
            desired.X + desired.Width / 2,
            desired.Y + desired.Height / 2);
        var hMon = MonitorFromPoint(center, MONITOR_DEFAULTTONEAREST);
        if (hMon == IntPtr.Zero) return desired;

        var mi = NewMonitorInfo();
        if (!GetMonitorInfo(hMon, ref mi)) return desired;

        var work = Rectangle.FromLTRB(
            mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom);

        // Cap dimensions first, then nudge position into the work area.
        var width  = Math.Min(desired.Width,  work.Width);
        var height = Math.Min(desired.Height, work.Height);
        var x = Math.Min(Math.Max(desired.X, work.Left), work.Right - width);
        var y = Math.Min(Math.Max(desired.Y, work.Top),  work.Bottom - height);

        return new Rectangle(x, y, width, height);
    }

    /// <summary>
    /// Inverse of WindowInfo's relative-frame computation: given a
    /// fractional frame and a monitor, produce the absolute virtual-
    /// screen rectangle. Uses Math.Round to land on integer pixels so
    /// SetWindowPlacement gets the same shape Windows itself would store.
    /// </summary>
    private static Rectangle AbsoluteFrame(RectangleF relative, Monitor monitor)
    {
        var f = monitor.Frame;
        return new Rectangle(
            (int)Math.Round(f.X + relative.X * f.Width),
            (int)Math.Round(f.Y + relative.Y * f.Height),
            (int)Math.Round(relative.Width  * f.Width),
            (int)Math.Round(relative.Height * f.Height)
        );
    }

    private static Rectangle RectangleFromRECT(RECT r) =>
        Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);

    private static WINDOWPLACEMENT NewPlacement() =>
        new() { length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };

    private static MONITORINFO NewMonitorInfo() =>
        new() { cbSize = Marshal.SizeOf<MONITORINFO>() };

    private static string GetClassNameSafe(IntPtr hwnd)
    {
        var buf = new System.Text.StringBuilder(256);
        var n = GetClassName(hwnd, buf, buf.Capacity);
        return n > 0 ? buf.ToString() : "";
    }

    private static string GetWindowTextSafe(IntPtr hwnd)
    {
        var len = GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        var buf = new System.Text.StringBuilder(len + 1);
        GetWindowText(hwnd, buf, buf.Capacity);
        return buf.ToString();
    }

    private static string TruncateForLog(string s) =>
        s.Length <= 60 ? s : s[..60] + "...";

    // MARK: P/Invoke

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    // On 64-bit Windows, user32.dll exports GetWindowLongPtrW (and ...A);
    // there is no unsuffixed export. The W/A suffix follows the header
    // convention even though the function takes no strings. We compile
    // for win-x64 only, so the Ptr variant is always present and the W
    // suffix is the canonical modern entry point.
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextLengthW")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    private const int GWL_EXSTYLE        = -20;
    private const uint WS_EX_TOOLWINDOW  = 0x00000080;
    private const uint WS_EX_APPWINDOW   = 0x00040000;
    private const uint GW_OWNER          = 4;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int  DWMWA_CLOAKED     = 14;
    private const uint SW_SHOWNORMAL     = 1;
    private const uint SW_SHOWMINIMIZED  = 2;
    private const uint SW_SHOWMAXIMIZED  = 3;

    // SetWindowPos flags. Values from WinUser.h.
    private const uint SWP_NOZORDER       = 0x0004;
    private const uint SWP_NOACTIVATE     = 0x0010;
    private const uint SWP_ASYNCWINDOWPOS = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
        public POINT(int x, int y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public uint length;
        public uint flags;
        public uint showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }
}

// MARK: - Blink overlay

/// <summary>
/// Briefly fades the screen to black during a swap, hiding the visible
/// motion of many windows being repositioned at once. Mirrors the
/// BlinkOverlay on Mac.
///
/// Implementation: one layered topmost window per monitor, filled solid
/// black, alpha animated via SetLayeredWindowAttributes. The overlay
/// windows are click-through (WS_EX_TRANSPARENT) so any UI underneath
/// stays interactive even during the fade — important if the user
/// triggers the hotkey from a focused app, the focus shouldn't move.
/// They're also kept off the taskbar (WS_EX_TOOLWINDOW) and out of
/// alt-tab so JASS doesn't pollute the user's window list.
///
/// The whole sequence is bounded by a hard 5 second timeout; if
/// anything hangs the overlays self-destruct rather than leaving a
/// black screen for the user to figure out.
/// </summary>
internal sealed class BlinkOverlay : IDisposable
{
    private const string OverlayClassName = "JASS_BlinkOverlay";

    // Fade timings. Picked to feel quick but not jarring. Total visual
    // duration when settle=10ms is ~120ms in + 10ms hold + ~120ms out.
    private const int FadeInMs    = 120;
    private const int FadeOutMs   = 120;
    private const int FrameMs     = 16;   // ~60fps fade animation
    private const int HardTimeoutMs = 5000;

    // Win32 layered-window alpha is a byte (0..255).
    private const byte MaxAlpha = 255;

    private readonly List<IntPtr> _overlays = new();
    private readonly WndProcDelegate _wndProc;
    private bool _classRegistered;
    private bool _disposed;

    public BlinkOverlay()
    {
        // Keep the delegate in a field so the GC doesn't collect it
        // while Windows still holds a function pointer to it. A common
        // crash source if you let it become an anonymous local.
        _wndProc = OverlayWndProc;
        EnsureWindowClassRegistered();
    }

    /// <summary>
    /// Run `swap` between a fade-in and a fade-out, with the screen
    /// briefly fully covered in black. If `settleMs` &gt; 0, the cover
    /// is held for that long after `swap` returns to give apps time to
    /// finish their own re-layout animations under the cover.
    ///
    /// If overlay creation fails for any reason (resource limits,
    /// security software interference) we fall through to running
    /// `swap` without the cover rather than blocking the user; the
    /// failure is logged but not surfaced as an alert.
    /// </summary>
    public void Run(IList<Monitor> monitors, int settleMs, Action swap)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BlinkOverlay));

        var overlays = TryCreateOverlays(monitors);
        if (overlays.Count == 0)
        {
            Log.Warn("BlinkOverlay: failed to create any overlay windows; running swap without cover");
            swap();
            return;
        }

        var deadline = Environment.TickCount + HardTimeoutMs;

        try
        {
            FadeAlpha(overlays, fromAlpha: 0, toAlpha: MaxAlpha, durationMs: FadeInMs, deadline);
            swap();
            if (settleMs > 0)
            {
                Sleep(Math.Min(settleMs, RemainingTime(deadline)));
            }
            FadeAlpha(overlays, fromAlpha: MaxAlpha, toAlpha: 0, durationMs: FadeOutMs, deadline);
        }
        finally
        {
            // Always tear down the overlays, even if swap threw or the
            // timeout was hit mid-fade. Leaving a black cover on screen
            // is the worst possible failure mode for a window-manager
            // tool.
            foreach (var hwnd in overlays)
            {
                DestroyWindow(hwnd);
            }
        }
    }

    private List<IntPtr> TryCreateOverlays(IList<Monitor> monitors)
    {
        var hInstance = GetModuleHandle(null);
        var made = new List<IntPtr>();

        foreach (var m in monitors)
        {
            // WS_EX_LAYERED   — enables alpha via SetLayeredWindowAttributes
            // WS_EX_TOPMOST   — stays above every regular window
            // WS_EX_TRANSPARENT — mouse clicks pass through
            // WS_EX_TOOLWINDOW  — keeps it off the taskbar
            // WS_EX_NOACTIVATE  — clicks (if any reached) wouldn't focus it
            const uint exStyle = WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TRANSPARENT
                               | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            // WS_POPUP gives a borderless window with no titlebar/frame.
            const uint style = WS_POPUP;

            var hwnd = CreateWindowEx(
                exStyle, OverlayClassName, "",
                style,
                m.Frame.X, m.Frame.Y, m.Frame.Width, m.Frame.Height,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

            if (hwnd == IntPtr.Zero)
            {
                Log.Warn($"BlinkOverlay: CreateWindowEx failed for monitor {m.Id}: Win32 error {Marshal.GetLastWin32Error()}");
                continue;
            }

            // Start fully transparent so the show-window doesn't flash
            // a solid rectangle before the fade begins.
            SetLayeredWindowAttributes(hwnd, 0, 0, LWA_ALPHA);

            // SW_SHOWNOACTIVATE — display the window without taking focus.
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);
            made.Add(hwnd);
        }
        return made;
    }

    private static void FadeAlpha(IList<IntPtr> overlays, byte fromAlpha, byte toAlpha, int durationMs, int deadline)
    {
        // Linear interpolation across whole frames; the small deviation
        // from a smooth curve isn't worth more code.
        var startTick = Environment.TickCount;
        while (true)
        {
            var elapsed = Environment.TickCount - startTick;
            if (elapsed >= durationMs || Environment.TickCount >= deadline)
            {
                ApplyAlpha(overlays, toAlpha);
                return;
            }
            var t = (double)elapsed / durationMs;
            var alpha = (byte)Math.Round(fromAlpha + (toAlpha - fromAlpha) * t);
            ApplyAlpha(overlays, alpha);
            Sleep(FrameMs);
        }
    }

    private static void ApplyAlpha(IList<IntPtr> overlays, byte alpha)
    {
        foreach (var hwnd in overlays)
        {
            SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
        }
    }

    private static int RemainingTime(int deadline) =>
        Math.Max(0, deadline - Environment.TickCount);

    private static void Sleep(int ms)
    {
        if (ms <= 0) return;
        // Pump messages while waiting so any pending paint requests
        // for our layered windows get processed and the fade actually
        // shows up.
        var until = Environment.TickCount + ms;
        while (Environment.TickCount < until)
        {
            // PeekMessage with PM_REMOVE drains the queue without
            // blocking. We don't dispatch input messages because
            // overlays are click-through and we don't want to handle
            // anything; we just pump paint.
            while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            System.Threading.Thread.Sleep(1);
        }
    }

    private void EnsureWindowClassRegistered()
    {
        if (_classRegistered) return;

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = GetModuleHandle(null),
            lpszClassName = OverlayClassName,
            // Solid black background brush from the system stock pool.
            // GDI manages its lifetime; we don't free it.
            hbrBackground = GetStockObject(BLACK_BRUSH),
        };
        var atom = RegisterClassEx(ref wc);
        if (atom == 0)
        {
            // ERROR_CLASS_ALREADY_EXISTS (1410) is fine — a previous
            // instance left the class registered, we can re-use it.
            var err = Marshal.GetLastWin32Error();
            if (err != 1410)
            {
                Log.Warn($"BlinkOverlay: RegisterClassEx failed: Win32 error {err}");
                return;
            }
        }
        _classRegistered = true;
    }

    private static IntPtr OverlayWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // We don't handle anything specific; the default proc paints
        // the window background (our black brush) which is exactly
        // what we want.
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Window class stays registered for the lifetime of the
        // process. Unregistering it would require all overlay windows
        // to already be destroyed, and risks ERROR_CLASS_HAS_WINDOWS
        // races; the cost of leaving it is one atom and a tiny WNDCLASS
        // entry. Acceptable.
    }

    // MARK: P/Invoke

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        [MarshalAs(UnmanagedType.FunctionPtr)]
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "RegisterClassExW")]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateWindowExW")]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int X, int Y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DispatchMessageW")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    private const uint WS_EX_LAYERED      = 0x00080000;
    private const uint WS_EX_TOPMOST      = 0x00000008;
    private const uint WS_EX_TRANSPARENT  = 0x00000020;
    private const uint WS_EX_TOOLWINDOW   = 0x00000080;
    private const uint WS_EX_NOACTIVATE   = 0x08000000;
    private const uint WS_POPUP           = 0x80000000;

    private const uint LWA_ALPHA          = 0x00000002;

    private const int  SW_SHOWNOACTIVATE  = 4;

    private const uint PM_REMOVE          = 0x0001;

    // Stock brush index for solid black; passed to GetStockObject.
    private const int  BLACK_BRUSH        = 4;

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int fnObject);
}

// MARK: - Global hotkey

/// <summary>
/// Registers a single global hotkey via the Win32 RegisterHotKey API.
///
/// Why RegisterHotKey: it's the built-in system-wide hotkey mechanism on
/// Windows. The combo is intercepted before any other app sees it, which
/// is what we want for a window manager. The alternative
/// (low-level keyboard hook) observes every keystroke system-wide; that
/// would let other apps also see the shortcut, and triggers SmartScreen
/// and antivirus more aggressively because it looks like keylogging.
///
/// RegisterHotKey delivers WM_HOTKEY messages to a window handle. Since
/// JASS is a tray app with no visible window, we create a tiny invisible
/// "message-only" window whose only job is to receive those messages.
///
/// One hotkey per instance: calling Register again unregisters the
/// previous combo first. Mirrors the Mac HotkeyManager.
/// </summary>
internal sealed class HotkeyManager : IDisposable
{
    // Win32 hotkey ID. Any non-zero int works as long as we use the same
    // one for register and unregister. Hardcoded because we only ever
    // manage one hotkey per process.
    private const int HotkeyId = 1;

    private readonly HotkeyWindow _window;
    private bool _registered;
    private bool _disposed;

    public HotkeyManager(Action onTrigger)
    {
        _window = new HotkeyWindow(onTrigger);
    }

    /// <summary>
    /// Install the hotkey for the given shortcut. Returns true on success,
    /// false if Windows refused the registration (most commonly because
    /// another app already owns that combination, or the combination is
    /// reserved by the shell, e.g. Win+L). Calling Register repeatedly
    /// just replaces the previous binding.
    /// </summary>
    public bool Register(Shortcut shortcut)
    {
        if (_disposed)
        {
            Log.Warn("HotkeyManager.Register called after Dispose; ignoring");
            return false;
        }
        Unregister();

        // Win32 modifier flags happen to be the same numeric values as our
        // Modifiers enum (we picked them to match), so we can pass the raw
        // uint straight through.
        var ok = RegisterHotKey(_window.Handle, HotkeyId, (uint)shortcut.Modifiers, shortcut.KeyCode);
        if (ok)
        {
            _registered = true;
            return true;
        }
        else
        {
            // Keep the GetLastError code in the log so failures can be
            // diagnosed from the file. 1409 = ERROR_HOTKEY_ALREADY_REGISTERED.
            var err = Marshal.GetLastWin32Error();
            Log.Warn($"RegisterHotKey failed for {shortcut.Description}: Win32 error {err}");
            return false;
        }
    }

    /// <summary>
    /// Remove any active registration. Safe to call when nothing is
    /// registered (no-op).
    /// </summary>
    public void Unregister()
    {
        if (!_registered) return;
        if (!UnregisterHotKey(_window.Handle, HotkeyId))
        {
            // Unusual: the registration existed but Windows refused to
            // remove it. We still flip our local flag so we don't keep
            // trying; the worst case is the combo stays bound until the
            // process exits, at which point the OS cleans it up.
            var err = Marshal.GetLastWin32Error();
            Log.Warn($"UnregisterHotKey failed: Win32 error {err}");
        }
        _registered = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unregister();
        _window.DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>
    /// Invisible window whose only purpose is to receive WM_HOTKEY messages
    /// from RegisterHotKey. Created as a "message-only" window
    /// (HWND_MESSAGE parent), which means it never appears in window
    /// enumerations, the taskbar, or Alt+Tab, and Windows skips it during
    /// monitor-related window operations. Exactly what we want for an
    /// invisible event sink.
    ///
    /// Lives inside HotkeyManager because nothing else needs to know it
    /// exists.
    /// </summary>
    private sealed class HotkeyWindow : NativeWindow
    {
        // WM_HOTKEY is the message Windows posts when a registered hotkey
        // fires. Value from WinUser.h.
        private const int WM_HOTKEY = 0x0312;

        // HWND_MESSAGE = (HWND)-3. Setting this as the Parent in CreateParams
        // tells CreateWindowEx to make a message-only window.
        private static readonly IntPtr HWND_MESSAGE = new(-3);

        private readonly Action _onTrigger;

        public HotkeyWindow(Action onTrigger)
        {
            _onTrigger = onTrigger;
            CreateHandle(new CreateParams { Parent = HWND_MESSAGE });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                // The wParam is the hotkey ID. We only ever register one,
                // so we don't bother checking it; if RegisterHotKey returned
                // success, every WM_HOTKEY here is ours. Any future
                // multi-hotkey support would dispatch on wParam.
                //
                // WndProc runs on the UI thread (the one that pumps this
                // window's message loop, which is the WinForms main thread).
                // That means _onTrigger is free to touch UI state directly,
                // including showing MessageBox.
                try
                {
                    _onTrigger();
                }
                catch (Exception ex)
                {
                    // A crash inside the hotkey handler should never tear
                    // down the message pump. Log and swallow.
                    Log.Error($"Hotkey handler threw: {ex}");
                }
                return;
            }
            base.WndProc(ref m);
        }
    }
}
