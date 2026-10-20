using System.Net.NetworkInformation;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExamBro.Core;
using ExamBro.Core.Models;
using ExamBro.Core.Network;
using ExamBro.Exam.Helpers;

namespace ExamBro.Exam.ViewModels;

/// <summary>
/// ViewModel backing the main examination kiosk window.
/// Manages navigation state, real-time status clock, network connectivity monitoring,
/// and toolbar commands.
/// </summary>
public partial class ExamViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _clockTimer;
    private readonly NetworkPingService _pingService;
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

    public NetworkPingService PingService => _pingService;

    [ObservableProperty]
    private string _pingTimeDisplay = "--- ms";

    [ObservableProperty]
    private Brush _pingStatusBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(PingStatus.ColorDisconnectedHex));

    [ObservableProperty]
    private PingStatus _currentPingStatus = PingStatus.Disconnected();

    [ObservableProperty]
    private string _networkStatus = "--- ms";

    [ObservableProperty]
    private bool _isNetworkConnected = true;

    [ObservableProperty]
    private string _appTitle = BrandingHelper.DefaultAppTitle;

    [ObservableProperty]
    private ImageSource? _logoImage;

    [ObservableProperty]
    private string _userInfo = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomDisplay))]
    [NotifyPropertyChangedFor(nameof(CanZoomIn))]
    [NotifyPropertyChangedFor(nameof(CanZoomOut))]
    private double _zoomFactor = 1.0;

    public string ZoomDisplay => $"{Math.Round(ZoomFactor * 100)}%";

    public bool CanZoomIn => ZoomFactor < 1.49;
    public bool CanZoomOut => ZoomFactor > 0.51;

    // Navigation and exit command callbacks
    public event Action? GoBackRequested;
    public event Action? GoForwardRequested;
    public event Action? ReloadRequested;
    public event Action? GoHomeRequested;
    public event Action? RequestExitRequested;
    public event Action<double>? ZoomFactorChangeRequested;

    public ExamViewModel(ExamConfig config)
    {
        AppTitle = string.IsNullOrWhiteSpace(config.General.AppTitle) ? BrandingHelper.DefaultAppTitle : config.General.AppTitle;
        IsToolbarVisible = config.General.ShowToolbar;

        byte[] logoBytes = BrandingHelper.ResolveLogoBytes(config.General.LogoBase64, config.General.LogoPath);
        LogoImage = ImageHelper.CreateImageSource(logoBytes);

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

        // Setup Real-time Network Ping Monitor to CBT Target Server
        _pingService = new NetworkPingService();
        _pingService.UpdateTargetFromUrl(config.General.StartUrl);
        _pingService.PingUpdated += OnPingUpdated;
        _pingService.Start();
    }

    private void UpdateClock()
    {
        ClockTime = DateTime.Now.ToString("HH:mm:ss");
    }

    private void OnPingUpdated(PingStatus status)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(() => ApplyPingStatus(status));
        }
        else
        {
            ApplyPingStatus(status);
        }
    }

    private void ApplyPingStatus(PingStatus status)
    {
        CurrentPingStatus = status;
        PingTimeDisplay = status.DisplayTime;
        IsNetworkConnected = status.IsConnected;
        NetworkStatus = status.DisplayTime;

        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(status.StatusColorHex));
            brush.Freeze();
            PingStatusBrush = brush;
        }
        catch
        {
            // fallback
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
                _pingService.UpdateTargetFromUrl(url);
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

    // Relay Commands

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

    [RelayCommand(CanExecute = nameof(CanZoomIn))]
    private void ZoomIn()
    {
        double next = Math.Min(1.50, Math.Round((ZoomFactor + 0.10) * 10.0) / 10.0);
        SetZoom(next);
    }

    [RelayCommand(CanExecute = nameof(CanZoomOut))]
    private void ZoomOut()
    {
        double next = Math.Max(0.50, Math.Round((ZoomFactor - 0.10) * 10.0) / 10.0);
        SetZoom(next);
    }

    [RelayCommand]
    private void ResetZoom()
    {
        SetZoom(1.0);
    }

    public void SetZoom(double zoom)
    {
        double clamped = Math.Clamp(Math.Round(zoom * 100.0) / 100.0, 0.50, 1.50);
        if (Math.Abs(ZoomFactor - clamped) > 0.001)
        {
            ZoomFactor = clamped;
            ZoomFactorChangeRequested?.Invoke(clamped);
            ZoomInCommand.NotifyCanExecuteChanged();
            ZoomOutCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void RequestExit()
    {
        RequestExitRequested?.Invoke();
    }

    // IDisposable

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
            _pingService.Dispose();
            _disposed = true;
        }
    }

    ~ExamViewModel()
    {
        Dispose(false);
    }
}
