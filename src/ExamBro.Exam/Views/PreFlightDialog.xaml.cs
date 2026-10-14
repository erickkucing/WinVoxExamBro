using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ExamBro.Core;
using ExamBro.Core.Models;
using ExamBro.Exam.Helpers;
using ExamBro.Exam.Services;
using Serilog;

namespace ExamBro.Exam.Views;

/// <summary>
/// Model representing an individual pre-flight verification step.
/// </summary>
public class PreFlightCheckItem : INotifyPropertyChanged
{
    private string _title = string.Empty;
    private string _details = string.Empty;
    private string _icon = "⏳";
    private Brush _iconColor = new SolidColorBrush(Color.FromRgb(100, 116, 139));
    private Brush _detailsColor = new SolidColorBrush(Color.FromRgb(148, 163, 184));
    private string _statusText = "Memeriksa...";
    private Brush _statusBadgeBg = new SolidColorBrush(Color.FromRgb(241, 245, 249));
    private Brush _statusBadgeBorder = new SolidColorBrush(Color.FromRgb(226, 232, 240));
    private Brush _statusBadgeFg = new SolidColorBrush(Color.FromRgb(100, 116, 139));

    public string Title { get => _title; set { _title = value; OnPropertyChanged(nameof(Title)); } }
    public string Details { get => _details; set { _details = value; OnPropertyChanged(nameof(Details)); } }
    public string Icon { get => _icon; set { _icon = value; OnPropertyChanged(nameof(Icon)); } }
    public Brush IconColor { get => _iconColor; set { _iconColor = value; OnPropertyChanged(nameof(IconColor)); } }
    public Brush DetailsColor { get => _detailsColor; set { _detailsColor = value; OnPropertyChanged(nameof(DetailsColor)); } }
    public string StatusText { get => _statusText; set { _statusText = value; OnPropertyChanged(nameof(StatusText)); } }
    public Brush StatusBadgeBg { get => _statusBadgeBg; set { _statusBadgeBg = value; OnPropertyChanged(nameof(StatusBadgeBg)); } }
    public Brush StatusBadgeBorder { get => _statusBadgeBorder; set { _statusBadgeBorder = value; OnPropertyChanged(nameof(StatusBadgeBorder)); } }
    public Brush StatusBadgeFg { get => _statusBadgeFg; set { _statusBadgeFg = value; OnPropertyChanged(nameof(StatusBadgeFg)); } }
    public bool IsCritical { get; set; } = true;
    public bool Passed { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string propName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
}

/// <summary>
/// Pre-flight system and security verification dialog displayed before entering kiosk mode.
/// </summary>
public partial class PreFlightDialog : Window
{
    private static readonly ILogger Log = Serilog.Log.ForContext<PreFlightDialog>();

    private readonly ExamConfig _config;
    private readonly bool _isDevSafe;
    private readonly ObservableCollection<PreFlightCheckItem> _checks = new();
    private DispatcherTimer? _autoProceedTimer;

    private static readonly Brush SuccessColor = new SolidColorBrush(Color.FromRgb(22, 163, 74));
    private static readonly Brush FailureColor = new SolidColorBrush(Color.FromRgb(220, 38, 38));
    private static readonly Brush WarningColor = new SolidColorBrush(Color.FromRgb(217, 119, 6));
    private static readonly Brush TextMutedColor = new SolidColorBrush(Color.FromRgb(100, 116, 139));

    // Pill badge brushes
    private static readonly Brush BadgeSuccessBg = new SolidColorBrush(Color.FromRgb(220, 252, 231));
    private static readonly Brush BadgeSuccessBorder = new SolidColorBrush(Color.FromRgb(187, 247, 208));
    private static readonly Brush BadgeSuccessFg = new SolidColorBrush(Color.FromRgb(21, 128, 61));

    private static readonly Brush BadgeFailureBg = new SolidColorBrush(Color.FromRgb(254, 226, 226));
    private static readonly Brush BadgeFailureBorder = new SolidColorBrush(Color.FromRgb(254, 202, 202));
    private static readonly Brush BadgeFailureFg = new SolidColorBrush(Color.FromRgb(185, 28, 28));

    private static readonly Brush BadgeWarningBg = new SolidColorBrush(Color.FromRgb(254, 243, 199));
    private static readonly Brush BadgeWarningBorder = new SolidColorBrush(Color.FromRgb(253, 230, 138));
    private static readonly Brush BadgeWarningFg = new SolidColorBrush(Color.FromRgb(180, 83, 9));

