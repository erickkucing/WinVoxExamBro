using System.Diagnostics;
using System.Runtime.Versioning;
using ExamBro.Core;
using ExamBro.Exam.Services;
using Microsoft.Win32;

namespace ExamBro.Tests.Windows.Fixtures;

/// <summary>
/// Guards Windows E2E tests that mutate HKCU/HKLM registry keys and Scheduled Tasks
/// so developer workstations are never modified accidentally outside CI or explicit opt-in.
/// </summary>
[SupportedOSPlatform("windows")]
public static class RegistryGuard
{
    public static bool AllowRegistryMutation =>
        string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Environment.GetEnvironmentVariable("EXAMBRO_E2E_ALLOW_REGISTRY"), "1", StringComparison.Ordinal);

    public static int? ReadDword(bool useHklm, string subKey, string valueName)
    {
        try
        {
            using var root = useHklm ? Registry.LocalMachine : Registry.CurrentUser;
            using var key = root.OpenSubKey(subKey);
            object? val = key?.GetValue(valueName);
            if (val is int intVal) return intVal;
            if (val != null && int.TryParse(val.ToString(), out int parsed)) return parsed;
        }
        catch { }

        return null;
    }

    public static bool ScheduledTaskExists(string taskName = Constants.RecoveryScheduledTaskName)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Query /TN \"{taskName}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (proc == null) return false;
            proc.WaitForExit(3000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static void RestoreCleanState()
    {
        if (!AllowRegistryMutation) return;
        KioskLockService.RestoreFromRecovery();
    }
}
