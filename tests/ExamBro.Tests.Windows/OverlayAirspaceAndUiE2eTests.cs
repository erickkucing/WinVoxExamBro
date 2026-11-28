using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ExamBro.Tests.Windows.Fixtures;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace ExamBro.Tests.Windows;

/// <summary>
/// E2E-13 &amp; E2E-14: Verifies WPF/WebView2 airspace separation (TopBar remains visible and interactive
/// above CompletionOverlay &amp; WarningOverlay, CompletionOverlay card is visible &amp; dismisses when
/// proctor reopens a finished session), and ExitDialog password field symmetry &amp; Escape cancel.
/// </summary>
[Collection("WindowsE2e")]
[Trait("Category", "WindowsOnly")]
[SupportedOSPlatform("windows")]
public sealed class OverlayAirspaceAndUiE2eTests
{
    private const uint WM_CLOSE = 0x0010;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private static async Task WaitForRequestAsync(FakeCbtServer server, string pathFragment, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (server.RequestedPaths.Any(p => p.Contains(pathFragment, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }
            await Task.Delay(200);
        }

        throw new TimeoutException($"FakeCbtServer did not receive '{pathFragment}' within {timeout.TotalSeconds}s.");
    }

    [Fact]
    public async Task E2E13_CompletionOverlay_KeepsTopBarInteractiveAndDismissesOnReopenReload()
    {
        await using var server = await FakeCbtServer.StartAsync();
        using var host = new ExamAppHost(preferDebug: true);

        var cfg = ExamAppHost.CreateE2eConfig($"{server.BaseUrl}/exam/finished/e2e-reopen", kioskMode: false, devSafeAllowedInRelease: true);
        cfg.Navigation.AllowReload = true;
        host.Configure(embeddedConfig: cfg);

        try
        {
            host.Launch(devSafe: true, enableCdp: true);
            await WaitForRequestAsync(server, "/exam/finished/e2e-reopen", TimeSpan.FromSeconds(25));

            var mainWin = await host.WaitForMainWindowAsync(TimeSpan.FromSeconds(15));

            // 1. CompletionOverlay must render unobstructed (btnExitNow visible in UIA tree)
            var exitBtn = mainWin.FindFirstDescendant(cf => cf.ByAutomationId("btnExitNow"));
            Assert.NotNull(exitBtn);
            Assert.False(exitBtn!.IsOffscreen);

            // 2. TopBar Reload button MUST remain visible, enabled, and clickable above CompletionOverlay
            var reloadBtn = mainWin.FindFirstDescendant(cf => cf.ByAutomationId("btnReload"));
            Assert.NotNull(reloadBtn);
            Assert.False(reloadBtn!.IsOffscreen);
            Assert.True(reloadBtn.IsEnabled);

            // 3. Simulate proctor reopening the session -> Reload triggers 302 redirect back to workspace
            server.ReopenFinishedSessions = true;
            reloadBtn.Click();

            await WaitForRequestAsync(server, "/exam/workspace/e2e-reopen", TimeSpan.FromSeconds(20));
            await Task.Delay(800);

            // 4. CompletionOverlay must automatically close once workspace settles
            var exitBtnAfterReopen = mainWin.FindFirstDescendant(cf => cf.ByAutomationId("btnExitNow"));
            Assert.True(exitBtnAfterReopen == null || exitBtnAfterReopen.IsOffscreen,
                "CompletionOverlay remained visible after session was reopened and redirected to workspace.");

            await using var cdp = await CdpClient.ConnectAsync(host.CdpPort!.Value, TimeSpan.FromSeconds(15));
            var suppressBlur = await cdp.EvaluateAsync("window.__exambro_overlay_active === true");
            Assert.NotEqual(System.Text.Json.JsonValueKind.True, suppressBlur.ValueKind);
        }
        catch
        {
            ExamAppHost.CaptureFailureScreenshot("E2E13_CompletionOverlay_Reopen");
            throw;
        }
    }

    [Fact]
    public async Task E2E14_ExitDialog_PasswordInputSymmetryFocusAndEscapeCancel()
    {
        await using var server = await FakeCbtServer.StartAsync();
        using var host = new ExamAppHost(preferDebug: true);

        var cfg = ExamAppHost.CreateE2eConfig($"{server.BaseUrl}/exam/workspace/e2e-14", kioskMode: false, devSafeAllowedInRelease: true);
        host.Configure(embeddedConfig: cfg);

        try
        {
            host.Launch(devSafe: true, enableCdp: true);
            await WaitForRequestAsync(server, "/exam/workspace/e2e-14", TimeSpan.FromSeconds(25));

            var mainWin = await host.WaitForMainWindowAsync(TimeSpan.FromSeconds(15));
            IntPtr hwnd = new IntPtr(mainWin.Properties.NativeWindowHandle.Value);

            SendMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            await Task.Delay(500);

            var automation = host.AttachUia();
            var exitWin = automation.GetDesktop().FindFirstChild(cf => cf.ByName("Konfirmasi Keluar Ujian"));
            Assert.NotNull(exitWin);

            var pwdBox = exitWin!.FindFirstDescendant(cf => cf.ByAutomationId("pwdExit"));
            Assert.NotNull(pwdBox);
            var rect = pwdBox!.BoundingRectangle;
            Assert.True(rect.Height >= 40, $"Expected pwdExit height >= 40px for centered cursor comfort, got {rect.Height}");
            Assert.True(pwdBox.IsEnabled);

            // Verify Escape cleanly cancels the ExitDialog without leaving __exambro_overlay_active stuck true
            Keyboard.Press(VirtualKeyShort.ESCAPE);
            await Task.Delay(400);

            var exitWinAfterEsc = automation.GetDesktop().FindFirstChild(cf => cf.ByName("Konfirmasi Keluar Ujian"));
            Assert.Null(exitWinAfterEsc);

            await using var cdp = await CdpClient.ConnectAsync(host.CdpPort!.Value, TimeSpan.FromSeconds(15));
            var overlayStillActive = await cdp.EvaluateAsync("window.__exambro_overlay_active === true");
            Assert.NotEqual(System.Text.Json.JsonValueKind.True, overlayStillActive.ValueKind);
        }
        catch
        {
            ExamAppHost.CaptureFailureScreenshot("E2E14_ExitDialog_UI");
            throw;
        }
    }
}
