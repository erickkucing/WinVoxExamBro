using System.IO;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using ExamBro.Core;
using ExamBro.Core.Models;
using ExamBro.Core.Validation;
using ExamBro.Exam.Services;
using ExamBro.Exam.ViewModels;
using Serilog;

namespace ExamBro.Exam.Views;

/// <summary>
/// Interaction logic for ExamWindow.xaml.
/// The primary kiosk window orchestrating WebView2 runtime security, hardware defenses,
/// background policy enforcement, and student interaction overlays.
/// </summary>
public partial class ExamWindow : Window
{
    private static readonly ILogger Log = Serilog.Log.ForContext<ExamWindow>();

    private readonly ExamConfig _config;
    private readonly bool _isDevSafe;
    private readonly string? _urlOverride;
    private readonly ExamViewModel _viewModel;

    // Security Services
    private KioskLockService? _kioskLock;
    private KeyboardHookService? _keyboardHook;
    private FocusGuardService? _focusGuard;
    private ProcessMonitorService? _processMonitor;
    private ClipboardService? _clipboardService;
    private ScreenProtectionService? _screenProtection;
    private ViolationManager? _violationManager;

    private IntPtr _hwnd = IntPtr.Zero;
    private string _resolvedStartUrl = string.Empty;
    private string _lastValidUrl = string.Empty;
    private bool _isClosingCleanly;

    public ExamWindow(ExamConfig config, bool isDevSafe = false, string? urlOverride = null)
    {
        InitializeComponent();

        _config = config;
        _isDevSafe = isDevSafe;
        _urlOverride = urlOverride;

        _viewModel = new ExamViewModel(_config);
        DataContext = _viewModel;

        // Wire ViewModel Commands
        _viewModel.GoBackRequested += OnGoBackRequested;
        _viewModel.GoForwardRequested += OnGoForwardRequested;
        _viewModel.ReloadRequested += OnReloadRequested;
        _viewModel.GoHomeRequested += OnGoHomeRequested;
        _viewModel.RequestExitRequested += ShowExitConfirmation;

        // Overlay Events
        lockScreen.Initialize(_config);
        lockScreen.Unlocked += OnLockScreenUnlocked;
        completionOverlay.ExitRequested += CloseGracefully;

        Loaded += OnWindowLoaded;
        Closing += OnWindowClosing;
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        _hwnd = helper.Handle;

        Log.Information("ExamWindow loaded. HWND: 0x{Hwnd:X}, DevSafe: {DevSafe}", _hwnd.ToInt64(), _isDevSafe);

        // 1. Resolve and prepare Start URL
        PrepareStartUrl();

        // 2. Initialize Security Services
        InitializeSecurityServices();

        // 3. Initialize WebView2 Browser Engine
        await InitializeWebView2Async();
    }

    private void PrepareStartUrl()
    {
        string rawUrl = (!string.IsNullOrWhiteSpace(_urlOverride) && (_config.General.AllowCommandLineOverride || _isDevSafe))
            ? _urlOverride
            : _config.General.StartUrl;

        _resolvedStartUrl = UrlMatcher.ResolveStartUrl(
            rawUrl,
            _config.General.PlaceholderUsername ? Environment.UserName : null,
            _config.General.PlaceholderMachine ? Environment.MachineName : null);

        // Auto-allow start URL host if configured
        if (_config.Navigation.AutoAllowStartUrlHost)
        {
            string hostPattern = UrlMatcher.BuildHostPattern(_resolvedStartUrl);
            if (!_config.Navigation.AllowedUrlPatterns.Contains(hostPattern, StringComparer.OrdinalIgnoreCase))
            {
                _config.Navigation.AllowedUrlPatterns.Insert(0, hostPattern);
                Log.Information("Auto-allowed start host pattern: {Pattern}", hostPattern);
            }
        }

        _lastValidUrl = _resolvedStartUrl;
        _viewModel.UpdateUrl(_resolvedStartUrl);
    }

