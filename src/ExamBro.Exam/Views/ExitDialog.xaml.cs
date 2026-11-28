using System.Windows;
using System.Windows.Threading;
using ExamBro.Core;
using ExamBro.Core.Models;
using ExamBro.Core.Security;
using ExamBro.Exam.Helpers;
using ExamBro.WpfHelpers;
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
    private readonly PasswordRevealBinding _passwordReveal;
    private int _lockoutSecondsRemaining;
    private readonly DispatcherTimer _lockoutTimer;

    public ExitDialog(ExamConfig config, PasswordAttemptTracker? attemptTracker = null)
    {
        InitializeComponent();
        _config = config;
        _attemptTracker = attemptTracker ?? new PasswordAttemptTracker();
        _passwordReveal = PasswordRevealBinding.Bind(
            pwdExit,
            txtExitPasswordVisible,
            btnTogglePassword,
            onInputChanged: HideError,
            onEnterPressed: AttemptExit);

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
            Activate();
            if (_attemptTracker.IsLockedOut(out int remaining))
            {
                ApplyLockoutUi(remaining);
            }
            else
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () => _passwordReveal.FocusActive());
            }
        };
        Closing += (_, _) =>
        {
            _lockoutTimer.Stop();
            _passwordReveal.Reset();
        };
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

        string input = _passwordReveal.GetPassword();
        if (string.IsNullOrEmpty(input))
        {
            ShowError("Masukkan kata sandi pengawas.");
            _passwordReveal.FocusActive();
            return;
        }

        bool isValid = PasswordVerifier.VerifyProctorOrAdmin(_config, input);

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
                _passwordReveal.Clear();
                _passwordReveal.FocusActive();
            }
        }
    }

    private void ApplyLockoutUi(int remainingSeconds)
    {
        _lockoutSecondsRemaining = Math.Max(1, remainingSeconds);

        btnConfirmExit.IsEnabled = false;
        _passwordReveal.SetEnabled(false);

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
            _passwordReveal.SetEnabled(true);
            txtLockout.Visibility = Visibility.Collapsed;
            txtLockout.Text = string.Empty;

            _passwordReveal.FocusActive();
        }
        else
        {
            _lockoutSecondsRemaining = trackerRemaining;
            txtLockout.Text = $"Terlalu banyak percobaan gagal. Silakan tunggu {_lockoutSecondsRemaining} detik.";
        }
    }

    private void HideError()
    {
        if (txtError.Visibility == Visibility.Visible)
        {
            txtError.Visibility = Visibility.Collapsed;
            txtError.Text = string.Empty;
        }
    }

    private void ShowError(string msg)
    {
        txtError.Text = msg;
        txtError.Visibility = Visibility.Visible;
    }
}
