using System.Runtime.InteropServices;
using System.Windows.Threading;
using Serilog;

namespace ExamBro.Exam.Services;

/// <summary>
/// Service responsible for keeping the kiosk examination window focused and topmost.
/// Monitors foreground window switches using SetWinEventHook and a secondary DispatcherTimer poll.
/// Reclaims focus aggressively by attaching thread inputs when another window takes the foreground.
/// </summary>
public class FocusGuardService : IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<FocusGuardService>();

    private IntPtr _targetHwnd = IntPtr.Zero;
    private IntPtr _hookHandle = IntPtr.Zero;
    private readonly WinEventDelegate _winEventDelegate; // Retain reference to prevent GC collection
    private readonly DispatcherTimer _backupTimer;
    private bool _suspended;
    private bool _disposed;

    public event Action? FocusLost;

    // ── Win32 P/Invoke Declarations ──────────────────────────────────────────

    /// <summary>
    /// Sets an event hook function for a range of events.
    /// Used here to listen for EVENT_SYSTEM_FOREGROUND (0x0003) system-wide.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    /// <summary>
    /// Removes an event hook function created by a previous call to SetWinEventHook.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    /// <summary>
    /// Retrieves a handle to the foreground window (the window with which the user is currently working).
    /// </summary>
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>
    /// Brings the thread that created the specified window into the foreground and activates the window.
    /// Keyboard input is directed to the window, and various visual cues are changed for the user.
    /// </summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    /// Brings the specified window to the top of the Z order.
    /// </summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    /// <summary>
    /// Attaches or detaches the input processing mechanism of one thread to that of another thread.
    /// Required on modern Windows to reliably bring a window to the foreground from a background state.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    /// <summary>
    /// Retrieves the identifier of the thread and process that created the specified window.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>
    /// Retrieves the thread identifier of the calling thread.
    /// </summary>
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    // ── Constants & Delegates ────────────────────────────────────────────────

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    public FocusGuardService()
    {
        _winEventDelegate = OnWinEvent;

        _backupTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _backupTimer.Tick += OnBackupTimerTick;
    }

    /// <summary>
    /// Starts monitoring foreground changes for the specified window handle.
    /// </summary>
    public void Start(IntPtr targetHwnd)
    {
        _targetHwnd = targetHwnd;
        if (_targetHwnd == IntPtr.Zero) return;

        // Hook system-wide foreground changes
        _hookHandle = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND,
            EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _winEventDelegate,
            0,
            0,
            WINEVENT_OUTOFCONTEXT);

        if (_hookHandle == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            Log.Warning("SetWinEventHook failed with code {ErrorCode}. Relying on backup timer.", err);
        }
        else
        {
            Log.Information("SetWinEventHook registered successfully for handle 0x{Handle:X}", _targetHwnd.ToInt64());
        }

        _backupTimer.Start();
        ReclaimFocus();
    }

    /// <summary>
    /// Stops monitoring and focus reclaiming.
    /// </summary>
    public void Stop()
    {
        _backupTimer.Stop();

        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWinEvent(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }

        _targetHwnd = IntPtr.Zero;
    }

    /// <summary>
    /// Temporarily suspends or resumes focus guarding (useful when opening modal proctor dialogs).
    /// </summary>
    public void SetSuspended(bool suspended)
    {
        _suspended = suspended;
    }

    private void OnWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (_suspended || _targetHwnd == IntPtr.Zero) return;

        if (eventType == EVENT_SYSTEM_FOREGROUND && hwnd != _targetHwnd && hwnd != IntPtr.Zero)
        {
            Log.Warning("Foreground change detected! New foreground window handle: 0x{Handle:X}", hwnd.ToInt64());
            FocusLost?.Invoke();
            ReclaimFocus();
        }
    }

    private void OnBackupTimerTick(object? sender, EventArgs e)
    {
        if (_suspended || _targetHwnd == IntPtr.Zero) return;

        IntPtr currentFore = GetForegroundWindow();
        if (currentFore != _targetHwnd && currentFore != IntPtr.Zero)
        {
            Log.Warning("Backup timer detected loss of focus. Current foreground: 0x{Handle:X}", currentFore.ToInt64());
            FocusLost?.Invoke();
            ReclaimFocus();
        }
    }

    /// <summary>
    /// Forcefully brings the exam window to the foreground using thread input attachment.
    /// </summary>
    public void ReclaimFocus()
    {
        if (_targetHwnd == IntPtr.Zero) return;

        try
        {
            IntPtr foreHwnd = GetForegroundWindow();
            if (foreHwnd == _targetHwnd)
            {
                return;
            }

            uint foreThread = GetWindowThreadProcessId(foreHwnd, out _);
            uint appThread = GetCurrentThreadId();

            if (foreThread != 0 && foreThread != appThread)
            {
                AttachThreadInput(foreThread, appThread, true);
                BringWindowToTop(_targetHwnd);
                SetForegroundWindow(_targetHwnd);
                AttachThreadInput(foreThread, appThread, false);
            }
            else
            {
                BringWindowToTop(_targetHwnd);
                SetForegroundWindow(_targetHwnd);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error while reclaiming window focus");
        }
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
            Stop();
            _disposed = true;
        }
    }

    ~FocusGuardService()
    {
        Dispose(false);
    }
}
