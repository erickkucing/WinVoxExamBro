using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExamBro.Core.Security;

namespace ExamBro.Core.Recovery;

/// <summary>
/// Persisted state that enables the application and the Windows boot/logon recovery task
/// to restore the system after an abnormal exit (crash, power loss, hard power-off).
/// Saved atomically to <see cref="Constants.SystemRecoveryFilePath"/> and <see cref="Constants.RecoveryFilePath"/>.
/// </summary>
public class RecoveryState
{
    /// <summary>Registry values that were overwritten during kiosk lock.
    /// Key = full registry path, Value = original value (null if key didn't exist).</summary>
    public Dictionary<string, int?> OriginalRegistryValues { get; set; } = new();

    /// <summary>Whether the taskbar was visible before we hid it.</summary>
    public bool TaskbarWasVisible { get; set; } = true;

    /// <summary>Original STICKYKEYS.dwFlags.</summary>
    public uint OriginalStickyKeysFlags { get; set; }

    /// <summary>Original FILTERKEYS.dwFlags.</summary>
    public uint OriginalFilterKeysFlags { get; set; }

    /// <summary>Original TOGGLEKEYS.dwFlags.</summary>
    public uint OriginalToggleKeysFlags { get; set; }

    /// <summary>True if we killed explorer.exe and need to restart it.</summary>
    public bool ExplorerWasKilled { get; set; }

    /// <summary>Original WEKF PredefinedKey Ctrl+Alt+Del enabled status.</summary>
    public bool? OriginalWekfCtrlAltDelEnabled { get; set; }

    /// <summary>Original WEKF Setting DisableKeyboardFilterForAdministrators value.</summary>
    public string? OriginalWekfDisableForAdminValue { get; set; }

    /// <summary>Original WEKF Setting ForceOffAccessibility value.</summary>
    public string? OriginalWekfForceOffAccessibilityValue { get; set; }

    /// <summary>Windows Security Identifier (SID) of the user session that engaged kiosk lockdown.</summary>
    public string? UserSid { get; set; }

    /// <summary>Absolute path to the user's NTUSER.DAT registry hive for offline boot-time restoration.</summary>
    public string? UserHivePath { get; set; }

    /// <summary>UTC timestamp when this state was written.</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Persist recovery state to disk atomically with physical disk flush.
    /// When <paramref name="path"/> is null, writes to both ProgramData (for SYSTEM boot recovery)
    /// and LocalAppData (for user session recovery).
    /// </summary>
    public void Save(string? path = null)
    {
        Timestamp = DateTime.UtcNow;
        string json = JsonSerializer.Serialize(this, JsonOpts);

        if (!string.IsNullOrWhiteSpace(path))
        {
            AtomicFileWriter.WriteAllTextFlushed(path, json);
            return;
        }

        bool wroteAny = false;
        Exception? lastError = null;

        foreach (string target in new[] { Constants.SystemRecoveryFilePath, Constants.RecoveryFilePath })
        {
            try
            {
                AtomicFileWriter.WriteAllTextFlushed(target, json);
                wroteAny = true;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        if (!wroteAny && lastError != null)
        {
            throw lastError;
        }
    }

    /// <summary>
    /// Load recovery state from disk. Returns null if missing or corrupt.
    /// Compares <see cref="Timestamp"/> across ProgramData and LocalAppData so the latest snapshot wins.
    /// </summary>
    public static RecoveryState? Load(string? path = null)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            return TryLoadSingle(path);
        }

        return LoadLatest(Constants.SystemRecoveryFilePath, Constants.RecoveryFilePath);
    }

    /// <summary>
    /// Loads all readable <see cref="RecoveryState"/> files from <paramref name="paths"/>
    /// and returns the entry with the newest <see cref="Timestamp"/>.
    /// </summary>
    public static RecoveryState? LoadLatest(params string[] paths)
    {
        RecoveryState? newest = null;
        foreach (string candidate in paths)
        {
            var loaded = TryLoadSingle(candidate);
            if (loaded != null && (newest == null || loaded.Timestamp > newest.Timestamp))
            {
                newest = loaded;
            }
        }
        return newest;
    }

