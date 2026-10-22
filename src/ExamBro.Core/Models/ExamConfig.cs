using System.Text.Json.Serialization;

namespace ExamBro.Core.Models;

// Enums

/// <summary>Policy for handling secondary monitors.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MultiMonitorPolicy { Block, CoverAll, Allow }

/// <summary>Action taken when a blacklisted process is detected.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProcessActionType { Kill, LogOnly }

/// <summary>Severity action for a violation event.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ViolationAction { Ignore, Log, Warn, Lock, Terminate }

/// <summary>Types of violation events the system can raise.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ViolationEventType
{
    FocusLost,
    BlockedKey,
    BlockedProcess,
    BlockedUrl,
    MultiMonitor,
    VirtualMachine,
    RemoteDesktop,
    ClipboardAttempt
}

// Nested config sections

public class GeneralConfig
{
    public string AppTitle { get; set; } = "LouisVox ExamBro";
    public string Language { get; set; } = "id";
    public string StartUrl { get; set; } = "http://192.168.70.9";
    public bool AllowCommandLineOverride { get; set; } = false;
    public bool ShowToolbar { get; set; } = true;
    public string LogoPath { get; set; } = "";
    public string? LogoBase64 { get; set; }
    public bool PlaceholderUsername { get; set; } = true;
    public bool PlaceholderMachine { get; set; } = true;
}

public class NavigationConfig
{
    public bool AutoAllowStartUrlHost { get; set; } = true;
    public List<string> AllowedUrlPatterns { get; set; } = new()
    {
        "*192.168.*",
        "http://192.168.*/*",
        "https://192.168.*/*",
        "192.168.0.0/16",
        "192.168.0.0",
        "*localhost*",
        "http://localhost/*",
        "https://localhost/*",
        "*127.0.0.1*",
        "http://127.0.0.1/*",
        "https://127.0.0.1/*"
    };
    public List<string> BlockedUrlPatterns { get; set; } = new();
    public string ExitUrlPattern { get; set; } = "*/exam/finished/*";
    public int ExitUrlAutoCloseSeconds { get; set; } = 5;
    public bool AllowBackForward { get; set; } = false;
    public bool AllowReload { get; set; } = true;
    public bool AllowDownloads { get; set; } = false;
    public bool AllowZoom { get; set; } = false;
}

public class SecurityConfig
{
    public bool KioskMode { get; set; } = true;

    public MultiMonitorPolicy MultiMonitor { get; set; } = MultiMonitorPolicy.CoverAll;

    public bool BlockVirtualMachine { get; set; } = true;
    public bool CheckVirtualNetworkAdapters { get; set; } = false;
    public bool BlockRemoteSession { get; set; } = true;
    public bool AllowClipboard { get; set; } = false;
    public bool ScreenCaptureProtection { get; set; } = true;

    public List<string> ProcessBlacklist { get; set; } = new()
    {
        "taskmgr", "cmd", "powershell", "pwsh", "wt",
        "regedit", "mmc", "procexp", "procmon",
        "snippingtool", "ScreenClippingHost",
        "obs64", "obs32",
        "TeamViewer", "AnyDesk", "rustdesk",
        "Discord", "Zoom", "Teams", "Skype",
        "AutoHotkey", "CheatEngine", "wireshark", "Sandboxie",
        "chrome", "firefox", "msedge",
        "DisplaySwitch"
    };

    public ProcessActionType ProcessAction { get; set; } = ProcessActionType.Kill;

    public bool KillExplorerShell { get; set; } = false;
    public bool DisableRegistryPolicies { get; set; } = true;
    public bool AllowCopyPaste { get; set; } = false;
    public bool AllowRefresh { get; set; } = true;
    public bool AllowAltNavigation { get; set; } = false;

    /// <summary>When true, --dev-safe is accepted even in Release builds.</summary>
    public bool DevSafeAllowedInRelease { get; set; } = false;
}

