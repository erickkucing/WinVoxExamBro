using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ExamBro.Core.Models;
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

    public string Title { get => _title; set { _title = value; OnPropertyChanged(nameof(Title)); } }
    public string Details { get => _details; set { _details = value; OnPropertyChanged(nameof(Details)); } }
    public string Icon { get => _icon; set { _icon = value; OnPropertyChanged(nameof(Icon)); } }
    public Brush IconColor { get => _iconColor; set { _iconColor = value; OnPropertyChanged(nameof(IconColor)); } }
    public Brush DetailsColor { get => _detailsColor; set { _detailsColor = value; OnPropertyChanged(nameof(DetailsColor)); } }
    public string StatusText { get => _statusText; set { _statusText = value; OnPropertyChanged(nameof(StatusText)); } }
    public bool IsCritical { get; set; } = true;
    public bool Passed { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string propName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
}

/// <summary>
/// Interaction logic for PreFlightDialog.xaml.
/// Verifies hardware, OS security, process blacklist, network, and policy compatibility
/// before allowing the exam session to commence.
/// </summary>
public partial class PreFlightDialog : Window
{
    private static readonly ILogger Log = Serilog.Log.ForContext<PreFlightDialog>();

    private readonly ExamConfig _config;
    private readonly bool _isDevSafe;
    private readonly ObservableCollection<PreFlightCheckItem> _checks = new();

    private static readonly Brush SuccessColor = new SolidColorBrush(Color.FromRgb(34, 197, 94));
    private static readonly Brush FailureColor = new SolidColorBrush(Color.FromRgb(239, 68, 68));
    private static readonly Brush WarningColor = new SolidColorBrush(Color.FromRgb(245, 158, 11));
    private static readonly Brush TextMutedColor = new SolidColorBrush(Color.FromRgb(100, 116, 139));

    public PreFlightDialog(ExamConfig config, bool isDevSafe = false)
    {
        InitializeComponent();
        _config = config;
        _isDevSafe = isDevSafe;

        icChecks.ItemsSource = _checks;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RunAllChecksAsync();
    }

    private async Task RunAllChecksAsync()
    {
        _checks.Clear();

        var itemAdmin = new PreFlightCheckItem { Title = "Hak Akses Administrator", Details = "Memeriksa izin penuh sistem...", IsCritical = true };
        var itemNetwork = new PreFlightCheckItem { Title = "Koneksi Jaringan", Details = "Memeriksa status sambungan internet..." };
        var itemVm = new PreFlightCheckItem { Title = "Lingkungan Mesin Virtual (VM)", Details = "Mendeteksi tanda-tanda virtualisasi...", IsCritical = _config.Security.BlockVirtualMachine && !_isDevSafe };
        var itemRdp = new PreFlightCheckItem { Title = "Sesi Remote Desktop (RDP)", Details = "Memeriksa koneksi jarak jauh...", IsCritical = _config.Security.BlockRemoteSession && !_isDevSafe };
        var itemDebugger = new PreFlightCheckItem { Title = "Integritas & Debugger", Details = "Memeriksa adanya proses debugging...", IsCritical = !_isDevSafe };
        var itemProcess = new PreFlightCheckItem { Title = "Aplikasi Terlarang", Details = "Memindai proses yang sedang berjalan...", IsCritical = true };
        var itemMonitors = new PreFlightCheckItem { Title = "Konfigurasi Monitor", Details = "Memeriksa jumlah tampilan layar aktif...", IsCritical = _config.Security.MultiMonitor == MultiMonitorPolicy.Block };

        _checks.Add(itemAdmin);
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
        }
        else
        {
            MarkFailed(itemAdmin, "Aplikasi wajib dijalankan sebagai Administrator.", isCritical: true);
            allPassed = false;
        }