    private void InitializeSecurityServices()
    {
        _violationManager = new ViolationManager(_config);
        _violationManager.ShowWarning += (type, msg, count) =>
        {
            Dispatcher.Invoke(() => warningOverlay.ShowWarning(msg, count));
        };
        _violationManager.ShowLockScreen += (type, reason) =>
        {
            Dispatcher.Invoke(() => lockScreen.ShowLock(reason));
        };
        _violationManager.RequestTerminate += (type, reason) =>
        {
            Dispatcher.Invoke(() =>
            {
                Log.Warning("Session terminated by ViolationManager: {Reason}", reason);
                CloseGracefully();
            });
        };

        // Kiosk lockdown (taskbar, policies) - skip if dev-safe
        if (!_isDevSafe && _config.Security.KioskMode)
        {
            _kioskLock = new KioskLockService();
            _kioskLock.Engage(_config);
        }

        // Low-level keyboard hook
        if (!_isDevSafe)
        {
            _keyboardHook = new KeyboardHookService(_config);
            _keyboardHook.BlockedKeyDetected += desc =>
            {
                _violationManager.RecordViolation(ViolationEventType.BlockedKey, desc);
            };
            _keyboardHook.ExitShortcutRequested += () =>
            {
                Dispatcher.Invoke(ShowExitConfirmation);
            };
            _keyboardHook.Start();
        }

        // Window Focus Guard
        if (!_isDevSafe)
        {
            _focusGuard = new FocusGuardService();
            _focusGuard.FocusLost += () =>
            {
                _violationManager.RecordViolation(ViolationEventType.FocusLost);
            };
            _focusGuard.Start(_hwnd);
        }

        // Background Process Scanner
        _processMonitor = new ProcessMonitorService(_config);
        _processMonitor.BlockedProcessDetected += (name, pid) =>
        {
            _violationManager.RecordViolation(ViolationEventType.BlockedProcess, $"{name} (PID: {pid})");
        };
        _processMonitor.Start();

        // Clipboard Protection
        _clipboardService = new ClipboardService(_config);
        _clipboardService.ClipboardViolationDetected += () =>
        {
            _violationManager.RecordViolation(ViolationEventType.ClipboardAttempt);
        };
        _clipboardService.Initialize(_hwnd);

        // Hardware Screen Protection & Multi-monitor enforcement
        _screenProtection = new ScreenProtectionService(_config);
        if (!_isDevSafe)
        {
            _screenProtection.ApplyCaptureProtection(_hwnd);
            _screenProtection.EnforceMonitorPolicy(msg =>
            {
                _violationManager.RecordViolation(ViolationEventType.MultiMonitor, msg);
            });
        }
    }

    private async Task InitializeWebView2Async()
    {
        try
        {
            Log.Information("Initializing WebView2 browser environment...");

            string dataFolder = Constants.WebView2DataFolder;
            string userAgentArg = $"--user-agent-product=\"{Constants.UserAgentSuffix.Trim()}\"";
            string additionalArgs = $"{userAgentArg} --inprivate --disable-features=Translate,OptimizationHints";

            var envOptions = new CoreWebView2EnvironmentOptions(additionalArgs);
            var env = await CoreWebView2Environment.CreateAsync(null, dataFolder, envOptions);

            await webView.EnsureCoreWebView2Async(env);

            ConfigureWebViewSettings();
            AttachWebViewEvents();

            // Clear cache and cookies for privacy isolation
            await webView.CoreWebView2.Profile.ClearBrowsingDataAsync();

            Log.Information("Navigating to start URL: {Url}", _resolvedStartUrl);
            webView.CoreWebView2.Navigate(_resolvedStartUrl);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize WebView2 engine");
            ShowErrorPage();
        }
    }

    private void ConfigureWebViewSettings()
    {
        var settings = webView.CoreWebView2.Settings;

        settings.AreDevToolsEnabled = _isDevSafe;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = _config.Navigation.AllowZoom;
        settings.IsBuiltInErrorPageEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
    }