public class ViolationRule
{
    public ViolationAction Action { get; set; } = ViolationAction.Log;
    public int Threshold { get; set; } = 0;
    public ViolationAction Escalation { get; set; } = ViolationAction.Log;
}

public class AdminConfig
{
    public string AdminPasswordHash { get; set; } = "";
    public string AdminPasswordSalt { get; set; } = "";
    public string ProctorPasswordHash { get; set; } = "";
    public string ProctorPasswordSalt { get; set; } = "";
    public string ExitShortcut { get; set; } = Constants.DefaultExitShortcut;
    public int MaxPasswordAttempts { get; set; } = 5;
    public int PasswordLockoutSeconds { get; set; } = 30;
}

public class LoggingConfig
{
    public string LogLevel { get; set; } = "Information";
    public string LogPath { get; set; } = "";
    public string WebhookUrl { get; set; } = "";
    public string WebhookHmacKey { get; set; } = "";
}

public class PermissionsConfig
{
    public bool AllowCamera { get; set; } = false;
    public bool AllowMicrophone { get; set; } = false;
    public bool AllowGeolocation { get; set; } = false;
    public bool AllowNotifications { get; set; } = false;
}

public class AudioConfig
{
    public bool MuteAudio { get; set; } = false;
    public int VolumePercent { get; set; } = 100;
}

// Root config

/// <summary>
/// Root configuration object serialized as encrypted JSON.
/// </summary>
public class ExamConfig
{
    public GeneralConfig General { get; set; } = new();
    public NavigationConfig Navigation { get; set; } = new();
    public SecurityConfig Security { get; set; } = new();
    public Dictionary<string, ViolationRule> Violations { get; set; } = new();
    public AdminConfig Admin { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();
    public PermissionsConfig Permissions { get; set; } = new();
    public AudioConfig Audio { get; set; } = new();

    /// <summary>
    /// Creates a default configuration with the full process blacklist
    /// and standard violation rules.
    /// </summary>
    public static ExamConfig CreateDefault()
    {
        var config = new ExamConfig();

        config.General.StartUrl = "http://192.168.70.9";
        config.Navigation.AllowedUrlPatterns = new()
        {
            "*192.168.*",
            "http://192.168.*/*",
            "https://192.168.*/*",
            "192.168.0.0/16",
            "192.168.0.0",
            "*localhost*",
            "http://localhost/*",
            "https://localhost/*",
            "*127.0.0.1*",
            "http://127.0.0.1/*",
            "https://127.0.0.1/*"
        };

        config.Logging.LogPath = Constants.LogFolder;

        config.Violations = new Dictionary<string, ViolationRule>
        {
            [nameof(ViolationEventType.FocusLost)] = new()
                { Action = ViolationAction.Warn, Threshold = 3, Escalation = ViolationAction.Lock },
            [nameof(ViolationEventType.BlockedKey)] = new()
                { Action = ViolationAction.Log, Threshold = 0, Escalation = ViolationAction.Log },
            [nameof(ViolationEventType.BlockedProcess)] = new()
                { Action = ViolationAction.Warn, Threshold = 1, Escalation = ViolationAction.Lock },
            [nameof(ViolationEventType.BlockedUrl)] = new()
                { Action = ViolationAction.Log, Threshold = 0, Escalation = ViolationAction.Log },
            [nameof(ViolationEventType.MultiMonitor)] = new()
                { Action = ViolationAction.Warn, Threshold = 1, Escalation = ViolationAction.Lock },
            [nameof(ViolationEventType.VirtualMachine)] = new()
                { Action = ViolationAction.Warn, Threshold = 0, Escalation = ViolationAction.Terminate },
            [nameof(ViolationEventType.RemoteDesktop)] = new()
                { Action = ViolationAction.Warn, Threshold = 0, Escalation = ViolationAction.Terminate },
            [nameof(ViolationEventType.ClipboardAttempt)] = new()
                { Action = ViolationAction.Log, Threshold = 0, Escalation = ViolationAction.Log },
        };

        return config;
    }
}
