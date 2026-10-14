using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ExamBro.Core;
using ExamBro.Core.Models;
using ExamBro.Core.Recovery;
using Serilog;

namespace ExamBro.Exam.Services;

/// <summary>
/// Service responsible for locking down Windows into kiosk mode:
/// hiding the taskbar, disabling security desktop policies (Task Manager, Logoff, Lock, Change Password),
/// and suppressing Windows accessibility hotkeys (StickyKeys, FilterKeys, ToggleKeys).
/// </summary>
public class KioskLockService : IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<KioskLockService>();

    private readonly RecoveryState _recovery = new();
    private bool _engaged;
    private bool _disposed;

    // Win32 P/Invoke Declarations

    /// <summary>
    /// FindWindow: Retrieves a handle to the top-level window whose class name matches the specified string.
    /// Used here to retrieve window handles for the primary taskbar ("Shell_TrayWnd")
    /// and secondary monitor taskbars ("Shell_SecondaryTrayWnd").
    /// </summary>
    /// <param name="lpClassName">The window class name.</param>
    /// <param name="lpWindowName">The window name (title). Can be null.</param>
    /// <returns>If the function succeeds, the return value is a window handle.</returns>
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    /// <summary>
    /// ShowWindow: Sets the specified window's show state.
    /// SW_HIDE (0) hides the window and activates another window.
    /// SW_SHOW (5) activates the window and displays it in its current size and position.
    /// </summary>
    /// <param name="hWnd">A handle to the window.</param>
    /// <param name="nCmdShow">Controls how the window is to be shown.</param>
    /// <returns>If the window was previously visible, the return value is nonzero.</returns>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>
    /// SystemParametersInfo: Queries or sets the parameters of the user interface.
    /// Used here with SPI_GETSTICKYKEYS / SPI_SETSTICKYKEYS to read and update StickyKeys configuration.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint uiAction,
        uint uiParam,
        ref STICKYKEYS pvParam,
        uint fWinIni);

    /// <summary>
    /// SystemParametersInfo: Queries or sets the parameters of the user interface.
    /// Used here with SPI_GETFILTERKEYS / SPI_SETFILTERKEYS to read and update FilterKeys configuration.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint uiAction,
        uint uiParam,
        ref FILTERKEYS pvParam,
        uint fWinIni);

    /// <summary>
    /// SystemParametersInfo: Queries or sets the parameters of the user interface.
    /// Used here with SPI_GETTOGGLEKEYS / SPI_SETTOGGLEKEYS to read and update ToggleKeys configuration.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint uiAction,
        uint uiParam,
        ref TOGGLEKEYS pvParam,
        uint fWinIni);

    // Win32 Constants

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    private const uint SPI_GETSTICKYKEYS = 0x003A;
    private const uint SPI_SETSTICKYKEYS = 0x003B;
    private const uint SPI_GETFILTERKEYS = 0x0032;
    private const uint SPI_SETFILTERKEYS = 0x0033;
    private const uint SPI_GETTOGGLEKEYS = 0x0034;
    private const uint SPI_SETTOGGLEKEYS = 0x0035;

    private const uint SPIF_SENDCHANGE = 0x0002;

    private const uint SKF_STICKYKEYSON = 0x00000001;
    private const uint SKF_HOTKEYACTIVE = 0x00000004;
    private const uint SKF_CONFIRMHOTKEY = 0x00000008;

    private const uint FKF_FILTERKEYSON = 0x00000001;
    private const uint FKF_HOTKEYACTIVE = 0x00000004;
    private const uint FKF_CONFIRMHOTKEY = 0x00000008;

    private const uint TKF_TOGGLEKEYSON = 0x00000001;
    private const uint TKF_HOTKEYACTIVE = 0x00000004;
    private const uint TKF_CONFIRMHOTKEY = 0x00000008;

    [StructLayout(LayoutKind.Sequential)]
    private struct STICKYKEYS
    {
        public uint cbSize;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILTERKEYS
    {
        public uint cbSize;
        public uint dwFlags;
        public uint iWaitMSec;
        public uint iDelayMSec;
        public uint iRepeatMSec;
        public uint iBounceMSec;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOGGLEKEYS
    {
        public uint cbSize;
        public uint dwFlags;
    }

    // Public Engagement & Disengagement

    /// <summary>
    /// Engages full kiosk lockdown: hides taskbars, writes registry lockdown policies,
    /// and suppresses accessibility hotkeys. Persists recovery state to disk.
    /// </summary>
    public void Engage(ExamConfig config)
    {
        if (_engaged)
        {
            Log.Information("Kiosk lockdown is already engaged");
            return;
        }

        Log.Information("Engaging kiosk lock service...");

        try
        {
            // 1. Hide primary and secondary taskbars
            HideTaskbar();

            // 2. Set registry policies if enabled
            if (config.Security.DisableRegistryPolicies)
            {
                ApplyRegistryPolicies();
            }

            // 3. Disable accessibility shortcut keys (StickyKeys, FilterKeys, ToggleKeys)
            DisableAccessibilityHotkeys();

            // 4. Terminate explorer.exe shell if requested by config
            if (config.Security.KillExplorerShell)
            {
                KillExplorer();
            }

            // 5. Persist recovery state to disk
            _recovery.Save();
            _engaged = true;

            Log.Information("Kiosk lockdown successfully engaged");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error while engaging kiosk lockdown. Attempting partial cleanup...");
            Disengage();
            throw;
        }
    }

    /// <summary>
    /// Disengages kiosk lockdown: restores taskbars, reverts registry policies,
    /// re-enables accessibility hotkeys, and deletes the recovery state file.
    /// </summary>
    public void Disengage()
    {
        if (!_engaged && !_recovery.OriginalRegistryValues.Any() && !_recovery.ExplorerWasKilled)
        {
            return;
        }

        Log.Information("Disengaging kiosk lock service...");

        try
        {
            // 1. Restore taskbars
            ShowTaskbar();

            // 2. Restore original registry policies
            RestoreRegistryPolicies();

            // 3. Restore accessibility hotkeys
            RestoreAccessibilityHotkeys();

            // 4. Restart explorer.exe shell if it was killed
            if (_recovery.ExplorerWasKilled)
            {
                RestartExplorer();
            }

            // 5. Delete recovery state file
            RecoveryState.ClearRecoveryFile();
            _engaged = false;

            Log.Information("Kiosk lockdown successfully disengaged");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error occurred while disengaging kiosk lockdown");
        }
    }

    // Taskbar Helpers

    private void HideTaskbar()
    {
        try
        {
            IntPtr primaryTray = FindWindow("Shell_TrayWnd", null);
            if (primaryTray != IntPtr.Zero)
            {
                ShowWindow(primaryTray, SW_HIDE);
                _recovery.TaskbarWasVisible = true;
            }

            IntPtr secondaryTray = FindWindow("Shell_SecondaryTrayWnd", null);
            if (secondaryTray != IntPtr.Zero)
            {
                ShowWindow(secondaryTray, SW_HIDE);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to hide taskbar");
        }
    }

    private static void ShowTaskbar()
    {
        try
        {
            IntPtr primaryTray = FindWindow("Shell_TrayWnd", null);
            if (primaryTray != IntPtr.Zero)
            {
                ShowWindow(primaryTray, SW_SHOW);
            }

            IntPtr secondaryTray = FindWindow("Shell_SecondaryTrayWnd", null);
            if (secondaryTray != IntPtr.Zero)
            {
                ShowWindow(secondaryTray, SW_SHOW);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to restore taskbar");
        }
    }

    // Registry Policy Helpers

    private void ApplyRegistryPolicies()
    {
        // System policies: DisableTaskMgr, DisableLockWorkstation, DisableChangePassword, HideFastUserSwitching
        // Applied to both HKCU and HKLM to ensure complete lockdown of Winlogon SAS screen (Ctrl+Alt+Del)
        foreach (bool useHklm in new[] { false, true })
        {
            SetPolicyAndSaveOriginal(Constants.SystemPolicySubKey, "DisableTaskMgr", 1, useHklm);
            SetPolicyAndSaveOriginal(Constants.SystemPolicySubKey, "DisableLockWorkstation", 1, useHklm);
            SetPolicyAndSaveOriginal(Constants.SystemPolicySubKey, "DisableChangePassword", 1, useHklm);
            SetPolicyAndSaveOriginal(Constants.SystemPolicySubKey, "HideFastUserSwitching", 1, useHklm);

            // Explorer policies: NoLogoff (Disable Sign out), NoClose (Disable Shutdown from SAS)
            SetPolicyAndSaveOriginal(Constants.ExplorerPolicySubKey, "NoLogoff", 1, useHklm);
            SetPolicyAndSaveOriginal(Constants.ExplorerPolicySubKey, "NoClose", 1, useHklm);
        }

        // Try Windows Keyboard Filter (WEKF) via WMI if available
        TryConfigureKeyboardFilter(enableFilter: true);
    }

    private void SetPolicyAndSaveOriginal(string subKey, string valueName, int newValue, bool useHklm = false)
    {
        string rootPrefix = useHklm ? "HKLM" : "HKCU";
        string fullPath = $@"{rootPrefix}\{subKey}\{valueName}";
        try
        {
            int? original = GetRegistryValue(subKey, valueName, useHklm);
            if (!_recovery.OriginalRegistryValues.ContainsKey(fullPath))
            {
                _recovery.OriginalRegistryValues[fullPath] = original;
            }

            SetRegistryValue(subKey, valueName, newValue, useHklm);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to set registry policy {Root}\\{SubKey}\\{ValueName}", rootPrefix, subKey, valueName);
        }
    }

    private void RestoreRegistryPolicies()
    {
        // Restore Keyboard Filter if previously configured
        TryConfigureKeyboardFilter(enableFilter: false);

        foreach (var entry in _recovery.OriginalRegistryValues)
        {
            string fullPath = entry.Key; // e.g. HKCU\Software\... or HKLM\Software\...
            int? originalValue = entry.Value;

            try
            {
                bool isHklm = fullPath.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase);
                string pathWithoutRoot = (isHklm || fullPath.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase))
                    ? fullPath[5..]
                    : fullPath;

                int lastBackslash = pathWithoutRoot.LastIndexOf('\\');
                if (lastBackslash <= 0) continue;

                string subKey = pathWithoutRoot[..lastBackslash];
                string valueName = pathWithoutRoot[(lastBackslash + 1)..];

                if (originalValue.HasValue)
                {
                    SetRegistryValue(subKey, valueName, originalValue.Value, useHklm: isHklm);
                }
                else
                {
                    DeleteRegistryValue(subKey, valueName, useHklm: isHklm);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to restore registry key {Key}", fullPath);
            }
        }

        _recovery.OriginalRegistryValues.Clear();
    }

    public static int? GetRegistryValue(string subKey, string valueName, bool useHklm = false)
    {
        try
        {
            var baseKey = useHklm ? Registry.LocalMachine : Registry.CurrentUser;
            using var key = baseKey.OpenSubKey(subKey);
            object? val = key?.GetValue(valueName);
            if (val is int intVal) return intVal;
            if (val is not null && int.TryParse(val.ToString(), out int parsed)) return parsed;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Registry read error for {Root}\\{SubKey}\\{ValueName}", useHklm ? "HKLM" : "HKCU", subKey, valueName);
        }
        return null;
    }

    public static void SetRegistryValue(string subKey, string valueName, int value, bool useHklm = false)
    {
        try
        {
            var baseKey = useHklm ? Registry.LocalMachine : Registry.CurrentUser;
            using var key = baseKey.CreateSubKey(subKey);
            key?.SetValue(valueName, value, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Registry write error for {Root}\\{SubKey}\\{ValueName}", useHklm ? "HKLM" : "HKCU", subKey, valueName);
        }
    }

    public static void DeleteRegistryValue(string subKey, string valueName, bool useHklm = false)
    {
        try
        {
            var baseKey = useHklm ? Registry.LocalMachine : Registry.CurrentUser;
            using var key = baseKey.OpenSubKey(subKey, true);
            key?.DeleteValue(valueName, false);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Registry delete error for {Root}\\{SubKey}\\{ValueName}", useHklm ? "HKLM" : "HKCU", subKey, valueName);
        }
    }

    private static void TryConfigureKeyboardFilter(bool enableFilter)
    {
        try
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;

            var scope = new ManagementScope(@"\\.\root\standardcimv2\embedded");
            scope.Connect();

            var query = new ObjectQuery("SELECT * FROM WEKF_PredefinedKey WHERE Id='Ctrl+Alt+Del' OR Id='Ctrl+Alt+Delete'");
            using var searcher = new ManagementObjectSearcher(scope, query);
            foreach (ManagementObject obj in searcher.Get())
            {
                obj["Enabled"] = enableFilter;
                obj.Put();
                Log.Information("WEKF PredefinedKey Ctrl+Alt+Del set to {Enabled}", enableFilter);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "WEKF (Keyboard Filter) unavailable or not installed on this system.");
        }
    }

    // Accessibility Hotkey Helpers

    private void DisableAccessibilityHotkeys()
    {
        try
        {
            // StickyKeys
            var sk = new STICKYKEYS { cbSize = (uint)Marshal.SizeOf<STICKYKEYS>() };
            if (SystemParametersInfo(SPI_GETSTICKYKEYS, sk.cbSize, ref sk, 0))
            {
                _recovery.OriginalStickyKeysFlags = sk.dwFlags;
                sk.dwFlags &= ~(SKF_HOTKEYACTIVE | SKF_CONFIRMHOTKEY | SKF_STICKYKEYSON);
                SystemParametersInfo(SPI_SETSTICKYKEYS, sk.cbSize, ref sk, 0);
            }

            // FilterKeys
            var fk = new FILTERKEYS { cbSize = (uint)Marshal.SizeOf<FILTERKEYS>() };
            if (SystemParametersInfo(SPI_GETFILTERKEYS, fk.cbSize, ref fk, 0))
            {
                _recovery.OriginalFilterKeysFlags = fk.dwFlags;
                fk.dwFlags &= ~(FKF_HOTKEYACTIVE | FKF_CONFIRMHOTKEY | FKF_FILTERKEYSON);
                SystemParametersInfo(SPI_SETFILTERKEYS, fk.cbSize, ref fk, 0);
            }

            // ToggleKeys
            var tk = new TOGGLEKEYS { cbSize = (uint)Marshal.SizeOf<TOGGLEKEYS>() };
            if (SystemParametersInfo(SPI_GETTOGGLEKEYS, tk.cbSize, ref tk, 0))
            {
                _recovery.OriginalToggleKeysFlags = tk.dwFlags;
                tk.dwFlags &= ~(TKF_HOTKEYACTIVE | TKF_CONFIRMHOTKEY | TKF_TOGGLEKEYSON);
                SystemParametersInfo(SPI_SETTOGGLEKEYS, tk.cbSize, ref tk, 0);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to suppress accessibility hotkeys");
        }
    }

    private void RestoreAccessibilityHotkeys()
    {
        try
        {
            if (_recovery.OriginalStickyKeysFlags != 0)
            {
                var sk = new STICKYKEYS
                {
                    cbSize = (uint)Marshal.SizeOf<STICKYKEYS>(),
                    dwFlags = _recovery.OriginalStickyKeysFlags
                };
                SystemParametersInfo(SPI_SETSTICKYKEYS, sk.cbSize, ref sk, 0);
            }

            if (_recovery.OriginalFilterKeysFlags != 0)
            {
                var fk = new FILTERKEYS
                {
                    cbSize = (uint)Marshal.SizeOf<FILTERKEYS>(),
                    dwFlags = _recovery.OriginalFilterKeysFlags
                };
                SystemParametersInfo(SPI_SETFILTERKEYS, fk.cbSize, ref fk, 0);
            }

            if (_recovery.OriginalToggleKeysFlags != 0)
            {
                var tk = new TOGGLEKEYS
                {
                    cbSize = (uint)Marshal.SizeOf<TOGGLEKEYS>(),
                    dwFlags = _recovery.OriginalToggleKeysFlags
                };
                SystemParametersInfo(SPI_SETTOGGLEKEYS, tk.cbSize, ref tk, 0);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to restore accessibility hotkeys");
        }
    }

    // Explorer Shell Helpers

    private void KillExplorer()
    {
        try
        {
            var explorerProcesses = Process.GetProcessesByName("explorer");
            if (explorerProcesses.Length > 0)
            {
                foreach (var p in explorerProcesses)
                {
                    try { p.Kill(); } catch { /* best effort */ }
                }
                _recovery.ExplorerWasKilled = true;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to terminate explorer.exe");
        }
    }

    private static void RestartExplorer()
    {
        try
        {
            var running = Process.GetProcessesByName("explorer");
            if (running.Length == 0)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to restart explorer.exe");
        }
    }

    // Static Recovery Method

    /// <summary>
    /// Restores system state after an ungraceful shutdown or crash using the persisted
    /// <see cref="RecoveryState"/>, or performs a safe fallback restore if the file is missing.
    /// </summary>
    public static void RestoreFromRecovery(ILogger? logger = null)
    {
        var log = logger ?? Log;
        log.Information("Performing emergency recovery of system state...");

        try
        {
            var state = RecoveryState.Load();

            // 1. Show taskbars
            ShowTaskbar();

            // 2. Revert registry policies
            if (state != null && state.OriginalRegistryValues.Any())
            {
                foreach (var entry in state.OriginalRegistryValues)
                {
                    try
                    {
                        string fullPath = entry.Key;
                        int? originalValue = entry.Value;
                        string pathWithoutRoot = fullPath.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase)
                            ? fullPath[5..]
                            : fullPath;

                        int lastBackslash = pathWithoutRoot.LastIndexOf('\\');
                        if (lastBackslash <= 0) continue;

                        string subKey = pathWithoutRoot[..lastBackslash];
                        string valueName = pathWithoutRoot[(lastBackslash + 1)..];

                        if (originalValue.HasValue)
                        {
                            SetRegistryValue(subKey, valueName, originalValue.Value);
                        }
                        else
                        {
                            DeleteRegistryValue(subKey, valueName);
                        }
                    }
                    catch (Exception ex)
                    {
                        log.Warning(ex, "Recovery: Failed to revert registry key {Key}", entry.Key);
                    }
                }
            }
            else
            {
                // Fallback registry cleanup: delete known lockout values
                DeleteRegistryValue(Constants.SystemPolicySubKey, "DisableTaskMgr");
                DeleteRegistryValue(Constants.SystemPolicySubKey, "DisableLockWorkstation");
                DeleteRegistryValue(Constants.SystemPolicySubKey, "DisableChangePassword");
                DeleteRegistryValue(Constants.ExplorerPolicySubKey, "NoLogoff");
            }

            // 3. Re-enable accessibility hotkeys
            try
            {
                uint skFlags = state?.OriginalStickyKeysFlags ?? (SKF_HOTKEYACTIVE | SKF_CONFIRMHOTKEY);
                var sk = new STICKYKEYS { cbSize = (uint)Marshal.SizeOf<STICKYKEYS>(), dwFlags = skFlags };
                SystemParametersInfo(SPI_SETSTICKYKEYS, sk.cbSize, ref sk, SPIF_SENDCHANGE);

                uint fkFlags = state?.OriginalFilterKeysFlags ?? (FKF_HOTKEYACTIVE | FKF_CONFIRMHOTKEY);
                var fk = new FILTERKEYS { cbSize = (uint)Marshal.SizeOf<FILTERKEYS>(), dwFlags = fkFlags };
                SystemParametersInfo(SPI_SETFILTERKEYS, fk.cbSize, ref fk, SPIF_SENDCHANGE);

                uint tkFlags = state?.OriginalToggleKeysFlags ?? (TKF_HOTKEYACTIVE | TKF_CONFIRMHOTKEY);
                var tk = new TOGGLEKEYS { cbSize = (uint)Marshal.SizeOf<TOGGLEKEYS>(), dwFlags = tkFlags };
                SystemParametersInfo(SPI_SETTOGGLEKEYS, tk.cbSize, ref tk, SPIF_SENDCHANGE);
            }
            catch (Exception ex)
            {
                log.Warning(ex, "Recovery: Failed to restore accessibility hotkeys");
            }

            // 4. Restart explorer if needed
            if (state?.ExplorerWasKilled == true || Process.GetProcessesByName("explorer").Length == 0)
            {
                RestartExplorer();
            }

            // 5. Clear recovery file
            RecoveryState.ClearRecoveryFile();
            log.Information("System state successfully restored");
        }
        catch (Exception ex)
        {
            log.Error(ex, "Critical error during RestoreFromRecovery");
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
            if (_engaged)
            {
                Disengage();
            }
            _disposed = true;
        }
    }

    ~KioskLockService()
    {
        Dispose(false);
    }
}
