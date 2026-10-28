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
            SetPolicyAndSaveOriginal(Constants.SystemPolicySubKey, "NoDispCPL", 1, useHklm);

            // Explorer policies: NoLogoff (Disable Sign out), NoClose (Disable Shutdown from SAS),
            // DisableNotificationCenter (blocks Action Center flyout), DisableControlCenter / QuickSettings (blocks Windows 11 Quick Settings flyout),
            // NoWinKeys (blocks Windows shortcut keys), HideNotificationCenterIcon, NoWindowsInkWorkspace
            SetPolicyAndSaveOriginal(Constants.ExplorerPolicySubKey, "NoLogoff", 1, useHklm);
            SetPolicyAndSaveOriginal(Constants.ExplorerPolicySubKey, "NoClose", 1, useHklm);
            SetPolicyAndSaveOriginal(Constants.ExplorerPolicySubKey, "DisableNotificationCenter", 1, useHklm);
            SetPolicyAndSaveOriginal(Constants.ExplorerPolicySubKey, "DisableNotificationCenterIcon", 1, useHklm);
            SetPolicyAndSaveOriginal(Constants.ExplorerPolicySubKey, "HideNotificationCenterIcon", 1, useHklm);
            SetPolicyAndSaveOriginal(Constants.ExplorerPolicySubKey, "DisableControlCenter", 1, useHklm);
            SetPolicyAndSaveOriginal(Constants.ExplorerPolicySubKey, "HideControlCenter", 1, useHklm);
            SetPolicyAndSaveOriginal(Constants.ExplorerPolicySubKey, "DisableQuickSettings", 1, useHklm);
            SetPolicyAndSaveOriginal(Constants.ExplorerPolicySubKey, "NoControlCenter", 1, useHklm);
            SetPolicyAndSaveOriginal(Constants.ExplorerPolicySubKey, "NoWindowsInkWorkspace", 1, useHklm);
            SetPolicyAndSaveOriginal(Constants.ExplorerPolicySubKey, "NoWinKeys", 1, useHklm);

            // Group Policy Explorer policies: DisableNotificationCenter, DisableControlCenter, DisableQuickSettings, NoWinKeys
            SetPolicyAndSaveOriginal(Constants.GroupPolicyExplorerSubKey, "DisableNotificationCenter", 1, useHklm);
            SetPolicyAndSaveOriginal(Constants.GroupPolicyExplorerSubKey, "DisableNotificationCenterIcon", 1, useHklm);
            SetPolicyAndSaveOriginal(Constants.GroupPolicyExplorerSubKey, "HideNotificationCenterIcon", 1, useHklm);
            SetPolicyAndSaveOriginal(Constants.GroupPolicyExplorerSubKey, "DisableControlCenter", 1, useHklm);
            SetPolicyAndSaveOriginal(Constants.GroupPolicyExplorerSubKey, "HideControlCenter", 1, useHklm);
            SetPolicyAndSaveOriginal(Constants.GroupPolicyExplorerSubKey, "DisableQuickSettings", 1, useHklm);
            SetPolicyAndSaveOriginal(Constants.GroupPolicyExplorerSubKey, "NoControlCenter", 1, useHklm);
            SetPolicyAndSaveOriginal(Constants.GroupPolicyExplorerSubKey, "NoWinKeys", 1, useHklm);
        }

        // Multi-layer hook protection: ensure LowLevelHooksTimeout is set to 10000ms (10 seconds)
        // so Windows never unhooks or times out WH_KEYBOARD_LL during rapid repeated key presses
        SetPolicyAndSaveOriginal(@"Control Panel\Desktop", "LowLevelHooksTimeout", 10000, useHklm: false);

        // Multi-layer projection disarming: Image File Execution Options (IFEO) debugger for DisplaySwitch.exe
        // When configured, any attempt by Windows or OEM hotkey drivers to launch DisplaySwitch.exe is redirected to a harmless dummy
        SetRegistryStringValue(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\DisplaySwitch.exe", "Debugger", "systray.exe", useHklm: true);

        // Try Windows Keyboard Filter (WEKF) via WMI if available
        ConfigureKeyboardFilter(enableFilter: true);
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
        ConfigureKeyboardFilter(enableFilter: false);

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

        // Clean up IFEO debugger for DisplaySwitch
        DeleteRegistrySubKeyTree(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\DisplaySwitch.exe", useHklm: true);

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

    public static void SetRegistryStringValue(string subKey, string valueName, string value, bool useHklm = false)
    {
        try
        {
            var baseKey = useHklm ? Registry.LocalMachine : Registry.CurrentUser;
            using var key = baseKey.CreateSubKey(subKey);
            key?.SetValue(valueName, value, RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Registry string write error for {Root}\\{SubKey}\\{ValueName}", useHklm ? "HKLM" : "HKCU", subKey, valueName);
        }
    }

    public static void DeleteRegistrySubKeyTree(string subKey, bool useHklm = false)
    {
        try
        {
            var baseKey = useHklm ? Registry.LocalMachine : Registry.CurrentUser;
            baseKey.DeleteSubKeyTree(subKey, false);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Registry delete subkey tree error for {Root}\\{SubKey}", useHklm ? "HKLM" : "HKCU", subKey);
        }
    }

    private static void EnsureKeyboardFilterServiceRunning()
    {
        try
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;

            using var sc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = "query MsKeyboardFilter",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            sc.Start();
            string output = sc.StandardOutput.ReadToEnd();
            sc.WaitForExit(2000);

            if (output.Contains("FAILED 1060"))
            {
                // Service not found. Attempt enabling Windows optional feature Client-KeyboardFilter
                Log.Information("MsKeyboardFilter service not found. Attempting to enable Client-KeyboardFilter feature via DISM...");
                using var dism = Process.Start(new ProcessStartInfo
                {
                    FileName = "dism.exe",
                    Arguments = "/online /Enable-Feature /FeatureName:Client-KeyboardFilter /NoRestart",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                dism?.WaitForExit(10000);
            }

            using var scConfig = Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = "config MsKeyboardFilter start= auto",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            scConfig?.WaitForExit(2000);

            using var scStart = Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = "start MsKeyboardFilter",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            scStart?.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to ensure MsKeyboardFilter service is running.");
        }
    }

    private void ConfigureKeyboardFilter(bool enableFilter)
    {
        try
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;

            if (enableFilter)
            {
                EnsureKeyboardFilterServiceRunning();
            }

            var scope = new ManagementScope(@"\\.\root\standardcimv2\embedded");
            scope.Connect();

            // 1. Configure WEKF_Settings: DisableKeyboardFilterForAdministrators must be "false"
            // By default, Windows ignores keyboard filters for administrator accounts.
            // Setting this to "false" ensures Ctrl+Alt+Del is blocked even for elevated administrator sessions.
            var querySettings = new ObjectQuery("SELECT * FROM WEKF_Settings WHERE Name='DisableKeyboardFilterForAdministrators'");
            using (var searcherSettings = new ManagementObjectSearcher(scope, querySettings))
            {
                foreach (ManagementObject obj in searcherSettings.Get())
                {
                    if (enableFilter)
                    {
                        string currentVal = obj["Value"]?.ToString() ?? "true";
                        _recovery.OriginalWekfDisableForAdminValue ??= currentVal;
                        obj["Value"] = "false";
                        obj.Put();
                        Log.Information("WEKF Setting DisableKeyboardFilterForAdministrators set to false (was {OldVal})", currentVal);
                    }
                    else if (_recovery.OriginalWekfDisableForAdminValue != null)
                    {
                        obj["Value"] = _recovery.OriginalWekfDisableForAdminValue;
                        obj.Put();
                        Log.Information("WEKF Setting DisableKeyboardFilterForAdministrators restored to {Value}", _recovery.OriginalWekfDisableForAdminValue);
                    }
                }
            }

            // 2. Configure WEKF_Settings: ForceOffAccessibility to prevent accessibility escapes
            var queryAccessibility = new ObjectQuery("SELECT * FROM WEKF_Settings WHERE Name='ForceOffAccessibility'");
            using (var searcherAccessibility = new ManagementObjectSearcher(scope, queryAccessibility))
            {
                foreach (ManagementObject obj in searcherAccessibility.Get())
                {
                    if (enableFilter)
                    {
                        string currentVal = obj["Value"]?.ToString() ?? "false";
                        _recovery.OriginalWekfForceOffAccessibilityValue ??= currentVal;
                        obj["Value"] = "true";
                        obj.Put();
                        Log.Information("WEKF Setting ForceOffAccessibility set to true (was {OldVal})", currentVal);
                    }
                    else if (_recovery.OriginalWekfForceOffAccessibilityValue != null)
                    {
                        obj["Value"] = _recovery.OriginalWekfForceOffAccessibilityValue;
                        obj.Put();
                        Log.Information("WEKF Setting ForceOffAccessibility restored to {Value}", _recovery.OriginalWekfForceOffAccessibilityValue);
                    }
                }
            }

            // 3. Configure WEKF_PredefinedKey: Ctrl+Alt+Del
            var queryKeys = new ObjectQuery("SELECT * FROM WEKF_PredefinedKey WHERE Id='Ctrl+Alt+Del' OR Id='Ctrl+Alt+Delete'");
            using (var searcherKeys = new ManagementObjectSearcher(scope, queryKeys))
            {
                foreach (ManagementObject obj in searcherKeys.Get())
                {
                    if (enableFilter)
                    {
                        bool currentEnabled = Convert.ToBoolean(obj["Enabled"]);
                        _recovery.OriginalWekfCtrlAltDelEnabled ??= currentEnabled;
                        obj["Enabled"] = true;
                        obj.Put();
                        Log.Information("WEKF PredefinedKey {Id} set to True (was {OldVal})", obj["Id"], currentEnabled);
                    }
                    else
                    {
                        bool restoreVal = _recovery.OriginalWekfCtrlAltDelEnabled ?? false;
                        obj["Enabled"] = restoreVal;
                        obj.Put();
                        Log.Information("WEKF PredefinedKey {Id} restored to {Value}", obj["Id"], restoreVal);
                    }
                }
            }

            // Fallback: Also try invoking static method Enable / Disable on WEKF_PredefinedKey
            try
            {
                using var wekfClass = new ManagementClass(scope, new ManagementPath("WEKF_PredefinedKey"), null);
                string methodName = enableFilter ? "Enable" : "Disable";
                wekfClass.InvokeMethod(methodName, new object[] { "Ctrl+Alt+Del" });
                Log.Information("Invoked WEKF_PredefinedKey.{MethodName}(\"Ctrl+Alt+Del\") successfully", methodName);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "WEKF_PredefinedKey static method invocation skipped or unsupported.");
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "WEKF (Keyboard Filter) unavailable or not installed on this system.");
        }
    }

    private static void RestoreKeyboardFilterStatic(RecoveryState? state, ILogger log)
    {
        try
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;

            var scope = new ManagementScope(@"\\.\root\standardcimv2\embedded");
            scope.Connect();

            if (!string.IsNullOrEmpty(state?.OriginalWekfDisableForAdminValue))
            {
                var querySettings = new ObjectQuery("SELECT * FROM WEKF_Settings WHERE Name='DisableKeyboardFilterForAdministrators'");
                using var searcherSettings = new ManagementObjectSearcher(scope, querySettings);
                foreach (ManagementObject obj in searcherSettings.Get())
                {
                    obj["Value"] = state.OriginalWekfDisableForAdminValue;
                    obj.Put();
                }
            }

            if (!string.IsNullOrEmpty(state?.OriginalWekfForceOffAccessibilityValue))
            {
                var queryAccessibility = new ObjectQuery("SELECT * FROM WEKF_Settings WHERE Name='ForceOffAccessibility'");
                using var searcherAccessibility = new ManagementObjectSearcher(scope, queryAccessibility);
                foreach (ManagementObject obj in searcherAccessibility.Get())
                {
                    obj["Value"] = state.OriginalWekfForceOffAccessibilityValue;
                    obj.Put();
                }
            }

            var queryKeys = new ObjectQuery("SELECT * FROM WEKF_PredefinedKey WHERE Id='Ctrl+Alt+Del' OR Id='Ctrl+Alt+Delete'");
            using var searcherKeys = new ManagementObjectSearcher(scope, queryKeys);
            foreach (ManagementObject obj in searcherKeys.Get())
            {
                obj["Enabled"] = state?.OriginalWekfCtrlAltDelEnabled ?? false;
                obj.Put();
            }

            try
            {
                using var wekfClass = new ManagementClass(scope, new ManagementPath("WEKF_PredefinedKey"), null);
                wekfClass.InvokeMethod("Disable", new object[] { "Ctrl+Alt+Del" });
            }
            catch { }

            log.Information("Recovery: WEKF settings successfully restored");
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Recovery: WEKF unavailable during cleanup.");
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
                        log.Warning(ex, "Recovery: Failed to revert registry key {Key}", entry.Key);
                    }
                }
            }
            else
            {
                // Fallback registry cleanup: delete known lockout values from both HKCU and HKLM
                foreach (bool useHklm in new[] { false, true })
                {
                    DeleteRegistryValue(Constants.SystemPolicySubKey, "DisableTaskMgr", useHklm);
                    DeleteRegistryValue(Constants.SystemPolicySubKey, "DisableLockWorkstation", useHklm);
                    DeleteRegistryValue(Constants.SystemPolicySubKey, "DisableChangePassword", useHklm);
                    DeleteRegistryValue(Constants.SystemPolicySubKey, "HideFastUserSwitching", useHklm);
                    DeleteRegistryValue(Constants.SystemPolicySubKey, "NoDispCPL", useHklm);
                    DeleteRegistryValue(Constants.ExplorerPolicySubKey, "NoLogoff", useHklm);
                    DeleteRegistryValue(Constants.ExplorerPolicySubKey, "NoClose", useHklm);
                    DeleteRegistryValue(Constants.ExplorerPolicySubKey, "DisableNotificationCenter", useHklm);
                    DeleteRegistryValue(Constants.ExplorerPolicySubKey, "DisableNotificationCenterIcon", useHklm);
                    DeleteRegistryValue(Constants.ExplorerPolicySubKey, "HideNotificationCenterIcon", useHklm);
                    DeleteRegistryValue(Constants.ExplorerPolicySubKey, "DisableControlCenter", useHklm);
                    DeleteRegistryValue(Constants.ExplorerPolicySubKey, "HideControlCenter", useHklm);
                    DeleteRegistryValue(Constants.ExplorerPolicySubKey, "DisableQuickSettings", useHklm);
                    DeleteRegistryValue(Constants.ExplorerPolicySubKey, "NoControlCenter", useHklm);
                    DeleteRegistryValue(Constants.ExplorerPolicySubKey, "NoWindowsInkWorkspace", useHklm);
                    DeleteRegistryValue(Constants.ExplorerPolicySubKey, "NoWinKeys", useHklm);
                    DeleteRegistryValue(Constants.GroupPolicyExplorerSubKey, "DisableNotificationCenter", useHklm);
                    DeleteRegistryValue(Constants.GroupPolicyExplorerSubKey, "DisableNotificationCenterIcon", useHklm);
                    DeleteRegistryValue(Constants.GroupPolicyExplorerSubKey, "HideNotificationCenterIcon", useHklm);
                    DeleteRegistryValue(Constants.GroupPolicyExplorerSubKey, "DisableControlCenter", useHklm);
                    DeleteRegistryValue(Constants.GroupPolicyExplorerSubKey, "HideControlCenter", useHklm);
                    DeleteRegistryValue(Constants.GroupPolicyExplorerSubKey, "DisableQuickSettings", useHklm);
                    DeleteRegistryValue(Constants.GroupPolicyExplorerSubKey, "NoControlCenter", useHklm);
                    DeleteRegistryValue(Constants.GroupPolicyExplorerSubKey, "NoWinKeys", useHklm);
                }
                DeleteRegistryValue(@"Control Panel\Desktop", "LowLevelHooksTimeout", useHklm: false);
            }

            DeleteRegistrySubKeyTree(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\DisplaySwitch.exe", useHklm: true);


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

            // 5. Restore Keyboard Filter (WEKF)
            RestoreKeyboardFilterStatic(state, log);

            // 6. Clear recovery file
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
