using System.IO;
using System.Windows;
using ExamBro.Core;
using ExamBro.Core.Logging;
using ExamBro.Core.Models;
using ExamBro.Core.Recovery;
using ExamBro.Core.Security;
using ExamBro.Exam.Services;
using ExamBro.Exam.Views;
using Serilog;

namespace ExamBro.Exam;

/// <summary>
/// Main application entry point for ExamBro.
/// Manages instance exclusivity, command-line arguments, system crash recovery,
/// configuration decryption, and session bootstrap.
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private ExamConfig? _config;

    /// <summary>
    /// Global flag indicating whether the application is running in developer-safe mode
    /// (bypassing intrusive system lockdowns).
    /// </summary>
    public static bool IsDevSafe { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 1. Check for immediate recovery flag (--restore)
        if (e.Args.Any(a => a.Equals("--restore", StringComparison.OrdinalIgnoreCase)))
        {
            KioskLockService.RestoreFromRecovery();
            MessageBox.Show(
                "Pemulihan sistem berhasil diselesaikan. Taskbar dan pengaturan telah dipulihkan.",
                "ExamBro Recovery",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        // 2. Enforce Single-Instance Execution via Named Mutex
        _singleInstanceMutex = new Mutex(true, Constants.MutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "Aplikasi ExamBro sudah berjalan di komputer ini.",
                "ExamBro",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown(0);
            return;
        }

        // 3. Register defensive global disaster-recovery handlers
        RegisterEmergencyRecoveryHandlers();

        // 4. Auto-restore system state if a prior abnormal crash left recovery state
        if (RecoveryState.NeedsRecovery())
        {
            KioskLockService.RestoreFromRecovery();
        }

        // 5. Load and decrypt configuration file
        if (!LoadConfiguration())
        {
            MessageBox.Show(
                "Konfigurasi tidak valid, hubungi pengawas.",
                "ExamBro",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // 6. Initialize Serilog Logging
        Serilog.Log.Logger = LoggerSetup.CreateLogger(_config);
        Serilog.Log.Information("ExamBro application startup. Version: {Version}", Constants.AppVersion);

        // 7. Parse command line arguments & dev-safe mode
        string? urlOverride = null;
        bool devSafeRequested = false;

        foreach (string arg in e.Args)
        {
            if (arg.Equals("--dev-safe", StringComparison.OrdinalIgnoreCase))
            {
                devSafeRequested = true;
            }
            else if (arg.StartsWith("--url=", StringComparison.OrdinalIgnoreCase))
            {
                urlOverride = arg[6..].Trim();
            }
        }

        if (Environment.GetEnvironmentVariable(Constants.DevSafeEnvVar) == "1")
        {
            devSafeRequested = true;
        }

        // In Release builds, dev-safe is strictly prohibited unless specifically permitted in config
#if !DEBUG
        if (devSafeRequested && !_config!.Security.DevSafeAllowedInRelease)
        {
            Serilog.Log.Warning("DevSafe mode requested but rejected in Release build: Security.DevSafeAllowedInRelease is false");
            devSafeRequested = false;
        }
#endif

        IsDevSafe = devSafeRequested;
        if (IsDevSafe)
        {
            Serilog.Log.Information("Application running in DEV-SAFE mode. Intrusive kiosk locks disabled.");
        }

        // 8. Execute Pre-Flight System Readiness Checks
        var preFlight = new PreFlightDialog(_config!, IsDevSafe);
        bool? preFlightPassed = preFlight.ShowDialog();
        if (preFlightPassed != true)
        {
            Serilog.Log.Information("Pre-flight checklist did not complete. Exiting application.");
            Shutdown(0);
            return;
        }

        // 9. Launch Exam Kiosk Window
        var examWindow = new ExamWindow(_config!, IsDevSafe, urlOverride);
        MainWindow = examWindow;
        examWindow.Show();
    }

    private bool LoadConfiguration()
    {
        string configPath = Constants.ConfigFilePath;

        if (!File.Exists(configPath))
        {
            // If running in development without a saved config file, generate a default one
            if (IsDevSafe || DebuggerCheck())
            {
                _config = ExamConfig.CreateDefault();
                return true;
            }
            return false;
        }

        try
        {
            byte[] encryptedBytes = File.ReadAllBytes(configPath);
            _config = ConfigEncryption.DecryptConfig(encryptedBytes);
            return _config != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool DebuggerCheck()
    {
#if DEBUG
        return true;
#else
        return false;
#endif
    }

    private void RegisterEmergencyRecoveryHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            try
            {
                Serilog.Log.Fatal(args.ExceptionObject as Exception, "Fatal unhandled exception in AppDomain. Triggering emergency recovery...");
            }
            catch { /* best effort */ }

            KioskLockService.RestoreFromRecovery();
        };

        DispatcherUnhandledException += (s, args) =>
        {
            try
            {
                Serilog.Log.Fatal(args.Exception, "Fatal unhandled exception in Dispatcher. Triggering emergency recovery...");
            }
            catch { /* best effort */ }

            KioskLockService.RestoreFromRecovery();
        };

        SessionEnding += (s, args) =>
        {
            try
            {
                Serilog.Log.Information("Windows session ending. Restoring system state...");
            }
            catch { /* best effort */ }

            KioskLockService.RestoreFromRecovery();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstanceMutex != null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch
            {
                // Mutex might already be released
            }
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        Serilog.Log.Information("ExamBro application exited with code: {ExitCode}", e.ApplicationExitCode);
        Serilog.Log.CloseAndFlush();

        base.OnExit(e);
    }
}
