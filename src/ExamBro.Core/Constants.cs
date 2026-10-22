namespace ExamBro.Core;

/// <summary>
/// Application-wide constants for ExamBro.
/// </summary>
public static class Constants
{
    public const string AppVersion = "1.0.0";
    public const string AppName = "LouisVox ExamBro";

    /// <summary>Named mutex to enforce single-instance execution.</summary>
    public const string MutexName = @"Global\LouisVoxExamBro_SingleInstance_Mutex";

    /// <summary>AES-256-GCM nonce length in bytes.</summary>
    public const int NonceLength = 12;

    /// <summary>AES-256-GCM tag length in bytes.</summary>
    public const int TagLength = 16;

    /// <summary>AES-256 key length in bytes.</summary>
    public const int KeyLength = 32;

    /// <summary>PBKDF2 iteration count (meets OWASP 2024 recommendation).</summary>
    public const int PasswordIterations = 310_000;

    /// <summary>Salt length for password hashing.</summary>
    public const int SaltLength = 16;

    /// <summary>Binary version stamp written at the head of every encrypted config blob.</summary>
    public const int ConfigVersion = 1;

    /// <summary>Suffix appended to the WebView2 User-Agent string.</summary>
    public const string UserAgentSuffix = " ExamBro/1.0.0 ExamBrowser/1.0.0 LouisVox/1.0.0";

    /// <summary>Default keyboard shortcut to request exam exit.</summary>
    public const string DefaultExitShortcut = "Ctrl+C+B";

    /// <summary>Environment variable that activates dev-safe mode.</summary>
    public const string DevSafeEnvVar = "EXAMBRO_DEV";

    /// <summary>Application-specific entropy fed into DPAPI to isolate the master key.</summary>
    public static readonly byte[] AppEntropy =
    {
        0x45, 0x78, 0x61, 0x6D, 0x42, 0x72, 0x6F, 0x5F,
        0x53, 0x65, 0x63, 0x75, 0x72, 0x65, 0x5F, 0x4B,
        0x65, 0x79, 0x5F, 0x45, 0x6E, 0x74, 0x72, 0x6F,
        0x70, 0x79, 0x5F, 0x56, 0x31, 0x2E, 0x30, 0x2E
    };

    /// <summary>Directory of the running executable.</summary>
    public static string AppDirectory
    {
        get
        {
            try
            {
                if (!string.IsNullOrEmpty(Environment.ProcessPath))
                {
                    string? dir = Path.GetDirectoryName(Environment.ProcessPath);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        return dir;
                    }
                }
            }
            catch { }

            return AppDomain.CurrentDomain.BaseDirectory;
        }
    }

    /// <summary>Portable config file path in the application directory.</summary>
    public static string PortableConfigPath =>
        Path.Combine(AppDirectory, "exambro.cfg");

    /// <summary>Installed system-wide config file path under ProgramData.</summary>
    public static string SystemConfigFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            AppName, "exambro.cfg");

    /// <summary>
    /// Config file path: checks application directory first (portable mode),
    /// then falls back to ProgramData (installed mode).
    /// </summary>
    public static string ConfigFilePath
    {
        get
        {
            // 1. Primary: exambro.cfg located in the application directory
            string localPath = PortableConfigPath;
            if (File.Exists(localPath))
            {
                return localPath;
            }

            // 2. Current working directory
            try
            {
                string cwdPath = Path.Combine(Directory.GetCurrentDirectory(), "exambro.cfg");
                if (File.Exists(cwdPath))
                {
                    return cwdPath;
                }
            }
            catch { }

            // 3. Parent / sibling directories if running in a modular build folder
            try
            {
                string? parentDir = Directory.GetParent(AppDirectory)?.FullName;
                if (!string.IsNullOrEmpty(parentDir))
                {
                    string parentPath = Path.Combine(parentDir, "exambro.cfg");
                    if (File.Exists(parentPath))
                    {
                        return parentPath;
                    }

                    string siblingConfig = Path.Combine(parentDir, "ExamBro.Config", "exambro.cfg");
                    if (File.Exists(siblingConfig))
                    {
                        return siblingConfig;
                    }

                    string siblingExam = Path.Combine(parentDir, "ExamBro.Exam", "exambro.cfg");
                    if (File.Exists(siblingExam))
                    {
                        return siblingExam;
                    }

                    string siblingPortable = Path.Combine(parentDir, "portable", "WinVoxExamBro", "exambro.cfg");
                    if (File.Exists(siblingPortable))
                    {
                        return siblingPortable;
                    }
                }
            }
            catch { }

            // 4. System-wide ProgramData path (installed mode)
            if (File.Exists(SystemConfigFilePath))
            {
                return SystemConfigFilePath;
            }

            // 5. Default fallback if not found anywhere: default to application directory (portable)
            return PortableConfigPath;
        }
    }

    /// <summary>Indicates whether the application is running in portable mode with local config.</summary>
    public static bool IsPortableMode => !ConfigFilePath.Equals(SystemConfigFilePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Crash-recovery state file under LocalAppData.</summary>
    public static string RecoveryFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppName, "recovery.json");

    /// <summary>WebView2 user data folder (InPrivate sessions).</summary>
    public static string WebView2DataFolder =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppName, "WebView2Profile");

    /// <summary>Rolling log folder under LocalAppData (user-writable without elevation).</summary>
    public static string LogFolder
    {
        get
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                return Path.Combine(localAppData, AppName, "logs");
            }

            return Path.Combine(AppDirectory, "logs");
        }
    }

    /// <summary>
    /// DPAPI-protected master key store path. Checks local application folder first,
    /// then falls back to ProgramData.
    /// </summary>
    public static string KeystorePath
    {
        get
        {
            string localKeystore = Path.Combine(AppDirectory, "keystore");
            if (File.Exists(localKeystore))
            {
                return localKeystore;
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                AppName, "keystore");
        }
    }

    /// <summary>Registry path for user-level policies set during kiosk lock.</summary>
    public const string PolicyRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Policies";
    public const string SystemPolicySubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    public const string ExplorerPolicySubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
    public const string GroupPolicyExplorerSubKey = @"Software\Policies\Microsoft\Windows\Explorer";
}
