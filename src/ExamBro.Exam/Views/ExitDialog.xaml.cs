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
/// premature termination of the exam kiosk session. Uses session-scoped attempt tracking
/// so canceling and reopening the dialog cannot reset lockout counters.
/// </summary>
public partial class ExitDialog : Window
{
    private static readonly ILogger Log = Serilog.Log.ForContext<ExitDialog>();

    private readonly ExamConfig _config;
    private readonly PasswordAttemptTracker _attemptTracker;
    private int _lockoutSecondsRemaining;
    private readonly DispatcherTimer _lockoutTimer;

    public ExitDialog(ExamConfig config, PasswordAttemptTracker? attemptTracker = null)
    {
        InitializeComponent();
        _config = config;
        _attemptTracker = attemptTracker ?? new PasswordAttemptTracker();

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

        Loaded += (_, _) =>
        {
            if (_attemptTracker.IsLockedOut(out int remaining))
            {
                ApplyLockoutUi(remaining);
            }
            else
            {
                pwdExit.Focus();
            }
        };
        Closing += (_, _) => _lockoutTimer.Stop();
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
        if (_attemptTracker.IsLockedOut(out int activeRemaining))
        {
            ApplyLockoutUi(activeRemaining);
            return;
        }

        string input = pwdExit.Password;
        if (string.IsNullOrEmpty(input))
        {
            ShowError("Masukkan kata sandi pengawas.");
            return;
        }

        bool isValid = false;

        if (!string.IsNullOrEmpty(_config.Admin.ProctorPasswordHash) &&
            !string.IsNullOrEmpty(_config.Admin.ProctorPasswordSalt))
        {
            isValid = PasswordHasher.VerifyPassword(
                input,
                _config.Admin.ProctorPasswordHash,
                _config.Admin.ProctorPasswordSalt);
        }

        if (!isValid &&
            !string.IsNullOrEmpty(_config.Admin.AdminPasswordHash) &&
            !string.IsNullOrEmpty(_config.Admin.AdminPasswordSalt))
        {
            isValid = PasswordHasher.VerifyPassword(
                input,
                _config.Admin.AdminPasswordHash,
                _config.Admin.AdminPasswordSalt);
        }

        if (!isValid && string.IsNullOrEmpty(_config.Admin.ProctorPasswordHash) &&
            string.IsNullOrEmpty(_config.Admin.AdminPasswordHash))
        {
            isValid = input == "admin";
        }

        if (isValid)
        {
            _attemptTracker.Reset();
            Log.Information("Proctor authorized exam exit");
            DialogResult = true;
            Close();
        }
        else
        {
            int maxAttempts = _config.Admin.MaxPasswordAttempts > 0 ? _config.Admin.MaxPasswordAttempts : 5;
            int remaining = _attemptTracker.RegisterFailedAttempt(
                _config.Admin.MaxPasswordAttempts,
                _config.Admin.PasswordLockoutSeconds,
                out bool triggeredLockout,
                out int lockoutSeconds);

            Log.Warning("Proctor exit attempt failed. ({Attempt}/{Max})", _attemptTracker.FailedAttempts, maxAttempts);

            if (triggeredLockout)
            {
                ApplyLockoutUi(lockoutSeconds);
            }
            else
            {
                ShowError($"Kata sandi salah! Sisa percobaan: {remaining}");
                pwdExit.Password = string.Empty;
                pwdExit.Focus();
            }
        }
    }

    private void ApplyLockoutUi(int remainingSeconds)
    {
        _lockoutSecondsRemaining = Math.Max(1, remainingSeconds);

        btnConfirmExit.IsEnabled = false;
        pwdExit.IsEnabled = false;
        pwdExit.Password = string.Empty;

        txtError.Visibility = Visibility.Collapsed;
        txtLockout.Visibility = Visibility.Visible;
        txtLockout.Text = $"Terlalu banyak percobaan gagal. Silakan tunggu {_lockoutSecondsRemaining} detik.";

        _lockoutTimer.Stop();
        _lockoutTimer.Start();
    }

    private void OnLockoutTick(object? sender, EventArgs e)
    {
        if (!_attemptTracker.IsLockedOut(out int trackerRemaining))
        {
            _lockoutTimer.Stop();

            btnConfirmExit.IsEnabled = true;
            pwdExit.IsEnabled = true;
            txtLockout.Visibility = Visibility.Collapsed;
            txtLockout.Text = string.Empty;

            pwdExit.Focus();
        }
        else
        {
            _lockoutSecondsRemaining = trackerRemaining;
            txtLockout.Text = $"Terlalu banyak percobaan gagal. Silakan tunggu {_lockoutSecondsRemaining} detik.";
        }
    }

    private void ShowError(string msg)
    {
        txtError.Text = msg;
        txtError.Visibility = Visibility.Visible;
    }
}
