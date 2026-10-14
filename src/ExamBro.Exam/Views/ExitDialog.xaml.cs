using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ExamBro.Core;
using ExamBro.Core.Models;
using ExamBro.Core.Security;
using ExamBro.Exam.Helpers;
using Serilog;

namespace ExamBro.Exam.Views;

/// <summary>
/// Interaction logic for ExitDialog.xaml.
/// Modal dialog prompting the proctor for an authorization password before permitting
/// premature termination of the exam kiosk session.
/// </summary>
public partial class ExitDialog : Window
{
    private static readonly ILogger Log = Serilog.Log.ForContext<ExitDialog>();

    private readonly ExamConfig _config;
    private int _failedAttempts;
    private int _lockoutSecondsRemaining;
    private readonly DispatcherTimer _lockoutTimer;

    public ExitDialog(ExamConfig config)
    {
        InitializeComponent();
        _config = config;

        byte[] logoBytes = BrandingHelper.ResolveLogoBytes(_config.General.LogoBase64, _config.General.LogoPath);
        var logoImage = ImageHelper.CreateImageSource(logoBytes);
        if (logoImage != null)
        {
            Icon = logoImage;
        }

        _lockoutTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _lockoutTimer.Tick += OnLockoutTick;

        Loaded += (s, e) => pwdExit.Focus();
    }

    private void PwdExit_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AttemptExit();
        }
    }

    private void BtnConfirmExit_Click(object sender, RoutedEventArgs e)
    {
        AttemptExit();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void AttemptExit()
    {
        if (_lockoutSecondsRemaining > 0)
        {
            return;
        }

        string input = pwdExit.Password;
        if (string.IsNullOrEmpty(input))
        {
            ShowError("Masukkan kata sandi pengawas.");
            return;
        }

        bool isValid = false;

        // Verify against Proctor Password
        if (!string.IsNullOrEmpty(_config.Admin.ProctorPasswordHash) &&
            !string.IsNullOrEmpty(_config.Admin.ProctorPasswordSalt))
        {
            isValid = PasswordHasher.VerifyPassword(
                input,
                _config.Admin.ProctorPasswordHash,
                _config.Admin.ProctorPasswordSalt);
        }

        // Verify against Admin Password
        if (!isValid &&
            !string.IsNullOrEmpty(_config.Admin.AdminPasswordHash) &&
            !string.IsNullOrEmpty(_config.Admin.AdminPasswordSalt))
        {
            isValid = PasswordHasher.VerifyPassword(
                input,
                _config.Admin.AdminPasswordHash,
                _config.Admin.AdminPasswordSalt);
        }

        // Development fallback
        if (!isValid && string.IsNullOrEmpty(_config.Admin.ProctorPasswordHash) &&
            string.IsNullOrEmpty(_config.Admin.AdminPasswordHash))
        {
            isValid = input == "admin";
        }

        if (isValid)
        {
            Log.Information("Proctor authorized exam exit");
            DialogResult = true;
            Close();
        }
        else
        {
            _failedAttempts++;
            int maxAttempts = _config.Admin.MaxPasswordAttempts > 0 ? _config.Admin.MaxPasswordAttempts : 5;
            int remaining = maxAttempts - _failedAttempts;

            Log.Warning("Proctor exit attempt failed. ({Attempt}/{Max})", _failedAttempts, maxAttempts);

            if (remaining <= 0)
            {
                TriggerLockout();
            }
            else
            {
                ShowError($"Kata sandi salah! Sisa percobaan: {remaining}");
                pwdExit.Password = string.Empty;
                pwdExit.Focus();
            }
        }
    }

    private void TriggerLockout()
    {
        int lockoutSeconds = _config.Admin.PasswordLockoutSeconds > 0 ? _config.Admin.PasswordLockoutSeconds : 30;
        _lockoutSecondsRemaining = lockoutSeconds;

        btnConfirmExit.IsEnabled = false;
        pwdExit.IsEnabled = false;
        pwdExit.Password = string.Empty;

        txtError.Visibility = Visibility.Collapsed;
        txtLockout.Visibility = Visibility.Visible;
        txtLockout.Text = $"Terlalu banyak percobaan gagal. Silakan tunggu {_lockoutSecondsRemaining} detik.";

        _lockoutTimer.Start();
    }

    private void OnLockoutTick(object? sender, EventArgs e)
    {
        _lockoutSecondsRemaining--;
        if (_lockoutSecondsRemaining <= 0)
        {
            _lockoutTimer.Stop();
            _failedAttempts = 0;

            btnConfirmExit.IsEnabled = true;
            pwdExit.IsEnabled = true;
            txtLockout.Visibility = Visibility.Collapsed;
            txtLockout.Text = string.Empty;

            pwdExit.Focus();
        }
        else
        {
            txtLockout.Text = $"Terlalu banyak percobaan gagal. Silakan tunggu {_lockoutSecondsRemaining} detik.";
        }
    }

    private void ShowError(string msg)
    {
        txtError.Text = msg;
        txtError.Visibility = Visibility.Visible;
    }
}