    private static RecoveryState? TryLoadSingle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;

        try
        {
            string json = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            return JsonSerializer.Deserialize<RecoveryState>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Check whether a recovery file exists (i.e. a previous session didn't exit cleanly).
    /// </summary>
    public static bool NeedsRecovery(string? path = null)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            return File.Exists(path);
        }

        return File.Exists(Constants.SystemRecoveryFilePath) ||
               File.Exists(Constants.RecoveryFilePath);
    }

    /// <summary>
    /// Delete all recovery files and scripts after successful restoration.
    /// </summary>
    public static void ClearRecoveryFile(string? path = null)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            TryDeleteFile(path);
            return;
        }

        TryDeleteFile(Constants.SystemRecoveryFilePath);
        TryDeleteFile(Constants.RecoveryFilePath);
        TryDeleteFile(Constants.SystemRecoveryScriptPath);
    }

    private static void TryDeleteFile(string filePath)
    {
        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            try { File.Delete(filePath); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Generates a self-contained PowerShell recovery script that restores all HKLM/HKCU registry
    /// policies, mounts NTUSER.DAT if executed at boot before user logon, removes IFEO DisplaySwitch
    /// hooks, resets WMI WEKF (Ctrl+Alt+Del) filter rules, and unregisters the recovery scheduled task.
    /// Does not depend on the ExamBro executable being present (works even if USB drive was removed).
    /// </summary>
    public string BuildPowerShellRecoveryScript(
        string? recoveryJsonPath = null,
        string? localRecoveryJsonPath = null,
        string? taskName = null)
    {
        string sysJsonPath = EscapePsSingleQuoted(recoveryJsonPath ?? Constants.SystemRecoveryFilePath);
        string usrJsonPath = EscapePsSingleQuoted(localRecoveryJsonPath ?? Constants.RecoveryFilePath);
        string schedTaskName = EscapePsSingleQuoted(taskName ?? Constants.RecoveryScheduledTaskName);
        string userSid = EscapePsSingleQuoted(UserSid ?? string.Empty);
        string userHivePath = EscapePsSingleQuoted(UserHivePath ?? string.Empty);
        string wekfAdminOrig = EscapePsSingleQuoted(OriginalWekfDisableForAdminValue ?? "true");
        string wekfAccessOrig = EscapePsSingleQuoted(OriginalWekfForceOffAccessibilityValue ?? "false");
        string wekfCadOrig = (OriginalWekfCtrlAltDelEnabled == true) ? "$true" : "$false";

        var sb = new StringBuilder();
        sb.AppendLine("# LouisVox ExamBro Self-Contained Emergency Boot/Logon Recovery Script");
        sb.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
        sb.AppendLine("# Abort immediately if an active ExamBro session is running (guards against delayed LogonTrigger racing a new exam launch)");
        sb.AppendLine("$activeExam = Get-Process -Name 'ExamBro.Exam','LouisVox ExamBro' -ErrorAction SilentlyContinue");
        sb.AppendLine("if ($activeExam) { exit 0 }");
        sb.AppendLine();
        sb.AppendLine($"$SysRecoveryJson = '{sysJsonPath}'");
        sb.AppendLine($"$UsrRecoveryJson = '{usrJsonPath}'");
        sb.AppendLine($"$TaskName = '{schedTaskName}'");
        sb.AppendLine($"$TargetUserSid = '{userSid}'");
        sb.AppendLine($"$TargetUserHivePath = '{userHivePath}'");
        sb.AppendLine();
        sb.AppendLine("function Restore-PolicyKey([string]$RootPath, [string]$SubKey, [string]$Name, $OrigValue) {");
        sb.AppendLine("    $fullKey = Join-Path $RootPath $SubKey");
        sb.AppendLine("    if ($null -ne $OrigValue -and $OrigValue -ne '') {");
        sb.AppendLine("        if (!(Test-Path $fullKey)) { New-Item -Path $fullKey -Force | Out-Null }");
        sb.AppendLine("        Set-ItemProperty -Path $fullKey -Name $Name -Value ([int]$OrigValue) -Type DWord -Force | Out-Null");
        sb.AppendLine("    } else {");
        sb.AppendLine("        if (Test-Path $fullKey) {");
        sb.AppendLine("            Remove-ItemProperty -Path $fullKey -Name $Name -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("function Clear-KnownLockdownPolicies([string]$HiveRoot) {");
        sb.AppendLine("    $sysPol = 'Software\\Microsoft\\Windows\\CurrentVersion\\Policies\\System'");
        sb.AppendLine("    $expPol = 'Software\\Microsoft\\Windows\\CurrentVersion\\Policies\\Explorer'");
        sb.AppendLine("    $gpExpPol = 'Software\\Policies\\Microsoft\\Windows\\Explorer'");
        sb.AppendLine("    foreach ($name in @('DisableTaskMgr','DisableLockWorkstation','DisableChangePassword','HideFastUserSwitching','NoDispCPL')) {");
        sb.AppendLine("        Restore-PolicyKey $HiveRoot $sysPol $name $null");
        sb.AppendLine("    }");
        sb.AppendLine("    foreach ($name in @('NoLogoff','NoClose','DisableNotificationCenter','DisableNotificationCenterIcon','HideNotificationCenterIcon','DisableControlCenter','HideControlCenter','DisableQuickSettings','NoControlCenter','NoWindowsInkWorkspace','NoWinKeys')) {");
        sb.AppendLine("        Restore-PolicyKey $HiveRoot $expPol $name $null");
        sb.AppendLine("    }");
        sb.AppendLine("    foreach ($name in @('DisableNotificationCenter','DisableNotificationCenterIcon','HideNotificationCenterIcon','DisableControlCenter','HideControlCenter','DisableQuickSettings','NoControlCenter','NoWinKeys')) {");
        sb.AppendLine("        Restore-PolicyKey $HiveRoot $gpExpPol $name $null");
        sb.AppendLine("    }");
        sb.AppendLine("    Restore-PolicyKey $HiveRoot 'Control Panel\\Desktop' 'LowLevelHooksTimeout' $null");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("# 1. Clean all known lockdown policies in HKLM and HKCU first (fail-safe baseline)");
        sb.AppendLine("Clear-KnownLockdownPolicies 'Registry::HKEY_LOCAL_MACHINE'");
        sb.AppendLine("Clear-KnownLockdownPolicies 'Registry::HKEY_CURRENT_USER'");
        sb.AppendLine();
        sb.AppendLine("# 2. Clean all loaded user SID hives under HKEY_USERS");
        sb.AppendLine("$userHiveCleaned = $false");
        sb.AppendLine("Get-ChildItem 'Registry::HKEY_USERS' -ErrorAction SilentlyContinue | ForEach-Object {");
        sb.AppendLine("    $sidName = $_.PSChildName");
        sb.AppendLine("    if ($sidName -like 'S-1-5-21-*' -and $sidName -notlike '*_Classes') {");
        sb.AppendLine("        Clear-KnownLockdownPolicies \"Registry::HKEY_USERS\\$sidName\"");
        sb.AppendLine("        if ($sidName -eq $TargetUserSid) { $userHiveCleaned = $true }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("# 3. If running at Boot before user logon, temporarily mount NTUSER.DAT to clean user policies");
        sb.AppendLine("$mountedTempHive = $false");
        sb.AppendLine("if (-not $userHiveCleaned -and -not [string]::IsNullOrWhiteSpace($TargetUserHivePath) -and (Test-Path $TargetUserHivePath)) {");
        sb.AppendLine("    & reg.exe load 'HKU\\ExamBroRecoveryHive' \"$TargetUserHivePath\" 2>&1 | Out-Null");
        sb.AppendLine("    if (Test-Path 'Registry::HKEY_USERS\\ExamBroRecoveryHive') {");
        sb.AppendLine("        $mountedTempHive = $true");
        sb.AppendLine("        Clear-KnownLockdownPolicies 'Registry::HKEY_USERS\\ExamBroRecoveryHive'");
        sb.AppendLine("        $userHiveCleaned = $true");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();

        // Embed exact captured original values from this RecoveryState instance
        if (OriginalRegistryValues.Count > 0)
        {
            sb.AppendLine("# 4. Restore exact pre-exam original registry values captured before lockdown");
            foreach (var kvp in OriginalRegistryValues)
            {
                string fullPath = kvp.Key;
                bool isHklm = fullPath.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase);
                string pathWithoutRoot = (isHklm || fullPath.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase))
                    ? fullPath[5..]
                    : fullPath;

                int lastSlash = pathWithoutRoot.LastIndexOf('\\');
                if (lastSlash <= 0) continue;

                string subKey = EscapePsSingleQuoted(pathWithoutRoot[..lastSlash]);
                string valName = EscapePsSingleQuoted(pathWithoutRoot[(lastSlash + 1)..]);
                string valLiteral = kvp.Value.HasValue ? kvp.Value.Value.ToString() : "$null";

                if (isHklm)
                {
                    sb.AppendLine($"Restore-PolicyKey 'Registry::HKEY_LOCAL_MACHINE' '{subKey}' '{valName}' {valLiteral}");
                }
                else
                {
                    sb.AppendLine($"Restore-PolicyKey 'Registry::HKEY_CURRENT_USER' '{subKey}' '{valName}' {valLiteral}");
                    sb.AppendLine($"if (-not [string]::IsNullOrWhiteSpace($TargetUserSid) -and (Test-Path \"Registry::HKEY_USERS\\$TargetUserSid\")) {{ Restore-PolicyKey \"Registry::HKEY_USERS\\$TargetUserSid\" '{subKey}' '{valName}' {valLiteral} }}");
                    sb.AppendLine($"if ($mountedTempHive) {{ Restore-PolicyKey 'Registry::HKEY_USERS\\ExamBroRecoveryHive' '{subKey}' '{valName}' {valLiteral} }}");
                }
            }
            sb.AppendLine();
        }

        sb.AppendLine("# 5. Unmount temporary user hive if we loaded NTUSER.DAT");
        sb.AppendLine("if ($mountedTempHive) {");
        sb.AppendLine("    [GC]::Collect()");
        sb.AppendLine("    [GC]::WaitForPendingFinalizers()");
        sb.AppendLine("    Start-Sleep -Milliseconds 200");
        sb.AppendLine("    & reg.exe unload 'HKU\\ExamBroRecoveryHive' 2>&1 | Out-Null");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("# 6. Remove IFEO debugger hijack for DisplaySwitch.exe");
        sb.AppendLine("$ifeoPath = 'Registry::HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options\\DisplaySwitch.exe'");
        sb.AppendLine("if (Test-Path $ifeoPath) { Remove-Item -Path $ifeoPath -Recurse -Force -ErrorAction SilentlyContinue }");
        sb.AppendLine();
        sb.AppendLine("# 7. Restore WMI Windows Keyboard Filter (WEKF) so Ctrl+Alt+Del works normally");
        sb.AppendLine("try {");
        sb.AppendLine("    $wekfNs = 'root\\standardcimv2\\embedded'");
        sb.AppendLine($"    $adminSetting = Get-CimInstance -Namespace $wekfNs -ClassName 'WEKF_Settings' -Filter \"Name='DisableKeyboardFilterForAdministrators'\" -ErrorAction SilentlyContinue");
        sb.AppendLine($"    if ($null -ne $adminSetting) {{ Set-CimInstance -InputObject $adminSetting -Property @{{ Value = '{wekfAdminOrig}' }} -ErrorAction SilentlyContinue }}");
        sb.AppendLine($"    $accSetting = Get-CimInstance -Namespace $wekfNs -ClassName 'WEKF_Settings' -Filter \"Name='ForceOffAccessibility'\" -ErrorAction SilentlyContinue");
        sb.AppendLine($"    if ($null -ne $accSetting) {{ Set-CimInstance -InputObject $accSetting -Property @{{ Value = '{wekfAccessOrig}' }} -ErrorAction SilentlyContinue }}");
        sb.AppendLine($"    $cadKeys = Get-CimInstance -Namespace $wekfNs -ClassName 'WEKF_PredefinedKey' -Filter \"Id='Ctrl+Alt+Del' OR Id='Ctrl+Alt+Delete'\" -ErrorAction SilentlyContinue");
        sb.AppendLine($"    foreach ($k in $cadKeys) {{ Set-CimInstance -InputObject $k -Property @{{ Enabled = {wekfCadOrig} }} -ErrorAction SilentlyContinue }}");
        sb.AppendLine("    Invoke-CimMethod -Namespace $wekfNs -ClassName 'WEKF_PredefinedKey' -MethodName 'Disable' -Arguments @{ Id = 'Ctrl+Alt+Del' } -ErrorAction SilentlyContinue | Out-Null");
        sb.AppendLine("} catch { }");
        sb.AppendLine();
        sb.AppendLine("# 8. Delete recovery state files and self-delete Scheduled Task once user hive is also clean");
        sb.AppendLine("if ($userHiveCleaned -or [string]::IsNullOrWhiteSpace($TargetUserSid)) {");
        sb.AppendLine("    if (Test-Path $SysRecoveryJson) { Remove-Item -Path $SysRecoveryJson -Force -ErrorAction SilentlyContinue }");
        sb.AppendLine("    if (Test-Path $UsrRecoveryJson) { Remove-Item -Path $UsrRecoveryJson -Force -ErrorAction SilentlyContinue }");
        sb.AppendLine("    & schtasks.exe /Delete /TN $TaskName /F 2>&1 | Out-Null");
        sb.AppendLine("    if (-not [string]::IsNullOrWhiteSpace($PSCommandPath) -and (Test-Path $PSCommandPath)) {");
        sb.AppendLine("        Remove-Item -Path $PSCommandPath -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>
    /// Generates a Windows Task Scheduler 1.2 XML definition that runs the self-contained
    /// recovery PowerShell script both at system boot (<c>BootTrigger</c>) and at user logon
    /// (<c>LogonTrigger</c>) under the SYSTEM account (<c>S-1-5-18</c>) with highest privileges.
    /// </summary>
    public static string BuildScheduledTaskXml(string scriptPath)
    {
        string escapedScriptPath = SecurityElement.Escape(scriptPath) ?? scriptPath;
        string arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File &quot;{escapedScriptPath}&quot;";

        return $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>LouisVox ExamBro Emergency Kiosk Lockdown Recovery Task (restores shutdown, sign-out, taskbar, and Ctrl+Alt+Del after hard power-off)</Description>
  </RegistrationInfo>
  <Triggers>
    <BootTrigger>
      <Enabled>true</Enabled>
    </BootTrigger>
    <LogonTrigger>
      <Enabled>true</Enabled>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>S-1-5-18</UserId>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT5M</ExecutionTimeLimit>
    <Priority>4</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>powershell.exe</Command>
      <Arguments>{arguments}</Arguments>
    </Exec>
  </Actions>
</Task>";
    }

    private static string EscapePsSingleQuoted(string input)
    {
        return input.Replace("'", "''");
    }
}
