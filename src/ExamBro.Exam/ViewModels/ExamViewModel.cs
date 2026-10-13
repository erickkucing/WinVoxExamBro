using System.Net.NetworkInformation;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExamBro.Core.Models;

namespace ExamBro.Exam.ViewModels;

/// <summary>
/// ViewModel backing the main examination kiosk window.
/// Manages navigation state, real-time status clock, network connectivity monitoring,
/// and toolbar commands.
/// </summary>
public partial class ExamViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _networkTimer;
    private bool _disposed;

    [ObservableProperty]
    private string _currentUrl = string.Empty;

    [ObservableProperty]
    private string _currentDomain = string.Empty;

    [ObservableProperty]
    private bool _isToolbarVisible = true;

    [ObservableProperty]
    private bool _canGoBack;

    [ObservableProperty]
    private bool _canGoForward;

    [ObservableProperty]
    private string _clockTime = string.Empty;

    [ObservableProperty]
    private string _networkStatus = "Terhubung";

    [ObservableProperty]
    private bool _isNetworkConnected = true;

    [ObservableProperty]
    private string _appTitle = "ExamBro - Ujian Online";

    [ObservableProperty]
    private string _userInfo = string.Empty;

    // Navigation and exit command callbacks
    public event Action? GoBackRequested;
    public event Action? GoForwardRequested;
    public event Action? ReloadRequested;
    public event Action? GoHomeRequested;
    public event Action? RequestExitRequested;

    public ExamViewModel(ExamConfig config)
    {
        AppTitle = config.General.AppTitle;
        IsToolbarVisible = config.General.ShowToolbar;

        string user = config.General.PlaceholderUsername ? Environment.UserName : "";
        string machine = config.General.PlaceholderMachine ? Environment.MachineName : "";
        UserInfo = $"{user} @ {machine}".Trim(' ', '@');

        // Setup Clock Timer (1-second tick)
        _clockTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _clockTimer.Tick += (s, e) => UpdateClock();
        UpdateClock();
        _clockTimer.Start();

        // Setup Network Monitor Timer (5-second tick)
        _networkTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _networkTimer.Tick += (s, e) => CheckNetworkConnectivity();
        CheckNetworkConnectivity();
        _networkTimer.Start();
    }

    private void UpdateClock()
    {
        ClockTime = DateTime.Now.ToString("HH:mm:ss");
    }

    private void CheckNetworkConnectivity()
    {
        try
        {
            bool isUp = NetworkInterface.GetIsNetworkAvailable();
            IsNetworkConnected = isUp;
            NetworkStatus = isUp ? "Terhubung" : "Terputus";
        }
        catch
        {
            NetworkStatus = "Tidak Diketahui";
        }
    }

    public void UpdateUrl(string url)
    {
        CurrentUrl = url;
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                CurrentDomain = uri.Host;
            }
            else
            {
                CurrentDomain = string.Empty;
            }
        }
        catch
        {
            CurrentDomain = string.Empty;
        }
    }

    // ── Relay Commands ───────────────────────────────────────────────────────

    [RelayCommand]
    private void GoBack()
    {
        if (CanGoBack)
        {
            GoBackRequested?.Invoke();
        }
    }

    [RelayCommand]
    private void GoForward()
    {
        if (CanGoForward)
        {
            GoForwardRequested?.Invoke();
        }
    }

    [RelayCommand]
    private void Reload()
    {
        ReloadRequested?.Invoke();
    }

    [RelayCommand]
    private void GoHome()
    {
        GoHomeRequested?.Invoke();
    }

    [RelayCommand]
    private void RequestExit()
    {
        RequestExitRequested?.Invoke();
    }

    // ── IDisposable ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _clockTimer.Stop();
            _networkTimer.Stop();
            _disposed = true;
        }
    }

    ~ExamViewModel()
    {
        Dispose(false);
    }
}
