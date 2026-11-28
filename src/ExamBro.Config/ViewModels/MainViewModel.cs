using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExamBro.Config.Helpers;
using ExamBro.Config.Views;
using ExamBro.Core;
using ExamBro.Core.Models;
using ExamBro.Core.Security;
using ExamBro.Core.Validation;
using Microsoft.Win32;
using Serilog;

namespace ExamBro.Config.ViewModels;

/// <summary>
/// Main ViewModel for ExamBro configuration management.
/// Flattens <see cref="ExamConfig"/> into observable properties with validation and command support.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    // General Config
    [ObservableProperty]
    private string _appTitle = BrandingHelper.DefaultAppTitle;

    [ObservableProperty]
    private string _language = "id";

    [ObservableProperty]
    private string _startUrl = "https://ujian.contoh.sch.id/login";

    [ObservableProperty]
    private bool _allowCommandLineOverride;

    [ObservableProperty]
    private bool _showToolbar = true;

    [ObservableProperty]
    private string _logoPath = string.Empty;

    [ObservableProperty]
    private string? _logoBase64;

    [ObservableProperty]
    private ImageSource? _logoPreview;

    [ObservableProperty]
    private bool _placeholderUsername = true;

    [ObservableProperty]
    private bool _placeholderMachine = true;

    // Navigation Config
    [ObservableProperty]
    private bool _autoAllowStartUrlHost = true;

    public ObservableCollection<string> AllowedUrlPatterns { get; } = new();

    public ObservableCollection<string> BlockedUrlPatterns { get; } = new();

    [ObservableProperty]
    private string _exitUrlPattern = "https://ujian.contoh.sch.id/selesai*";

    [ObservableProperty]
    private int _exitUrlAutoCloseSeconds = 5;

    [ObservableProperty]
    private bool _allowBackForward;

    [ObservableProperty]
    private bool _allowReload = true;

    [ObservableProperty]
    private bool _allowDownloads;

    [ObservableProperty]
    private bool _allowZoom;

    // Temporary Navigation Inputs
    [ObservableProperty]
    private string _newAllowedPattern = string.Empty;

    [ObservableProperty]
    private string? _selectedAllowedPattern;

    [ObservableProperty]
    private string _newBlockedPattern = string.Empty;

    [ObservableProperty]
    private string? _selectedBlockedPattern;

    // Security Config
    [ObservableProperty]
    private bool _kioskMode = true;

    [ObservableProperty]
    private MultiMonitorPolicy _multiMonitor = MultiMonitorPolicy.CoverAll;

    [ObservableProperty]
    private bool _blockVirtualMachine = true;

    [ObservableProperty]
    private bool _checkVirtualNetworkAdapters;

    [ObservableProperty]
    private bool _blockRemoteSession = true;

    [ObservableProperty]
    private bool _allowClipboard;

    [ObservableProperty]
    private bool _screenCaptureProtection = true;

    public ObservableCollection<string> ProcessBlacklist { get; } = new();

    [ObservableProperty]
    private ProcessActionType _processAction = ProcessActionType.Kill;

    [ObservableProperty]
    private bool _killExplorerShell;

    [ObservableProperty]
    private bool _disableRegistryPolicies = true;

    [ObservableProperty]
    private bool _allowCopyPaste;

    [ObservableProperty]
    private bool _allowRefresh = true;

    [ObservableProperty]
    private bool _allowAltNavigation;

    [ObservableProperty]
    private bool _devSafeAllowedInRelease;

    // Temporary Process Inputs
    [ObservableProperty]
    private string _newProcess = string.Empty;

    [ObservableProperty]
    private string? _selectedProcess;

    // Permissions Config
    [ObservableProperty]
    private bool _allowCamera;

    [ObservableProperty]
    private bool _allowMicrophone;

    [ObservableProperty]
    private bool _allowGeolocation;

    [ObservableProperty]
    private bool _allowNotifications;

    // Audio Config
    [ObservableProperty]
    private bool _muteAudio;

    [ObservableProperty]
    private int _volumePercent = 100;

    // Admin Config
    [ObservableProperty]
    private string _adminPasswordHash = string.Empty;

    [ObservableProperty]
    private string _adminPasswordSalt = string.Empty;

    [ObservableProperty]
    private string _proctorPasswordHash = string.Empty;

    [ObservableProperty]
    private string _proctorPasswordSalt = string.Empty;

    [ObservableProperty]
    private string _exitShortcut = Constants.DefaultExitShortcut;

    [ObservableProperty]
    private int _maxPasswordAttempts = 5;

    [ObservableProperty]
    private int _passwordLockoutSeconds = 30;

    // Logging Config
    [ObservableProperty]
    private string _logLevel = "Information";

    [ObservableProperty]
    private string _logPath = string.Empty;

    [ObservableProperty]
    private string _webhookUrl = string.Empty;

    [ObservableProperty]
    private string _webhookHmacKey = string.Empty;

    // Violations
    public ObservableCollection<ViolationRuleItemViewModel> ViolationRules { get; } = new();

    // Meta, Validation & Status
    [ObservableProperty]
    private string _configHash = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "Siap.";

    [ObservableProperty]
    private bool _hasErrors;

    public ObservableCollection<ValidationError> ValidationErrors { get; } = new();

    // Option Lists for UI Binding
    public IReadOnlyList<MultiMonitorPolicy> MultiMonitorOptions => Enum.GetValues<MultiMonitorPolicy>();
    public IReadOnlyList<ProcessActionType> ProcessActionOptions => Enum.GetValues<ProcessActionType>();
    public IReadOnlyList<ViolationAction> ViolationActionOptions => Enum.GetValues<ViolationAction>();
    public IReadOnlyList<string> LogLevelOptions => new[] { "Verbose", "Debug", "Information", "Warning", "Error", "Fatal" };
    public IReadOnlyList<string> LanguageOptions => new[] { "id", "en" };

    public MainViewModel()
    {
    }

    public MainViewModel(ExamConfig config)
    {
        LoadFromConfig(config);
    }

    /// <summary>
    /// Loads properties from an <see cref="ExamConfig"/> domain model.
    /// </summary>
    public void LoadFromConfig(ExamConfig config)
    {
        // General
        AppTitle = string.IsNullOrWhiteSpace(config.General.AppTitle) ? BrandingHelper.DefaultAppTitle : config.General.AppTitle;
        Language = config.General.Language;
        StartUrl = config.General.StartUrl;
        AllowCommandLineOverride = config.General.AllowCommandLineOverride;
        ShowToolbar = config.General.ShowToolbar;
        LogoPath = config.General.LogoPath ?? string.Empty;
        LogoBase64 = config.General.LogoBase64;
        PlaceholderUsername = config.General.PlaceholderUsername;
        PlaceholderMachine = config.General.PlaceholderMachine;

        byte[] logoBytes = BrandingHelper.ResolveLogoBytes(LogoBase64, LogoPath);
        LogoPreview = ImageHelper.CreateImageSource(logoBytes);
        if (Application.Current?.MainWindow != null && LogoPreview != null)
        {
            Application.Current.MainWindow.Icon = LogoPreview;
        }

        // Navigation
        AutoAllowStartUrlHost = config.Navigation.AutoAllowStartUrlHost;
        AllowedUrlPatterns.Clear();
        foreach (var pattern in config.Navigation.AllowedUrlPatterns ?? new List<string>())
            AllowedUrlPatterns.Add(pattern);

        BlockedUrlPatterns.Clear();
        foreach (var pattern in config.Navigation.BlockedUrlPatterns ?? new List<string>())
            BlockedUrlPatterns.Add(pattern);

        ExitUrlPattern = config.Navigation.ExitUrlPattern;
        ExitUrlAutoCloseSeconds = config.Navigation.ExitUrlAutoCloseSeconds;
        AllowBackForward = config.Navigation.AllowBackForward;
        AllowReload = config.Navigation.AllowReload;
        AllowDownloads = config.Navigation.AllowDownloads;
        AllowZoom = config.Navigation.AllowZoom;

        // Security
        KioskMode = config.Security.KioskMode;
        MultiMonitor = config.Security.MultiMonitor;
        BlockVirtualMachine = config.Security.BlockVirtualMachine;
        CheckVirtualNetworkAdapters = config.Security.CheckVirtualNetworkAdapters;
        BlockRemoteSession = config.Security.BlockRemoteSession;
        AllowClipboard = config.Security.AllowClipboard;
        ScreenCaptureProtection = config.Security.ScreenCaptureProtection;

        ProcessBlacklist.Clear();
        foreach (var proc in config.Security.ProcessBlacklist ?? new List<string>())
            ProcessBlacklist.Add(proc);

        ProcessAction = config.Security.ProcessAction;
        KillExplorerShell = config.Security.KillExplorerShell;
        DisableRegistryPolicies = config.Security.DisableRegistryPolicies;
        AllowCopyPaste = config.Security.AllowCopyPaste;
        AllowRefresh = config.Security.AllowRefresh;
        AllowAltNavigation = config.Security.AllowAltNavigation;
        DevSafeAllowedInRelease = config.Security.DevSafeAllowedInRelease;

        // Permissions
        AllowCamera = config.Permissions.AllowCamera;
        AllowMicrophone = config.Permissions.AllowMicrophone;
        AllowGeolocation = config.Permissions.AllowGeolocation;
        AllowNotifications = config.Permissions.AllowNotifications;

        // Audio
        MuteAudio = config.Audio?.MuteAudio ?? false;
        VolumePercent = Math.Clamp(config.Audio?.VolumePercent ?? 100, 0, 100);

        // Admin
        AdminPasswordHash = config.Admin.AdminPasswordHash;
        AdminPasswordSalt = config.Admin.AdminPasswordSalt;
        ProctorPasswordHash = config.Admin.ProctorPasswordHash;
        ProctorPasswordSalt = config.Admin.ProctorPasswordSalt;
        ExitShortcut = config.Admin.ExitShortcut;
        MaxPasswordAttempts = config.Admin.MaxPasswordAttempts;
        PasswordLockoutSeconds = config.Admin.PasswordLockoutSeconds;

        // Logging
        LogLevel = config.Logging.LogLevel;
        LogPath = config.Logging.LogPath;
        WebhookUrl = config.Logging.WebhookUrl;
        WebhookHmacKey = config.Logging.WebhookHmacKey;

        // Violations
        ViolationRules.Clear();
        var defaultRules = ExamConfig.CreateDefault().Violations;
        var eventTypes = new (string Key, string Name, ViolationAction DefAction, int DefThreshold, ViolationAction DefEsc)[]
        {
            (nameof(ViolationEventType.FocusLost), "Kehilangan Fokus Jendela", ViolationAction.Warn, 3, ViolationAction.Lock),
            (nameof(ViolationEventType.BlockedKey), "Penekanan Tombol Terlarang", ViolationAction.Log, 0, ViolationAction.Log),
            (nameof(ViolationEventType.BlockedProcess), "Aplikasi Terlarang Terdeteksi", ViolationAction.Warn, 1, ViolationAction.Lock),
            (nameof(ViolationEventType.BlockedUrl), "Akses URL Terlarang", ViolationAction.Log, 0, ViolationAction.Log),
            (nameof(ViolationEventType.MultiMonitor), "Koneksi Multi Monitor", ViolationAction.Warn, 1, ViolationAction.Lock),
            (nameof(ViolationEventType.VirtualMachine), "Lingkungan Mesin Virtual (VM)", ViolationAction.Warn, 0, ViolationAction.Terminate),
            (nameof(ViolationEventType.RemoteDesktop), "Sesi Remote Desktop / RDP", ViolationAction.Warn, 0, ViolationAction.Terminate),
            (nameof(ViolationEventType.ClipboardAttempt), "Percobaan Akses Clipboard", ViolationAction.Log, 0, ViolationAction.Log)
        };

        foreach (var et in eventTypes)
        {
            ViolationRule rule;
            if (config.Violations != null && config.Violations.TryGetValue(et.Key, out var existing))
            {
                rule = existing;
            }
            else if (defaultRules.TryGetValue(et.Key, out var def))
            {
                rule = def;
            }
            else
            {
                rule = new ViolationRule { Action = et.DefAction, Threshold = et.DefThreshold, Escalation = et.DefEsc };
            }

            ViolationRules.Add(new ViolationRuleItemViewModel(et.Key, et.Name, rule.Action, rule.Threshold, rule.Escalation));
        }

        ConfigHash = ConfigEncryption.ComputeConfigHash(config);
        StatusMessage = "Konfigurasi berhasil dimuat.";
    }

    /// <summary>
    /// Converts observable ViewModel properties back to an <see cref="ExamConfig"/> domain model.
    /// </summary>
    public ExamConfig ToConfig()
    {
        var config = new ExamConfig
        {
            General = new GeneralConfig
            {
                AppTitle = AppTitle,
                Language = Language,
                StartUrl = StartUrl,
                AllowCommandLineOverride = AllowCommandLineOverride,
                ShowToolbar = ShowToolbar,
                LogoPath = LogoPath,
                LogoBase64 = LogoBase64,
                PlaceholderUsername = PlaceholderUsername,
                PlaceholderMachine = PlaceholderMachine
            },
            Navigation = new NavigationConfig
            {
                AutoAllowStartUrlHost = AutoAllowStartUrlHost,
                AllowedUrlPatterns = new List<string>(AllowedUrlPatterns),
                BlockedUrlPatterns = new List<string>(BlockedUrlPatterns),
                ExitUrlPattern = ExitUrlPattern,
                ExitUrlAutoCloseSeconds = ExitUrlAutoCloseSeconds,
                AllowBackForward = AllowBackForward,
                AllowReload = AllowReload,
                AllowDownloads = AllowDownloads,
                AllowZoom = AllowZoom
            },
            Security = new SecurityConfig
            {
                KioskMode = KioskMode,
                MultiMonitor = MultiMonitor,
                BlockVirtualMachine = BlockVirtualMachine,
                CheckVirtualNetworkAdapters = CheckVirtualNetworkAdapters,
                BlockRemoteSession = BlockRemoteSession,
                AllowClipboard = AllowClipboard,
                ScreenCaptureProtection = ScreenCaptureProtection,
                ProcessBlacklist = new List<string>(ProcessBlacklist),
                ProcessAction = ProcessAction,
                KillExplorerShell = KillExplorerShell,
                DisableRegistryPolicies = DisableRegistryPolicies,
                AllowCopyPaste = AllowCopyPaste,
                AllowRefresh = AllowRefresh,
                AllowAltNavigation = AllowAltNavigation,
                DevSafeAllowedInRelease = DevSafeAllowedInRelease
            },
            Permissions = new PermissionsConfig
            {
                AllowCamera = AllowCamera,
                AllowMicrophone = AllowMicrophone,
                AllowGeolocation = AllowGeolocation,
                AllowNotifications = AllowNotifications
            },
            Audio = new AudioConfig
            {
                MuteAudio = MuteAudio,
                VolumePercent = Math.Clamp(VolumePercent, 0, 100)
            },
            Admin = new AdminConfig
            {
                AdminPasswordHash = AdminPasswordHash,
                AdminPasswordSalt = AdminPasswordSalt,
                ProctorPasswordHash = ProctorPasswordHash,
                ProctorPasswordSalt = ProctorPasswordSalt,
                ExitShortcut = ExitShortcut,
                MaxPasswordAttempts = MaxPasswordAttempts,
                PasswordLockoutSeconds = PasswordLockoutSeconds
            },
            Logging = new LoggingConfig
            {
                LogLevel = LogLevel,
                LogPath = LogPath,
                WebhookUrl = WebhookUrl,
                WebhookHmacKey = WebhookHmacKey
            },
            Violations = new Dictionary<string, ViolationRule>()
        };

        foreach (var item in ViolationRules)
        {
            config.Violations[item.EventKey] = item.ToViolationRule();
        }

        return config;
    }

    // Command Handlers

    [RelayCommand]
    private void Save()
    {
        try
        {
            if (!TryValidateConfig(out var config, "Gagal menyimpan: terdapat kesalahan konfigurasi yang harus diperbaiki."))
            {
                return;
            }

            var (_, embeddedExes, lockedExes) = PersistConfigAndEmbed(config);

            StatusMessage = $"Konfigurasi berhasil disimpan! ({DateTime.Now:HH:mm:ss})";
            Log.Information("Konfigurasi ExamBro berhasil disimpan ke {Path}", Constants.PortableConfigPath);

            if (lockedExes.Count > 0)
            {
                MessageBox.Show(
                    $"File 'exambro.cfg' berhasil disimpan, namun aplikasi ujian ({string.Join(", ", lockedExes)}) sedang berjalan sehingga konfigurasi belum bisa disematkan ke dalam .exe.\n\n" +
                    "Tutup aplikasi ujian yang sedang berjalan lalu klik 'Simpan Konfigurasi' kembali.",
                    "Aplikasi Ujian Sedang Aktif",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            string embedNote = embeddedExes.Count > 0
                ? $"\n• Konfigurasi juga disematkan langsung ke dalam: {string.Join(", ", embeddedExes)}\n  (aman walau dijalankan langsung dari ZIP atau disalin tanpa file lain)."
                : string.Empty;

            MessageBox.Show(
                $"Konfigurasi ExamBro berhasil disimpan ke folder aplikasi:\n{Constants.PortableConfigPath}{embedNote}",
                "Simpan Berhasil",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Gagal menyimpan konfigurasi");
            StatusMessage = $"Kesalahan saat menyimpan: {ex.Message}";
            MessageBox.Show(
                $"Terjadi kesalahan saat menyimpan file konfigurasi: {ex.Message}",
                "Kesalahan",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void SavePortable()
    {
        try
        {
            if (!TryValidateConfig(out var config, "Gagal menyimpan: perbaiki kesalahan konfigurasi terlebih dahulu."))
            {
                return;
            }

            var (_, embeddedExes, lockedExes) = PersistConfigAndEmbed(config);

            if (lockedExes.Count > 0)
            {
                StatusMessage = "Gagal menyematkan ke .exe: aplikasi ujian sedang berjalan.";
                MessageBox.Show(
                    $"Tidak dapat menyematkan konfigurasi ke '{string.Join(", ", lockedExes)}' karena aplikasi ujian sedang aktif.\n\n" +
                    "Tutup aplikasi ujian terlebih dahulu lalu klik 'Simpan Portable' ulang.",
                    "Aplikasi Ujian Sedang Berjalan",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            StatusMessage = $"Konfigurasi portable berhasil disimpan di folder program! ({DateTime.Now:HH:mm:ss})";
            Log.Information("Konfigurasi ExamBro portable berhasil disimpan ke {Path}", Constants.PortableConfigPath);

            if (embeddedExes.Count > 0)
            {
                var zipChoice = MessageBox.Show(
                    $"Konfigurasi berhasil disimpan ke 'exambro.cfg' dan disematkan langsung ke dalam aplikasi ujian ({string.Join(", ", embeddedExes)}).\n\n" +
                    "Apakah Anda ingin langsung membuat file paket '.zip' ringan siap distribusi ke siswa (otomatis tanpa aplikasi Pengaturan)?",
                    "Simpan Portable Berhasil",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (zipChoice == MessageBoxResult.Yes)
                {
                    ExportLightweightPortableZip();
                }
            }
            else
            {
                MessageBox.Show(
                    $"Konfigurasi ExamBro versi Portable berhasil disimpan langsung ke folder aplikasi:\n{Constants.PortableConfigPath}\n\n" +
                    "Letakkan 'exambro.cfg' di folder yang sama dengan aplikasi ujian sebelum disalin ke komputer siswa.",
                    "Simpan Portable Berhasil",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Gagal menyimpan konfigurasi portable");
            StatusMessage = $"Kesalahan saat menyimpan portable: {ex.Message}";
            MessageBox.Show(
                $"Terjadi kesalahan saat menyimpan file konfigurasi portable: {ex.Message}",
                "Kesalahan",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private bool TryValidateConfig(out ExamConfig config, string failureStatus)
    {
        config = ToConfig();
        var errors = ConfigValidator.Validate(config);

        ValidationErrors.Clear();
        bool hasFatalError = false;
        foreach (var err in errors)
        {
            ValidationErrors.Add(err);
            if (err.Severity == ValidationSeverity.Error)
                hasFatalError = true;
        }

        HasErrors = errors.Count > 0;

        if (hasFatalError)
        {
            StatusMessage = failureStatus;
            string errorList = string.Join("\n• ", errors.Where(e => e.Severity == ValidationSeverity.Error).Select(e => e.Message));
            MessageBox.Show(
                $"Konfigurasi tidak dapat disimpan karena terdapat kesalahan validasi:\n\n• {errorList}",
                "Validasi Gagal",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }

        return true;
    }

    private (byte[] EncryptedBytes, List<string> EmbeddedExes, List<string> LockedExes) PersistConfigAndEmbed(ExamConfig config)
    {
        config.UpdatedAtUtcTicks = DateTime.UtcNow.Ticks;

        string portableFile = Constants.PortableConfigPath;
        string? directory = Path.GetDirectoryName(portableFile);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        byte[] encrypted = ConfigEncryption.EncryptPortableConfig(config);
        AtomicFileWriter.WriteAllBytesFlushed(portableFile, encrypted);

        if (!string.IsNullOrEmpty(directory))
        {
            byte[] logoBytes = BrandingHelper.ResolveLogoBytes(config.General.LogoBase64, config.General.LogoPath);
            if (logoBytes.Length > 0)
            {
                try { AtomicFileWriter.WriteAllBytesFlushed(Path.Combine(directory, "logo.png"), logoBytes); } catch { }
            }
        }

        var embeddedExes = new List<string>();
        var lockedExes = new List<string>();

        foreach (string exePath in EmbeddedConfigHelper.FindSiblingExamExecutables(Constants.AppDirectory))
        {
            string exeName = Path.GetFileName(exePath);
            try
            {
                EmbeddedConfigHelper.WriteEmbeddedConfig(exePath, encrypted);
                embeddedExes.Add(exeName);
                Log.Information("Konfigurasi berhasil disematkan ke {ExePath}", exePath);
            }
            catch (Exception ex)
            {
                lockedExes.Add(exeName);
                Log.Warning(ex, "Gagal menyematkan konfigurasi ke {ExePath}", exePath);
            }
        }

        // Sinkronisasi ke folder sibling jika ada (ExamBro.Exam / portable)
        SyncToRelatedDirectories(config, encrypted);

        // Tulis cadangan ke ProgramData HANYA jika aplikasi terinstal di Program Files
        if (Constants.IsInstalledInProgramFiles)
        {
            try
            {
                string sysFile = Constants.SystemConfigFilePath;
                AtomicFileWriter.WriteAllBytesFlushed(sysFile, encrypted);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Tidak dapat menulis cadangan konfigurasi ke ProgramData");
            }
        }

        ConfigHash = ConfigEncryption.ComputeConfigHash(config);
        return (encrypted, embeddedExes, lockedExes);
    }

    private void ExportLightweightPortableZip()
    {
        var saveDialog = new SaveFileDialog
        {
            Title = "Simpan Paket ZIP Distribusi Siswa (Tanpa Aplikasi Pengaturan)",
            Filter = "Paket ZIP (*.zip)|*.zip",
            FileName = "LouisVoxExamBro-Portable.zip",
            DefaultExt = ".zip"
        };

        if (saveDialog.ShowDialog() != true)
            return;

        string appDir = Constants.AppDirectory;
        var siblingExes = EmbeddedConfigHelper.FindSiblingExamExecutables(appDir);
        string? primaryExe = siblingExes.FirstOrDefault(p =>
            Path.GetFileName(p).Equals("LouisVox ExamBro.exe", StringComparison.OrdinalIgnoreCase))
            ?? siblingExes.FirstOrDefault();

        if (string.IsNullOrEmpty(primaryExe) || !File.Exists(primaryExe))
        {
            MessageBox.Show(
                "Berkas aplikasi ujian ('LouisVox ExamBro.exe' atau 'ExamBro.Exam.exe') tidak ditemukan di folder aplikasi.",
                "Gagal Membuat ZIP",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        string targetZip = saveDialog.FileName;
        string tempZip = $"{targetZip}.{Guid.NewGuid():N}.tmp";

        try
        {
            const string rootPrefix = "LouisVoxExamBro/";
            using (var zipStream = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(primaryExe, rootPrefix + Path.GetFileName(primaryExe), CompressionLevel.Optimal);

                string cfgPath = Constants.PortableConfigPath;
                if (File.Exists(cfgPath))
                {
                    archive.CreateEntryFromFile(cfgPath, rootPrefix + "exambro.cfg", CompressionLevel.Optimal);
                }

                string[] optionalAssets =
                {
                    "logo.png",
                    "exam.ico",
                    "PETUNJUK_PENGGUNAAN_DAN_INSTALASI.txt",
                    "PETUNJUK_PENGGUNAAN.txt"
                };

                foreach (string asset in optionalAssets)
                {
                    string assetPath = Path.Combine(appDir, asset);
                    if (File.Exists(assetPath))
                    {
                        archive.CreateEntryFromFile(assetPath, rootPrefix + asset, CompressionLevel.Optimal);
                    }
                }
            }

            File.Move(tempZip, targetZip, overwrite: true);

            double sizeMb = new FileInfo(targetZip).Length / (1024.0 * 1024.0);
            StatusMessage = $"Paket ZIP siswa dibuat: {Path.GetFileName(targetZip)} ({sizeMb:0.0} MB)";
            Log.Information("Paket ZIP portable ringan dibuat di {Path} ({SizeMb:0.0} MB)", targetZip, sizeMb);

            MessageBox.Show(
                $"Paket distribusi siswa berhasil dibuat:\n{targetZip} ({sizeMb:0.0} MB)\n\n" +
                "Paket hanya berisi aplikasi ujian yang sudah membawa konfigurasi Anda (tanpa aplikasi Pengaturan).",
                "Paket ZIP Siap Dikirim",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            Log.Error(ex, "Gagal membuat paket ZIP portable ke {Path}", targetZip);
            MessageBox.Show(
                $"Gagal membuat paket ZIP distribusi: {ex.Message}",
                "Kesalahan",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void SyncToRelatedDirectories(ExamConfig config, byte[] encryptedBytes)
    {
        try
        {
            string appDir = Constants.AppDirectory;
            string? parentDir = Directory.GetParent(appDir)?.FullName;
            if (string.IsNullOrEmpty(parentDir)) return;

            string[] siblingFolders = new[]
            {
                Path.Combine(parentDir, "ExamBro.Exam"),
                Path.Combine(parentDir, "ExamBro"),
                Path.Combine(parentDir, "portable", "WinVoxExamBro"),
                Path.Combine(parentDir, "WinVoxExamBro"),
                Path.Combine(parentDir, "dist", "ExamBro.Exam"),
                Path.Combine(parentDir, "dist", "portable", "WinVoxExamBro")
            };

            byte[] logoBytes = BrandingHelper.ResolveLogoBytes(config.General.LogoBase64, config.General.LogoPath);

            foreach (var folder in siblingFolders)
            {
                if (Directory.Exists(folder) && !folder.Equals(appDir, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        string targetCfg = Path.Combine(folder, "exambro.cfg");
                        AtomicFileWriter.WriteAllBytesFlushed(targetCfg, encryptedBytes);
                        Log.Information("Sinkronisasi konfigurasi ke folder terkait: {Folder}", folder);

                        if (logoBytes.Length > 0)
                        {
                            try { AtomicFileWriter.WriteAllBytesFlushed(Path.Combine(folder, "logo.png"), logoBytes); } catch { }
                        }

                        foreach (string exePath in EmbeddedConfigHelper.FindSiblingExamExecutables(folder))
                        {
                            try { EmbeddedConfigHelper.WriteEmbeddedConfig(exePath, encryptedBytes); } catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, "Gagal sinkronisasi ke folder {Folder}", folder);
                    }
                }
            }
        }
        catch { }
    }

    [RelayCommand]
    private void TestUrl()
    {
        if (string.IsNullOrWhiteSpace(StartUrl))
        {
            MessageBox.Show(
                "Harap masukkan Start URL terlebih dahulu.",
                "Peringatan",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var window = new TestUrlWindow(StartUrl)
        {
            Owner = Application.Current.MainWindow
        };
        window.ShowDialog();
    }

    [RelayCommand]
    private void Export()
    {
        try
        {
            var config = ToConfig();
            config.UpdatedAtUtcTicks = DateTime.UtcNow.Ticks;
            var saveDialog = new SaveFileDialog
            {
                Title = "Ekspor Konfigurasi ExamBro",
                Filter = "ExamBro Config (*.exbcfg)|*.exbcfg|Semua File (*.*)|*.*",
                FileName = "exambro-config.exbcfg",
                DefaultExt = ".exbcfg"
            };

            if (saveDialog.ShowDialog() != true)
                return;

            var pwdDialog = new PasswordPromptDialog(
                "Password Ekspor",
                "Tentukan password untuk mengenkripsi file ekspor konfigurasi (.exbcfg):",
                requireConfirm: true)
            {
                Owner = Application.Current.MainWindow
            };

            if (pwdDialog.ShowDialog() != true)
                return;

            byte[] exportedBytes = ConfigEncryption.ExportConfig(config, pwdDialog.Password);
            File.WriteAllBytes(saveDialog.FileName, exportedBytes);

            StatusMessage = $"Konfigurasi berhasil diekspor ke: {Path.GetFileName(saveDialog.FileName)}";
            Log.Information("Konfigurasi diekspor ke {Path}", saveDialog.FileName);

            MessageBox.Show(
                "Konfigurasi berhasil diekspor dan dienkripsi dengan aman.",
                "Ekspor Berhasil",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Gagal mengekspor konfigurasi");
            StatusMessage = $"Gagal ekspor: {ex.Message}";
            MessageBox.Show(
                $"Terjadi kesalahan saat ekspor konfigurasi: {ex.Message}",
                "Kesalahan",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void Import()
    {
        try
        {
            var openDialog = new OpenFileDialog
            {
                Title = "Impor Konfigurasi ExamBro",
                Filter = "ExamBro Config (*.exbcfg)|*.exbcfg|Semua File (*.*)|*.*",
                DefaultExt = ".exbcfg"
            };

            if (openDialog.ShowDialog() != true)
                return;

            var pwdDialog = new PasswordPromptDialog(
                "Password Impor",
                "Masukkan password untuk membuka file konfigurasi (.exbcfg):",
                requireConfirm: false)
            {
                Owner = Application.Current.MainWindow
            };

            if (pwdDialog.ShowDialog() != true)
                return;

            byte[] data = File.ReadAllBytes(openDialog.FileName);
            var imported = ConfigEncryption.ImportConfig(data, pwdDialog.Password);

            LoadFromConfig(imported);
            StatusMessage = $"Konfigurasi berhasil diimpor dari: {Path.GetFileName(openDialog.FileName)}";
            Log.Information("Konfigurasi diimpor dari {Path}", openDialog.FileName);

            MessageBox.Show(
                "Konfigurasi berhasil diimpor. Jangan lupa klik 'Simpan Konfigurasi' untuk menerapkannya secara lokal.",
                "Impor Berhasil",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Gagal mengimpor konfigurasi");
            StatusMessage = $"Gagal impor: {ex.Message}";
            MessageBox.Show(
                $"Gagal membuka file konfigurasi: Pastikan password benar dan file tidak rusak.\n({ex.Message})",
                "Impor Gagal",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void ChangeAdminPassword()
    {
        var dialog = new ChangePasswordDialog(
            "Ganti Password Admin",
            AdminPasswordHash,
            AdminPasswordSalt)
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() == true)
        {
            AdminPasswordHash = dialog.Result.Hash;
            AdminPasswordSalt = dialog.Result.Salt;
            StatusMessage = "Password administrator berhasil diubah (klik Simpan untuk menyimpan ke disk).";
            Log.Information("Password administrator diperbarui di memori");
        }
    }

    [RelayCommand]
    private void ChangeProctorPassword()
    {
        var dialog = new ChangePasswordDialog(
            "Ganti Password Pengawas",
            ProctorPasswordHash,
            ProctorPasswordSalt)
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() == true)
        {
            ProctorPasswordHash = dialog.Result.Hash;
            ProctorPasswordSalt = dialog.Result.Salt;
            StatusMessage = "Password pengawas berhasil diubah (klik Simpan untuk menyimpan ke disk).";
            Log.Information("Password pengawas diperbarui di memori");
        }
    }

    [RelayCommand]
    private void AddAllowedPattern()
    {
        string pattern = NewAllowedPattern.Trim();
        if (string.IsNullOrEmpty(pattern))
            return;

        if (!AllowedUrlPatterns.Contains(pattern))
        {
            AllowedUrlPatterns.Add(pattern);
            NewAllowedPattern = string.Empty;
        }
    }

    [RelayCommand]
    private void RemoveAllowedPattern()
    {
        if (!string.IsNullOrEmpty(SelectedAllowedPattern))
        {
            AllowedUrlPatterns.Remove(SelectedAllowedPattern);
        }
    }

    [RelayCommand]
    private void AddBlockedPattern()
    {
        string pattern = NewBlockedPattern.Trim();
        if (string.IsNullOrEmpty(pattern))
            return;

        if (!BlockedUrlPatterns.Contains(pattern))
        {
            BlockedUrlPatterns.Add(pattern);
            NewBlockedPattern = string.Empty;
        }
    }

    [RelayCommand]
    private void RemoveBlockedPattern()
    {
        if (!string.IsNullOrEmpty(SelectedBlockedPattern))
        {
            BlockedUrlPatterns.Remove(SelectedBlockedPattern);
        }
    }

    [RelayCommand]
    private void AddProcess()
    {
        string proc = NewProcess.Trim();
        if (string.IsNullOrEmpty(proc))
            return;

        if (!ProcessBlacklist.Contains(proc))
        {
            ProcessBlacklist.Add(proc);
            NewProcess = string.Empty;
        }
    }

    [RelayCommand]
    private void RemoveProcess()
    {
        if (!string.IsNullOrEmpty(SelectedProcess))
        {
            ProcessBlacklist.Remove(SelectedProcess);
        }
    }

    [RelayCommand]
    private void BrowseLogo()
    {
        var openDialog = new OpenFileDialog
        {
            Title = "Pilih Berkas Logo & Ikon",
            Filter = "Berkas Gambar (*.png;*.jpg;*.jpeg;*.ico;*.bmp)|*.png;*.jpg;*.jpeg;*.ico;*.bmp|Semua Berkas (*.*)|*.*"
        };

        if (openDialog.ShowDialog() == true)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(openDialog.FileName);
                var image = ImageHelper.CreateImageSource(bytes);
                if (image != null)
                {
                    LogoPath = openDialog.FileName;
                    LogoBase64 = Convert.ToBase64String(bytes);
                    LogoPreview = image;
                    if (Application.Current?.MainWindow != null)
                    {
                        Application.Current.MainWindow.Icon = image;
                    }
                    StatusMessage = "Logo dan ikon berhasil diperbarui.";
                }
                else
                {
                    MessageBox.Show("Format berkas gambar tidak dapat dibaca atau tidak didukung.", "Peringatan", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Gagal memuat berkas logo");
                MessageBox.Show($"Gagal memuat logo: {ex.Message}", "Kesalahan", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    [RelayCommand]
    private void ResetLogo()
    {
        LogoPath = string.Empty;
        LogoBase64 = null;
        byte[] defaultBytes = BrandingHelper.GetDefaultLogoBytes();
        LogoPreview = ImageHelper.CreateImageSource(defaultBytes);
        if (Application.Current?.MainWindow != null && LogoPreview != null)
        {
            Application.Current.MainWindow.Icon = LogoPreview;
        }
        StatusMessage = "Logo dikembalikan ke default (LouisVox ExamBro).";
    }

    [RelayCommand]
    private void BrowseLogPath()
    {
        var folderDialog = new OpenFolderDialog
        {
            Title = "Pilih Direktori Penyimpanan Log"
        };

        if (folderDialog.ShowDialog() == true)
        {
            LogPath = folderDialog.FolderName;
        }
    }
}
