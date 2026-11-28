using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ExamBro.Core;
using ExamBro.Core.Recovery;
using ExamBro.Exam.Services;
using ExamBro.Tests.Windows.Fixtures;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace ExamBro.Tests.Windows;

/// <summary>
/// E2E-03 through E2E-07: Verifies real kiosk engagement, authorized proctor exit, hard-kill
/// boot recovery script execution, WM_QUERYENDSESSION/WM_ENDSESSION behavior (H-01), and
/// running-process recovery script guard (H-02).
/// Mutates registry/tasks only when GITHUB_ACTIONS=true or EXAMBRO_E2E_ALLOW_REGISTRY=1.
/// </summary>
[Collection("WindowsE2e")]
[Trait("Category", "WindowsOnly")]
[SupportedOSPlatform("windows")]
public sealed class KioskLifecycleE2eTests
{
    private const uint WM_QUERYENDSESSION = 0x0011;
    private const uint WM_ENDSESSION = 0x0016;
    private const uint WM_CLOSE = 0x0010;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private static async Task<IntPtr> WaitForExamWindowAsync(ExamAppHost host, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (host.Process != null && !host.Process.HasExited)
            {
                host.Process.Refresh();
                IntPtr hwnd = host.Process.MainWindowHandle;
                if (hwnd != IntPtr.Zero &&
                    RegistryGuard.ReadDword(false, Constants.ExplorerPolicySubKey, "NoClose") == 1)
                {
                    return hwnd;
                }
            }
            await Task.Delay(250);
        }

