using ExamBro.Core.Validation;

namespace ExamBro.Core.UI;

/// <summary>
/// Immutable snapshot of all overlay and host visibility invariants in <c>ExamWindow</c>.
/// </summary>
public readonly record struct OverlayVisualState(
    bool IsWarningVisible,
    bool IsCompletionVisible,
    bool IsInitErrorVisible,
    bool IsLockScreenVisible,
    bool IsExitDialogOpen)
{
    /// <summary>
    /// Native WebView2 (HwndHost) must stay collapsed whenever any WPF overlay is visible
    /// inside the window so the Win32 child HWND cannot punch through WPF overlays.
    /// </summary>
    public bool IsWebViewVisible =>
        !IsWarningVisible &&
        !IsCompletionVisible &&
        !IsInitErrorVisible &&
        !IsLockScreenVisible;

    /// <summary>
    /// Top toolbar remains interactive above Warning, Completion, and InitError overlays,
    /// and is only covered when full-window proctor lockout is active.
    /// </summary>
    public bool IsTopBarInteractive => !IsLockScreenVisible;

    /// <summary>
    /// Suppresses web anti-cheat blur false positives whenever any native overlay or ExitDialog is active.
    /// </summary>
    public bool ShouldSuppressWebBlur =>
        IsWarningVisible ||
        IsCompletionVisible ||
        IsInitErrorVisible ||
        IsLockScreenVisible ||
        IsExitDialogOpen;
}

/// <summary>
/// Thread-safe state machine coordinating warning, lockout, init-error, completion,
/// and exit confirmation overlays in <c>ExamWindow</c> across navigation and bridge updates.
/// </summary>
public sealed class ExamOverlayCoordinator
{
    private readonly object _lock = new();
    private bool _isWarningVisible;
    private bool _isCompletionVisible;
    private bool _isInitErrorVisible;
    private bool _isLockScreenVisible;
    private bool _isExitDialogOpen;

    public OverlayVisualState CurrentState
    {
        get
        {
            lock (_lock)
            {
                return SnapshotUnsafe();
            }
        }
    }

    public OverlayVisualState ShowWarning()
    {
        lock (_lock)
        {
            _isWarningVisible = true;
            return SnapshotUnsafe();
        }
    }

    public OverlayVisualState HideWarning()
    {
        lock (_lock)
        {
            _isWarningVisible = false;
            return SnapshotUnsafe();
        }
    }

    public OverlayVisualState ShowCompletion()
    {
        lock (_lock)
        {
            _isCompletionVisible = true;
            return SnapshotUnsafe();
        }
    }

    public OverlayVisualState HideCompletion()
    {
        lock (_lock)
        {
            _isCompletionVisible = false;
            return SnapshotUnsafe();
        }
    }

    public OverlayVisualState ShowLockScreen()
    {
        lock (_lock)
        {
            _isLockScreenVisible = true;
            _isWarningVisible = false;
            return SnapshotUnsafe();
        }
    }

    public OverlayVisualState HideLockScreen()
    {
        lock (_lock)
        {
            _isLockScreenVisible = false;
            return SnapshotUnsafe();
        }
    }

    public OverlayVisualState ShowInitError()
    {
        lock (_lock)
        {
            _isInitErrorVisible = true;
            return SnapshotUnsafe();
        }
    }

    public OverlayVisualState HideInitError()
    {
        lock (_lock)
        {
            _isInitErrorVisible = false;
            return SnapshotUnsafe();
        }
    }

    public OverlayVisualState SetExitDialogOpen(bool open)
    {
        lock (_lock)
        {
            _isExitDialogOpen = open;
            return SnapshotUnsafe();
        }
    }

    /// <summary>
    /// Updates overlay state when WebView2 begins navigating.
    /// Does not activate completion overlay until navigation settles, and dismisses any stale
    /// completion overlay if navigating to a non-exit page (e.g., reopened session redirect).
    /// </summary>
    public OverlayVisualState OnNavigationStarting(string? uri, string? exitUrlPattern)
    {
        lock (_lock)
        {
            if (IsMeaningfulHttpUrl(uri) && !UrlMatcher.MatchesExitUrl(uri!, exitUrlPattern))
            {
                _isCompletionVisible = false;
                _isInitErrorVisible = false;
            }

            return SnapshotUnsafe();
        }
    }

    /// <summary>
    /// Reconciles completion overlay state after navigation settles on a final URI.
    /// Activates completion overlay when settled on the exit URL and clears it on active exam routes.
    /// </summary>
    public OverlayVisualState OnNavigationCompleted(string? finalUri, bool isSuccess, string? exitUrlPattern)
    {
        lock (_lock)
        {
            if (!isSuccess || !IsMeaningfulHttpUrl(finalUri))
            {
                return SnapshotUnsafe();
            }

            if (UrlMatcher.MatchesExitUrl(finalUri!, exitUrlPattern))
            {
                _isCompletionVisible = true;
            }
            else
            {
                _isCompletionVisible = false;
                _isInitErrorVisible = false;
            }

            return SnapshotUnsafe();
        }
    }

    /// <summary>
    /// Reconciles overlay state when the projectCBT anti-cheat config bridge reports session state.
    /// Clears stale completion overlays when an active unlocked workspace loads.
    /// </summary>
    public OverlayVisualState OnWebExamConfigReceived(bool isSessionLocked, string? currentUri, string? exitUrlPattern)
    {
        lock (_lock)
        {
            if (isSessionLocked)
            {
                _isLockScreenVisible = true;
                _isWarningVisible = false;
            }
            else if (string.IsNullOrWhiteSpace(currentUri) || !UrlMatcher.MatchesExitUrl(currentUri, exitUrlPattern))
            {
                _isCompletionVisible = false;
            }

            return SnapshotUnsafe();
        }
    }

    private static bool IsMeaningfulHttpUrl(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return false;

        if (uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
            return false;

        return uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    private OverlayVisualState SnapshotUnsafe() =>
        new(_isWarningVisible, _isCompletionVisible, _isInitErrorVisible, _isLockScreenVisible, _isExitDialogOpen);
}
