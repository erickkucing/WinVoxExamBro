using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ExamBro.Core.Models;
using ExamBro.Core.Security;
using Serilog;

namespace ExamBro.Config.Views;

/// <summary>
/// Interaction logic for LoginDialog.xaml
/// Modal dialog for administrator authentication with brute-force lockout protection.
/// </summary>
public partial class LoginDialog : Window
{
    private int _failedAttempts;
    private int _remainingLockoutSeconds;
    private readonly DispatcherTimer _lockoutTimer;

    public ExamConfig Config { get; }

    public LoginDialog(ExamConfig config)
    {
        InitializeComponent();
        Config = config;

        int maxAttempts = Config.Admin.MaxPasswordAttempts > 0 ? Config.Admin.MaxPasswordAttempts : 5;
        TxtAttemptsInfo.Text = $"Batas percobaan: {maxAttempts} kali";

        _lockoutTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _lockoutTimer.Tick += LockoutTimer_Tick;

        Loaded += (_, _) => TxtPassword.Focus();
    }

    private void TxtPassword_PasswordChanged(object sender, RoutedEventArgs e)
    {
        TxtError.Visibility = Visibility.Collapsed;
    }

    private void TxtPassword_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && BtnLogin.IsEnabled)
        {
            PerformLogin();
        }
    }

    private void BtnLogin_Click(object sender, RoutedEventArgs e)
    {
        PerformLogin();
    }

    private void PerformLogin()
    {
        string enteredPassword = TxtPassword.Password;
        if (string.IsNullOrEmpty(enteredPassword))
        {
            TxtError.Text = "Masukkan password administrator.";
            TxtError.Visibility = Visibility.Visible;
            TxtPassword.Focus();
            return;
        }

        bool isValid = PasswordHasher.VerifyPassword(
            enteredPassword,
            Config.Admin.AdminPasswordHash,
            Config.Admin.AdminPasswordSalt);

        if (isValid)
        {
            Log.Information("Otentikasi administrator berhasil");
            _failedAttempts = 0;
            DialogResult = true;
            Close();
            return;
        }

        _failedAttempts++;
        int maxAttempts = Config.Admin.MaxPasswordAttempts > 0 ? Config.Admin.MaxPasswordAttempts : 5;
        int remainingAttempts = maxAttempts - _failedAttempts;

        Log.Warning("Percobaan login administrator gagal. Percobaan {FailedAttempts}/{MaxAttempts}",
            _failedAttempts, maxAttempts);

        if (_failedAttempts >= maxAttempts)
        {
            TriggerLockout();
        }
        else
        {
            TxtError.Text = $"Password salah! Sisa percobaan: {remainingAttempts}.";
            TxtError.Visibility = Visibility.Visible;
            TxtAttemptsInfo.Text = $"Percobaan gagal: {_failedAttempts} / {maxAttempts}";
            TxtPassword.SelectAll();
            TxtPassword.Focus();
        }
    }

    private void TriggerLockout()
    {
        int lockoutSeconds = Config.Admin.PasswordLockoutSeconds > 0 ? Config.Admin.PasswordLockoutSeconds : 30;
        _remainingLockoutSeconds = lockoutSeconds;

        TxtPassword.IsEnabled = false;
        BtnLogin.IsEnabled = false;
        TxtError.Visibility = Visibility.Collapsed;
        TxtLockoutMessage.Text = $"Terlalu banyak percobaan gagal. Akses dikunci selama {_remainingLockoutSeconds} detik.";
        TxtLockoutMessage.Visibility = Visibility.Visible;

        _lockoutTimer.Start();
        Log.Warning("Lockout login administrator diaktifkan selama {Seconds} detik", lockoutSeconds);
    }

    private void LockoutTimer_Tick(object? sender, EventArgs e)
    {
        _remainingLockoutSeconds--;

        if (_remainingLockoutSeconds <= 0)
        {
            _lockoutTimer.Stop();
            _failedAttempts = 0;
            TxtPassword.IsEnabled = true;
            BtnLogin.IsEnabled = true;
            TxtLockoutMessage.Visibility = Visibility.Collapsed;

            int maxAttempts = Config.Admin.MaxPasswordAttempts > 0 ? Config.Admin.MaxPasswordAttempts : 5;
            TxtAttemptsInfo.Text = $"Batas percobaan: {maxAttempts} kali";
            TxtPassword.Clear();
            TxtPassword.Focus();
        }
        else
        {
            TxtLockoutMessage.Text = $"Akun terkunci sementara. Coba lagi dalam {_remainingLockoutSeconds} detik.";
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        _lockoutTimer.Stop();
        DialogResult = false;
        Close();
    }
}
