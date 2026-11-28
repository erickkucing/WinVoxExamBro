using System.Collections.Concurrent;
using ExamBro.Core.UI;
using Xunit;

namespace ExamBro.Tests.UI;

/// <summary>
/// Regression &amp; concurrency tests for <see cref="ExamOverlayCoordinator"/> verifying that
/// WPF airspace constraints, TopBar interactivity, reopened session dismissal, and overlapping
/// overlay suppression remain consistent across all transitions.
/// </summary>
public sealed class ExamOverlayCoordinatorTests
{
    private const string ExitPattern = "*/exam/finished/*";

    [Fact]
    public void CompletionOverlay_ReopenedSessionRedirectToWorkspace_DismissesOverlayAndRestoresWebView()
    {
        var coordinator = new ExamOverlayCoordinator();

        var completed = coordinator.OnNavigationCompleted(
            "http://192.168.70.9/exam/finished/session-1",
            isSuccess: true,
            ExitPattern);

        Assert.True(completed.IsCompletionVisible);
        Assert.False(completed.IsWebViewVisible);
        Assert.True(completed.IsTopBarInteractive);
        Assert.True(completed.ShouldSuppressWebBlur);

        // User clicks Reload on finished URL -> initial navigation request is still /exam/finished/*
        var startingFinished = coordinator.OnNavigationStarting(
            "http://192.168.70.9/exam/finished/session-1",
            ExitPattern);
        Assert.True(startingFinished.IsCompletionVisible);

        // Server sees proctor reopened session -> 302 redirects to /exam/workspace/session-1
        var redirectedStarting = coordinator.OnNavigationStarting(
            "http://192.168.70.9/exam/workspace/session-1",
            ExitPattern);
        Assert.False(redirectedStarting.IsCompletionVisible);
        Assert.True(redirectedStarting.IsWebViewVisible);
        Assert.True(redirectedStarting.IsTopBarInteractive);
        Assert.False(redirectedStarting.ShouldSuppressWebBlur);

        var settled = coordinator.OnNavigationCompleted(
            "http://192.168.70.9/exam/workspace/session-1",
            isSuccess: true,
            ExitPattern);
        Assert.False(settled.IsCompletionVisible);
        Assert.True(settled.IsWebViewVisible);
    }

    [Fact]
    public void CompletionOverlay_WebConfigOnUnlockedWorkspace_ClearsStaleCompletionOverlay()
    {
        var coordinator = new ExamOverlayCoordinator();
        coordinator.ShowCompletion();

        var synced = coordinator.OnWebExamConfigReceived(
            isSessionLocked: false,
            currentUri: "http://192.168.70.9/exam/workspace/abc",
            exitUrlPattern: ExitPattern);

        Assert.False(synced.IsCompletionVisible);
        Assert.True(synced.IsWebViewVisible);
        Assert.False(synced.ShouldSuppressWebBlur);
    }

    [Fact]
    public void WarningAndInitErrorOverlays_HideWebViewButKeepTopBarInteractive()
    {
        var coordinator = new ExamOverlayCoordinator();

        var warn = coordinator.ShowWarning();
        Assert.True(warn.IsWarningVisible);
        Assert.False(warn.IsWebViewVisible);
        Assert.True(warn.IsTopBarInteractive);
        Assert.True(warn.ShouldSuppressWebBlur);

        var dismissedWarn = coordinator.HideWarning();
        Assert.True(dismissedWarn.IsWebViewVisible);
        Assert.True(dismissedWarn.IsTopBarInteractive);
        Assert.False(dismissedWarn.ShouldSuppressWebBlur);

        var err = coordinator.ShowInitError();
        Assert.True(err.IsInitErrorVisible);
        Assert.False(err.IsWebViewVisible);
        Assert.True(err.IsTopBarInteractive);
    }

