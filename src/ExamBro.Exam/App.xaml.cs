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
        RegisterEmergencyRecoveryHandlers();

        try
        {
            base.OnStartup(e);

            Serilog.Log.Logger = LoggerSetup.CreateLogger();

            string? urlOverride = null;
            bool devSafeRequested = false;
            bool restoreRequested = false;
            bool silentRequested = false;

            foreach (string arg in e.Args)
            {
                if (arg.Equals("--restore", StringComparison.OrdinalIgnoreCase))
                {
                    restoreRequested = true;
                }
                else if (arg.Equals("--silent", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("-q", StringComparison.OrdinalIgnoreCase))
                {
                    silentRequested = true;
                }
                else if (arg.Equals("--dev-safe", StringComparison.OrdinalIgnoreCase))
                {
                    devSafeRequested = true;
                }
                else if (arg.StartsWith("--url=", StringComparison.OrdinalIgnoreCase))
                {
                    urlOverride = arg[6..].Trim();
                }
            }

            if (restoreRequested)
            {
                KioskLockService.RestoreFromRecovery();
                if (!silentRequested && Environment.UserInteractive)
                {
                    MessageBox.Show(
                        "Pemulihan sistem berhasil diselesaikan. Taskbar dan pengaturan telah dipulihkan.",
                        "ExamBro Recovery",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                Shutdown(0);
                return;
            }

            if (Environment.GetEnvironmentVariable(Constants.DevSafeEnvVar) == "1")
            {
                devSafeRequested = true;
            }

            bool createdNew = true;
            try
            {
                _singleInstanceMutex = new Mutex(true, Constants.MutexName, out createdNew);
            }
            catch (AbandonedMutexException)
            {
                createdNew = true;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Failed creating global mutex. Retrying with local session mutex.");
                try
                {
                    _singleInstanceMutex = new Mutex(true, @"Local\LouisVoxExamBro_SingleInstance_Mutex", out createdNew);
                }
                catch (AbandonedMutexException)
                {
                    createdNew = true;
                }
                catch (Exception localEx)
                {
                    Serilog.Log.Warning(localEx, "Failed creating local mutex. Allowing instance to proceed.");
                    createdNew = true;
                }
            }

            if (!createdNew)
            {
                MessageBox.Show(
                    $"Aplikasi {Constants.AppName} sudah berjalan di komputer ini.",
                    Constants.AppName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Shutdown(0);
                return;
            }

            try
            {
                ProcessMonitorService.TerminateSiblingExamBroProcesses();
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "Error checking sibling processes during startup");
            }

            if (KioskLockService.HasResidualLockdownState())
            {
                KioskLockService.RestoreFromRecovery();
            }

            if (!LoadConfiguration(out string configSource))
            {
                return;
            }

            Serilog.Log.Logger = LoggerSetup.CreateLogger(_config);
            Serilog.Log.Information(
                "ExamBro application startup. Version: {Version}, PortableMode: {IsPortable}, ConfigSource: {ConfigSource}",
                Constants.AppVersion,
                Constants.IsPortableMode,
                configSource);

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

            var preFlight = new PreFlightDialog(_config!, IsDevSafe);
            bool? preFlightPassed = preFlight.ShowDialog();
            if (preFlightPassed != true)
            {
                Serilog.Log.Information("Pre-flight checklist did not complete. Exiting application.");
                Shutdown(0);
                return;
            }

            var examWindow = new ExamWindow(_config!, IsDevSafe, urlOverride);
            examWindow.Closed += (s, ev) => Shutdown(0);
            MainWindow = examWindow;
            examWindow.Show();
        }
        catch (Exception fatalEx)
        {
            WriteEmergencyCrashLog("Fatal Startup Exception", fatalEx);
            MessageBox.Show(
                $"Aplikasi {Constants.AppName} gagal dimulai:\n\n{fatalEx.Message}\n\nDetail kesalahan telah dicatat di berkas crash.log.",
                Constants.AppName,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private bool LoadConfiguration(out string configSource)
    {
        string? currentExe = EmbeddedConfigHelper.ResolveCurrentExecutablePath();
        var (loaded, source, rawBytes) = EmbeddedConfigHelper.ResolveConfig(
            targetExePath: currentExe,
            appDirectory: Constants.AppDirectory);

        if (loaded != null)
        {
            _config = loaded;
            configSource = source;

            // Heal an existing sibling exambro.cfg if the embedded payload inside the .exe is newer
            if (source.StartsWith("Embedded:", StringComparison.Ordinal) &&
                File.Exists(Constants.PortableConfigPath) &&
                rawBytes != null)
            {
                try
                {
                    AtomicFileWriter.WriteAllBytesFlushed(Constants.PortableConfigPath, rawBytes);
                    Serilog.Log.Information("Synchronized sibling exambro.cfg with newer embedded configuration");
                }
                catch { }
            }

            return true;
        }

        configSource = "None";
        Serilog.Log.Error("No valid embedded or local configuration found in {AppDir}", Constants.AppDirectory);
        MessageBox.Show(
            "Konfigurasi ujian belum diatur atau berkas konfigurasi rusak.\n\n" +
            "Pastikan Anda menjalankan aplikasi ujian yang telah dikonfigurasi oleh pengawas, atau ekstrak seluruh isi paket ZIP ujian terlebih dahulu.",
            Constants.AppName,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        Shutdown(1);
        return false;
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
            WriteEmergencyCrashLog("AppDomain.UnhandledException", args.ExceptionObject as Exception);
            KioskLockService.RestoreFromRecovery();
        };

        DispatcherUnhandledException += (s, args) =>
        {
            WriteEmergencyCrashLog("DispatcherUnhandledException", args.Exception);
            KioskLockService.RestoreFromRecovery();
        };

        SessionEnding += (s, args) =>
        {
            // WPF raises SessionEnding during WM_QUERYENDSESSION before shutdown is confirmed;
            // ExamWindow.WndProc disengages cleanly upon WM_ENDSESSION with wParam != 0.
            WriteEmergencyCrashLog("SessionEnding", null);
        };
    }

    private static void WriteEmergencyCrashLog(string context, Exception? ex)
    {
        string message = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff UTC}] [{context}] {ex?.GetType().FullName}: {ex?.Message}\n{ex?.StackTrace}\n";
        if (ex?.InnerException != null)
        {
            message += $"InnerException: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}\n{ex.InnerException.StackTrace}\n";
        }

        string[] targets =
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Constants.AppName, "crash.log"),
            Path.Combine(Path.GetTempPath(), "LouisVoxExamBro_crash.log")
        };

        foreach (string target in targets)
        {
            try
            {
                string? dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.AppendAllText(target, message);
            }
            catch
            {
            }
        }

        try
        {
            Serilog.Log.Fatal(ex, "Emergency Crash Dump: {Context}", context);
        }
        catch
        {
        }
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

        // Forcefully terminate any child/descendant processes and sibling config tools
        try
        {
            ProcessMonitorService.KillDescendantProcesses(Environment.ProcessId);
            ProcessMonitorService.TerminateSiblingExamBroProcesses();
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Error terminating descendant or sibling processes on exit");
        }

        Serilog.Log.Information("ExamBro application exited with code: {ExitCode}", e.ApplicationExitCode);
        Serilog.Log.CloseAndFlush();

        base.OnExit(e);

        // Force complete process termination to guarantee no orphan background threads or RAM leaks
        Environment.Exit(e.ApplicationExitCode);
    }
}