    private void AttachWebViewEvents()
    {
        var core = webView.CoreWebView2;

        core.NavigationStarting += OnNavigationStarting;
        core.FrameNavigationStarting += OnFrameNavigationStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.DownloadStarting += OnDownloadStarting;
        core.PermissionRequested += OnPermissionRequested;
        core.ProcessFailed += OnProcessFailed;
        core.WebMessageReceived += OnWebMessageReceived;
        core.NavigationCompleted += OnNavigationCompleted;
    }

    // ── WebView2 Event Handlers ──────────────────────────────────────────────

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        string uri = e.Uri;

        // Allow internal about:blank or data-error rendering
        if (uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // 1. Check Exit URL
        if (UrlMatcher.MatchesExitUrl(uri, _config.Navigation.ExitUrlPattern))
        {
            Log.Information("Exam exit URL reached: {Uri}", uri);
            Dispatcher.Invoke(() =>
            {
                completionOverlay.ShowCompletion(_config.Navigation.ExitUrlAutoCloseSeconds);
            });
            return;
        }

        // 2. Validate URL against whitelist / blacklist / blocked schemes
        if (!UrlMatcher.IsUrlAllowed(uri, _config.Navigation.AllowedUrlPatterns, _config.Navigation.BlockedUrlPatterns))
        {
            Log.Warning("Navigation to blocked or unwhitelisted URL intercepted: {Uri}", uri);
            e.Cancel = true;
            _violationManager?.RecordViolation(ViolationEventType.BlockedUrl, uri);
            ShowErrorPage();
            return;
        }

        _lastValidUrl = uri;
        _viewModel.UpdateUrl(uri);
    }

