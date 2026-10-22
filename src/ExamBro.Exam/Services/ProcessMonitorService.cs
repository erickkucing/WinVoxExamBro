using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using ExamBro.Core.Models;
using Serilog;

namespace ExamBro.Exam.Services;

/// <summary>
/// Service responsible for scanning active processes every second.
/// Compares running processes against the configured blacklist, kills offending processes
/// if configured, and triggers security violation events.
/// Excludes legitimate child processes (such as WebView2 renderer processes created by ExamBro).
/// </summary>
public class ProcessMonitorService : IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<ProcessMonitorService>();

    private readonly ExamConfig _config;
    private readonly Timer _timer;
    private readonly HashSet<string> _blacklist;
    private readonly int _currentPid;
    private readonly int _currentSessionId;
    private int _isScanning;
    private bool _disposed;

    private static readonly HashSet<string> BackgroundSafeWindowsTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "wt", "taskmgr", "regedit", "mmc", "conhost", "wscript", "cscript"
    };

    public event Action<string, int>? BlockedProcessDetected;

    // Win32 P/Invoke Declarations

    /// <summary>
    /// Determines the visibility state of the specified window.
    /// Used to distinguish interactive GUI/console sessions from headless background tasks.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>
    /// Takes a snapshot of the specified processes, as well as the heaps, modules, and threads used by these processes.
    /// Used with TH32CS_SNAPPROCESS to discover parent-child relationships between processes.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    /// <summary>
    /// Retrieves information about the first process encountered in a system snapshot.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    /// <summary>
    /// Retrieves information about the next process recorded in a system snapshot.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    /// <summary>
    /// Closes an open object handle.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    public ProcessMonitorService(ExamConfig config)
    {
        _config = config;
        _currentPid = Environment.ProcessId;

        try
        {
            _currentSessionId = Process.GetCurrentProcess().SessionId;
        }
        catch
        {
            _currentSessionId = -1;
        }

        _blacklist = new HashSet<string>(
            config.Security.ProcessBlacklist.Select(NormalizeProcessName),
            StringComparer.OrdinalIgnoreCase);

        _timer = new Timer(OnTimerTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Starts the background process scan timer.
    /// </summary>
    public void Start()
    {
        Log.Information("Starting ProcessMonitorService with {BlacklistCount} blacklisted signatures", _blacklist.Count);
        _timer.Change(0, 1500);
    }

    /// <summary>
    /// Stops the process scanner.
    /// </summary>
    public void Stop()
    {
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        Log.Information("ProcessMonitorService stopped");
    }

    private void OnTimerTick(object? state)
    {
        if (Interlocked.CompareExchange(ref _isScanning, 1, 0) != 0)
        {
            return;
        }

        try
        {
            ScanProcesses();
        }
        finally
        {
            Interlocked.Exchange(ref _isScanning, 0);
        }
    }

    /// <summary>
    /// Performs a one-time check of running processes against the blacklist.
    /// Returns any currently running offending process names.
    /// Excludes headless background tasks without active interactive user windows.
    /// </summary>
    public List<string> CheckRunningBlacklist()
    {
        var violatingProcesses = new List<string>();
        var childPids = GetDescendantPids(_currentPid);

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to enumerate running processes");
            return violatingProcesses;
        }

        foreach (var proc in processes)
        {
            try
            {
                if (proc.Id == _currentPid) continue;
                if (childPids.Contains(proc.Id)) continue;

                string rawName = proc.ProcessName;
                string procName = NormalizeProcessName(rawName);

                // Skip legitimate WebView2 child processes created by this instance
                if (procName.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase) &&
                    childPids.Contains(proc.Id))
                {
                    continue;
                }

                if (IsActiveOffendingProcess(proc, procName))
                {
                    violatingProcesses.Add(rawName);
                }
            }
            catch
            {
                // Process may have exited
            }
            finally
            {
                proc.Dispose();
            }
        }

        return violatingProcesses.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Forcefully terminates all currently detected active offending processes.
    /// Returns the number of processes successfully killed.
    /// </summary>
    public int KillOffendingProcesses()
    {
        int killedCount = 0;
        var childPids = GetDescendantPids(_currentPid);

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to enumerate running processes for auto-kill");
            return 0;
        }

        foreach (var proc in processes)
        {
            try
            {
                if (proc.Id == _currentPid) continue;
                if (childPids.Contains(proc.Id)) continue;

                string rawName = proc.ProcessName;
                string procName = NormalizeProcessName(rawName);

                // Skip legitimate WebView2 child processes created by this instance
                if (procName.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase) &&
                    childPids.Contains(proc.Id))
                {
                    continue;
                }

                if (IsActiveOffendingProcess(proc, procName))
                {
                    try
                    {
                        proc.Kill(true);
                        killedCount++;
                        Log.Information("Terminated prohibited process: {ProcessName} (PID: {Pid})", rawName, proc.Id);
                    }
                    catch (Exception killEx)
                    {
                        Log.Warning(killEx, "Failed to kill prohibited process {ProcessName} (PID: {Pid})", rawName, proc.Id);
                    }
                }
            }
            catch
            {
                // Process may have exited
            }
            finally
            {
                proc.Dispose();
            }
        }

        return killedCount;
    }

    /// <summary>
    /// Checks whether a running process is an active offending process that poses a security violation.
    /// For standard Windows built-in tools (cmd, powershell, taskmgr, etc.), ignores headless
    /// background tasks that have no interactive window in the current desktop session.
    /// </summary>
    private bool IsActiveOffendingProcess(Process proc, string normalizedName)
    {
        if (!_blacklist.Contains(normalizedName))
        {
            return false;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Check session ID: ignore services or other user sessions
            if (_currentSessionId >= 0)
            {
                try
                {
                    if (proc.SessionId != _currentSessionId)
                    {
                        return false;
                    }
                }
                catch
                {
                    return false;
                }
            }

            // Only for background-safe Windows tools (cmd, powershell, etc.), ignore headless background tasks.
            // Prohibited tools such as DisplaySwitch, snippingtool, cheat tools, etc., are always violations regardless of MainWindowHandle.
            if (BackgroundSafeWindowsTools.Contains(normalizedName))
            {
                try
                {
                    IntPtr mainWindow = proc.MainWindowHandle;
                    if (mainWindow == IntPtr.Zero || !IsWindowVisible(mainWindow))
                    {
                        return false;
                    }
                }
                catch
                {
                    return false;
                }
            }
        }

        return true;
    }

    private void ScanProcesses()
    {
        try
        {
            var childPids = GetDescendantPids(_currentPid);
            Process[] processes = Process.GetProcesses();

            foreach (var proc in processes)
            {
                try
                {
                    int pid = proc.Id;
                    if (pid == _currentPid) continue;

                    string rawName = proc.ProcessName;
                    string normalized = NormalizeProcessName(rawName);

                    // Skip legitimate WebView2 child processes created by this instance
                    if (normalized.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase) &&
                        childPids.Contains(pid))
                    {
                        continue;
                    }

                    if (IsActiveOffendingProcess(proc, normalized))
                    {
                        Log.Warning("Blacklisted process detected: {ProcessName} (PID: {Pid})", rawName, pid);

                        // Take configured action
                        if (_config.Security.ProcessAction == ProcessActionType.Kill)
                        {
                            try
                            {
                                proc.Kill(true);
                                Log.Information("Terminated blacklisted process: {ProcessName} (PID: {Pid})", rawName, pid);
                            }
                            catch (Exception killEx)
                            {
                                Log.Warning(killEx, "Failed to kill process {ProcessName} (PID: {Pid})", rawName, pid);
                            }
                        }

                        BlockedProcessDetected?.Invoke(rawName, pid);
                    }
                }
                catch
                {
                    // Process may have exited during scan
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error occurred during process monitor scan");
        }
    }

    private static string NormalizeProcessName(string name)
    {
        string n = name.Trim();
        if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            n = n[..^4];
        }
        return n;
    }

    /// <summary>
    /// Queries all descendant process IDs (children, grandchildren) of the given root process ID
    /// using CreateToolhelp32Snapshot.
    /// </summary>
    public static HashSet<int> GetDescendantPids(int rootPid)
    {
        var descendants = new HashSet<int>();
        var parentChildMap = new Dictionary<int, List<int>>();

        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return descendants;
        }

        try
        {
            var pe = new PROCESSENTRY32
            {
                dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>()
            };

            if (Process32First(snapshot, ref pe))
            {
                do
                {
                    int child = (int)pe.th32ProcessID;
                    int parent = (int)pe.th32ParentProcessID;

                    if (!parentChildMap.TryGetValue(parent, out var list))
                    {
                        list = new List<int>();
                        parentChildMap[parent] = list;
                    }
                    list.Add(child);
                }
                while (Process32Next(snapshot, ref pe));
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to build process tree snapshot");
        }
        finally
        {
            CloseHandle(snapshot);
        }

        // Traverse down from rootPid
        var queue = new Queue<int>();
        queue.Enqueue(rootPid);

        while (queue.Count > 0)
        {
            int current = queue.Dequeue();
            if (parentChildMap.TryGetValue(current, out var children))
            {
                foreach (int child in children)
                {
                    if (descendants.Add(child))
                    {
                        queue.Enqueue(child);
                    }
                }
            }
        }

        return descendants;
    }

    /// <summary>
    /// Forcefully terminates all child and descendant processes (such as WebView2 renderers)
    /// spawned by the specified root process.
    /// </summary>
    public static void KillDescendantProcesses(int rootPid)
    {
        try
        {
            var descendants = GetDescendantPids(rootPid);
            foreach (int pid in descendants)
            {
                try
                {
                    using var proc = Process.GetProcessById(pid);
                    if (!proc.HasExited)
                    {
                        proc.Kill(true);
                        Log.Information("Terminated child/descendant process (PID: {Pid})", pid);
                    }
                }
                catch
                {
                    // Process already exited
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Error while cleaning up descendant processes");
        }
    }

    /// <summary>
    /// Terminates any running ExamBro config instances to prevent orphaned background processes.
    /// </summary>
    public static void TerminateSiblingExamBroProcesses()
    {
        string[] targetProcessNames = { "ExamBro.Config", "LouisVox ExamBro Pengaturan" };
        foreach (string name in targetProcessNames)
        {
            try
            {
                var configProcesses = Process.GetProcessesByName(name);
                foreach (var proc in configProcesses)
                {
                    try
                    {
                        if (!proc.HasExited)
                        {
                            proc.Kill();
                            proc.WaitForExit(1000);
                            Log.Information("Terminated sibling {Name} process (PID: {Pid})", name, proc.Id);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, "Failed killing {Name} process", name);
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Error scanning sibling {Name} processes", name);
            }
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
            _timer.Dispose();
            _disposed = true;
        }
    }

    ~ProcessMonitorService()
    {
        Dispose(false);
    }
}
