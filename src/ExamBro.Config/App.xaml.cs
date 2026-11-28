using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using ExamBro.Config.Views;
using ExamBro.Core;
using ExamBro.Core.Logging;
using ExamBro.Core.Models;
using ExamBro.Core.Security;
using Serilog;

namespace ExamBro.Config;

/// <summary>
/// Interaction logic for App.xaml
/// Coordinates initial startup, decryption, wizard, authentication, and error recovery.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            WriteEmergencyCrashLog("AppDomain.UnhandledException", args.ExceptionObject as Exception);
        };
        DispatcherUnhandledException += (s, args) =>
        {
            WriteEmergencyCrashLog("DispatcherUnhandledException", args.Exception);
        };

        try
        {
            base.OnStartup(e);

            Log.Logger = LoggerSetup.CreateLogger();
            Log.Information("Aplikasi ExamBro.Config dimulai");

            string configPath = Constants.ConfigFilePath;
            string? siblingExe = EmbeddedConfigHelper.FindSiblingExamExecutables(Constants.AppDirectory).FirstOrDefault();

            bool anyCandidateExists =
                File.Exists(Constants.PortableConfigPath) ||
                (siblingExe != null && EmbeddedConfigHelper.ReadEmbeddedConfigBytes(siblingExe) != null) ||
                (Constants.IsInstalledInProgramFiles && File.Exists(Constants.SystemConfigFilePath));

            // Scenario 1: First run - No config exists locally or embedded in sibling exe
            if (!anyCandidateExists)
            {
                Log.Information("File konfigurasi tidak ditemukan di {Path}. Menjalankan SetupWizardDialog...", configPath);
                RunFirstTimeSetup(configPath);
                return;
            }

            // Scenario 2: Load newest config between sibling exambro.cfg and embedded config inside student exe
            var (loadedConfig, configSource, resolvedBytes) = EmbeddedConfigHelper.ResolveConfig(
                targetExePath: siblingExe,
                appDirectory: Constants.AppDirectory);

            if (loadedConfig != null)
            {
                Log.Information("File konfigurasi berhasil dimuat dari {Source}", configSource);
                if (!configSource.Equals(Constants.PortableConfigPath, StringComparison.OrdinalIgnoreCase) &&
                    resolvedBytes != null)
                {
                    try
                    {
                        AtomicFileWriter.WriteAllBytesFlushed(Constants.PortableConfigPath, resolvedBytes);
                    }
                    catch { }
                }
            }
            else
            {
                Log.Error("Seluruh kandidat file konfigurasi rusak atau gagal didekripsi dari {Path}", configPath);

                var promptResult = MessageBox.Show(
                    $"File konfigurasi di '{configPath}' rusak atau tidak dapat didekripsi.\n\n" +
                    "Apakah Anda ingin membuat konfigurasi baru? (File lama akan ditimpa)",
                    "Konfigurasi Rusak",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (promptResult == MessageBoxResult.Yes)
                {
                    RunFirstTimeSetup(configPath);
                    return;
                }
                else
                {
                    Log.Information("Pengguna membatalkan pembuatan konfigurasi baru. Aplikasi ditutup.");
                    Shutdown();
                    return;
                }
            }

            // Scenario 3: Config successfully loaded - Show LoginDialog
            if (loadedConfig != null)
            {
                var loginDialog = new LoginDialog(loadedConfig);
                bool? loginResult = loginDialog.ShowDialog();

                if (loginResult == true)
                {
                    Log.Information("Login berhasil. Membuka MainWindow...");
                    var mainWindow = new MainWindow(loadedConfig);
                    MainWindow = mainWindow;
                    mainWindow.Show();
                }
                else
                {
                    Log.Information("Login dibatalkan atau gagal. Aplikasi keluar.");
                    Shutdown();
                }
            }
        }
        catch (Exception ex)
        {
            WriteEmergencyCrashLog("Fatal Config Startup Exception", ex);
            MessageBox.Show(
                $"Terjadi kesalahan fatal saat memulai aplikasi:\n{ex.Message}\n\nDetail telah dicatat di berkas crash.log.",
                "Kesalahan Fatal",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
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
            Path.Combine(Path.GetTempPath(), "LouisVoxExamBroConfig_crash.log")
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
            Log.Fatal(ex, "Emergency Crash Dump: {Context}", context);
        }
        catch
        {
        }
    }

    private void RunFirstTimeSetup(string configPath)
    {
        var wizard = new SetupWizardDialog();
        bool? wizardResult = wizard.ShowDialog();

        if (wizardResult == true && wizard.Config != null)
        {
            try
            {
                wizard.Config.UpdatedAtUtcTicks = DateTime.UtcNow.Ticks;
                string portablePath = Constants.PortableConfigPath;
                string? dir = Path.GetDirectoryName(portablePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                byte[] encrypted = ConfigEncryption.EncryptPortableConfig(wizard.Config);
                AtomicFileWriter.WriteAllBytesFlushed(portablePath, encrypted);
                Log.Information("Konfigurasi baru berhasil disimpan ke folder program: {Path}", portablePath);

                // Sematkan langsung ke exe ujian di folder yang sama agar tetap portable walau dibuka dari ZIP
                foreach (string exePath in EmbeddedConfigHelper.FindSiblingExamExecutables(Constants.AppDirectory))
                {
                    try
                    {
                        EmbeddedConfigHelper.WriteEmbeddedConfig(exePath, encrypted);
                        Log.Information("Konfigurasi disematkan ke {ExePath}", exePath);
                    }
                    catch (Exception embedEx)
                    {
                        Log.Warning(embedEx, "Gagal menyematkan konfigurasi awal ke {ExePath}", exePath);
                    }
                }

                // Tulis ke ProgramData hanya jika aplikasi diinstal di Program Files
                if (Constants.IsInstalledInProgramFiles)
                {
                    try
                    {
                        string sysPath = Constants.SystemConfigFilePath;
                        if (!sysPath.Equals(portablePath, StringComparison.OrdinalIgnoreCase))
                        {
                            AtomicFileWriter.WriteAllBytesFlushed(sysPath, encrypted);
                        }
                    }
                    catch (Exception sysEx)
                    {
                        Log.Warning(sysEx, "Tidak dapat menulis cadangan konfigurasi ke ProgramData");
                    }
                }

                var mainWindow = new MainWindow(wizard.Config);
                MainWindow = mainWindow;
                mainWindow.Show();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Gagal menyimpan konfigurasi baru dari wisaya ke {Path}", configPath);
                MessageBox.Show(
                    $"Gagal menyimpan file konfigurasi: {ex.Message}",
                    "Kesalahan Penyimpanan",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown();
            }
        }
        else
        {
            Log.Information("Wisaya konfigurasi awal dibatalkan. Menutup aplikasi.");
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Aplikasi ExamBro.Config ditutup (ExitCode: {ExitCode})", e.ApplicationExitCode);
        Log.CloseAndFlush();
        base.OnExit(e);
        Environment.Exit(e.ApplicationExitCode);
    }
}
