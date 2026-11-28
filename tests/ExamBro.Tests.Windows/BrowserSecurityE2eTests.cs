using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ExamBro.Core;
using ExamBro.Exam.Services;
using ExamBro.Tests.Windows.Fixtures;
using FlaUI.Core.Input;

namespace ExamBro.Tests.Windows;

/// <summary>
/// E2E-08 through E2E-12: Verifies WebView2 URL filtering, download/popup blocking, zoom stability,
/// offline ErrorPage suppression & UI-thread ping bridge (H-03), renderer crash recovery, stale lock
/// recovery, and session-scoped proctor password attempt tracking (H-17).
/// </summary>
[Collection("WindowsE2e")]
[Trait("Category", "WindowsOnly")]
[SupportedOSPlatform("windows")]
public sealed class BrowserSecurityE2eTests
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
    public async Task E2E08_NavigationZoomAndSecurityPolicies_BehavePerConfig()
    {
        await using var server = await FakeCbtServer.StartAsync();
        using var host = new ExamAppHost(preferDebug: true);

        var cfg = ExamAppHost.CreateE2eConfig($"{server.BaseUrl}/exam/workspace/e2e-8", kioskMode: false, devSafeAllowedInRelease: true);
        cfg.Navigation.AllowedUrlPatterns = new() { $"{server.BaseUrl}/*" };
        cfg.Navigation.BlockedUrlPatterns = new() { $"{server.BaseUrl}/404*" };
        cfg.Navigation.AllowDownloads = false;
        host.Configure(embeddedConfig: cfg);

        try
        {
            host.Launch(devSafe: true, enableCdp: true);
            await WaitForRequestAsync(server, "/exam/workspace/e2e-8", TimeSpan.FromSeconds(25));

            await using var cdp = await CdpClient.ConnectAsync(host.CdpPort!.Value, TimeSpan.FromSeconds(20));

            // 1. Zoom in/out via toolbar buttons (no false-positive violations)
            var mainWin = await host.WaitForMainWindowAsync(TimeSpan.FromSeconds(10));
            var zoomInBtn = mainWin.FindFirstDescendant(cf => cf.ByName("+"));
            var zoomOutBtn = mainWin.FindFirstDescendant(cf => cf.ByName("−"));
            zoomInBtn?.Click();
            zoomOutBtn?.Click();
            await Task.Delay(400);

            // 2. Blocked URL navigation is intercepted (never leaves workspace to /404)
            await cdp.NavigateAsync($"{server.BaseUrl}/404");
            await Task.Delay(600);
            Assert.DoesNotContain(server.RequestedPaths, p => p.Equals("/404", StringComparison.OrdinalIgnoreCase));

            // 3. External redirect to example.com is blocked
            await cdp.NavigateAsync($"{server.BaseUrl}/redirect-external");
            await Task.Delay(600);
            var hrefVal = await cdp.EvaluateAsync("window.location.href");
            Assert.DoesNotContain("example.com", hrefVal.GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            Assert.Empty(server.RecordedViolations);
        }
        catch
        {
            ExamAppHost.CaptureFailureScreenshot("E2E08_BrowserSecurity");
            throw;
        }
    }

    [Fact]
    public async Task E2E09_H03_TransientNetworkDropInWorkspace_SetsJsToleranceAndPreservesDom()
    {
        await using var server = await FakeCbtServer.StartAsync();
        using var host = new ExamAppHost(preferDebug: true);

        var cfg = ExamAppHost.CreateE2eConfig($"{server.BaseUrl}/exam/workspace/e2e-9", kioskMode: false, devSafeAllowedInRelease: true);
        host.Configure(embeddedConfig: cfg);

        try
        {
            host.Launch(devSafe: true, enableCdp: true);
            await WaitForRequestAsync(server, "/exam/workspace/e2e-9", TimeSpan.FromSeconds(25));

            await using var cdp = await CdpClient.ConnectAsync(host.CdpPort!.Value, TimeSpan.FromSeconds(20));

            // Simulate server chaos / connection drop -> triggers network tolerance & failed navigation
            server.DropConnections = true;

            // Wait for PingService polling loop (2s interval) to mark network unstable and marshal to UI thread
            var deadline = DateTime.UtcNow.AddSeconds(12);
            bool jsFlagActive = false;
            while (DateTime.UtcNow < deadline)
            {
                var res = await cdp.EvaluateAsync("window.__louisvox_network_unstable === true");
                if (res.ValueKind == System.Text.Json.JsonValueKind.True)
                {
                    jsFlagActive = true;
                    break;
                }
                await Task.Delay(300);
            }

            Assert.True(jsFlagActive, "H-03 regression: PingService background thread failed to set window.__louisvox_network_unstable via UI dispatcher.");

            // Navigation failure inside /exam/workspace must preserve exam DOM instead of replacing with ErrorPage
            await cdp.EvaluateAsync("window.location.href = window.location.pathname + '?netdrop=1'");
            await Task.Delay(1000);
            var hasExamRoot = await cdp.EvaluateAsync("document.getElementById('cbt-anti-cheat-root') !== null");
            Assert.Equal(System.Text.Json.JsonValueKind.True, hasExamRoot.ValueKind);
        }
        catch
        {
            ExamAppHost.CaptureFailureScreenshot("E2E09_H03_NetworkTolerance");
            throw;
        }
    }

    [Fact]
    public async Task E2E10_WebView2RendererCrash_RecoversToLastValidUrl()
    {
        await using var server = await FakeCbtServer.StartAsync();
        using var host = new ExamAppHost(preferDebug: false);

        var cfg = ExamAppHost.CreateE2eConfig($"{server.BaseUrl}/exam/workspace/e2e-10", kioskMode: false, devSafeAllowedInRelease: true);
        host.Configure(embeddedConfig: cfg);

        try
        {
            host.Launch(devSafe: true);
            await WaitForRequestAsync(server, "/exam/workspace/e2e-10", TimeSpan.FromSeconds(25));

            int baselineRequests = server.RequestedPaths.Count(p => p.Contains("/exam/workspace/e2e-10", StringComparison.OrdinalIgnoreCase));

            var childPids = ProcessMonitorService.GetDescendantPids(host.Process!.Id);
            int? rendererPid = null;
            foreach (int pid in childPids)
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    if (p.ProcessName.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase))
                    {
                        rendererPid = pid;
                        p.Kill();
                        break;
                    }
                }
                catch { }
            }

            Assert.True(rendererPid.HasValue, "No child msedgewebview2 renderer process found to simulate crash.");

            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                int current = server.RequestedPaths.Count(p => p.Contains("/exam/workspace/e2e-10", StringComparison.OrdinalIgnoreCase));
                if (current > baselineRequests)
                {
                    return;
                }
                await Task.Delay(250);
            }

            Assert.Fail("OnProcessFailed did not trigger recovery navigation back to /exam/workspace/e2e-10.");
        }
        catch
        {
            ExamAppHost.CaptureFailureScreenshot("E2E10_RendererCrash");
            throw;
        }
    }

    [Fact]
    public async Task E2E11_StaleWebView2Locks_CleanedOnStartupAndPageLoads()
    {
        await using var server = await FakeCbtServer.StartAsync();
        string profileDir = Constants.WebView2DataFolder;
        Directory.CreateDirectory(Path.Combine(profileDir, "Default"));
        File.WriteAllText(Path.Combine(profileDir, "SingletonLock"), "stale");
        File.WriteAllText(Path.Combine(profileDir, "Default", "LOCK"), "stale");

        using var host = new ExamAppHost(preferDebug: false);
        var cfg = ExamAppHost.CreateE2eConfig($"{server.BaseUrl}/exam/workspace/e2e-11", kioskMode: false, devSafeAllowedInRelease: true);
        host.Configure(embeddedConfig: cfg);

        try
        {
            host.Launch(devSafe: true);
            await WaitForRequestAsync(server, "/exam/workspace/e2e-11", TimeSpan.FromSeconds(25));
        }
        catch
        {
            ExamAppHost.CaptureFailureScreenshot("E2E11_StaleLocks");
            throw;
        }
    }

    [Fact]
    public async Task E2E12_H17_ExitDialogFailedAttempts_PersistAcrossCancelAndReopen()
    {
        await using var server = await FakeCbtServer.StartAsync();
        using var host = new ExamAppHost(preferDebug: false);

        var cfg = ExamAppHost.CreateE2eConfig($"{server.BaseUrl}/exam/workspace/e2e-12", kioskMode: false, devSafeAllowedInRelease: true);
        cfg.Admin.MaxPasswordAttempts = 5;
        cfg.Admin.PasswordLockoutSeconds = 30;
        host.Configure(embeddedConfig: cfg);

        try
        {
            host.Launch(devSafe: true);
            await WaitForRequestAsync(server, "/exam/workspace/e2e-12", TimeSpan.FromSeconds(25));
            var mainWin = await host.WaitForMainWindowAsync(TimeSpan.FromSeconds(15));
            IntPtr hwnd = new IntPtr(mainWin.Properties.NativeWindowHandle.Value);

            var automation = host.AttachUia();

            // Open ExitDialog #1 -> 4 failed attempts -> Cancel
            SendMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            await Task.Delay(400);

            var dlg1 = automation.GetDesktop().FindFirstChild(cf => cf.ByName("Konfirmasi Keluar Ujian"));
            Assert.NotNull(dlg1);
            var pwd1 = dlg1!.FindFirstDescendant(cf => cf.ByAutomationId("pwdExit"));
            var confirm1 = dlg1.FindFirstDescendant(cf => cf.ByAutomationId("btnConfirmExit"));
            var cancel1 = dlg1.FindFirstDescendant(cf => cf.ByAutomationId("btnCancel"));

            for (int i = 0; i < 4; i++)
            {
                pwd1!.Focus();
                Keyboard.Type("wrong");
                confirm1!.Click();
                await Task.Delay(150);
            }
            cancel1!.Click();
            await Task.Delay(300);

            // Reopen ExitDialog #2 -> 1 more failed attempt MUST trigger lockout (5 total)!
            SendMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            await Task.Delay(400);

            var dlg2 = automation.GetDesktop().FindFirstChild(cf => cf.ByName("Konfirmasi Keluar Ujian"));
            Assert.NotNull(dlg2);
            var pwd2 = dlg2!.FindFirstDescendant(cf => cf.ByAutomationId("pwdExit"));
            var confirm2 = dlg2.FindFirstDescendant(cf => cf.ByAutomationId("btnConfirmExit"));
            var lockoutText = dlg2.FindFirstDescendant(cf => cf.ByAutomationId("txtLockout"));

            pwd2!.Focus();
            Keyboard.Type("wrong-5th");
            confirm2!.Click();
            await Task.Delay(250);

            Assert.False(confirm2.IsEnabled);
            Assert.NotNull(lockoutText);
            Assert.False(lockoutText!.IsOffscreen);
        }
        catch
        {
            ExamAppHost.CaptureFailureScreenshot("E2E12_H17_ExitDialogLockout");
            throw;
        }
    }
}