    private void OnFrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        string uri = e.Uri;
        if (uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase)) return;

        if (!UrlMatcher.IsUrlAllowed(uri, _config.Navigation.AllowedUrlPatterns, _config.Navigation.BlockedUrlPatterns))
        {
            Log.Warning("Frame navigation blocked: {Uri}", uri);
            e.Cancel = true;
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true; // Prevent external popup windows

        string uri = e.Uri;
        Log.Information("New window requested for {Uri}. Redirecting in same frame.", uri);

        if (UrlMatcher.IsUrlAllowed(uri, _config.Navigation.AllowedUrlPatterns, _config.Navigation.BlockedUrlPatterns))
        {
            webView.CoreWebView2.Navigate(uri);
        }
        else
        {
            _violationManager?.RecordViolation(ViolationEventType.BlockedUrl, uri);
        }
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        if (!_config.Navigation.AllowDownloads)
        {
            Log.Warning("File download blocked: {Uri}", e.DownloadOperation.Uri);
            e.Cancel = true;
        }
    }

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        bool allowed = e.PermissionKind switch
        {
            CoreWebView2PermissionKind.Camera => _config.Permissions.AllowCamera,
            CoreWebView2PermissionKind.Microphone => _config.Permissions.AllowMicrophone,
            CoreWebView2PermissionKind.Geolocation => _config.Permissions.AllowGeolocation,
            CoreWebView2PermissionKind.Notifications => _config.Permissions.AllowNotifications,
            _ => false
        };

        e.State = allowed ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
        Log.Information("Permission request for {Kind} set to {State}", e.PermissionKind, e.State);
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Log.Error("WebView2 process failed: {Kind}, Reason: {Reason}", e.ProcessFailedKind, e.Reason);
        try
        {
            // Recover back to last good URL or start URL
            string recoverUrl = !string.IsNullOrEmpty(_lastValidUrl) ? _lastValidUrl : _resolvedStartUrl;
            webView.CoreWebView2.Navigate(recoverUrl);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to recover from WebView2 process crash");
            ShowErrorPage();
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            string msg = e.TryGetWebMessageAsString();
            if (msg.Equals("retry", StringComparison.OrdinalIgnoreCase))
            {
                Log.Information("User requested retry from error page");
                string target = !string.IsNullOrEmpty(_lastValidUrl) ? _lastValidUrl : _resolvedStartUrl;
                webView.CoreWebView2.Navigate(target);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error handling WebMessage");
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _viewModel.CanGoBack = _config.Navigation.AllowBackForward && webView.CanGoBack;
        _viewModel.CanGoForward = _config.Navigation.AllowBackForward && webView.CanGoForward;

        if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
        {
            Log.Warning("Navigation completed with failure status: {Status}", e.WebErrorStatus);
            ShowErrorPage();
        }
    }

    private void ShowErrorPage()
    {
        try
        {
            string html = GetErrorPageHtml();
            webView.NavigateToString(html);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to render error page");
        }
    }

    private static string GetErrorPageHtml()
    {
        try
        {
            var assembly = typeof(ExamWindow).Assembly;
            using var stream = assembly.GetManifestResourceStream("ExamBro.Exam.Resources.ErrorPage.html");
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Embedded ErrorPage.html resource read failure");
        }

        return "<html><body style='font-family:sans-serif;text-align:center;padding:50px;'>" +
               "<h2>Halaman Tidak Dapat Dimuat</h2><p>Terjadi kendala pada koneksi ujian.</p>" +
               "<button onclick=\"window.chrome.webview.postMessage('retry')\">Coba Lagi</button></body></html>";
    }

    // ── Navigation Command Callbacks ─────────────────────────────────────────

    private void OnGoBackRequested()
    {
        if (_config.Navigation.AllowBackForward && webView.CanGoBack)
        {
            webView.GoBack();
        }
    }

    private void OnGoForwardRequested()
    {
        if (_config.Navigation.AllowBackForward && webView.CanGoForward)
        {
            webView.GoForward();
        }
    }

    private void OnReloadRequested()
    {
        if (_config.Navigation.AllowReload)
        {
            webView.Reload();
        }
    }

    private void OnGoHomeRequested()
    {
        webView.CoreWebView2?.Navigate(_resolvedStartUrl);
    }

    // ── Proctor Exit & Lock Screen Handling ──────────────────────────────────

    private void ShowExitConfirmation()
    {
        _focusGuard?.SetSuspended(true);
        try
        {
            var dialog = new ExitDialog(_config)
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true)
            {
                Log.Information("Authorized proctor exit initiated");
                CloseGracefully();
            }
        }
        finally
        {
            _focusGuard?.SetSuspended(false);
            _focusGuard?.ReclaimFocus();
        }
    }

    private void OnLockScreenUnlocked()
    {
        _violationManager?.ResetCount(ViolationEventType.FocusLost);
        _violationManager?.ResetCount(ViolationEventType.BlockedKey);
        _violationManager?.ResetCount(ViolationEventType.BlockedProcess);
        _focusGuard?.ReclaimFocus();
    }

    public void CloseGracefully()
    {
        _isClosingCleanly = true;
        Close();
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isClosingCleanly)
        {
            // If closed through unexpected mechanism, prompt proctor
            e.Cancel = true;
            ShowExitConfirmation();
            return;
        }

        Log.Information("Closing ExamWindow and tearing down security services...");

        // Defensively teardown all services
        try { _focusGuard?.Dispose(); } catch (Exception ex) { Log.Error(ex, "FocusGuard disposal error"); }
        try { _keyboardHook?.Dispose(); } catch (Exception ex) { Log.Error(ex, "KeyboardHook disposal error"); }
        try { _processMonitor?.Dispose(); } catch (Exception ex) { Log.Error(ex, "ProcessMonitor disposal error"); }
        try { _clipboardService?.Dispose(); } catch (Exception ex) { Log.Error(ex, "ClipboardService disposal error"); }
        try { _screenProtection?.Dispose(); } catch (Exception ex) { Log.Error(ex, "ScreenProtection disposal error"); }
        try { _kioskLock?.Dispose(); } catch (Exception ex) { Log.Error(ex, "KioskLock disposal error"); }
        try { _viewModel.Dispose(); } catch (Exception ex) { Log.Error(ex, "ViewModel disposal error"); }

        // Clear browsing data on exit
        try
        {
            webView.CoreWebView2?.Profile?.ClearBrowsingDataAsync();
        }
        catch
        {
            // best effort
        }
    }
}
