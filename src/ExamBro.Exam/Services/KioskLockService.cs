using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using ExamBro.Core;
using ExamBro.Core.Models;
using ExamBro.Core.Recovery;
using ExamBro.Core.Security;
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
    /// Engages full kiosk lockdown: records user SID and pre-exam policy values, registers
    /// the self-contained boot/logon recovery scheduled task, hides taskbars, writes registry
    /// lockdown policies, and suppresses accessibility hotkeys.
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
            // 0. Capture user SID/hive, pre-record original registry values, flush recovery journal,
            //    and register the boot/logon scheduled task BEFORE applying any system lockdown.
            CaptureCurrentUserInfo();
            if (config.Security.DisableRegistryPolicies)
            {
                CaptureOriginalRegistryValues();
            }
            PersistRecoverySnapshotAndTask();

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

            // 5. Re-persist recovery state and script with final WEKF/accessibility/explorer flags
            PersistRecoverySnapshotAndTask(registerTask: false);
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
    /// re-enables accessibility hotkeys, removes the boot recovery scheduled task,
    /// and deletes the recovery state files.
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

            // 5. Remove boot recovery scheduled task and delete recovery state files
            UnregisterEmergencyRecoveryTask();
            RecoveryState.ClearRecoveryFile();
            _engaged = false;

            Log.Information("Kiosk lockdown successfully disengaged");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error occurred while disengaging kiosk lockdown");
        }
    }

    private void CaptureCurrentUserInfo()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                _recovery.UserSid = identity.User?.Value;
            }

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                string ntUserDat = Path.Combine(userProfile, "NTUSER.DAT");
                _recovery.UserHivePath = ntUserDat;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to capture current user SID or NTUSER.DAT path");
        }
    }

    private void CaptureOriginalRegistryValues()
    {
        foreach (bool useHklm in new[] { false, true })
        {
            foreach (string name in SystemPolicyValueNames)
            {
                CaptureSingleOriginalValue(Constants.SystemPolicySubKey, name, useHklm);
            }
            foreach (string name in ExplorerPolicyValueNames)
            {
                CaptureSingleOriginalValue(Constants.ExplorerPolicySubKey, name, useHklm);
            }
            foreach (string name in GroupPolicyExplorerValueNames)
            {
                CaptureSingleOriginalValue(Constants.GroupPolicyExplorerSubKey, name, useHklm);
            }
        }
        CaptureSingleOriginalValue(@"Control Panel\Desktop", "LowLevelHooksTimeout", useHklm: false);
    }

    private void CaptureSingleOriginalValue(string subKey, string valueName, bool useHklm)
    {
        string rootPrefix = useHklm ? "HKLM" : "HKCU";
        string fullPath = $@"{rootPrefix}\{subKey}\{valueName}";
        if (!_recovery.OriginalRegistryValues.ContainsKey(fullPath))
        {
            _recovery.OriginalRegistryValues[fullPath] = GetRegistryValue(subKey, valueName, useHklm);
        }
    }

    private void PersistRecoverySnapshotAndTask(bool registerTask = true)
    {
        try
        {
            _recovery.Save();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save recovery state JSON");
        }

        try
        {
            string scriptContent = _recovery.BuildPowerShellRecoveryScript();
            AtomicFileWriter.WriteAllTextFlushed(Constants.SystemRecoveryScriptPath, scriptContent);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to write self-contained PowerShell recovery script to {Path}", Constants.SystemRecoveryScriptPath);
        }

        if (registerTask)
        {
            RegisterEmergencyRecoveryTask();
        }
    }

    private static void RegisterEmergencyRecoveryTask()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        string tempXmlPath = Path.Combine(Path.GetTempPath(), $"exambro_task_{Guid.NewGuid():N}.xml");
        try
        {
            string xmlContent = RecoveryState.BuildScheduledTaskXml(Constants.SystemRecoveryScriptPath);
            File.WriteAllText(tempXmlPath, xmlContent, Encoding.Unicode);

            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Create /TN \"{Constants.RecoveryScheduledTaskName}\" /XML \"{tempXmlPath}\" /F",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (proc != null)
            {
                proc.WaitForExit(4000);
                if (proc.ExitCode == 0)
                {
                    Log.Information("Registered boot/logon recovery Scheduled Task: {TaskName}", Constants.RecoveryScheduledTaskName);
                    return;
                }
            }

            // Fallback registration using command-line triggers if XML import was rejected
            string psArgs = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \\\"{Constants.SystemRecoveryScriptPath}\\\"";
            using var fallbackProc = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Create /TN \"{Constants.RecoveryScheduledTaskName}\" /TR \"powershell.exe {psArgs}\" /SC ONSTART /RU SYSTEM /RL HIGHEST /F",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            fallbackProc?.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to register emergency recovery Scheduled Task");
        }
        finally
        {
            try
            {
                if (File.Exists(tempXmlPath))
                    File.Delete(tempXmlPath);
            }
            catch { }
        }
    }

    private static void UnregisterEmergencyRecoveryTask()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Delete /TN \"{Constants.RecoveryScheduledTaskName}\" /F",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            proc?.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to unregister emergency recovery Scheduled Task");
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

    private static readonly string[] SystemPolicyValueNames =
    {
        "DisableTaskMgr",
        "DisableLockWorkstation",
        "DisableChangePassword",
        "HideFastUserSwitching",
        "NoDispCPL"
    };

    private static readonly string[] ExplorerPolicyValueNames =
    {
        "NoLogoff",
        "NoClose",
        "DisableNotificationCenter",
        "DisableNotificationCenterIcon",
        "HideNotificationCenterIcon",
        "DisableControlCenter",
        "HideControlCenter",
        "DisableQuickSettings",
        "NoControlCenter",
        "NoWindowsInkWorkspace",
        "NoWinKeys"
    };

    private static readonly string[] GroupPolicyExplorerValueNames =
    {
        "DisableNotificationCenter",
        "DisableNotificationCenterIcon",
        "HideNotificationCenterIcon",
        "DisableControlCenter",
        "HideControlCenter",
        "DisableQuickSettings",
        "NoControlCenter",
        "NoWinKeys"
    };

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
                    finally { p.Dispose(); }
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
            bool isRunning = running.Length > 0;
            foreach (var p in running)
            {
                p.Dispose();
            }

            if (!isRunning && Environment.UserInteractive)
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
    /// Checks whether the system has either a recovery journal file OR residual kiosk lockdown
    /// policies left over in the Windows Registry after a hard power-off (even if recovery.json was lost).
    /// </summary>
    public static bool HasResidualLockdownState()
    {
        if (RecoveryState.NeedsRecovery())
        {
            return true;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return false;
        }

        try
        {
            foreach (bool useHklm in new[] { false, true })
            {
                if (GetRegistryValue(Constants.ExplorerPolicySubKey, "NoClose", useHklm) == 1 ||
                    GetRegistryValue(Constants.ExplorerPolicySubKey, "NoLogoff", useHklm) == 1 ||
                    GetRegistryValue(Constants.SystemPolicySubKey, "DisableTaskMgr", useHklm) == 1)
                {
                    return true;
                }
            }

            using var ifeoKey = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\DisplaySwitch.exe");
            string? debugger = ifeoKey?.GetValue("Debugger")?.ToString();
            if (!string.IsNullOrEmpty(debugger) &&
                debugger.Contains("systray.exe", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Error checking for residual lockdown registry keys");
        }

        return false;
    }

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

            // 2. Always perform baseline cleanup of all known lockdown policy keys first
            //    so that even a partial/interrupted lockdown leaves zero NoClose/NoLogoff keys behind
            foreach (bool useHklm in new[] { false, true })
            {
                foreach (string name in SystemPolicyValueNames)
                {
                    DeleteRegistryValue(Constants.SystemPolicySubKey, name, useHklm);
                }
                foreach (string name in ExplorerPolicyValueNames)
                {
                    DeleteRegistryValue(Constants.ExplorerPolicySubKey, name, useHklm);
                }
                foreach (string name in GroupPolicyExplorerValueNames)
                {
                    DeleteRegistryValue(Constants.GroupPolicyExplorerSubKey, name, useHklm);
                }
            }
            DeleteRegistryValue(@"Control Panel\Desktop", "LowLevelHooksTimeout", useHklm: false);

            // Also clean the recorded user SID hive under HKEY_USERS (crucial when running as SYSTEM)
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !string.IsNullOrWhiteSpace(state?.UserSid))
            {
                CleanupUserSidHivePolicies(state.UserSid);
            }

            // 2b. Re-apply any pre-existing non-null original registry values recorded before lockdown
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

            DeleteRegistrySubKeyTree(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\DisplaySwitch.exe", useHklm: true);

            // 3. Re-enable accessibility hotkeys
            try
            {
                uint skFlags = (state != null && state.OriginalStickyKeysFlags != 0)
                    ? state.OriginalStickyKeysFlags
                    : (SKF_HOTKEYACTIVE | SKF_CONFIRMHOTKEY);
                var sk = new STICKYKEYS { cbSize = (uint)Marshal.SizeOf<STICKYKEYS>(), dwFlags = skFlags };
                SystemParametersInfo(SPI_SETSTICKYKEYS, sk.cbSize, ref sk, SPIF_SENDCHANGE);

                uint fkFlags = (state != null && state.OriginalFilterKeysFlags != 0)
                    ? state.OriginalFilterKeysFlags
                    : (FKF_HOTKEYACTIVE | FKF_CONFIRMHOTKEY);
                var fk = new FILTERKEYS { cbSize = (uint)Marshal.SizeOf<FILTERKEYS>(), dwFlags = fkFlags };
                SystemParametersInfo(SPI_SETFILTERKEYS, fk.cbSize, ref fk, SPIF_SENDCHANGE);

                uint tkFlags = (state != null && state.OriginalToggleKeysFlags != 0)
                    ? state.OriginalToggleKeysFlags
                    : (TKF_HOTKEYACTIVE | TKF_CONFIRMHOTKEY);
                var tk = new TOGGLEKEYS { cbSize = (uint)Marshal.SizeOf<TOGGLEKEYS>(), dwFlags = tkFlags };
                SystemParametersInfo(SPI_SETTOGGLEKEYS, tk.cbSize, ref tk, SPIF_SENDCHANGE);
            }
            catch (Exception ex)
            {
                log.Warning(ex, "Recovery: Failed to restore accessibility hotkeys");
            }

            // 4. Restart explorer if needed
            var explorerProcs = Process.GetProcessesByName("explorer");
            bool explorerRunning = explorerProcs.Length > 0;
            foreach (var ep in explorerProcs) ep.Dispose();

            if (state?.ExplorerWasKilled == true || !explorerRunning)
            {
                RestartExplorer();
            }

            // 5. Restore Keyboard Filter (WEKF)
            RestoreKeyboardFilterStatic(state, log);

            // 6. Unregister boot recovery scheduled task and clear recovery files
            UnregisterEmergencyRecoveryTask();
            RecoveryState.ClearRecoveryFile();
            log.Information("System state successfully restored");
        }
        catch (Exception ex)
        {
            log.Error(ex, "Critical error during RestoreFromRecovery");
        }
    }

    private static void CleanupUserSidHivePolicies(string userSid)
    {
        try
        {
            foreach (string name in SystemPolicyValueNames)
            {
                DeleteUsersRegistryValue($@"{userSid}\{Constants.SystemPolicySubKey}", name);
            }
            foreach (string name in ExplorerPolicyValueNames)
            {
                DeleteUsersRegistryValue($@"{userSid}\{Constants.ExplorerPolicySubKey}", name);
            }
            foreach (string name in GroupPolicyExplorerValueNames)
            {
                DeleteUsersRegistryValue($@"{userSid}\{Constants.GroupPolicyExplorerSubKey}", name);
            }
            DeleteUsersRegistryValue($@"{userSid}\Control Panel\Desktop", "LowLevelHooksTimeout");
        }
        catch { }
    }

    private static void DeleteUsersRegistryValue(string subKeyUnderUsers, string valueName)
    {
        try
        {
            using var key = Registry.Users.OpenSubKey(subKeyUnderUsers, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }
        catch { }
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
