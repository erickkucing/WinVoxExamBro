using System.IO;
using System.Security.Cryptography;
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
        base.OnStartup(e);

        // Initialize logging
        Log.Logger = LoggerSetup.CreateLogger();
        Log.Information("Aplikasi ExamBro.Config dimulai");

        try
        {
            string configPath = Constants.ConfigFilePath;

            // Scenario 1: First run - Config file does not exist
            if (!File.Exists(configPath))
            {
                Log.Information("File konfigurasi tidak ditemukan di {Path}. Menjalankan SetupWizardDialog...", configPath);
                RunFirstTimeSetup(configPath);
                return;
            }

            // Scenario 2: Config file exists - Attempt to decrypt and load
            ExamConfig? loadedConfig = null;
            try
            {
                byte[] encryptedData = File.ReadAllBytes(configPath);
                loadedConfig = ConfigEncryption.DecryptConfig(encryptedData);
                Log.Information("File konfigurasi berhasil didekripsi dari {Path}", configPath);
            }
            catch (Exception ex) when (ex is CryptographicException || ex is IOException || ex is FormatException)
            {
                Log.Error(ex, "File konfigurasi rusak atau gagal didekripsi dari {Path}", configPath);

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
            Log.Fatal(ex, "Kesalahan tidak terduga saat inisialisasi aplikasi ExamBro.Config");
            MessageBox.Show(
                $"Terjadi kesalahan fatal saat memulai aplikasi:\n{ex.Message}",
                "Kesalahan Fatal",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
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
                string? dir = Path.GetDirectoryName(configPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                byte[] encrypted = ConfigEncryption.EncryptConfig(wizard.Config);
                File.WriteAllBytes(configPath, encrypted);
                Log.Information("Konfigurasi baru berhasil disimpan ke {Path}", configPath);

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
    }
}