    [Fact]
    public void LockScreen_CoversTopBarAndSuppressesStaleWarningAndExitCancelRaces()
    {
        var coordinator = new ExamOverlayCoordinator();

        // 1. Warning opens -> escalated to LockScreen
        coordinator.ShowWarning();
        var locked = coordinator.ShowLockScreen();
        Assert.False(locked.IsWarningVisible);
        Assert.True(locked.IsLockScreenVisible);
        Assert.False(locked.IsWebViewVisible);
        Assert.False(locked.IsTopBarInteractive);
        Assert.True(locked.ShouldSuppressWebBlur);

        // 2. Late warning timer tick fires Dismiss -> must NOT unmask WebView or blur guard
        var afterLateWarningDismiss = coordinator.HideWarning();
        Assert.True(afterLateWarningDismiss.IsLockScreenVisible);
        Assert.False(afterLateWarningDismiss.IsWebViewVisible);
        Assert.True(afterLateWarningDismiss.ShouldSuppressWebBlur);

        // 3. Proctor opens ExitDialog then clicks Cancel -> LockScreen remains active & suppressed
        coordinator.SetExitDialogOpen(true);
        var afterExitCancel = coordinator.SetExitDialogOpen(false);
        Assert.True(afterExitCancel.IsLockScreenVisible);
        Assert.False(afterExitCancel.IsWebViewVisible);
        Assert.False(afterExitCancel.IsTopBarInteractive);
        Assert.True(afterExitCancel.ShouldSuppressWebBlur);

        // 4. Proctor unlocks -> WebView and TopBar both restore cleanly
        var unlocked = coordinator.HideLockScreen();
        Assert.False(unlocked.IsLockScreenVisible);
        Assert.True(unlocked.IsWebViewVisible);
        Assert.True(unlocked.IsTopBarInteractive);
        Assert.False(unlocked.ShouldSuppressWebBlur);
    }

    [Fact]
    public void FailedOrBlankNavigations_PreserveCompletionAndErrorStates()
    {
        var coordinator = new ExamOverlayCoordinator();
        coordinator.ShowCompletion();

        var blank = coordinator.OnNavigationStarting("about:blank", ExitPattern);
        Assert.True(blank.IsCompletionVisible);

        var failed = coordinator.OnNavigationCompleted("http://192.168.70.9/exam/workspace/s1", isSuccess: false, ExitPattern);
        Assert.True(failed.IsCompletionVisible);
    }

    [Fact]
    [Trait("Category", "Stress")]
    public void ConcurrentOverlayAndNavigationTransitions_AlwaysPreserveInvariants()
    {
        var coordinator = new ExamOverlayCoordinator();
        var exceptions = new ConcurrentQueue<Exception>();

        Parallel.For(0, 16, worker =>
        {
            try
            {
                for (int i = 0; i < 2000; i++)
                {
                    OverlayVisualState state = ((i + worker) % 10) switch
                    {
                        0 => coordinator.ShowWarning(),
                        1 => coordinator.HideWarning(),
                        2 => coordinator.ShowCompletion(),
                        3 => coordinator.HideCompletion(),
                        4 => coordinator.ShowLockScreen(),
                        5 => coordinator.HideLockScreen(),
                        6 => coordinator.SetExitDialogOpen((i & 1) == 0),
                        7 => coordinator.OnNavigationStarting("http://cbt.local/exam/workspace/1", ExitPattern),
                        8 => coordinator.OnNavigationCompleted("http://cbt.local/exam/finished/1", true, ExitPattern),
                        _ => coordinator.OnWebExamConfigReceived(false, "http://cbt.local/exam/workspace/1", ExitPattern)
                    };

                    bool expectedWebViewVisible =
                        !state.IsWarningVisible &&
                        !state.IsCompletionVisible &&
                        !state.IsInitErrorVisible &&
                        !state.IsLockScreenVisible;

                    Assert.Equal(expectedWebViewVisible, state.IsWebViewVisible);
                    Assert.Equal(!state.IsLockScreenVisible, state.IsTopBarInteractive);
                    Assert.Equal(!expectedWebViewVisible || state.IsExitDialogOpen, state.ShouldSuppressWebBlur);
                }
            }
            catch (Exception ex)
            {
                exceptions.Enqueue(ex);
            }
        });

        Assert.Empty(exceptions);
    }
}
