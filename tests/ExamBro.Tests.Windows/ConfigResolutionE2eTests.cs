using System.IO;
using System.Runtime.Versioning;
using ExamBro.Core;
using ExamBro.Core.Models;
using ExamBro.Core.Security;
using ExamBro.Tests.Windows.Fixtures;

namespace ExamBro.Tests.Windows;

/// <summary>
/// E2E-01 & E2E-02: Verifies configuration resolution across embedded trailers, sibling exambro.cfg,
/// timestamp precedence, fail-closed unconfigured launch, ProgramData isolation in portable mode,
/// and standalone single-exe execution from %TEMP%.
/// </summary>
[Collection("WindowsE2e")]
[Trait("Category", "WindowsOnly")]
[SupportedOSPlatform("windows")]
public sealed class ConfigResolutionE2eTests
{
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

        throw new TimeoutException($"FakeCbtServer did not receive request matching '{pathFragment}' within {timeout.TotalSeconds}s.");
    }

    [Theory]
    [InlineData("embedded_only")]
    [InlineData("sibling_only")]
    [InlineData("embedded_newer")]
    [InlineData("sibling_newer")]
    public async Task E2E01_ConfigMatrix_LoadsExpectedStartUrl(string mode)
    {
        await using var server = await FakeCbtServer.StartAsync();
        using var host = new ExamAppHost(preferDebug: false);

        string expectedSuffix = $"/login?case={mode}";
        var olderCfg = ExamAppHost.CreateE2eConfig($"{server.BaseUrl}/login?case=stale", devSafeAllowedInRelease: true);
        olderCfg.UpdatedAtUtcTicks = 1_000_000;

        var targetCfg = ExamAppHost.CreateE2eConfig($"{server.BaseUrl}{expectedSuffix}", devSafeAllowedInRelease: true);
        targetCfg.UpdatedAtUtcTicks = 2_000_000;

        switch (mode)
        {
            case "embedded_only":
                host.Configure(embeddedConfig: targetCfg, siblingConfig: null);
                break;
            case "sibling_only":
                host.Configure(embeddedConfig: null, siblingConfig: targetCfg);
                break;
            case "embedded_newer":
                host.Configure(embeddedConfig: targetCfg, siblingConfig: olderCfg);
                break;
            case "sibling_newer":
                host.Configure(embeddedConfig: olderCfg, siblingConfig: targetCfg);
                break;
        }

        try
        {
            host.Launch(devSafe: true);
            await WaitForRequestAsync(server, expectedSuffix, TimeSpan.FromSeconds(25));
            Assert.Contains("LouisVox", server.LastUserAgent ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            ExamAppHost.CaptureFailureScreenshot($"E2E01_{mode}");
            throw;
        }
    }

    [Fact]
    public async Task E2E01e_UnconfiguredExecutable_FailsClosedWithExitCode1AndCreatesNoConfig()
    {
        using var host = new ExamAppHost(preferDebug: false);
        // Intentionally do NOT call host.Configure()
        var proc = host.Launch(devSafe: true);

        try
        {
            var automation = host.AttachUia();
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline && !proc.HasExited)
            {
                var desktop = automation.GetDesktop();
                var dlg = desktop.FindFirstChild(cf => cf.ByName(Constants.AppName));
                var okBtn = dlg?.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button));
                if (okBtn != null)
                {
                    okBtn.Click();
                    break;
                }
                await Task.Delay(250);
            }

            bool exited = proc.WaitForExit(10_000);
            Assert.True(exited, "Unconfigured executable did not exit after dismissing error dialog.");
            Assert.Equal(1, proc.ExitCode);
            Assert.False(File.Exists(Path.Combine(host.WorkDir, "exambro.cfg")));
        }
        catch
        {
            ExamAppHost.CaptureFailureScreenshot("E2E01e_Unconfigured");
            throw;
        }
    }

    [Fact]
    public async Task E2E01f_PortableApp_IgnoresStaleProgramDataConfig()
    {
        if (!RegistryGuard.AllowRegistryMutation)
        {
            return;
        }

        string sysCfgPath = Constants.SystemConfigFilePath;
        byte[]? backupBytes = File.Exists(sysCfgPath) ? File.ReadAllBytes(sysCfgPath) : null;

        try
        {
            var staleSystemCfg = ExamAppHost.CreateE2eConfig("http://192.168.70.9:8008/stale", devSafeAllowedInRelease: true);
            AtomicFileWriter.WriteAllBytesFlushed(sysCfgPath, ConfigEncryption.EncryptPortableConfig(staleSystemCfg));

            await using var server = await FakeCbtServer.StartAsync();
            using var host = new ExamAppHost(preferDebug: false);
            var portableCfg = ExamAppHost.CreateE2eConfig($"{server.BaseUrl}/login?portable=isolated", devSafeAllowedInRelease: true);
            host.Configure(embeddedConfig: portableCfg, siblingConfig: null);

            host.Launch(devSafe: true);
            await WaitForRequestAsync(server, "/login?portable=isolated", TimeSpan.FromSeconds(25));
        }
        catch
        {
            ExamAppHost.CaptureFailureScreenshot("E2E01f_StaleProgramData");
            throw;
        }
        finally
        {
            if (backupBytes != null)
            {
                AtomicFileWriter.WriteAllBytesFlushed(sysCfgPath, backupBytes);
            }
            else if (File.Exists(sysCfgPath))
            {
                File.Delete(sysCfgPath);
            }
        }
    }

    [Fact]
    public async Task E2E02_SingleExeCopiedToTempWithoutCompanionFiles_LoadsEmbeddedUrl()
    {
        await using var server = await FakeCbtServer.StartAsync();
        string tempZipSimDir = Path.Combine(Path.GetTempPath(), $"exambro_zip_sim_{Guid.NewGuid():N}");

        using var host = new ExamAppHost(preferDebug: false, customDir: tempZipSimDir, exeFileName: "LouisVox ExamBro.exe");
        var cfg = ExamAppHost.CreateE2eConfig($"{server.BaseUrl}/login?zip_temp=1", devSafeAllowedInRelease: true);
        host.Configure(embeddedConfig: cfg, siblingConfig: null);

        Assert.False(File.Exists(Path.Combine(tempZipSimDir, "exambro.cfg")));

        try
        {
            host.Launch(devSafe: true);
            await WaitForRequestAsync(server, "/login?zip_temp=1", TimeSpan.FromSeconds(25));
        }
        catch
        {
            ExamAppHost.CaptureFailureScreenshot("E2E02_ZipTemp");
            throw;
        }
    }
}
