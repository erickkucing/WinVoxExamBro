using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using ExamBro.Core.Models;
using ExamBro.Core.Security;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.UIA3;
using FlaUIApp = FlaUI.Core.Application;
using FlaUIWindow = FlaUI.Core.AutomationElements.Window;

namespace ExamBro.Tests.Windows.Fixtures;

/// <summary>
/// Manages launching ExamBro.Exam.exe inside an isolated temporary directory for Windows E2E tests,
/// embedding/writing test configs, attaching FlaUI UIA3 automation, capturing failure screenshots,
/// and tearing down cleanly.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ExamAppHost : IDisposable
{
    private readonly string _sourceExePath;
    private FlaUIApp? _flauiApp;
    private UIA3Automation? _automation;

    public string WorkDir { get; }
    public string TargetExePath { get; }
    public Process? Process { get; private set; }
    public int? CdpPort { get; private set; }

    public ExamAppHost(bool preferDebug = false, string? customDir = null, string exeFileName = "LouisVox ExamBro.exe")
    {
        _sourceExePath = ResolveExecutablePath(preferDebug);
        WorkDir = customDir ?? Path.Combine(Path.GetTempPath(), $"exambro_e2e_{Guid.NewGuid():N}");
        Directory.CreateDirectory(WorkDir);
        TargetExePath = Path.Combine(WorkDir, exeFileName);

        CopyRuntimeBinary(_sourceExePath, TargetExePath);
    }

    public static string ResolveExecutablePath(bool preferDebug)
    {
        string? envOverride = preferDebug
            ? Environment.GetEnvironmentVariable("EXAMBRO_DEBUG_EXE")
            : Environment.GetEnvironmentVariable("EXAMBRO_EXE");

        if (!string.IsNullOrWhiteSpace(envOverride) && File.Exists(envOverride))
        {
            return Path.GetFullPath(envOverride);
        }

        string repoRoot = FindRepoRoot();
        var candidates = preferDebug
            ? new[]
            {
                Path.Combine(repoRoot, "src", "ExamBro.Exam", "bin", "Debug", "net8.0-windows", "ExamBro.Exam.exe"),
                Path.Combine(repoRoot, "src", "ExamBro.Exam", "bin", "Release", "net8.0-windows", "ExamBro.Exam.exe")
            }
            : new[]
            {
                Path.Combine(repoRoot, "dist", "exam", "ExamBro.Exam.exe"),
                Path.Combine(repoRoot, "dist", "ExamBro.Exam", "ExamBro.Exam.exe"),
                Path.Combine(repoRoot, "src", "ExamBro.Exam", "bin", "Release", "net8.0-windows", "win-x64", "publish", "ExamBro.Exam.exe"),
                Path.Combine(repoRoot, "src", "ExamBro.Exam", "bin", "Release", "net8.0-windows", "ExamBro.Exam.exe"),
                Path.Combine(repoRoot, "src", "ExamBro.Exam", "bin", "Debug", "net8.0-windows", "ExamBro.Exam.exe")
            };

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"ExamBro.Exam.exe not found (preferDebug={preferDebug}). Set EXAMBRO_EXE / EXAMBRO_DEBUG_EXE or build ExamBro.Exam first.");
    }

    private static void CopyRuntimeBinary(string sourceExe, string targetExe)
    {
        string? sourceDir = Path.GetDirectoryName(sourceExe);
        string? targetDir = Path.GetDirectoryName(targetExe);
        if (string.IsNullOrWhiteSpace(sourceDir) || string.IsNullOrWhiteSpace(targetDir))
        {
            throw new InvalidOperationException("Invalid executable copy path.");
        }

        File.Copy(sourceExe, targetExe, overwrite: true);

        // If running from non-single-file build output, copy companion assemblies so dotnet host starts cleanly
        string baseName = Path.GetFileNameWithoutExtension(sourceExe);
        string depsFile = Path.Combine(sourceDir, $"{baseName}.deps.json");
        if (File.Exists(depsFile))
        {
            foreach (string file in Directory.GetFiles(sourceDir))
            {
                string fileName = Path.GetFileName(file);
                if (fileName.Equals(Path.GetFileName(sourceExe), StringComparison.OrdinalIgnoreCase) ||
                    fileName.Equals("exambro.cfg", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string dest = Path.Combine(targetDir, fileName);
                File.Copy(file, dest, overwrite: true);
            }
        }
    }

    public static ExamConfig CreateE2eConfig(string startUrl, bool kioskMode = false, bool devSafeAllowedInRelease = false)
    {
        var cfg = ExamConfig.CreateDefault();
        cfg.General.StartUrl = startUrl;
        cfg.General.AppTitle = "WinVox E2E Test";
        cfg.Security.BlockVirtualMachine = false; // Azure/VM CI runners do not represent physical lab hardware
        cfg.Security.BlockRemoteSession = false;
        cfg.Security.KioskMode = kioskMode;
        cfg.Security.DisableRegistryPolicies = kioskMode;
        cfg.Security.ScreenCaptureProtection = false;
        cfg.Security.DevSafeAllowedInRelease = devSafeAllowedInRelease;
        cfg.Admin.MaxPasswordAttempts = 5;
        cfg.Admin.PasswordLockoutSeconds = 30;

        var (adminHash, adminSalt) = PasswordHasher.HashPassword("TestAdmin#2026");
        cfg.Admin.AdminPasswordHash = adminHash;
        cfg.Admin.AdminPasswordSalt = adminSalt;

        var (proctorHash, proctorSalt) = PasswordHasher.HashPassword("TestProctor#2026");
        cfg.Admin.ProctorPasswordHash = proctorHash;
        cfg.Admin.ProctorPasswordSalt = proctorSalt;

        cfg.UpdatedAtUtcTicks = DateTime.UtcNow.Ticks;
        return cfg;
    }

    public void Configure(ExamConfig? embeddedConfig = null, ExamConfig? siblingConfig = null)
    {
        if (embeddedConfig != null)
        {
            byte[] encrypted = ConfigEncryption.EncryptPortableConfig(embeddedConfig);
            EmbeddedConfigHelper.WriteEmbeddedConfig(TargetExePath, encrypted);
        }

        if (siblingConfig != null)
        {
            byte[] encrypted = ConfigEncryption.EncryptPortableConfig(siblingConfig);
            AtomicFileWriter.WriteAllBytesFlushed(Path.Combine(WorkDir, "exambro.cfg"), encrypted);
        }
    }

    public Process Launch(bool devSafe = true, bool enableCdp = false, params string[] extraArgs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = TargetExePath,
            WorkingDirectory = WorkDir,
            UseShellExecute = false,
            CreateNoWindow = false
        };

        if (devSafe)
        {
            psi.ArgumentList.Add("--dev-safe");
        }

        foreach (string arg in extraArgs)
        {
            psi.ArgumentList.Add(arg);
        }

        if (enableCdp)
        {
            int port = AllocateFreePort();
            CdpPort = port;
            psi.Environment["EXAMBRO_E2E"] = "1";
            psi.Environment["EXAMBRO_E2E_CDP_PORT"] = port.ToString();
        }

        Process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ExamBro.Exam process.");
        return Process;
    }

    public UIA3Automation AttachUia()
    {
        if (Process == null) throw new InvalidOperationException("Process has not been launched.");
        _automation ??= new UIA3Automation();
        _flauiApp ??= FlaUIApp.Attach(Process);
        return _automation;
    }

    public async Task<FlaUIWindow> WaitForMainWindowAsync(TimeSpan timeout)
    {
        var automation = AttachUia();
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (Process == null || Process.HasExited)
            {
                throw new InvalidOperationException($"Process exited prematurely with code {Process?.ExitCode}.");
            }

            try
            {
                var win = _flauiApp?.GetMainWindow(automation, TimeSpan.FromMilliseconds(500));
                if (win != null)
                {
                    return win;
                }
            }
            catch { }

            await Task.Delay(200);
        }

        throw new TimeoutException("Timed out waiting for ExamBro main window.");
    }

    public static void CaptureFailureScreenshot(string testName)
    {
        try
        {
            string outDir = Path.Combine(FindRepoRoot(), "tests", "ExamBro.Tests.Windows", "TestResults");
            Directory.CreateDirectory(outDir);
            string safeName = string.Concat(testName.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_'));
            string path = Path.Combine(outDir, $"{safeName}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png");
            using var img = Capture.Screen();
            img.ToFile(path);
        }
        catch { }
    }

    public void KillHard()
    {
        try
        {
            if (Process != null && !Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
                Process.WaitForExit(3000);
            }
        }
        catch { }
    }

    public void Dispose()
    {
        KillHard();
        _flauiApp?.Dispose();
        _automation?.Dispose();
        Process?.Dispose();

        try
        {
            if (Directory.Exists(WorkDir))
            {
                Directory.Delete(WorkDir, recursive: true);
            }
        }
        catch { }
    }

    private static int AllocateFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ExamBro.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
