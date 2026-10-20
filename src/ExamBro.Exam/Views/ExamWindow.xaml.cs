using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using ExamBro.Core;
using ExamBro.Core.Models;
using ExamBro.Core.Network;
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

        Title = _viewModel.AppTitle;
        if (_viewModel.LogoImage != null)
        {
            Icon = _viewModel.LogoImage;
        }

        // Wire ViewModel Commands
        _viewModel.GoBackRequested += OnGoBackRequested;
        _viewModel.GoForwardRequested += OnGoForwardRequested;
        _viewModel.ReloadRequested += OnReloadRequested;
        _viewModel.GoHomeRequested += OnGoHomeRequested;
        _viewModel.ZoomFactorChangeRequested += OnZoomFactorChangeRequested;
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

        _viewModel.PingService.UpdateTargetFromUrl(_resolvedStartUrl);
        _viewModel.PingService.PingUpdated += OnPingStatusChanged;

        // 2. Initialize Security Services
        InitializeSecurityServices();

        // 3. Initialize WebView2 Browser Engine
        await InitializeWebView2Async();
    }

    private void OnPingStatusChanged(PingStatus status)
    {
        bool toleranceActive = _viewModel.PingService.TolerancePolicy.IsToleranceActive();
        SetWebNetworkTolerance(toleranceActive);
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
                NotifyWebAntiCheatViolation("window_blur", $"Kombinasi tombol terlarang: {desc}");
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
            _focusGuard.DesktopSwitched += () =>
            {
                _violationManager.RecordViolation(ViolationEventType.BlockedKey, "Kombinasi Ctrl + Alt + Delete (SAS Desktop Switch)");
                NotifyWebAntiCheatViolation("window_blur", "Kombinasi tombol terlarang: Ctrl + Alt + Delete");
            };
            _focusGuard.FocusLost += () =>
            {
                // Reclaim focus silently without penalizing the student or recording a violation
                Log.Information("Focus lost to foreign window, reclaiming focus silently without penalty.");
            };
            _focusGuard.Start(_hwnd);
        }

        // Background Process Scanner
        _processMonitor = new ProcessMonitorService(_config);
        _processMonitor.BlockedProcessDetected += (name, pid) =>
        {
            Dispatcher.Invoke(() =>
            {
                _violationManager.RecordViolation(ViolationEventType.BlockedProcess, $"{name} (PID: {pid})");
                NotifyWebAntiCheatSevereViolation($"Aplikasi terlarang terdeteksi di Windows: {name} (PID: {pid})");
            });
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
        string dataFolder = Constants.WebView2DataFolder;
        // Valid Chromium arguments only (NO --inprivate or --user-agent-product which cause E_INVALIDARG)
        string additionalArgs = "--disable-session-crashed-bubble --hide-crash-restore-bubble --no-recovery-component --disable-features=Translate,OptimizationHints,RestoreTabs --ignore-certificate-errors --ignore-certificate-errors-spki-list --allow-insecure-localhost";

        try
        {
            // Set dark background color immediately so webView never renders as a blinding white rectangle
            webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 15, 23, 42);
        }
        catch { }

        try
        {
            // Clean stale lock files from sudden computer shutdown / power cut
            string singletonLock = Path.Combine(dataFolder, "SingletonLock");
            if (File.Exists(singletonLock))
            {
                try { File.Delete(singletonLock); } catch { }
            }
        }
        catch { }

        bool initialized = false;
        try
        {
            Log.Information("Initializing WebView2 browser environment with projectCBT synchronization...");
            var envOptions = new CoreWebView2EnvironmentOptions(additionalArgs);
            var env = await CoreWebView2Environment.CreateAsync(null, dataFolder, envOptions);
            await webView.EnsureCoreWebView2Async(env);
            initialized = true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Initial WebView2 environment creation failed. Purging crash artifacts from sudden shutdown and retrying...");
            try
            {
                if (Directory.Exists(dataFolder))
                {
                    try { Directory.Delete(dataFolder, recursive: true); } catch { }
                }

                var envOptions = new CoreWebView2EnvironmentOptions(additionalArgs);
                var env = await CoreWebView2Environment.CreateAsync(null, dataFolder, envOptions);
                await webView.EnsureCoreWebView2Async(env);
                initialized = true;
            }
            catch (Exception retryEx)
            {
                Log.Error(retryEx, "Fatal failure initializing WebView2 engine after crash recovery");
                ShowErrorPage();
                return;
            }
        }

        if (!initialized || webView.CoreWebView2 == null)
        {
            Log.Error("WebView2 failed to initialize CoreWebView2");
            ShowErrorPage();
            return;
        }

        try
        {
            ConfigureWebViewSettings();
            AttachWebViewEvents();

            // Inject projectCBT JavaScript bridge before document creation
            await InjectProjectCbtBridgeAsync();

            // Apply CBT Audio volume and mute settings
            await ApplyAudioSettingsAsync();

            // Clear cache and cookies for privacy isolation asynchronously without blocking navigation
            try
            {
                _ = webView.CoreWebView2.Profile.ClearBrowsingDataAsync();
            }
            catch { }

            Log.Information("Navigating to start URL: {Url}", _resolvedStartUrl);
            webView.CoreWebView2.Navigate(_resolvedStartUrl);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed configuring WebView2 after initialization");
            ShowErrorPage();
        }
    }

    private async Task InjectProjectCbtBridgeAsync()
    {
        string bridgeScript = @"
(function() {
    window.__louisvox_suppress_blur = false;
    window.__louisvox_network_unstable = false;
    window.__exambro_overlay_active = false;
    window.__cbt_lightbox_active = false;
    window.__cbt_last_unstable_time = 0;

    // Pantau event status koneksi browser untuk toleransi tinggi
    window.addEventListener('offline', function() {
        window.__louisvox_network_unstable = true;
        window.__louisvox_suppress_blur = true;
        window.__cbt_last_unstable_time = Date.now();
    });
    window.addEventListener('online', function() {
        window.__cbt_last_unstable_time = Date.now();
        setTimeout(function() {
            if (typeof navigator !== 'undefined' && navigator.onLine && !window.__louisvox_network_unstable) {
                window.__louisvox_suppress_blur = false;
            }
        }, 15000);
    });

    try {
        const origGetAttr = Element.prototype.getAttribute;
        Element.prototype.getAttribute = function(name) {
            if (name === 'data-is-exambrowser' && this && this.id === 'cbt-anti-cheat-root') {
                return '1';
            }
            return origGetAttr.apply(this, arguments);
        };
    } catch(e) {}

    try {
        const fsElemGetter = {
            get: function() { return document.documentElement; },
            configurable: true
        };
        Object.defineProperty(document, 'fullscreenElement', fsElemGetter);
        Object.defineProperty(document, 'webkitFullscreenElement', fsElemGetter);
        Object.defineProperty(document, 'mozFullScreenElement', fsElemGetter);
    } catch(e) {}

    try {
        const resolveFs = function() {
            try {
                const evt = new Event('fullscreenchange');
                document.dispatchEvent(evt);
            } catch(e) {}
            return Promise.resolve();
        };
        Element.prototype.requestFullscreen = resolveFs;
        Element.prototype.webkitRequestFullscreen = resolveFs;
        Element.prototype.mozRequestFullScreen = resolveFs;
        Element.prototype.msRequestFullscreen = resolveFs;
    } catch(e) {}

    function dismissFullscreenGate() {
        const root = document.getElementById('cbt-anti-cheat-root');
        if (root) {
            root.setAttribute('data-is-exambrowser', '1');
        }
        const gate = document.getElementById('anti-cheat-fullscreen-gate');
        if (gate) {
            gate.classList.add('hidden');
            gate.classList.remove('flex');
            gate.style.display = 'none';
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', dismissFullscreenGate);
    } else {
        dismissFullscreenGate();
    }
    setInterval(dismissFullscreenGate, 500);

    // Intercept fetch untuk toleransi jaringan putus sementara dan auto-retry penyimpanan jawaban
    try {
        const origFetch = window.fetch;
        window.fetch = async function(input, init) {
            const url = typeof input === 'string' ? input : (input && input.url ? input.url : '');

            // 1. Intercept pelaporan pelanggaran saat jaringan tidak stabil / putus sementara
            if (url.includes('/violation')) {
                const now = Date.now();
                const isUnstable = (typeof navigator !== 'undefined' && !navigator.onLine) ||
                                   window.__louisvox_suppress_blur === true ||
                                   window.__louisvox_network_unstable === true ||
                                   (now - (window.__cbt_last_unstable_time || 0) < 15000);

                if (isUnstable) {
                    let isSensitive = true;
                    try {
                        if (init && init.body) {
                            const parsed = JSON.parse(init.body);
                            const vt = parsed.violation_type;
                            if (vt === 'severe_unpin' || parsed.metadata?.is_unpin) {
                                isSensitive = false;
                            }
                        }
                    } catch(e) {}

                    if (isSensitive) {
                        return new Response(JSON.stringify({
                            action: 'ignored',
                            message: 'Toleransi jaringan tidak stabil / putus sementara aktif.',
                            is_locked: false
                        }), {
                            status: 200,
                            headers: { 'Content-Type': 'application/json' }
                        });
                    }
                }
            }

            // 2. Proteksi penyimpanan jawaban: auto-retry jika gagal karena gangguan koneksi
            if (url.includes('/answer')) {
                try {
                    return await origFetch.apply(this, arguments);
                } catch (err) {
                    window.__louisvox_network_unstable = true;
                    window.__cbt_last_unstable_time = Date.now();
                    const retryArgs = arguments;
                    setTimeout(async () => {
                        for (let attempt = 0; attempt < 5; attempt++) {
                            try {
                                await new Promise(r => setTimeout(r, 1000 * Math.pow(2, attempt)));
                                await origFetch.apply(window, retryArgs);
                                break;
                            } catch(re) {}
                        }
                    }, 1000);
                    throw err;
                }
            }

            return origFetch.apply(this, arguments);
        };
    } catch(e) {}

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
            const now = Date.now();
            const isRecentUnstable = (now - (window.__cbt_last_unstable_time || 0)) < 15000;
            const isOffline = typeof navigator !== 'undefined' && !navigator.onLine;
            return window.__exambro_overlay_active === true ||
                   window.__louisvox_suppress_blur === true ||
                   window.__louisvox_network_unstable === true ||
                   isRecentUnstable ||
                   isOffline;
        },
        setLightboxActive: function(active) {
            window.__cbt_lightbox_active = !!active;
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
        Log.Information("Injected LouisVoxExamApp JavaScript bridge for projectCBT with high network tolerance.");
    }

    private async Task ApplyAudioSettingsAsync()
    {
        if (webView?.CoreWebView2 == null)
            return;

        try
        {
            bool isMuted = _config.Audio?.MuteAudio == true || (_config.Audio?.VolumePercent ?? 100) <= 0;
            webView.CoreWebView2.IsMuted = isMuted;

            int volPercent = Math.Clamp(_config.Audio?.VolumePercent ?? 100, 0, 100);
            double volumeRatio = volPercent / 100.0;
            string volStr = volumeRatio.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            string mutedStr = isMuted ? "true" : "false";

            string audioScript = $@"
(function() {{
    const targetVolume = {volStr};
    const isMuted = {mutedStr};

    function enforceVolume(el) {{
        try {{
            if (el) {{
                el.volume = targetVolume;
                el.muted = isMuted;
            }}
        }} catch(e) {{}}
    }}

    const origPlay = HTMLMediaElement.prototype.play;
    HTMLMediaElement.prototype.play = function() {{
        enforceVolume(this);
        return origPlay.apply(this, arguments);
    }};

    document.addEventListener('play', function(e) {{
        if (e.target && e.target instanceof HTMLMediaElement) {{
            enforceVolume(e.target);
        }}
    }}, true);

    function applyAllMedia() {{
        const allMedia = document.querySelectorAll('audio, video');
        for (let i = 0; i < allMedia.length; i++) {{
            enforceVolume(allMedia[i]);
        }}
    }}

    if (document.readyState === 'loading') {{
        document.addEventListener('DOMContentLoaded', applyAllMedia);
    }} else {{
        applyAllMedia();
    }}
}})();";

            await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(audioScript);
            Log.Information("Applied CBT audio configuration: Muted={IsMuted}, Volume={VolumePercent}%", isMuted, volPercent);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to apply CBT audio settings to WebView2");
        }
    }

    private void ConfigureWebViewSettings()
    {
        var settings = webView.CoreWebView2.Settings;

        settings.AreDevToolsEnabled = _isDevSafe;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;

        try
        {
            string suffix = Constants.UserAgentSuffix.Trim();
            string currentUa = settings.UserAgent;
            string baseUa = string.IsNullOrWhiteSpace(currentUa)
                ? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Edg/120.0.0.0"
                : currentUa;

            if (!baseUa.Contains("ExamBrowser", StringComparison.OrdinalIgnoreCase))
            {
                settings.UserAgent = $"{baseUa} {suffix}";
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to customize UserAgent in settings");
        }
    }

    private void AttachWebViewEvents()
    {
        var core = webView.CoreWebView2;

        core.ContainsFullScreenElementChanged += OnContainsFullScreenElementChanged;
        core.ServerCertificateErrorDetected += OnServerCertificateErrorDetected;
        core.NavigationStarting += OnNavigationStarting;
        core.FrameNavigationStarting += OnFrameNavigationStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.DownloadStarting += OnDownloadStarting;
        core.PermissionRequested += OnPermissionRequested;
        core.ProcessFailed += OnProcessFailed;
        core.WebMessageReceived += OnWebMessageReceived;
        core.NavigationCompleted += OnNavigationCompleted;
        webView.ZoomFactorChanged += OnWebViewZoomFactorChanged;
    }

    private void OnContainsFullScreenElementChanged(object? sender, object e)
    {
        Dispatcher.Invoke(() =>
        {
            if (Mouse.Captured != null)
            {
                Mouse.Capture(null);
            }
        });
    }

    private void OnServerCertificateErrorDetected(object? sender, CoreWebView2ServerCertificateErrorDetectedEventArgs e)
    {
        Log.Warning("Server certificate warning automatically bypassed on {Uri} ({Status})", e.RequestUri, e.ErrorStatus);
        e.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
    }

    // WebView2 Event Handlers

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
            _ => true
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

    private void SetWebNetworkTolerance(bool active)
    {
        try
        {
            string js = active
                ? "window.__louisvox_network_unstable = true; window.__louisvox_suppress_blur = true; window.__cbt_last_unstable_time = Date.now();"
                : "window.__louisvox_network_unstable = false;";
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

        if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled
            && e.WebErrorStatus != CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect
            && e.WebErrorStatus != CoreWebView2WebErrorStatus.CertificateExpired
            && e.WebErrorStatus != CoreWebView2WebErrorStatus.CertificateRevoked
            && e.WebErrorStatus != CoreWebView2WebErrorStatus.CertificateIsInvalid)
        {
            Log.Warning("Navigation completed with failure status: {Status}", e.WebErrorStatus);

            // Toleransi tinggi: Jika siswa berada dalam lembar ujian (/exam/workspace),
            // jangan hapus DOM lembar soal dengan ErrorPage saat koneksi bermasalah sementara!
            string currentUrl = webView.Source?.ToString() ?? _lastValidUrl;
            if (_viewModel.PingService.TolerancePolicy.ShouldPreventErrorPageNavigation(currentUrl))
            {
                Log.Warning("Suppressing ErrorPage navigation during transient network drop/instability to protect exam workspace.");
                SetWebNetworkTolerance(true);
                return;
            }

            ShowErrorPage();
        }
    }

    private void ShowErrorPage()
    {
        try
        {
            if (webView?.CoreWebView2 != null)
            {
                string html = GetErrorPageHtml();
                webView.NavigateToString(html);
            }
            else
            {
                pnlWebViewInitError.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to render error page");
            pnlWebViewInitError.Visibility = Visibility.Visible;
        }
    }

    private void BtnInitErrorExit_Click(object sender, RoutedEventArgs e)
    {
        CloseGracefully();
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

    protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseUp(e);
        if (Mouse.Captured != null)
        {
            Mouse.Capture(null);
        }
    }

    private void Toolbar_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (Mouse.Captured != null)
        {
            Mouse.Capture(null);
        }
    }

    private void Toolbar_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Mouse.Captured != null)
        {
            Mouse.Capture(null);
        }
    }

    // Navigation Command Callbacks

    private void OnGoBackRequested()
    {
        if (Mouse.Captured != null) Mouse.Capture(null);
        if (_config.Navigation.AllowBackForward && webView.CanGoBack)
        {
            webView.GoBack();
        }
    }

    private void OnGoForwardRequested()
    {
        if (Mouse.Captured != null) Mouse.Capture(null);
        if (_config.Navigation.AllowBackForward && webView.CanGoForward)
        {
            webView.GoForward();
        }
    }

    private void OnReloadRequested()
    {
        if (Mouse.Captured != null) Mouse.Capture(null);
        if (_config.Navigation.AllowReload)
        {
            webView.Reload();
        }
    }

    private void OnGoHomeRequested()
    {
        if (Mouse.Captured != null) Mouse.Capture(null);
        webView.CoreWebView2?.Navigate(_resolvedStartUrl);
    }

    private void OnZoomFactorChangeRequested(double zoom)
    {
        Dispatcher.Invoke(() =>
        {
            if (webView.CoreWebView2 != null)
            {
                webView.ZoomFactor = zoom;
            }
        });
    }

    private void OnWebViewZoomFactorChanged(object? sender, EventArgs e)
    {
        double current = webView.ZoomFactor;
        if (current < 0.50 || current > 1.50)
        {
            double clamped = Math.Clamp(current, 0.50, 1.50);
            webView.ZoomFactor = clamped;
            _viewModel.SetZoom(clamped);
        }
        else
        {
            _viewModel.SetZoom(current);
        }
    }

    private void OnWindowPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Lock scroll zoom: suppress wheel when Ctrl is pressed
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Fallback for dev mode where low-level keyboard hook is not installed
        if (_isDevSafe && (e.Key == Key.Escape || (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && (e.Key == Key.C || e.Key == Key.B))))
        {
            ShowExitConfirmation();
            e.Handled = true;
        }
    }

    // Proctor Exit & Lock Screen Handling

    private bool _isExitDialogOpen;

    private void ShowExitConfirmation()
    {
        if (_isExitDialogOpen) return;
        _isExitDialogOpen = true;

        if (Mouse.Captured != null) Mouse.Capture(null);
        _focusGuard?.SetSuspended(true);
        _keyboardHook?.SetSuspended(true);
        SetWebOverlayActive(true);
        try
        {
            var dialog = new ExitDialog(_config)
            {
                Owner = this,
                Topmost = true
            };

            if (dialog.ShowDialog() == true)
            {
                Log.Information("Authorized proctor exit initiated");
                CloseGracefully();
            }
        }
        finally
        {
            _isExitDialogOpen = false;
            if (Mouse.Captured != null) Mouse.Capture(null);
            SetWebOverlayActive(false);
            _keyboardHook?.SetSuspended(false);
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
        try { _viewModel.PingService.PingUpdated -= OnPingStatusChanged; } catch { }
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

        // Dispose WebView2 to terminate browser engine threads and child renderers
        try
        {
            webView.Stop();
            webView.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "WebView2 disposal error");
        }

        // Terminate any leftover ExamBro.Config sibling processes
        ProcessMonitorService.TerminateSiblingExamBroProcesses();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        ProcessMonitorService.TerminateSiblingExamBroProcesses();
        Application.Current?.Shutdown(0);
    }
}
