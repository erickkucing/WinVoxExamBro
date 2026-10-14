using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Serilog;

namespace ExamBro.Exam.Services;

/// <summary>
/// Result of the system environment checks including VM, RDP, and debugger detection.
/// </summary>
public record EnvironmentCheckResult
{
    public bool IsVirtualMachine { get; init; }
    public string? VmReason { get; init; }

    public bool IsRemoteSession { get; init; }
    public string? RemoteSessionReason { get; init; }

    public bool IsDebuggerPresent { get; init; }
    public string? DebuggerReason { get; init; }

    public bool HasViolation => IsVirtualMachine || IsRemoteSession || IsDebuggerPresent;
}

/// <summary>
/// Service responsible for detecting whether the application is running inside a virtual machine,
/// an active Remote Desktop Protocol (RDP) session, or under an attached debugger.
/// </summary>
public static class EnvironmentDetector
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(EnvironmentDetector));

    // Win32 P/Invoke Declarations

    /// <summary>
    /// SM_REMOTESESSION (0x1000): Nonzero if the current session is remotely controlled.
    /// This metric is used in Terminal Services and Remote Desktop environments.
    /// </summary>
    private const int SM_REMOTESESSION = 0x1000;

    /// <summary>
    /// SM_REMOTECONTROL (0x2001): Nonzero if the current session is remotely controlled.
    /// Used by Remote Assistance and Terminal Services shadow sessions.
    /// </summary>
    private const int SM_REMOTECONTROL = 0x2001;

    /// <summary>
    /// Retrieves the specified system metric or system configuration setting.
    /// Used here to query SM_REMOTESESSION and SM_REMOTECONTROL.
    /// </summary>
    /// <param name="nIndex">The system metric or configuration setting to be retrieved.</param>
    /// <returns>If the function succeeds, the return value is the requested system metric or configuration setting.</returns>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetSystemMetrics(int nIndex);

    /// <summary>
    /// Determines whether the calling process is being debugged by a user-mode debugger.
    /// </summary>
    /// <returns>If the current process is running in the context of a debugger, the return value is nonzero.</returns>
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsDebuggerPresent();

    /// <summary>
    /// Determines whether the specified process is being debugged by another process.
    /// </summary>
    /// <param name="hProcess">A handle to the process to check.</param>
    /// <param name="isDebuggerPresent">A pointer to a variable that receives true if the process is being debugged.</param>
    /// <returns>If the function succeeds, the return value is nonzero.</returns>
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CheckRemoteDebuggerPresent(
        IntPtr hProcess,
        [MarshalAs(UnmanagedType.Bool)] ref bool isDebuggerPresent);

    /// <summary>
    /// Retrieves a pseudo handle for the current process.
    /// </summary>
    /// <returns>The return value is a pseudo handle to the current process.</returns>
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCurrentProcess();

    // Known VM Signature Tables

    private static readonly string[] KnownVmKeywords =
    {
        "vmware",
        "virtualbox",
        "vbox",
        "qemu",
        "kvm",
        "xen",
        "hyper-v",
        "parallels",
        "bochs",
        "red hat",
        "innotek",
        "bhyve"
    };

    private static readonly string[] KnownVmBiosKeywords =
    {
        "vmware",
        "virtualbox",
        "vbox",
        "qemu",
        "xen",
        "hyper-v",
        "parallels",
        "seabios",
        "0000000000"
    };

    private static readonly string[] KnownVmMacPrefixes =
    {
        "000569", // VMware
        "000C29", // VMware
        "005056", // VMware
        "001C14", // VMware
        "080027", // VirtualBox
        "00155D", // Microsoft Hyper-V
        "00163E", // Xen
        "001C42", // Parallels
        "525400"  // QEMU / KVM
    };

    // Public API

    /// <summary>
    /// Runs all environment checks and aggregates the findings.
    /// </summary>
    public static EnvironmentCheckResult CheckEnvironment(bool checkVm = true, bool checkRdp = true, bool checkVirtualNetwork = false)
    {
        bool isVm = false;
        string? vmReason = null;
        if (checkVm)
        {
            (isVm, vmReason) = DetectVirtualMachine(checkVirtualNetwork);
        }

        bool isRdp = false;
        string? rdpReason = null;
        if (checkRdp)
        {
            (isRdp, rdpReason) = DetectRemoteSession();
        }

        var (isDebugger, debuggerReason) = DetectDebugger();

        return new EnvironmentCheckResult
        {
            IsVirtualMachine = isVm,
            VmReason = vmReason,
            IsRemoteSession = isRdp,
            RemoteSessionReason = rdpReason,
            IsDebuggerPresent = isDebugger,
            DebuggerReason = debuggerReason
        };
    }

    /// <summary>
    /// Checks whether the current process is running with elevated Administrator privileges.
    /// Full administrator rights are required to lock system policies and terminate illicit processes.
    /// </summary>
    public static bool IsElevatedAdministrator()
    {
        if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            return true;
        }

        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to inspect process administrator elevation token");
            return false;
        }
    }

    /// <summary>
    /// Detects if the current system appears to be running inside a virtual machine.
    /// When checkVirtualNetwork is false, virtual ethernet adapter checks are skipped.
    /// </summary>
    public static (bool IsVm, string? Reason) DetectVirtualMachine(bool checkVirtualNetwork = false)
    {
        try
        {
            // 1. WMI Win32_ComputerSystem: Manufacturer and Model check
            var (wmiCsDetected, wmiCsReason) = CheckWmiComputerSystem();
            if (wmiCsDetected)
            {
                Log.Warning("VM detected via Win32_ComputerSystem: {Reason}", wmiCsReason);
                return (true, wmiCsReason);
            }

            // 2. WMI Win32_BIOS: Serial Number and BIOS Version check
            var (wmiBiosDetected, wmiBiosReason) = CheckWmiBios();
            if (wmiBiosDetected)
            {
                Log.Warning("VM detected via Win32_BIOS: {Reason}", wmiBiosReason);
                return (true, wmiBiosReason);
            }

            // 3. Registry signatures check
            var (regDetected, regReason) = CheckRegistryArtifacts();
            if (regDetected)
            {
                Log.Warning("VM detected via Registry: {Reason}", regReason);
                return (true, regReason);
            }

            // 4. Network adapter MAC address OUI check (optional, configurable)
            if (checkVirtualNetwork)
            {
                var (macDetected, macReason) = CheckMacAddresses();
                if (macDetected)
                {
                    Log.Warning("VM detected via MAC OUI: {Reason}", macReason);
                    return (true, macReason);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unexpected error occurred during VM detection");
        }

        return (false, null);
    }

    /// <summary>
    /// Detects if the current process is running in a Remote Desktop / Terminal Services session.
    /// </summary>
    public static (bool IsRdp, string? Reason) DetectRemoteSession()
    {
        try
        {
            // 1. Check SM_REMOTESESSION via user32.dll GetSystemMetrics
            int remoteSession = GetSystemMetrics(SM_REMOTESESSION);
            if (remoteSession != 0)
            {
                string reason = "Sesi jarak jauh terdeteksi (SM_REMOTESESSION != 0).";
                Log.Warning("Remote session detected: {Reason}", reason);
                return (true, reason);
            }

            // 2. Check SM_REMOTECONTROL via user32.dll GetSystemMetrics
            int remoteControl = GetSystemMetrics(SM_REMOTECONTROL);
            if (remoteControl != 0)
            {
                string reason = "Remote control terdeteksi (SM_REMOTECONTROL != 0).";
                Log.Warning("Remote control detected: {Reason}", reason);
                return (true, reason);
            }

            // 3. Environment variables check (CLIENTNAME, SESSIONNAME)
            string? clientName = Environment.GetEnvironmentVariable("CLIENTNAME");
            if (!string.IsNullOrWhiteSpace(clientName))
            {
                string reason = $"Sesi RDP terdeteksi dengan CLIENTNAME: {clientName}";
                Log.Warning("Remote session detected: {Reason}", reason);
                return (true, reason);
            }

            string? sessionName = Environment.GetEnvironmentVariable("SESSIONNAME");
            if (!string.IsNullOrWhiteSpace(sessionName) &&
                sessionName.StartsWith("RDP-", StringComparison.OrdinalIgnoreCase))
            {
                string reason = $"Sesi RDP terdeteksi dengan SESSIONNAME: {sessionName}";
                Log.Warning("Remote session detected: {Reason}", reason);
                return (true, reason);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error checking remote session state");
        }

        return (false, null);
    }

    /// <summary>
    /// Detects if a debugger is attached to the current process.
    /// </summary>
    public static (bool IsDebugger, string? Reason) DetectDebugger()
    {
        try
        {
            // 1. Managed debugger check
            if (Debugger.IsAttached)
            {
                return (true, "Debugger terdeteksi (Managed Debugger.IsAttached).");
            }

            // 2. Win32 IsDebuggerPresent check
            if (IsDebuggerPresent())
            {
                return (true, "Debugger terdeteksi (Win32 IsDebuggerPresent).");
            }

            // 3. Win32 CheckRemoteDebuggerPresent check
            bool remoteDebugger = false;
            if (CheckRemoteDebuggerPresent(GetCurrentProcess(), ref remoteDebugger) && remoteDebugger)
            {
                return (true, "Debugger terdeteksi (Win32 CheckRemoteDebuggerPresent).");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error checking debugger presence");
        }

        return (false, null);
    }

    // Helper Detection Methods

    private static (bool Detected, string? Reason) CheckWmiComputerSystem()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem");
            using var collection = searcher.Get();

            foreach (var item in collection)
            {
                string manufacturer = (item["Manufacturer"]?.ToString() ?? "").Trim();
                string model = (item["Model"]?.ToString() ?? "").Trim();

                string combined = $"{manufacturer} {model}".ToLowerInvariant();

                foreach (string keyword in KnownVmKeywords)
                {
                    if (combined.Contains(keyword))
                    {
                        return (true, $"Terdeteksi mesin virtual ({manufacturer} - {model}).");
                    }
                }

                if (manufacturer.Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase) &&
                    (model.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                     model.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)))
                {
                    return (true, $"Terdeteksi Microsoft Hyper-V ({model}).");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "WMI Win32_ComputerSystem query failed or not available");
        }

        return (false, null);
    }

    private static (bool Detected, string? Reason) CheckWmiBios()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT SerialNumber, Version, SMBIOSBIOSVersion, Manufacturer FROM Win32_BIOS");
            using var collection = searcher.Get();

            foreach (var item in collection)
            {
                string serial = (item["SerialNumber"]?.ToString() ?? "").Trim();
                string version = (item["Version"]?.ToString() ?? "").Trim();
                string smbios = (item["SMBIOSBIOSVersion"]?.ToString() ?? "").Trim();
                string manufacturer = (item["Manufacturer"]?.ToString() ?? "").Trim();

                string combined = $"{serial} {version} {smbios} {manufacturer}".ToLowerInvariant();

                foreach (string keyword in KnownVmBiosKeywords)
                {
                    if (combined.Contains(keyword))
                    {
                        return (true, $"Terdeteksi BIOS Mesin Virtual ({keyword}).");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "WMI Win32_BIOS query failed or not available");
        }

        return (false, null);
    }

    private static (bool Detected, string? Reason) CheckRegistryArtifacts()
    {
        try
        {
            // System Bios Version
            using var biosKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\Description\System");
            if (biosKey != null)
            {
                object? biosVersion = biosKey.GetValue("SystemBiosVersion");
                if (biosVersion is string[] stringArr)
                {
                    string joined = string.Join(" ", stringArr).ToLowerInvariant();
                    foreach (string keyword in KnownVmKeywords)
                    {
                        if (joined.Contains(keyword))
                            return (true, $"Registry BIOS mencurigakan: {keyword}");
                    }
                }
                else if (biosVersion is string singleStr)
                {
                    string val = singleStr.ToLowerInvariant();
                    foreach (string keyword in KnownVmKeywords)
                    {
                        if (val.Contains(keyword))
                            return (true, $"Registry BIOS mencurigakan: {keyword}");
                    }
                }
            }

            // Known VM Guest additions and drivers (exclusive to guest environments)
            string[] vmServicePaths =
            {
                @"SYSTEM\CurrentControlSet\Services\VBoxGuest",
                @"SYSTEM\CurrentControlSet\Services\VBoxMouse",
                @"SYSTEM\CurrentControlSet\Services\vmguestlibrary",
                @"SOFTWARE\VMware, Inc.\VMware Tools",
                @"SOFTWARE\Oracle\VirtualBox Guest Additions"
            };

            foreach (string path in vmServicePaths)
            {
                using var key = Registry.LocalMachine.OpenSubKey(path);
                if (key != null)
                {
                    return (true, $"Layanan/Driver VM terinstal ({path}).");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Registry inspection failed");
        }

        return (false, null);
    }

    private static (bool Detected, string? Reason) CheckMacAddresses()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var nic in interfaces)
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;

                string mac = nic.GetPhysicalAddress().ToString().ToUpperInvariant();
                if (mac.Length >= 6)
                {
                    string prefix = mac[..6];
                    foreach (string vmPrefix in KnownVmMacPrefixes)
                    {
                        if (prefix.Equals(vmPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            return (true, $"Alamat MAC adapter jaringan ({nic.Name}: {mac}) cocok dengan prefix VM ({vmPrefix}).");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Network interface inspection failed");
        }

        return (false, null);
    }
}