    public PreFlightDialog(ExamConfig config, bool isDevSafe = false)
    {
        InitializeComponent();
        _config = config;
        _isDevSafe = isDevSafe;

        Title = $"{_config.General.AppTitle} - Pemeriksaan Sistem";
        tbTitle.Text = $"Pemeriksaan Sistem - {_config.General.AppTitle}";

        byte[] logoBytes = BrandingHelper.ResolveLogoBytes(_config.General.LogoBase64, _config.General.LogoPath);
        var logoImage = ImageHelper.CreateImageSource(logoBytes);
        if (logoImage != null)
        {
            Icon = logoImage;
            imgLogo.Source = logoImage;
        }

        icChecks.ItemsSource = _checks;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RunAllChecksAsync();
    }

    private async Task RunAllChecksAsync()
    {
        _autoProceedTimer?.Stop();
        _autoProceedTimer = null;

        btnStart.IsEnabled = false;
        btnRetry.IsEnabled = false;
        btnKillOffending.IsEnabled = false;

        SetAlertBannerChecking("Memverifikasi integritas dan keamanan sistem perangkat...");

        _checks.Clear();

        var itemAdmin = new PreFlightCheckItem { Title = "Hak Akses Administrator", Details = "Memeriksa izin penuh sistem...", IsCritical = true };
        var itemWebView2 = new PreFlightCheckItem { Title = "Microsoft Edge WebView2", Details = "Memeriksa runtime browser...", IsCritical = true };
        var itemNetwork = new PreFlightCheckItem { Title = "Koneksi Jaringan", Details = "Memeriksa status sambungan internet atau server lokal..." };
        var itemVm = new PreFlightCheckItem { Title = "Lingkungan Mesin Virtual (VM)", Details = "Mendeteksi virtualisasi...", IsCritical = _config.Security.BlockVirtualMachine && !_isDevSafe };
        var itemRdp = new PreFlightCheckItem { Title = "Sesi Remote Desktop (RDP)", Details = "Memeriksa koneksi jarak jauh...", IsCritical = _config.Security.BlockRemoteSession && !_isDevSafe };
        var itemDebugger = new PreFlightCheckItem { Title = "Integritas & Debugger", Details = "Memeriksa proses debugging...", IsCritical = !_isDevSafe };
        var itemProcess = new PreFlightCheckItem { Title = "Aplikasi Terlarang", Details = "Memindai proses yang sedang aktif...", IsCritical = true };
        var itemMonitors = new PreFlightCheckItem { Title = "Konfigurasi Monitor", Details = "Memeriksa jumlah tampilan layar aktif...", IsCritical = _config.Security.MultiMonitor == MultiMonitorPolicy.Block };

        _checks.Add(itemAdmin);
        _checks.Add(itemWebView2);
        _checks.Add(itemNetwork);
        _checks.Add(itemVm);
        _checks.Add(itemRdp);
        _checks.Add(itemDebugger);
        _checks.Add(itemProcess);
        _checks.Add(itemMonitors);

        bool allPassed = true;

        // 1. Administrator Elevation Check
        await Task.Delay(100);
        bool isAdmin = EnvironmentDetector.IsElevatedAdministrator();
        if (isAdmin)
        {
            MarkPassed(itemAdmin, "Berjalan dengan hak penuh Administrator.");
            btnElevateAdmin.Visibility = Visibility.Collapsed;
        }
        else
        {
            MarkFailed(itemAdmin, "Aplikasi wajib dijalankan sebagai Administrator.", isCritical: true);
            allPassed = false;
            btnElevateAdmin.Visibility = Visibility.Visible;
        }

        // 2. Microsoft Edge WebView2 Runtime Check
        await Task.Delay(100);
        try
        {
            string wvVersion = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
            MarkPassed(itemWebView2, $"Runtime browser terpasang (versi {wvVersion}).");
        }
        catch
        {
            MarkFailed(itemWebView2, "Microsoft Edge WebView2 Runtime belum terpasang. Unduh dari web resmi Microsoft.", isCritical: true);
            allPassed = false;
        }

        // 3. Network Check (Localhost & 100% Offline Ready)
        await Task.Delay(120);
        bool isLocalhostServer = false;
        try
        {
            if (Uri.TryCreate(_config.General.StartUrl, UriKind.Absolute, out var startUri))
            {
                isLocalhostServer = startUri.IsLoopback || startUri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { }

        bool netOk = NetworkInterface.GetIsNetworkAvailable();
        if (isLocalhostServer)
        {
            MarkPassed(itemNetwork, "Server ujian lokal (localhost / intranet) aktif.");
        }
        else if (netOk)
        {
            MarkPassed(itemNetwork, "Koneksi jaringan aktif.");
        }
        else
        {
            MarkWarning(itemNetwork, "Mode offline / jaringan lokal siap digunakan.");
        }

        // 3. Virtual Machine Check (Respects settings toggle)
        await Task.Delay(150);
        if (!_config.Security.BlockVirtualMachine)
        {
            MarkPassed(itemVm, "Pemeriksaan VM dinonaktifkan di pengaturan (Diizinkan).");
        }
        else
        {
            var (isVm, vmReason) = EnvironmentDetector.DetectVirtualMachine(_config.Security.CheckVirtualNetworkAdapters);
            if (!isVm)
            {
                MarkPassed(itemVm, "Berjalan pada perangkat fisik (bukan VM).");
            }
            else if (_isDevSafe)
            {
                MarkWarning(itemVm, $"VM terdeteksi ({vmReason}), diizinkan dalam mode dev.");
            }
            else
            {
                MarkFailed(itemVm, vmReason ?? "Mesin virtual terdeteksi dan diblokir oleh pengawas.", isCritical: true);
                allPassed = false;
            }
        }

        // 4. Remote Desktop Session Check
        await Task.Delay(120);
        var (isRdp, rdpReason) = EnvironmentDetector.DetectRemoteSession();
        if (!isRdp)
        {
            MarkPassed(itemRdp, "Sesi lokal valid.");
        }
        else if (_isDevSafe || !_config.Security.BlockRemoteSession)
        {
            MarkWarning(itemRdp, $"Sesi RDP terdeteksi ({rdpReason}), diizinkan dalam mode ini.");
        }
        else
        {
            MarkFailed(itemRdp, rdpReason ?? "Sesi jarak jauh terdeteksi.", isCritical: true);
            allPassed = false;
        }

        // 5. Debugger Check
        await Task.Delay(100);
        var (isDebugger, debuggerReason) = EnvironmentDetector.DetectDebugger();
        if (!isDebugger)
        {
            MarkPassed(itemDebugger, "Tidak ada debugger terdeteksi.");
        }
        else if (_isDevSafe)
        {
            MarkWarning(itemDebugger, "Debugger terdeteksi, diizinkan untuk mode pengembangan.");
        }
        else
        {
            MarkFailed(itemDebugger, debuggerReason ?? "Debugger terdeteksi.", isCritical: true);
            allPassed = false;
        }

        // 6. Process Blacklist Check
        await Task.Delay(150);
        using var procMonitor = new ProcessMonitorService(_config);
        var offendingProcs = procMonitor.CheckRunningBlacklist();
        if (offendingProcs.Count == 0)
        {
            MarkPassed(itemProcess, "Tidak ada aplikasi terlarang yang sedang aktif.");
            btnKillOffending.Visibility = Visibility.Collapsed;
        }
        else
        {
            string procsStr = string.Join(", ", offendingProcs);
            MarkFailed(itemProcess, $"Tutup aplikasi berikut: {procsStr}", isCritical: true);
            allPassed = false;
            btnKillOffending.Visibility = Visibility.Visible;
            btnKillOffending.IsEnabled = true;
            btnKillOffending.Content = offendingProcs.Count == 1
                ? "⚡ Tutup 1 Aplikasi Terlarang"
                : $"⚡ Tutup {offendingProcs.Count} Aplikasi Terlarang";
        }

        // 7. Multi-Monitor Configuration Check
        await Task.Delay(100);
        var monitors = ScreenProtectionService.EnumerateMonitors();
        if (monitors.Count <= 1 || _config.Security.MultiMonitor != MultiMonitorPolicy.Block)
        {
            MarkPassed(itemMonitors, $"Terdeteksi {monitors.Count} monitor (kebijakan: {_config.Security.MultiMonitor}).");
        }
        else
        {
            MarkFailed(itemMonitors, $"Terdeteksi {monitors.Count} layar aktif. Lepaskan kabel monitor tambahan.", isCritical: true);
            allPassed = false;
        }

        btnRetry.IsEnabled = true;

        if (allPassed)
        {
            SetAlertBannerSuccess("Semua pemeriksaan lolos! Memulai ujian secara otomatis dalam 2 detik...");
            btnStart.IsEnabled = true;

            _autoProceedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.0) };
            _autoProceedTimer.Tick += (s, ev) =>
            {
                _autoProceedTimer?.Stop();
                if (DialogResult != true)
                {
                    DialogResult = true;
                    Close();
                }
            };
            _autoProceedTimer.Start();
        }
        else
        {
            SetAlertBannerFailed("Perangkat belum memenuhi ketentuan keamanan. Tutup aplikasi yang dilarang atau sesuaikan konfigurasi, lalu klik 'Periksa Ulang'.");
            btnStart.IsEnabled = false;
        }
    }

