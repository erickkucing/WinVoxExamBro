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
        warningOverlay.Dismissed += () => SetWebOverlayActive(false);
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

        // Auto-allow start URL host and authority if configured (including ports like :8000)
        if (_config.Navigation.AutoAllowStartUrlHost)
        {
            string hostPattern = UrlMatcher.BuildHostPattern(_resolvedStartUrl);
            if (!_config.Navigation.AllowedUrlPatterns.Contains(hostPattern, StringComparer.OrdinalIgnoreCase))
            {
                _config.Navigation.AllowedUrlPatterns.Insert(0, hostPattern);
                Log.Information("Auto-allowed start host pattern: {Pattern}", hostPattern);
            }

            if (Uri.TryCreate(_resolvedStartUrl, UriKind.Absolute, out Uri? uri))
            {
                string wildcardAuth = $"*://{uri.Authority}/*";
                if (!_config.Navigation.AllowedUrlPatterns.Contains(wildcardAuth, StringComparer.OrdinalIgnoreCase))
                {
                    _config.Navigation.AllowedUrlPatterns.Insert(0, wildcardAuth);
                }
                string wildcardHost = $"*://{uri.Host}/*";
                if (!_config.Navigation.AllowedUrlPatterns.Contains(wildcardHost, StringComparer.OrdinalIgnoreCase))
                {
                    _config.Navigation.AllowedUrlPatterns.Insert(0, wildcardHost);
                }
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
            Dispatcher.Invoke(() =>
            {
                SetWebOverlayActive(true);
                warningOverlay.ShowWarning(msg, count);
            });
        };
        _violationManager.ShowLockScreen += (type, reason) =>
        {
            Dispatcher.Invoke(() =>
            {
                SetWebOverlayActive(true);
                lockScreen.ShowLock(reason);
            });
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
                NotifyWebAntiCheatViolation("devtools_open", $"Shortcut keyboard terlarang: {desc}");
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
                NotifyWebAntiCheatViolation("window_blur", "Fokus jendela ExamBro berpindah di sistem operasi");
            };
            _focusGuard.Start(_hwnd);
        }

        // Background Process Scanner
        _processMonitor = new ProcessMonitorService(_config);
        _processMonitor.BlockedProcessDetected += (name, pid) =>
        {
            _violationManager.RecordViolation(ViolationEventType.BlockedProcess, $"{name} (PID: {pid})");
            NotifyWebAntiCheatSevereViolation($"Aplikasi terlarang terdeteksi di Windows: {name} (PID: {pid})");
        };
        _processMonitor.Start();

        // Clipboard Protection
        _clipboardService = new ClipboardService(_config);
        _clipboardService.ClipboardViolationDetected += () =>
        {
            _violationManager.RecordViolation(ViolationEventType.ClipboardAttempt);
            NotifyWebAntiCheatViolation("clipboard_action", "Akses clipboard sistem terdeteksi");
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
                NotifyWebAntiCheatViolation("multi_monitor", msg);
            });
        }
    }

    private async Task InitializeWebView2Async()
    {
        try
        {
            Log.Information("Initializing WebView2 browser environment with projectCBT synchronization...");

            string dataFolder = Constants.WebView2DataFolder;
            string userAgentArg = $"--user-agent-product=\"{Constants.UserAgentSuffix.Trim()}\"";
            string additionalArgs = $"{userAgentArg} --inprivate --disable-features=Translate,OptimizationHints";

            var envOptions = new CoreWebView2EnvironmentOptions(additionalArgs);
            var env = await CoreWebView2Environment.CreateAsync(null, dataFolder, envOptions);

            await webView.EnsureCoreWebView2Async(env);

            ConfigureWebViewSettings();
            AttachWebViewEvents();

            // Inject projectCBT JavaScript bridge before document creation
            await InjectProjectCbtBridgeAsync();

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

    private async Task InjectProjectCbtBridgeAsync()
    {
        // Bridge script providing window.LouisVoxExamApp and anti-blur suppression
        string bridgeScript = @"
(function() {
    window.__louisvox_suppress_blur = false;
    window.__exambro_overlay_active = false;

    window.LouisVoxExamApp = {
        onWebExamConfig: function(maxStrikes, strikes, isLocked) {
            try {
                window.chrome.webview.postMessage(JSON.stringify({
                    handler: 'CBT_CONFIG',
                    maxStrikes: maxStrikes,
                    strikes: strikes,
                    isLocked: isLocked
                }));
            } catch (e) {}
        },
        onWebViolationRecorded: function(type, strikes, maxStrikes, message, isLocked) {
            try {
                window.chrome.webview.postMessage(JSON.stringify({
                    handler: 'CBT_VIOLATION',
                    type: type,
                    strikes: strikes,
                    maxStrikes: maxStrikes,
                    message: message,
                    isLocked: isLocked
                }));
            } catch (e) {}
        },
        isAlarmOverlayActive: function() {
            return window.__exambro_overlay_active === true || window.__louisvox_suppress_blur === true;
        },
        onWebSessionUnlocked: function(strikes) {
            try {
                window.chrome.webview.postMessage(JSON.stringify({
                    handler: 'CBT_UNLOCKED',
                    strikes: strikes
                }));
            } catch (e) {}
        },
        onExamFinished: function() {
            try {
                window.chrome.webview.postMessage(JSON.stringify({
                    handler: 'CBT_FINISHED'
                }));
            } catch (e) {}
        }
    };
})();
";
        await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(bridgeScript);
        Log.Information("Injected LouisVoxExamApp JavaScript bridge for projectCBT.");
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

        // 1. Add WebResourceRequested filter to inject projectCBT authentication & signature headers
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;

        core.NavigationStarting += OnNavigationStarting;
        core.FrameNavigationStarting += OnFrameNavigationStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.DownloadStarting += OnDownloadStarting;
        core.PermissionRequested += OnPermissionRequested;
        core.ProcessFailed += OnProcessFailed;
        core.WebMessageReceived += OnWebMessageReceived;
        core.NavigationCompleted += OnNavigationCompleted;
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            // Injects ExamBrowser headers verified by projectCBT EnsureExamBrowser / SiteSetting::isExamBrowser
            e.Request.Headers.SetHeader("X-ExamBro-Version", Constants.AppVersion);
            e.Request.Headers.SetHeader("X-Exambrowser", "1");
            e.Request.Headers.SetHeader("X-Exambro", "1");
            e.Request.Headers.SetHeader("X-Exam-Browser", "1");
            e.Request.Headers.SetHeader("X-CBT-Browser", "1");
        }
        catch
        {
            // best effort
        }
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
            if (string.IsNullOrEmpty(msg)) return;

            if (msg.Equals("retry", StringComparison.OrdinalIgnoreCase))
            {
                Log.Information("User requested retry from error page");
                string target = !string.IsNullOrEmpty(_lastValidUrl) ? _lastValidUrl : _resolvedStartUrl;
                webView.CoreWebView2.Navigate(target);
                return;
            }

            // Handle projectCBT JSON messages from LouisVoxExamApp bridge
            if (msg.StartsWith("{") && msg.EndsWith("}"))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(msg);
                var root = doc.RootElement;
                if (root.TryGetProperty("handler", out var handlerProp))
                {
                    string handler = handlerProp.GetString() ?? "";
                    switch (handler)
                    {
                        case "CBT_CONFIG":
                            int maxStrikes = root.TryGetProperty("maxStrikes", out var ms) ? ms.GetInt32() : 3;
                            int strikes = root.TryGetProperty("strikes", out var s) ? s.GetInt32() : 0;
                            bool isLocked = root.TryGetProperty("isLocked", out var l) && l.GetBoolean();
                            Log.Information("projectCBT Config Synced: maxStrikes={Max}, currentStrikes={Strikes}, locked={Locked}", maxStrikes, strikes, isLocked);
                            if (isLocked)
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    SetWebOverlayActive(true);
                                    lockScreen.ShowLock("Sesi ujian berstatus terkunci di server CBT.");
                                });
                            }
                            break;

                        case "CBT_VIOLATION":
                            string vType = root.TryGetProperty("type", out var vt) ? vt.GetString() ?? "" : "";
                            string vMsg = root.TryGetProperty("message", out var vm) ? vm.GetString() ?? "" : "Pelanggaran ujian terdeteksi.";
                            int vStrikes = root.TryGetProperty("strikes", out var vs) ? vs.GetInt32() : 1;
                            bool vLocked = root.TryGetProperty("isLocked", out var vl) && vl.GetBoolean();
                            Log.Warning("projectCBT Violation Event: Type={Type}, Message={Msg}, Strikes={Strikes}, Locked={Locked}", vType, vMsg, vStrikes, vLocked);
                            Dispatcher.Invoke(() =>
                            {
                                SetWebOverlayActive(true);
                                if (vLocked)
                                {
                                    lockScreen.ShowLock(vMsg);
                                }
                                else
                                {
                                    warningOverlay.ShowWarning(vMsg, vStrikes);
                                }
                            });
                            break;

                        case "CBT_UNLOCKED":
                            Log.Information("projectCBT Remote Unlock: Proctor unlocked student session via web dashboard.");
                            Dispatcher.Invoke(() =>
                            {
                                lockScreen.RemoteUnlock();
                                OnLockScreenUnlocked();
                            });
                            break;

                        case "CBT_FINISHED":
                            Log.Information("projectCBT Finished: Exam submitted successfully. Initiating completion countdown.");
                            Dispatcher.Invoke(() =>
                            {
                                completionOverlay.ShowCompletion(_config.Navigation.ExitUrlAutoCloseSeconds);
                            });
                            break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error handling WebMessage from WebView2");
        }
    }

    private void SetWebOverlayActive(bool active)
    {
        try
        {
            string js = active
                ? "window.__exambro_overlay_active = true; window.__louisvox_suppress_blur = true;"
                : "window.__exambro_overlay_active = false; window.__louisvox_suppress_blur = false;";
            _ = webView.CoreWebView2?.ExecuteScriptAsync(js);
        }
        catch
        {
            // best effort
        }
    }

    private void NotifyWebAntiCheatViolation(string type, string reason)
    {
        try
        {
            string sanitized = reason.Replace("'", "\\'").Replace("\r", "").Replace("\n", " ");
            string js = $"if (typeof window.__cbtReportViolation === 'function') {{ window.__cbtReportViolation('{type}', {{ reason: '{sanitized}' }}); }}";
            _ = webView.CoreWebView2?.ExecuteScriptAsync(js);
        }
        catch
        {
            // best effort
        }
    }

    private void NotifyWebAntiCheatSevereViolation(string reason)
    {
        try
        {
            string sanitized = reason.Replace("'", "\\'").Replace("\r", "").Replace("\n", " ");
            string js = $"if (typeof window.__cbtReportSevereViolation === 'function') {{ window.__cbtReportSevereViolation('{sanitized}'); }}";
            _ = webView.CoreWebView2?.ExecuteScriptAsync(js);
        }
        catch
        {
            // best effort
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
        SetWebOverlayActive(true);
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
            SetWebOverlayActive(false);
            _focusGuard?.SetSuspended(false);
            _focusGuard?.ReclaimFocus();
        }
    }

    private void OnLockScreenUnlocked()
    {
        SetWebOverlayActive(false);
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
