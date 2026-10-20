using System.Diagnostics;
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
    private readonly DispatcherTimer _sasWatchTimer;
    private bool _suspended;
    private bool _disposed;

    public event Action? FocusLost;
    public event Action? DesktopSwitched;

    // Win32 P/Invoke Declarations

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

    /// <summary>
    /// Retrieves the handle to the ancestor of the specified window.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr TokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool DisableAllPrivileges,
        ref TOKEN_PRIVILEGES NewState,
        uint BufferLength,
        IntPtr PreviousState,
        IntPtr ReturnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(
        IntPtr hExistingToken,
        uint dwDesiredAccess,
        IntPtr lpTokenAttributes,
        int ImpersonationLevel,
        int TokenType,
        out IntPtr phNewToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImpersonateLoggedOnUser(IntPtr hToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RevertToSelf();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SwitchDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, [Out] byte[] pvInfo, uint nLength, out uint lpnLengthNeeded);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    // Constants & Delegates

    private const uint GA_PARENT = 1;
    private const uint GA_ROOT = 2;
    private const uint GA_ROOTOWNER = 3;

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_SYSTEM_DESKTOPSWITCH = 0x0020;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint MAXIMUM_ALLOWED = 0x02000000;

    private const int SecurityImpersonation = 2;
    private const int TokenTypeImpersonation = 2;

    private const uint PROCESS_QUERY_INFORMATION = 0x0400;

    private const uint DESKTOP_READOBJECTS = 0x0001;
    private const uint DESKTOP_WRITEOBJECTS = 0x0080;
    private const uint DESKTOP_SWITCHDESKTOP = 0x0100;
    private const uint DESKTOP_ALL_ACCESS = 0x01FF;

    private const byte VK_ESCAPE = 0x1B;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const int UOI_NAME = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privileges;
    }

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

        _backupTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _backupTimer.Tick += OnBackupTimerTick;

        _sasWatchTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _sasWatchTimer.Tick += OnSasWatchTimerTick;
    }

    /// <summary>
    /// Starts monitoring foreground changes for the specified window handle.
    /// </summary>
    public void Start(IntPtr targetHwnd)
    {
        _targetHwnd = targetHwnd;
        if (_targetHwnd == IntPtr.Zero) return;

        // Hook system-wide foreground and desktop changes
        _hookHandle = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND,
            EVENT_SYSTEM_DESKTOPSWITCH,
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
        _sasWatchTimer.Start();
    }

    /// <summary>
    /// Stops monitoring and focus reclaiming.
    /// </summary>
    public void Stop()
    {
        _backupTimer.Stop();
        _sasWatchTimer.Stop();

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

    /// <summary>
    /// Determines whether the given HWND belongs to the ExamBro process, its window tree,
    /// or its legitimate child processes (such as WebView2 renderers).
    /// </summary>
    private bool IsOurWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || _targetHwnd == IntPtr.Zero)
            return false;

        if (hwnd == _targetHwnd)
            return true;

        try
        {
            IntPtr root = GetAncestor(hwnd, GA_ROOT);
            if (root == _targetHwnd)
                return true;

            IntPtr rootOwner = GetAncestor(hwnd, GA_ROOTOWNER);
            if (rootOwner == _targetHwnd)
                return true;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == (uint)Environment.ProcessId)
                return true;

            // Check if the window belongs to a child/descendant process (like WebView2 renderers)
            var childPids = ProcessMonitorService.GetDescendantPids(Environment.ProcessId);
            if (childPids.Contains((int)pid))
                return true;
        }
        catch { }

        return false;
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
        if (eventType == EVENT_SYSTEM_DESKTOPSWITCH)
        {
            Log.Warning("Desktop switch detected (e.g. Winlogon / SAS)!");
            DesktopSwitched?.Invoke();
            DismissWinlogonSas();
            ReclaimFocus();
            return;
        }

        if (eventType == EVENT_SYSTEM_FOREGROUND)
        {
            if (hwnd == IntPtr.Zero || IsOurWindow(hwnd))
            {
                return;
            }

            Log.Warning("Foreground change detected! Foreign window handle: 0x{Handle:X}", hwnd.ToInt64());
            FocusLost?.Invoke();
            ReclaimFocus();
        }
    }

    private void OnBackupTimerTick(object? sender, EventArgs e)
    {
        if (_suspended || _targetHwnd == IntPtr.Zero) return;

        IntPtr currentFore = GetForegroundWindow();
        if (currentFore == IntPtr.Zero || IsOurWindow(currentFore))
        {
            return;
        }

        Log.Warning("Backup timer detected loss of focus. Foreign foreground: 0x{Handle:X}", currentFore.ToInt64());
        FocusLost?.Invoke();
        ReclaimFocus();
    }

    private void OnSasWatchTimerTick(object? sender, EventArgs e)
    {
        if (_suspended || _targetHwnd == IntPtr.Zero || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        try
        {
            IntPtr hDesk = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
            if (hDesk == IntPtr.Zero)
            {
                // Inability to open input desktop indicates switch to a restricted secure desktop like Winlogon
                DismissWinlogonSas();
                ReclaimFocus();
                return;
            }

            try
            {
                byte[] nameBytes = new byte[256];
                if (GetUserObjectInformation(hDesk, UOI_NAME, nameBytes, (uint)nameBytes.Length, out uint needed))
                {
                    string deskName = System.Text.Encoding.Unicode.GetString(nameBytes, 0, (int)needed).TrimEnd('\0');
                    if (deskName.Equals("Winlogon", StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Warning("SAS Watcher: Winlogon desktop detected active. Dismissing SAS screen immediately.");
                        DismissWinlogonSas();
                        ReclaimFocus();
                    }
                }
            }
            finally
            {
                CloseDesktop(hDesk);
            }
        }
        catch { }
    }

    /// <summary>
    /// Instantly dismisses the Winlogon SAS screen (Ctrl+Alt+Del) by attaching a SYSTEM-level
    /// worker thread to the Winlogon desktop and injecting VK_ESCAPE, then switching back to Default.
    /// </summary>
    public static void DismissWinlogonSas()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        // Dedicated worker thread prevents ERROR_BUSY on SetThreadDesktop caused by GUI windows
        var thread = new Thread(() =>
        {
            IntPtr hDesktop = IntPtr.Zero;
            IntPtr hDupToken = IntPtr.Zero;
            bool impersonated = false;

            try
            {
                // Direct access attempt
                hDesktop = OpenDesktop("Winlogon", 0, false, DESKTOP_ALL_ACCESS);
                if (hDesktop == IntPtr.Zero)
                {
                    hDesktop = OpenInputDesktop(0, false, DESKTOP_ALL_ACCESS);
                }

                // If direct access fails, escalate to SYSTEM via token duplication from winlogon.exe
                if (hDesktop == IntPtr.Zero)
                {
                    hDupToken = AcquireSystemTokenFromWinlogon();
                    if (hDupToken != IntPtr.Zero && ImpersonateLoggedOnUser(hDupToken))
                    {
                        impersonated = true;
                        hDesktop = OpenDesktop("Winlogon", 0, false, DESKTOP_ALL_ACCESS);
                        if (hDesktop == IntPtr.Zero)
                        {
                            hDesktop = OpenInputDesktop(0, false, DESKTOP_ALL_ACCESS);
                        }
                    }
                }

                if (hDesktop != IntPtr.Zero)
                {
                    if (SetThreadDesktop(hDesktop))
                    {
                        keybd_event(VK_ESCAPE, 0, 0, UIntPtr.Zero);
                        Thread.Sleep(5);
                        keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                        Log.Information("DismissWinlogonSas: Escape injected into Winlogon desktop successfully");
                    }
                    CloseDesktop(hDesktop);
                    hDesktop = IntPtr.Zero;
                }

                // Switch display back to Default desktop
                IntPtr hDefault = OpenDesktop("Default", 0, false, DESKTOP_SWITCHDESKTOP);
                if (hDefault != IntPtr.Zero)
                {
                    SwitchDesktop(hDefault);
                    CloseDesktop(hDefault);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "DismissWinlogonSas encountered an error while neutralizing SAS screen");
            }
            finally
            {
                if (hDesktop != IntPtr.Zero)
                {
                    CloseDesktop(hDesktop);
                }
                if (impersonated)
                {
                    RevertToSelf();
                }
                if (hDupToken != IntPtr.Zero)
                {
                    CloseHandle(hDupToken);
                }
            }
        })
        {
            IsBackground = true,
            Name = "SasNeutralizerWorker"
        };

        thread.Start();
    }

    private static IntPtr AcquireSystemTokenFromWinlogon()
    {
        try
        {
            EnablePrivilege("SeDebugPrivilege");

            int currentSessionId = Process.GetCurrentProcess().SessionId;
            var winlogonProcesses = Process.GetProcessesByName("winlogon");

            foreach (var proc in winlogonProcesses)
            {
                try
                {
                    if (proc.SessionId == currentSessionId)
                    {
                        IntPtr hProc = OpenProcess(PROCESS_QUERY_INFORMATION, false, proc.Id);
                        if (hProc != IntPtr.Zero)
                        {
                            if (OpenProcessToken(hProc, TOKEN_DUPLICATE | TOKEN_QUERY, out IntPtr hToken))
                            {
                                bool dupSuccess = DuplicateTokenEx(
                                    hToken,
                                    MAXIMUM_ALLOWED,
                                    IntPtr.Zero,
                                    SecurityImpersonation,
                                    TokenTypeImpersonation,
                                    out IntPtr hDup);
                                CloseHandle(hToken);
                                CloseHandle(hProc);
                                if (dupSuccess)
                                {
                                    return hDup;
                                }
                            }
                            CloseHandle(hProc);
                        }
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "AcquireSystemTokenFromWinlogon failed.");
        }
        return IntPtr.Zero;
    }

    private static void EnablePrivilege(string privilegeName)
    {
        try
        {
            if (OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr hToken))
            {
                if (LookupPrivilegeValue(null, privilegeName, out LUID luid))
                {
                    var tp = new TOKEN_PRIVILEGES
                    {
                        PrivilegeCount = 1,
                        Privileges = new LUID_AND_ATTRIBUTES
                        {
                            Luid = luid,
                            Attributes = 0x00000002 // SE_PRIVILEGE_ENABLED
                        }
                    };
                    AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                }
                CloseHandle(hToken);
            }
        }
        catch { }
    }

    /// <summary>
    /// Forcefully brings the exam window to the foreground if a foreign window steals focus.
    /// </summary>
    public void ReclaimFocus()
    {
        if (_targetHwnd == IntPtr.Zero || _suspended) return;

        try
        {
            IntPtr foreHwnd = GetForegroundWindow();
            if (foreHwnd != IntPtr.Zero && IsOurWindow(foreHwnd))
            {
                return;
            }

            BringWindowToTop(_targetHwnd);
            SetForegroundWindow(_targetHwnd);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error while reclaiming window focus");
        }
    }

    // IDisposable

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