    private void SetAlertBannerChecking(string message)
    {
        pnlAlertBanner.Background = new SolidColorBrush(Color.FromRgb(248, 250, 252));
        pnlAlertBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240));
        txtAlertIcon.Text = "⏳";
        txtAlertIcon.Foreground = TextMutedColor;
        txtOverallStatus.Text = message;
        txtOverallStatus.Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105));
    }

    private void SetAlertBannerSuccess(string message)
    {
        pnlAlertBanner.Background = new SolidColorBrush(Color.FromRgb(240, 253, 244));
        pnlAlertBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(187, 247, 208));
        txtAlertIcon.Text = "✓";
        txtAlertIcon.Foreground = SuccessColor;
        txtOverallStatus.Text = message;
        txtOverallStatus.Foreground = new SolidColorBrush(Color.FromRgb(21, 128, 61));
    }

    private void SetAlertBannerFailed(string message)
    {
        pnlAlertBanner.Background = new SolidColorBrush(Color.FromRgb(254, 242, 242));
        pnlAlertBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(254, 202, 202));
        txtAlertIcon.Text = "⚠";
        txtAlertIcon.Foreground = FailureColor;
        txtOverallStatus.Text = message;
        txtOverallStatus.Foreground = new SolidColorBrush(Color.FromRgb(153, 27, 27));
    }

    private static void MarkPassed(PreFlightCheckItem item, string details)
    {
        item.Icon = "✓";
        item.IconColor = SuccessColor;
        item.DetailsColor = TextMutedColor;
        item.StatusText = "Lolos";
        item.StatusBadgeBg = BadgeSuccessBg;
        item.StatusBadgeBorder = BadgeSuccessBorder;
        item.StatusBadgeFg = BadgeSuccessFg;
        item.Details = details;
        item.Passed = true;
    }

    private static void MarkWarning(PreFlightCheckItem item, string details)
    {
        item.Icon = "⚠";
        item.IconColor = WarningColor;
        item.DetailsColor = WarningColor;
        item.StatusText = "Peringatan";
        item.StatusBadgeBg = BadgeWarningBg;
        item.StatusBadgeBorder = BadgeWarningBorder;
        item.StatusBadgeFg = BadgeWarningFg;
        item.Details = details;
        item.Passed = true;
    }

    private static void MarkFailed(PreFlightCheckItem item, string details, bool isCritical)
    {
        item.Icon = "✕";
        item.IconColor = FailureColor;
        item.DetailsColor = FailureColor;
        item.StatusText = isCritical ? "Gagal" : "Peringatan";
        item.StatusBadgeBg = isCritical ? BadgeFailureBg : BadgeWarningBg;
        item.StatusBadgeBorder = isCritical ? BadgeFailureBorder : BadgeWarningBorder;
        item.StatusBadgeFg = isCritical ? BadgeFailureFg : BadgeWarningFg;
        item.Details = details;
        item.Passed = false;
    }

    private async void BtnRetry_Click(object sender, RoutedEventArgs e)
    {
        await RunAllChecksAsync();
    }

    private async void BtnKillOffending_Click(object sender, RoutedEventArgs e)
    {
        btnKillOffending.IsEnabled = false;
        btnRetry.IsEnabled = false;
        btnStart.IsEnabled = false;
        SetAlertBannerChecking("Menutup seluruh aplikasi terlarang yang terdeteksi...");

        try
        {
            using var procMonitor = new ProcessMonitorService(_config);
            int killed = procMonitor.KillOffendingProcesses();
            Log.Information("User requested termination of offending processes. Terminated: {KilledCount}", killed);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to terminate some offending processes");
        }

        await Task.Delay(600);
        await RunAllChecksAsync();
    }

    private void BtnElevateAdmin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "ExamBro.Exam.exe",
                UseShellExecute = true,
                Verb = "runas"
            };

            foreach (string arg in Environment.GetCommandLineArgs().Skip(1))
            {
                startInfo.ArgumentList.Add(arg);
            }

            System.Diagnostics.Process.Start(startInfo);
            _autoProceedTimer?.Stop();
            DialogResult = false;
            Close();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to elevate process to administrator");
            MessageBox.Show(
                "Tidak dapat meminta hak akses Administrator. Silakan klik kanan berkas aplikasi dan pilih 'Run as administrator'.",
                "Hak Akses Administrator Diperlukan",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        _autoProceedTimer?.Stop();
        DialogResult = true;
        Close();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        _autoProceedTimer?.Stop();
        DialogResult = false;
        Close();
    }
}
