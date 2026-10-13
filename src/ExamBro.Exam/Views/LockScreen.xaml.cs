using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ExamBro.Core.Models;
using ExamBro.Core.Security;
using Serilog;

namespace ExamBro.Exam.Views;

/// <summary>
/// Interaction logic for LockScreen.xaml.
/// Displays an unbypassable full-screen lockout window requiring proctor authorization
/// to resume the exam session, with attempt limiting and timed lockouts.
/// </summary>
public partial class LockScreen : UserControl
{
    private static readonly ILogger Log = Serilog.Log.ForContext<LockScreen>();

    private ExamConfig? _config;
    private int _failedAttempts;
    private int _lockoutSecondsRemaining;
    private readonly DispatcherTimer _lockoutTimer;

    public event Action? Unlocked;

    public LockScreen()
    {
        InitializeComponent();

        _lockoutTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _lockoutTimer.Tick += OnLockoutTick;
    }

    public void Initialize(ExamConfig config)
    {
        _config = config;
    }

    public void ShowLock(string reason)
    {
        txtReason.Text = reason;
        txtError.Visibility = Visibility.Collapsed;
        txtError.Text = string.Empty;
        pwdProctor.Password = string.Empty;

        Visibility = Visibility.Visible;
        pwdProctor.Focus();
    }

    private void PwdProctor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AttemptUnlock();
        }
    }

    private void BtnUnlock_Click(object sender, RoutedEventArgs e)
    {
        AttemptUnlock();
    }

    private void AttemptUnlock()
    {
        if (_config == null || _lockoutSecondsRemaining > 0)
        {
            return;
        }

        string input = pwdProctor.Password;
        if (string.IsNullOrEmpty(input))
        {
            ShowError("Masukkan kata sandi pengawas.");
            return;
        }

        bool isValid = false;

        // 1. Verify against Proctor password
        if (!string.IsNullOrEmpty(_config.Admin.ProctorPasswordHash) &&
            !string.IsNullOrEmpty(_config.Admin.ProctorPasswordSalt))
        {
            isValid = PasswordHasher.VerifyPassword(
                input,
                _config.Admin.ProctorPasswordHash,
                _config.Admin.ProctorPasswordSalt);
        }

        // 2. Also check Admin password as an override
        if (!isValid &&
            !string.IsNullOrEmpty(_config.Admin.AdminPasswordHash) &&
            !string.IsNullOrEmpty(_config.Admin.AdminPasswordSalt))
        {
            isValid = PasswordHasher.VerifyPassword(
                input,
                _config.Admin.AdminPasswordHash,
                _config.Admin.AdminPasswordSalt);
        }

        // 3. Fallback for unconfigured dev/test environments
        if (!isValid && string.IsNullOrEmpty(_config.Admin.ProctorPasswordHash) &&
            string.IsNullOrEmpty(_config.Admin.AdminPasswordHash))
        {
            // If neither is set, allow default "admin" or empty
            isValid = input == "admin";
        }

        if (isValid)
        {
            Log.Information("Proctor successfully unlocked kiosk session");
            _failedAttempts = 0;
            pwdProctor.Password = string.Empty;
            Visibility = Visibility.Collapsed;
            Unlocked?.Invoke();
        }
        else
        {
            _failedAttempts++;
            int maxAttempts = _config.Admin.MaxPasswordAttempts > 0 ? _config.Admin.MaxPasswordAttempts : 5;
            int remaining = maxAttempts - _failedAttempts;

            Log.Warning("Proctor unlock failed. Attempt {Attempt}/{Max}", _failedAttempts, maxAttempts);

            if (remaining <= 0)
            {
                TriggerLockout();
            }
            else
            {
                ShowError($"Kata sandi salah! Sisa percobaan: {remaining}");
                pwdProctor.Password = string.Empty;
                pwdProctor.Focus();
            }
        }
    }

    private void TriggerLockout()
    {
        int lockoutSeconds = _config?.Admin.PasswordLockoutSeconds > 0 ? _config.Admin.PasswordLockoutSeconds : 30;
        _lockoutSecondsRemaining = lockoutSeconds;

        btnUnlock.IsEnabled = false;
        pwdProctor.IsEnabled = false;
        pwdProctor.Password = string.Empty;

        txtError.Visibility = Visibility.Collapsed;
        txtLockoutTimer.Visibility = Visibility.Visible;
        txtLockoutTimer.Text = $"Terlalu banyak percobaan gagal. Silakan tunggu {_lockoutSecondsRemaining} detik.";

        _lockoutTimer.Start();
    }

    private void OnLockoutTick(object? sender, EventArgs e)
    {
        _lockoutSecondsRemaining--;
        if (_lockoutSecondsRemaining <= 0)
        {
            _lockoutTimer.Stop();
            _failedAttempts = 0;

            btnUnlock.IsEnabled = true;
            pwdProctor.IsEnabled = true;
            txtLockoutTimer.Visibility = Visibility.Collapsed;
            txtLockoutTimer.Text = string.Empty;

            pwdProctor.Focus();
        }
        else
        {
            txtLockoutTimer.Text = $"Terlalu banyak percobaan gagal. Silakan tunggu {_lockoutSecondsRemaining} detik.";
        }
    }

    private void ShowError(string msg)
    {
        txtError.Text = msg;
        txtError.Visibility = Visibility.Visible;
    }
}
