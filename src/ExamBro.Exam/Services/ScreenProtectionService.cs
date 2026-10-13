using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ExamBro.Core.Models;
using ExamBro.Exam.Views;
using Serilog;

namespace ExamBro.Exam.Services;

/// <summary>
/// Service responsible for screen-level defenses:
/// 1. Hardware screen capture protection (blocking Snipping Tool, OBS, screenshots) via SetWindowDisplayAffinity.
/// 2. Multi-monitor management (covering secondary screens or detecting extra monitors per policy).
/// </summary>
public class ScreenProtectionService : IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<ScreenProtectionService>();

    private readonly ExamConfig _config;
    private readonly List<SecondaryScreenOverlayWindow> _overlayWindows = new();
    private bool _disposed;

    // ── Win32 P/Invoke Declarations ──────────────────────────────────────────

    /// <summary>
    /// Stores the display affinity setting for a window.
    /// WDA_EXCLUDEFROMCAPTURE (0x00000011) completely prevents the window from being captured
    /// by any capture API (Desktop Duplication, GDI, PrintWindow, screen recorder).
    /// WDA_MONITOR (0x00000001) renders the window content black in captures.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    /// <summary>
    /// Enumerates display monitors that intersect a region formed by the intersection of a
    /// specified clipping rectangle and the visible region of a device context.
    /// </summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc,
        IntPtr lprcClip,
        MonitorEnumProc lpfnEnum,
        IntPtr dwData);

    /// <summary>
    /// Retrieves information about a display monitor.
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private const uint WDA_NONE = 0x00000000;
    private const uint WDA_MONITOR = 0x00000001;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    private const uint MONITORINFOF_PRIMARY = 0x00000001;

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public uint CbSize;
        public RECT RcMonitor;
        public RECT RcWork;
        public uint DwFlags;
    }

    public record MonitorInfoRecord(RECT MonitorRect, bool IsPrimary);

    public ScreenProtectionService(ExamConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Applies hardware screen capture protection to the target window.
    /// Tries WDA_EXCLUDEFROMCAPTURE (Win10 2004+) first, then falls back to WDA_MONITOR.
    /// </summary>
    public bool ApplyCaptureProtection(IntPtr hwnd)
    {
        if (!_config.Security.ScreenCaptureProtection || hwnd == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            // Attempt modern exclusion from capture
            if (SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE))
            {
                Log.Information("Applied WDA_EXCLUDEFROMCAPTURE screen protection to window 0x{Handle:X}", hwnd.ToInt64());
                return true;
            }

            int lastErr = Marshal.GetLastWin32Error();
            Log.Debug("WDA_EXCLUDEFROMCAPTURE failed (code: {Code}), falling back to WDA_MONITOR", lastErr);

            // Fallback to monitor affinity
            if (SetWindowDisplayAffinity(hwnd, WDA_MONITOR))
            {
                Log.Information("Applied WDA_MONITOR screen protection to window 0x{Handle:X}", hwnd.ToInt64());
                return true;
            }

            int fallbackErr = Marshal.GetLastWin32Error();
            Log.Warning("Failed to apply screen display affinity protection. Error code: {Code}", fallbackErr);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Exception while applying screen capture protection");
        }

        return false;
    }

    /// <summary>
    /// Removes screen capture protection from the specified window.
    /// </summary>
    public void RemoveCaptureProtection(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
        {
            try
            {
                SetWindowDisplayAffinity(hwnd, WDA_NONE);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Error removing capture protection");
            }
        }
    }

    /// <summary>
    /// Enumerate all active physical monitors on the system.
    /// </summary>
    public static List<MonitorInfoRecord> EnumerateMonitors()
    {
        var monitors = new List<MonitorInfoRecord>();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data) =>
        {
            var mi = new MONITORINFO
            {
                CbSize = (uint)Marshal.SizeOf<MONITORINFO>()
            };

            if (GetMonitorInfo(hMon, ref mi))
            {
                bool isPrimary = (mi.DwFlags & MONITORINFOF_PRIMARY) != 0;
                monitors.Add(new MonitorInfoRecord(mi.RcMonitor, isPrimary));
            }

            return true;
        }, IntPtr.Zero);

        return monitors;
    }

    /// <summary>
    /// Enforces the configured MultiMonitorPolicy.
    /// Returns true if monitor policy is compliant, or false if multi-monitor is blocked.
    /// </summary>
    public bool EnforceMonitorPolicy(Action<string>? onViolation = null)
    {
        var monitors = EnumerateMonitors();
        int monitorCount = monitors.Count;

        Log.Information("Detected {Count} monitor(s). Active policy: {Policy}", monitorCount, _config.Security.MultiMonitor);

        if (monitorCount <= 1)
        {
            return true;
        }

        switch (_config.Security.MultiMonitor)
        {
            case MultiMonitorPolicy.Block:
                string msg = $"Terdeteksi {monitorCount} layar aktif. Konfigurasi melarang penggunaan monitor ganda.";
                Log.Warning(msg);
                onViolation?.Invoke(msg);
                return false;

            case MultiMonitorPolicy.CoverAll:
                CoverSecondaryMonitors(monitors);
                return true;

            case MultiMonitorPolicy.Allow:
            default:
                return true;
        }
    }

    /// <summary>
    /// Creates full-screen blackout overlay windows over all non-primary displays.
    /// </summary>
    private void CoverSecondaryMonitors(List<MonitorInfoRecord> monitors)
    {
        CloseOverlays();

        foreach (var monitor in monitors)
        {
            if (monitor.IsPrimary) continue;

            try
            {
                var overlay = new SecondaryScreenOverlayWindow
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = monitor.MonitorRect.Left,
                    Top = monitor.MonitorRect.Top,
                    Width = monitor.MonitorRect.Width,
                    Height = monitor.MonitorRect.Height
                };

                overlay.Show();

                var helper = new WindowInteropHelper(overlay);
                ApplyCaptureProtection(helper.Handle);

                _overlayWindows.Add(overlay);
                Log.Information("Created blackout overlay on secondary monitor at ({X}, {Y}, {W}x{H})",
                    monitor.MonitorRect.Left, monitor.MonitorRect.Top, monitor.MonitorRect.Width, monitor.MonitorRect.Height);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to create secondary monitor overlay window");
            }
        }
    }

    /// <summary>
    /// Closes all secondary monitor overlay windows.
    /// </summary>
    public void CloseOverlays()
    {
        foreach (var overlay in _overlayWindows)
        {
            try
            {
                var helper = new WindowInteropHelper(overlay);
                RemoveCaptureProtection(helper.Handle);
                overlay.Close();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Error closing secondary overlay window");
            }
        }
        _overlayWindows.Clear();
    }

    // ── IDisposable ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            CloseOverlays();
            _disposed = true;
        }
    }

    ~ScreenProtectionService()
    {
        Dispose(false);
    }
}
