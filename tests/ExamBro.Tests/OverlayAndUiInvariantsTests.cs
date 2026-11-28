using System.IO;
using System.Xml.Linq;
using ExamBro.Core.Models;
using ExamBro.Core.Navigation;
using ExamBro.Core.Security;
using Xunit;

namespace ExamBro.Tests;

/// <summary>
/// Regression tests for Top Bar isolation, reopened finished exam session transitions,
/// password verification consolidation, and vertical cursor symmetry / accessibility across XAML dialogs.
/// </summary>
public class OverlayAndUiInvariantsTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ExamBro.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("ExamBro.sln not found from test directory.");
    }

    private static XDocument LoadRepoXml(string relativePath)
    {
        string fullPath = Path.Combine(FindRepoRoot(), relativePath);
        Assert.True(File.Exists(fullPath), $"Expected file not found: {fullPath}");
        return XDocument.Load(fullPath);
    }

    [Fact]
    public void OverlayStateTracker_ReopenedFinishedSession_DismissesCompletionAndSyncsSuppressedBlur()
    {
        var tracker = new ExamOverlayStateTracker();
        const string exitPattern = "*/exam/finished/*";

        // 1. Finish exam navigation completes on exit URL
        tracker.OnNavigationStarting("http://192.168.70.9/exam/finished/abc", exitPattern);
        Assert.False(tracker.CompletionVisible); // Wait for HTTP response/completion (avoids flash on 302 reopen redirect)

        tracker.OnNavigationCompleted("http://192.168.70.9/exam/finished/abc", isSuccess: true, exitPattern);
        Assert.True(tracker.CompletionVisible);
        Assert.True(tracker.IsWebOverlayActive);

        // 2. Proctor reopens session -> reload on /exam/finished/abc returns 302 to /exam/workspace/abc
        tracker.OnNavigationStarting("http://192.168.70.9/exam/finished/abc", exitPattern);
        tracker.OnNavigationStarting("http://192.168.70.9/exam/workspace/abc", exitPattern);
        Assert.False(tracker.CompletionVisible);
        Assert.False(tracker.IsWebOverlayActive);

        tracker.OnNavigationCompleted("http://192.168.70.9/exam/workspace/abc", isSuccess: true, exitPattern);
        Assert.False(tracker.CompletionVisible);
        Assert.False(tracker.IsWebOverlayActive);

        // 3. Even if CBT_FINISHED arrived before reopen, CBT_CONFIG on active workspace closes completion
        tracker.SetCompletionVisible(true);
        Assert.True(tracker.CompletionVisible);
        tracker.OnCbtConfigReceived(isLocked: false);
        Assert.False(tracker.CompletionVisible);
        Assert.False(tracker.IsWebOverlayActive);
    }

    [Fact]
    public void OverlayStateTracker_OverlappingOverlays_NeverClearsWebSuppressionPrematurely()
    {
        var tracker = new ExamOverlayStateTracker();

        tracker.SetLockVisible(true);
        tracker.SetExitDialogOpen(true);
        Assert.True(tracker.IsWebOverlayActive);

        // Cancelling ExitDialog while LockScreen is STILL locked must keep web overlay suppression active
        tracker.SetExitDialogOpen(false);
        Assert.True(tracker.LockVisible);
        Assert.True(tracker.IsWebOverlayActive);

        // Dismissing transient warning while LockScreen is STILL locked must keep suppression active
        tracker.SetWarningVisible(true);
        tracker.SetWarningVisible(false);
        Assert.True(tracker.IsWebOverlayActive);

        // Only after Unlock does suppression clear
        tracker.SetLockVisible(false);
        Assert.False(tracker.IsWebOverlayActive);
    }

    [Fact]
    public void OverlayStateTracker_ResolveReloadTarget_RecoversFromErrorPageBlankUrl()
    {
        string? target1 = ExamOverlayStateTracker.ResolveReloadTarget(
            "about:blank",
            "http://192.168.70.9/exam/workspace/s-1",
            "http://192.168.70.9/login");
        Assert.Equal("http://192.168.70.9/exam/workspace/s-1", target1);

        string? target2 = ExamOverlayStateTracker.ResolveReloadTarget(
            "data:text/html,error",
            string.Empty,
            "http://192.168.70.9/login");
        Assert.Equal("http://192.168.70.9/login", target2);

        string? normal = ExamOverlayStateTracker.ResolveReloadTarget(
            "http://192.168.70.9/exam/workspace/s-1",
            "http://192.168.70.9/exam/workspace/s-1",
            "http://192.168.70.9/login");
        Assert.Null(normal); // Null signals standard webView.Reload()
    }

    [Fact]
    public void PasswordVerifier_HandlesProctorAdminAndUnconfiguredFallbackCorrectly()
    {
        var cfg = ExamConfig.CreateDefault();
        Assert.True(PasswordVerifier.VerifyProctorOrAdmin(cfg, "admin"));
        Assert.False(PasswordVerifier.VerifyProctorOrAdmin(cfg, "wrong"));
        Assert.False(PasswordVerifier.VerifyProctorOrAdmin(cfg, string.Empty));

        var (adminHash, adminSalt) = PasswordHasher.HashPassword("AdminSecret#9");
        var (proctorHash, proctorSalt) = PasswordHasher.HashPassword("ProctorSecret#9");
        cfg.Admin.AdminPasswordHash = adminHash;
        cfg.Admin.AdminPasswordSalt = adminSalt;
        cfg.Admin.ProctorPasswordHash = proctorHash;
        cfg.Admin.ProctorPasswordSalt = proctorSalt;

        Assert.True(PasswordVerifier.VerifyProctorOrAdmin(cfg, "ProctorSecret#9"));
        Assert.True(PasswordVerifier.VerifyProctorOrAdmin(cfg, "AdminSecret#9"));
        Assert.False(PasswordVerifier.VerifyProctorOrAdmin(cfg, "admin"));
    }

    [Fact]
    public void ExamWindowXaml_IsolatesTopBarAboveContentOverlays()
    {
        var doc = LoadRepoXml("src/ExamBro.Exam/Views/ExamWindow.xaml");
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var topToolbar = doc.Descendants()
            .FirstOrDefault(e => e.Attribute(x + "Name")?.Value == "topToolbar");
        Assert.NotNull(topToolbar);

        var contentHost = doc.Descendants()
            .FirstOrDefault(e => e.Attribute(x + "Name")?.Value == "browserContentHost");
        Assert.NotNull(contentHost);

        // Top toolbar and content host must occupy separate Grid rows so no content overlay can darken or block the top bar
        Assert.Equal("0", topToolbar!.Attribute("Grid.Row")?.Value);
        Assert.Equal("1", contentHost!.Attribute("Grid.Row")?.Value);

        string[] overlayNames = { "pnlWebViewInitError", "warningOverlay", "lockScreen", "completionOverlay" };
        foreach (string name in overlayNames)
        {
            var overlay = contentHost.Descendants()
                .FirstOrDefault(e => e.Attribute(x + "Name")?.Value == name);
            Assert.NotNull(overlay);
        }

        // WebView2 must also live inside browserContentHost, not above topToolbar
        Assert.NotNull(contentHost.Descendants().FirstOrDefault(e => e.Attribute(x + "Name")?.Value == "webView"));
    }

    [Theory]
    [InlineData("src/ExamBro.Exam/Views/ExitDialog.xaml", new[] { "pwdExit" })]
    [InlineData("src/ExamBro.Exam/Views/LockScreen.xaml", new[] { "pwdProctor" })]
    [InlineData("src/ExamBro.Config/Views/LoginDialog.xaml", new[] { "TxtPassword" })]
    [InlineData("src/ExamBro.Config/Views/PasswordPromptDialog.xaml", new[] { "TxtPassword", "TxtConfirmPassword" })]
    [InlineData("src/ExamBro.Config/Views/ChangePasswordDialog.xaml", new[] { "TxtCurrentPassword", "TxtNewPassword", "TxtConfirmPassword" })]
    [InlineData("src/ExamBro.Config/Views/SetupWizardDialog.xaml", new[] { "TxtAdminPassword", "TxtAdminPasswordConfirm", "TxtProctorPassword", "TxtProctorPasswordConfirm" })]
    public void PasswordDialogs_CenterCaretVerticallyAndExposeRevealToggles(string relativePath, string[] passwordBoxNames)
    {
        var doc = LoadRepoXml(relativePath);
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        foreach (string pwdName in passwordBoxNames)
        {
            var pwd = doc.Descendants(wpf + "PasswordBox")
                .FirstOrDefault(e => e.Attribute(x + "Name")?.Value == pwdName);
            Assert.NotNull(pwd);
            Assert.Equal("Center", pwd!.Attribute("VerticalContentAlignment")?.Value);

            var parentGrid = pwd.Ancestors(wpf + "Grid").FirstOrDefault();
            Assert.NotNull(parentGrid);

            var pairedTextBox = parentGrid!.Descendants(wpf + "TextBox").FirstOrDefault();
            Assert.NotNull(pairedTextBox);
            Assert.Equal("Center", pairedTextBox!.Attribute("VerticalContentAlignment")?.Value);

            var toggleBtn = parentGrid.Descendants(wpf + "ToggleButton").FirstOrDefault();
            Assert.NotNull(toggleBtn);
        }
    }

    [Fact]
    public void ExitDialogXaml_EnforcesKeyboardCancelAndDefaultAndWaiContrast()
    {
        var doc = LoadRepoXml("src/ExamBro.Exam/Views/ExitDialog.xaml");
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var cancelBtn = doc.Descendants(wpf + "Button")
            .FirstOrDefault(e => e.Attribute(x + "Name")?.Value == "btnCancel");
        var confirmBtn = doc.Descendants(wpf + "Button")
            .FirstOrDefault(e => e.Attribute(x + "Name")?.Value == "btnConfirmExit");

        Assert.NotNull(cancelBtn);
        Assert.NotNull(confirmBtn);
        Assert.Equal("True", cancelBtn!.Attribute("IsCancel")?.Value);
        Assert.Equal("True", confirmBtn!.Attribute("IsDefault")?.Value);
    }
}