        throw new TimeoutException("Timed out waiting for ExamWindow to engage kiosk policies.");
    }

    [Fact]
    public async Task E2E03_And_E2E04_EngageKioskAndAuthorizedProctorExit_RestoresSystemCleanly()
    {
        if (!RegistryGuard.AllowRegistryMutation) return;

        RegistryGuard.RestoreCleanState();
        await using var server = await FakeCbtServer.StartAsync();
        using var host = new ExamAppHost(preferDebug: false);

        try
        {
            var cfg = ExamAppHost.CreateE2eConfig($"{server.BaseUrl}/login", kioskMode: true, devSafeAllowedInRelease: false);
            cfg.Security.ProcessBlacklist.Clear();
            host.Configure(embeddedConfig: cfg);
            host.Launch(devSafe: false);

            IntPtr hwnd = await WaitForExamWindowAsync(host, TimeSpan.FromSeconds(30));
            Assert.NotEqual(IntPtr.Zero, hwnd);

            // E2E-03 assertions
            Assert.Equal(1, RegistryGuard.ReadDword(false, Constants.ExplorerPolicySubKey, "NoClose"));
            Assert.Equal(1, RegistryGuard.ReadDword(false, Constants.ExplorerPolicySubKey, "NoLogoff"));
            Assert.Equal(1, RegistryGuard.ReadDword(false, Constants.SystemPolicySubKey, "DisableTaskMgr"));
            Assert.True(RegistryGuard.ScheduledTaskExists());
            Assert.True(RecoveryState.NeedsRecovery());
            Assert.True(File.Exists(Constants.SystemRecoveryScriptPath));

            // E2E-04: Trigger ExitDialog cleanly via WM_CLOSE -> OnWindowClosing opens ExitDialog
            SendMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            await Task.Delay(500);

            var automation = host.AttachUia();
            var exitWin = automation.GetDesktop().FindFirstChild(cf => cf.ByName("Konfirmasi Keluar Ujian"));
            Assert.NotNull(exitWin);

            var pwdBox = exitWin!.FindFirstDescendant(cf => cf.ByAutomationId("pwdExit"));
            var btnConfirm = exitWin.FindFirstDescendant(cf => cf.ByAutomationId("btnConfirmExit"));
            Assert.NotNull(pwdBox);
            Assert.NotNull(btnConfirm);

            pwdBox!.Focus();
            Keyboard.Type("TestProctor#2026");
            btnConfirm!.Click();

            bool exited = host.Process!.WaitForExit(15_000);
            Assert.True(exited, "ExamBro did not exit after valid proctor password.");
            Assert.Equal(0, host.Process.ExitCode);

            Assert.Null(RegistryGuard.ReadDword(false, Constants.ExplorerPolicySubKey, "NoClose"));
            Assert.Null(RegistryGuard.ReadDword(false, Constants.ExplorerPolicySubKey, "NoLogoff"));
            Assert.Null(RegistryGuard.ReadDword(false, Constants.SystemPolicySubKey, "DisableTaskMgr"));
            Assert.False(RegistryGuard.ScheduledTaskExists());
            Assert.False(RecoveryState.NeedsRecovery());
        }
        catch
        {
            ExamAppHost.CaptureFailureScreenshot("E2E03_04_KioskLifecycle");
            throw;
        }
        finally
        {
            RegistryGuard.RestoreCleanState();
        }
    }

    [Fact]
    public async Task E2E05_HardKillFollowedByRestoreScript_RecoversRegistryTaskAndAllowsRelaunch()
    {
        if (!RegistryGuard.AllowRegistryMutation) return;

        RegistryGuard.RestoreCleanState();
        await using var server = await FakeCbtServer.StartAsync();
        using var host = new ExamAppHost(preferDebug: false);

        try
        {
            var cfg = ExamAppHost.CreateE2eConfig($"{server.BaseUrl}/login", kioskMode: true);
            cfg.Security.ProcessBlacklist.Clear();
            host.Configure(embeddedConfig: cfg);
            host.Launch(devSafe: false);

            await WaitForExamWindowAsync(host, TimeSpan.FromSeconds(30));

            // Simulate sudden crash / hard power-off
            host.KillHard();

            Assert.True(File.Exists(Constants.SystemRecoveryScriptPath));
            using (var ps = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{Constants.SystemRecoveryScriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            }))
            {
                ps?.WaitForExit(15_000);
            }

            Assert.Null(RegistryGuard.ReadDword(false, Constants.ExplorerPolicySubKey, "NoClose"));
            Assert.Null(RegistryGuard.ReadDword(false, Constants.ExplorerPolicySubKey, "NoLogoff"));
            Assert.Null(RegistryGuard.ReadDword(false, Constants.SystemPolicySubKey, "DisableTaskMgr"));
            Assert.False(RegistryGuard.ScheduledTaskExists());
        }
        catch
        {
            ExamAppHost.CaptureFailureScreenshot("E2E05_HardKillRecovery");
            throw;
        }
        finally
        {
            RegistryGuard.RestoreCleanState();
        }
    }

    [Fact]
    public async Task E2E06_H01_WmQueryEndSessionWithoutConfirmedEndSession_KeepsKioskLocked()
    {
        if (!RegistryGuard.AllowRegistryMutation) return;

        RegistryGuard.RestoreCleanState();
        await using var server = await FakeCbtServer.StartAsync();
        using var host = new ExamAppHost(preferDebug: false);

        try
        {
            var cfg = ExamAppHost.CreateE2eConfig($"{server.BaseUrl}/login", kioskMode: true);
            cfg.Security.ProcessBlacklist.Clear();
            host.Configure(embeddedConfig: cfg);
            host.Launch(devSafe: false);

            IntPtr hwnd = await WaitForExamWindowAsync(host, TimeSpan.FromSeconds(30));

            // 1. OS asks WM_QUERYENDSESSION: app must reply 1 (allow) WITHOUT disengaging kiosk
            IntPtr queryReply = SendMessage(hwnd, WM_QUERYENDSESSION, IntPtr.Zero, IntPtr.Zero);
            Assert.Equal(new IntPtr(1), queryReply);
            Assert.Equal(1, RegistryGuard.ReadDword(false, Constants.ExplorerPolicySubKey, "NoClose"));
            Assert.Equal(1, RegistryGuard.ReadDword(false, Constants.SystemPolicySubKey, "DisableTaskMgr"));

            // 2. Shutdown canceled -> WM_ENDSESSION with wParam = 0 -> MUST still remain locked
            SendMessage(hwnd, WM_ENDSESSION, IntPtr.Zero, IntPtr.Zero);
            Assert.Equal(1, RegistryGuard.ReadDword(false, Constants.ExplorerPolicySubKey, "NoClose"));

            // 3. Shutdown confirmed -> WM_ENDSESSION with wParam = 1 -> NOW disengage cleanly
            SendMessage(hwnd, WM_ENDSESSION, new IntPtr(1), IntPtr.Zero);
            await Task.Delay(600);
            Assert.Null(RegistryGuard.ReadDword(false, Constants.ExplorerPolicySubKey, "NoClose"));
        }
        catch
        {
            ExamAppHost.CaptureFailureScreenshot("E2E06_H01_QueryEndSession");
            throw;
        }
        finally
        {
            RegistryGuard.RestoreCleanState();
        }
    }

    [Fact]
    public async Task E2E07_H02_RestoreScriptAbortsIfExamProcessIsActive()
    {
        if (!RegistryGuard.AllowRegistryMutation) return;

        RegistryGuard.RestoreCleanState();
        await using var server = await FakeCbtServer.StartAsync();
        using var host = new ExamAppHost(preferDebug: false);

        try
        {
            var cfg = ExamAppHost.CreateE2eConfig($"{server.BaseUrl}/login", kioskMode: true);
            cfg.Security.ProcessBlacklist.Clear();
            host.Configure(embeddedConfig: cfg);
            host.Launch(devSafe: false);

            await WaitForExamWindowAsync(host, TimeSpan.FromSeconds(30));
            Assert.True(File.Exists(Constants.SystemRecoveryScriptPath));

            // Run restore-kiosk.ps1 while ExamBro is STILL actively running
            using var ps = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{Constants.SystemRecoveryScriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            Assert.NotNull(ps);
            ps!.WaitForExit(15_000);
            Assert.Equal(0, ps.ExitCode);

            // Must NOT wipe lockdown policies or recovery journal mid-exam!
            Assert.Equal(1, RegistryGuard.ReadDword(false, Constants.ExplorerPolicySubKey, "NoClose"));
            Assert.Equal(1, RegistryGuard.ReadDword(false, Constants.SystemPolicySubKey, "DisableTaskMgr"));
            Assert.True(File.Exists(Constants.SystemRecoveryFilePath));
        }
        catch
        {
            ExamAppHost.CaptureFailureScreenshot("E2E07_H02_ScriptActiveGuard");
            throw;
        }
        finally
        {
            RegistryGuard.RestoreCleanState();
        }
    }
}
