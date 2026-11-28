using ExamBro.Core.Validation;

namespace ExamBro.Core.Navigation;

/// <summary>
/// Synchronizes active UI overlay state across navigation redirects, reopened finished sessions,
/// warning countdowns, proctor lockouts, and modal exit dialogs so web blur-suppression flags
/// and completion overlays never drift out of sync.
/// </summary>
public sealed class ExamOverlayStateTracker
{
    public bool WarningVisible { get; private set; }
    public bool LockVisible { get; private set; }
    public bool CompletionVisible { get; private set; }
    public bool ExitDialogOpen { get; private set; }

    public bool IsWebOverlayActive => ExitDialogOpen || WarningVisible || LockVisible || CompletionVisible;

    public void SetExitDialogOpen(bool open)
    {
        ExitDialogOpen = open;
    }

    public void SetWarningVisible(bool visible)
    {
        WarningVisible = visible;
    }

    public void SetLockVisible(bool visible)
    {
        if (visible)
        {
            WarningVisible = false;
        }
        LockVisible = visible;
    }

    public void SetCompletionVisible(bool visible)
    {
        if (visible)
        {
            WarningVisible = false;
        }
        CompletionVisible = visible;
    }

    /// <summary>
    /// Updates completion overlay state when a real navigation begins.
    /// Does NOT show completion preemptively before HTTP redirect resolution (e.g. a reopened
    /// finished session redirecting from /exam/finished/* back to /exam/workspace/*).
    /// </summary>
    public void OnNavigationStarting(string? uri, string? exitPattern)
    {
        if (IsInternalUri(uri))
        {
            return;
        }

        if (!UrlMatcher.MatchesExitUrl(uri!, exitPattern))
        {
            CompletionVisible = false;
        }
    }

    /// <summary>
    /// Synchronizes completion overlay state with the final resolved navigation URI.
    /// </summary>
    public void OnNavigationCompleted(string? finalUri, bool isSuccess, string? exitPattern)
    {
        if (!isSuccess || IsInternalUri(finalUri))
        {
            return;
        }

        if (UrlMatcher.MatchesExitUrl(finalUri!, exitPattern))
        {
            SetCompletionVisible(true);
        }
        else
        {
            CompletionVisible = false;
        }
    }

    public void OnCbtConfigReceived(bool isLocked)
    {
        CompletionVisible = false;
        if (isLocked)
        {
            SetLockVisible(true);
        }
    }

    public void OnCbtFinished() => SetCompletionVisible(true);

    public void OnWindowCloseRequested() => SetCompletionVisible(true);

    /// <summary>
    /// Returns a URL to navigate to when Reload is pressed on an internal error/blank page
    /// (<c>about:blank</c> or <c>data:</c>), or <see langword="null"/> when standard reload applies.
    /// </summary>
    public static string? ResolveReloadTarget(string? currentSource, string? lastValidUrl, string? resolvedStartUrl)
    {
        if (string.IsNullOrWhiteSpace(currentSource) || IsInternalUri(currentSource))
        {
            string fallback = !string.IsNullOrWhiteSpace(lastValidUrl) ? lastValidUrl : (resolvedStartUrl ?? string.Empty);
            return string.IsNullOrWhiteSpace(fallback) ? null : fallback;
        }

        return null;
    }

    private static bool IsInternalUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return true;
        }

        return uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
               uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
    }
}
