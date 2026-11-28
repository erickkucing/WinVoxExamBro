using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ExamBro.Core.Models;
using ExamBro.Core.Security;
using ExamBro.WpfHelpers;
using Serilog;

namespace ExamBro.Exam.Views;

/// <summary>
/// Interaction logic for LockScreen.xaml.
/// Displays a full-screen lockout overlay requiring proctor authorization
/// to resume the exam session, with wall-clock attempt tracking and timed lockouts.
/// </summary>
public partial class LockScreen : UserControl
{
    private static readonly ILogger Log = Serilog.Log.ForContext<LockScreen>();

    private ExamConfig? _config;
    private readonly PasswordAttemptTracker _attemptTracker = new();
    private readonly PasswordRevealBinding _passwordReveal;
    private readonly DispatcherTimer _lockoutTimer;

    public event Action? Unlocked;

    public LockScreen()
    {
        InitializeComponent();
        _passwordReveal = PasswordRevealBinding.Bind(
            pwdProctor,
            txtProctorPasswordVisible,
            btnTogglePassword,
            onInputChanged: HideError,
            onEnterPressed: AttemptUnlock);

        _lockoutTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _lockoutTimer.Tick += OnLockoutTick;
        Unloaded += (_, _) => _lockoutTimer.Stop();
    }

    public void Initialize(ExamConfig config)
    {
        _config = config;
    }

    public bool IsLocked => Visibility == Visibility.Visible;

    public void ShowLock(string reason)
    {
        txtReason.Text = reason;
        HideError();
        _passwordReveal.Reset();

        Visibility = Visibility.Visible;

        if (_attemptTracker.IsLockedOut(out int activeRemaining))
        {
            ApplyLockoutUi(activeRemaining);
        }
        else
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => _passwordReveal.FocusActive());
        }
    }

    /// <summary>
    /// Unlocks the session remotely when authorized by proctor from projectCBT web dashboard.
    /// </summary>
    public void RemoteUnlock()
    {
        Log.Information("Remote unlock received from CBT web server");
        _attemptTracker.Reset();
        _lockoutTimer.Stop();
        btnUnlock.IsEnabled = true;
        _passwordReveal.SetEnabled(true);
        _passwordReveal.Reset();
        HideError();
        txtLockoutTimer.Visibility = Visibility.Collapsed;
        txtLockoutTimer.Text = string.Empty;
        Visibility = Visibility.Collapsed;
        Unlocked?.Invoke();
    }

    private void BtnUnlock_Click(object sender, RoutedEventArgs e)
    {
        AttemptUnlock();
    }

    private void AttemptUnlock()
    {
        if (_config == null)
        {
            return;
        }

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
            Log.Information("Proctor successfully unlocked kiosk session");
            _attemptTracker.Reset();
            _lockoutTimer.Stop();
            _passwordReveal.Reset();
            HideError();
            Visibility = Visibility.Collapsed;
            Unlocked?.Invoke();
        }
        else
        {
            int maxAttempts = _config.Admin.MaxPasswordAttempts > 0 ? _config.Admin.MaxPasswordAttempts : 5;
            int remaining = _attemptTracker.RegisterFailedAttempt(
                _config.Admin.MaxPasswordAttempts,
                _config.Admin.PasswordLockoutSeconds,
                out bool triggeredLockout,
                out int lockoutSeconds);

            Log.Warning("Proctor unlock failed. Attempt {Attempt}/{Max}", _attemptTracker.FailedAttempts, maxAttempts);

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
        int clamped = Math.Max(1, remainingSeconds);

        btnUnlock.IsEnabled = false;
        _passwordReveal.SetEnabled(false);

        HideError();
        txtLockoutTimer.Visibility = Visibility.Visible;
        txtLockoutTimer.Text = $"Terlalu banyak percobaan gagal. Silakan tunggu {clamped} detik.";

        _lockoutTimer.Stop();
        _lockoutTimer.Start();
    }

    private void OnLockoutTick(object? sender, EventArgs e)
    {
        if (!_attemptTracker.IsLockedOut(out int remaining))
        {
            _lockoutTimer.Stop();

            btnUnlock.IsEnabled = true;
            _passwordReveal.SetEnabled(true);
            txtLockoutTimer.Visibility = Visibility.Collapsed;
            txtLockoutTimer.Text = string.Empty;

            _passwordReveal.FocusActive();
        }
        else
        {
            txtLockoutTimer.Text = $"Terlalu banyak percobaan gagal. Silakan tunggu {remaining} detik.";
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
