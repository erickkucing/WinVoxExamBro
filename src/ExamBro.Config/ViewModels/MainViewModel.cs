using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    // ── General Config ──────────────────────────────────────────────────
    [ObservableProperty]
    private string _appTitle = "Ujian Online";

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
    private bool _placeholderUsername = true;

    [ObservableProperty]
    private bool _placeholderMachine = true;

    // ── Navigation Config ───────────────────────────────────────────────
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

    // ── Security Config ─────────────────────────────────────────────────
    [ObservableProperty]
    private bool _kioskMode = true;

    [ObservableProperty]
    private MultiMonitorPolicy _multiMonitor = MultiMonitorPolicy.CoverAll;

    [ObservableProperty]
    private bool _blockVirtualMachine = true;

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

    // ── Permissions Config ──────────────────────────────────────────────
    [ObservableProperty]
    private bool _allowCamera;

    [ObservableProperty]
    private bool _allowMicrophone;

    [ObservableProperty]
    private bool _allowGeolocation;

    [ObservableProperty]
    private bool _allowNotifications;

    // ── Admin Config ────────────────────────────────────────────────────
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

    // ── Logging Config ──────────────────────────────────────────────────
    [ObservableProperty]
    private string _logLevel = "Information";

    [ObservableProperty]
    private string _logPath = string.Empty;

    [ObservableProperty]
    private string _webhookUrl = string.Empty;

    [ObservableProperty]
    private string _webhookHmacKey = string.Empty;

    // ── Violations ──────────────────────────────────────────────────────
    public ObservableCollection<ViolationRuleItemViewModel> ViolationRules { get; } = new();

    // ── Meta, Validation & Status ───────────────────────────────────────
    [ObservableProperty]
    private string _configHash = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "Siap.";

    [ObservableProperty]
    private bool _hasErrors;

    public ObservableCollection<ValidationError> ValidationErrors { get; } = new();

    // ── Option Lists for UI Binding ─────────────────────────────────────
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
        AppTitle = config.General.AppTitle;
        Language = config.General.Language;
        StartUrl = config.General.StartUrl;
        AllowCommandLineOverride = config.General.AllowCommandLineOverride;
        ShowToolbar = config.General.ShowToolbar;
        LogoPath = config.General.LogoPath;
        PlaceholderUsername = config.General.PlaceholderUsername;
        PlaceholderMachine = config.General.PlaceholderMachine;

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

    // ── Command Handlers ────────────────────────────────────────────────

    [RelayCommand]
    private void Save()
    {
        try
        {
            var config = ToConfig();
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
                StatusMessage = "Gagal menyimpan: terdapat kesalahan konfigurasi yang harus diperbaiki.";
                string errorList = string.Join("\n• ", errors.Where(e => e.Severity == ValidationSeverity.Error).Select(e => e.Message));
                MessageBox.Show(
                    $"Konfigurasi tidak dapat disimpan karena terdapat kesalahan validasi:\n\n• {errorList}",
                    "Validasi Gagal",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            string targetFile = Constants.ConfigFilePath;
            string? directory = Path.GetDirectoryName(targetFile);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            byte[] encrypted = ConfigEncryption.EncryptConfig(config);
            File.WriteAllBytes(targetFile, encrypted);

            ConfigHash = ConfigEncryption.ComputeConfigHash(config);
            StatusMessage = $"Konfigurasi berhasil disimpan! ({DateTime.Now:HH:mm:ss})";
            Log.Information("Konfigurasi ExamBro berhasil disimpan ke {Path}", targetFile);

            MessageBox.Show(
                "Konfigurasi ExamBro berhasil disimpan dan dienkripsi dengan aman.",
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
            Title = "Pilih Berkas Logo",
            Filter = "Gambar (*.png;*.jpg;*.jpeg;*.ico;*.bmp)|*.png;*.jpg;*.jpeg;*.ico;*.bmp|Semua Berkas (*.*)|*.*"
        };

        if (openDialog.ShowDialog() == true)
        {
            LogoPath = openDialog.FileName;
        }
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
