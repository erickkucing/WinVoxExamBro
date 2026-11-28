using System.Windows;
using System.Windows.Threading;
using ExamBro.Core.Models;
using ExamBro.Core.Security;
using ExamBro.WpfHelpers;
using Serilog;

namespace ExamBro.Config.Views;

/// <summary>
/// Interaction logic for LoginDialog.xaml
/// Modal dialog for administrator authentication with brute-force lockout protection.
/// </summary>
public partial class LoginDialog : Window
{
    private readonly PasswordAttemptTracker _attemptTracker = new();
    private readonly PasswordRevealBinding _passwordReveal;
    private readonly DispatcherTimer _lockoutTimer;

    public ExamConfig Config { get; }

    public LoginDialog(ExamConfig config)
    {
        InitializeComponent();
        Config = config;
        _passwordReveal = PasswordRevealBinding.Bind(
            TxtPassword,
            TxtPasswordVisible,
            BtnTogglePassword,
            onInputChanged: () => TxtError.Visibility = Visibility.Collapsed,
            onEnterPressed: () =>
            {
                if (BtnLogin.IsEnabled)
                {
                    PerformLogin();
                }
            });

        int maxAttempts = Config.Admin.MaxPasswordAttempts > 0 ? Config.Admin.MaxPasswordAttempts : 5;
        TxtAttemptsInfo.Text = $"Batas percobaan: {maxAttempts} kali";

        _lockoutTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _lockoutTimer.Tick += LockoutTimer_Tick;

        Loaded += (_, _) =>
        {
            Activate();
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => _passwordReveal.FocusActive());
        };
        Closing += (_, _) =>
        {
            _lockoutTimer.Stop();
            _passwordReveal.Reset();
        };
    }

    private void BtnLogin_Click(object sender, RoutedEventArgs e)
    {
        PerformLogin();
    }

    private void PerformLogin()
    {
        if (_attemptTracker.IsLockedOut(out int activeRemaining))
        {
            ApplyLockoutUi(activeRemaining);
            return;
        }

        string enteredPassword = _passwordReveal.GetPassword();
        if (string.IsNullOrEmpty(enteredPassword))
        {
            TxtError.Text = "Masukkan password administrator.";
            TxtError.Visibility = Visibility.Visible;
            _passwordReveal.FocusActive();
            return;
        }

        bool isValid = PasswordHasher.VerifyPassword(
            enteredPassword,
            Config.Admin.AdminPasswordHash,
            Config.Admin.AdminPasswordSalt);

        if (isValid)
        {
            Log.Information("Otentikasi administrator berhasil");
            _attemptTracker.Reset();
            DialogResult = true;
            Close();
            return;
        }

        int maxAttempts = Config.Admin.MaxPasswordAttempts > 0 ? Config.Admin.MaxPasswordAttempts : 5;
        int remainingAttempts = _attemptTracker.RegisterFailedAttempt(
            Config.Admin.MaxPasswordAttempts,
            Config.Admin.PasswordLockoutSeconds,
            out bool triggeredLockout,
            out int lockoutSeconds);

        Log.Warning("Percobaan login administrator gagal. Percobaan {FailedAttempts}/{MaxAttempts}",
            _attemptTracker.FailedAttempts, maxAttempts);

        if (triggeredLockout)
        {
            ApplyLockoutUi(lockoutSeconds);
        }
        else
        {
            TxtError.Text = $"Password salah! Sisa percobaan: {remainingAttempts}.";
            TxtError.Visibility = Visibility.Visible;
            TxtAttemptsInfo.Text = $"Percobaan gagal: {_attemptTracker.FailedAttempts} / {maxAttempts}";
            _passwordReveal.SelectAllAndFocus();
        }
    }

    private void ApplyLockoutUi(int remainingSeconds)
    {
        int clamped = Math.Max(1, remainingSeconds);
        _passwordReveal.SetEnabled(false);
        BtnLogin.IsEnabled = false;
        TxtError.Visibility = Visibility.Collapsed;
        TxtLockoutMessage.Text = $"Terlalu banyak percobaan gagal. Akses dikunci selama {clamped} detik.";
        TxtLockoutMessage.Visibility = Visibility.Visible;

        _lockoutTimer.Stop();
        _lockoutTimer.Start();
        Log.Warning("Lockout login administrator diaktifkan selama {Seconds} detik", clamped);
    }

    private void LockoutTimer_Tick(object? sender, EventArgs e)
    {
        if (!_attemptTracker.IsLockedOut(out int remaining))
        {
            _lockoutTimer.Stop();
            _passwordReveal.SetEnabled(true);
            BtnLogin.IsEnabled = true;
            TxtLockoutMessage.Visibility = Visibility.Collapsed;

            int maxAttempts = Config.Admin.MaxPasswordAttempts > 0 ? Config.Admin.MaxPasswordAttempts : 5;
            TxtAttemptsInfo.Text = $"Batas percobaan: {maxAttempts} kali";
            _passwordReveal.Clear();
            _passwordReveal.FocusActive();
        }
        else
        {
            TxtLockoutMessage.Text = $"Akun terkunci sementara. Coba lagi dalam {remaining} detik.";
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