        // 2. Network Check
        await Task.Delay(150);
        bool netOk = NetworkInterface.GetIsNetworkAvailable();
        if (netOk)
        {
            MarkPassed(itemNetwork, "Koneksi jaringan aktif.");
        }
        else
        {
            MarkFailed(itemNetwork, "Perangkat tidak terhubung ke jaringan internet/intranet.", isCritical: false);
        }

        // 2. VM Check
        await Task.Delay(200);
        var (isVm, vmReason) = EnvironmentDetector.DetectVirtualMachine();
        if (!isVm)
        {
            MarkPassed(itemVm, "Berjalan pada perangkat fisik (bukan VM).");
        }
        else if (_isDevSafe || !_config.Security.BlockVirtualMachine)
        {
            MarkWarning(itemVm, $"VM terdeteksi ({vmReason}), namun diizinkan dalam mode ini.");
        }
        else
        {
            MarkFailed(itemVm, vmReason ?? "Mesin virtual terdeteksi dan diblokir oleh pengawas.", isCritical: true);
            allPassed = false;
        }

        // 3. RDP Check
        await Task.Delay(200);
        var (isRdp, rdpReason) = EnvironmentDetector.DetectRemoteSession();
        if (!isRdp)
        {
            MarkPassed(itemRdp, "Sesi lokal valid.");
        }
        else if (_isDevSafe || !_config.Security.BlockRemoteSession)
        {
            MarkWarning(itemRdp, $"Sesi RDP terdeteksi ({rdpReason}), namun diizinkan dalam mode ini.");
        }
        else
        {
            MarkFailed(itemRdp, rdpReason ?? "Sesi jarak jauh terdeteksi.", isCritical: true);
            allPassed = false;
        }

        // 4. Debugger Check
        await Task.Delay(150);
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

        // 5. Process Blacklist Check
        await Task.Delay(200);
        using var procMonitor = new ProcessMonitorService(_config);
        var offendingProcs = procMonitor.CheckRunningBlacklist();
        if (offendingProcs.Count == 0)
        {
            MarkPassed(itemProcess, "Tidak ada aplikasi terlarang yang sedang aktif.");
        }
        else
        {
            string procsStr = string.Join(", ", offendingProcs);
            MarkFailed(itemProcess, $"Aplikasi berikut harus ditutup: {procsStr}", isCritical: true);
            allPassed = false;
        }

        // 6. Monitor Check
        await Task.Delay(150);
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

        // Final summary
        if (allPassed)
        {
            txtOverallStatus.Text = "Pemeriksaan berhasil. Memulai ujian...";
            txtOverallStatus.Foreground = SuccessColor;
            btnStart.IsEnabled = true;

            // Auto-proceed after 1.5 seconds if everything passed
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            timer.Tick += (s, ev) =>
            {
                timer.Stop();
                if (DialogResult != true)
                {
                    DialogResult = true;
                    Close();
                }
            };
            timer.Start();
        }
        else
        {
            txtOverallStatus.Text = "Perangkat belum memenuhi ketentuan keamanan ujian.";
            txtOverallStatus.Foreground = FailureColor;
            btnClose.Visibility = Visibility.Visible;
            btnStart.IsEnabled = false;
        }
    }

    private static void MarkPassed(PreFlightCheckItem item, string details)
    {
        item.Icon = "✓";
        item.IconColor = SuccessColor;
        item.DetailsColor = TextMutedColor;
        item.StatusText = "Lolos";
        item.Details = details;
        item.Passed = true;
    }

    private static void MarkWarning(PreFlightCheckItem item, string details)
    {
        item.Icon = "⚠";
        item.IconColor = WarningColor;
        item.DetailsColor = WarningColor;
        item.StatusText = "Peringatan";
        item.Details = details;
        item.Passed = true;
    }

    private static void MarkFailed(PreFlightCheckItem item, string details, bool isCritical)
    {
        item.Icon = "✕";
        item.IconColor = FailureColor;
        item.DetailsColor = FailureColor;
        item.StatusText = isCritical ? "Gagal" : "Peringatan";
        item.Details = details;
        item.Passed = false;
    }

    private void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
