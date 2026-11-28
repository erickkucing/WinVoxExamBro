using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ExamBro.Core.Security;
using ExamBro.WpfHelpers;

namespace ExamBro.Config.Views;

/// <summary>
/// Interaction logic for ChangePasswordDialog.xaml
/// </summary>
public partial class ChangePasswordDialog : Window
{
    private readonly string _existingHash;
    private readonly string _existingSalt;
    private readonly bool _hasExistingPassword;
    private readonly PasswordRevealBinding _currentReveal;
    private readonly PasswordRevealBinding _newReveal;
    private readonly PasswordRevealBinding _confirmReveal;

    public (string Hash, string Salt) Result { get; private set; }

    public ChangePasswordDialog(string title, string existingHash = "", string existingSalt = "")
    {
        InitializeComponent();
        Title = title;
        TxtTitle.Text = title;
        _existingHash = existingHash;
        _existingSalt = existingSalt;
        _hasExistingPassword = !string.IsNullOrEmpty(existingHash) && !string.IsNullOrEmpty(existingSalt);

        _currentReveal = PasswordRevealBinding.Bind(
            TxtCurrentPassword,
            TxtCurrentPasswordVisible,
            BtnToggleCurrentPassword,
            onInputChanged: HideError,
            onEnterPressed: SavePassword);

        _newReveal = PasswordRevealBinding.Bind(
            TxtNewPassword,
            TxtNewPasswordVisible,
            BtnToggleNewPassword,
            onInputChanged: OnNewPasswordChanged,
            onEnterPressed: SavePassword);

        _confirmReveal = PasswordRevealBinding.Bind(
            TxtConfirmPassword,
            TxtConfirmPasswordVisible,
            BtnToggleConfirmPassword,
            onInputChanged: HideError,
            onEnterPressed: SavePassword);

        if (!_hasExistingPassword)
        {
            CurrentPasswordPanel.Visibility = Visibility.Collapsed;
        }

        Loaded += (_, _) =>
        {
            Activate();
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (_hasExistingPassword)
                    _currentReveal.FocusActive();
                else
                    _newReveal.FocusActive();
            });
        };
        Closing += (_, _) =>
        {
            _currentReveal.Reset();
            _newReveal.Reset();
            _confirmReveal.Reset();
        };
    }

    private void OnNewPasswordChanged()
    {
        HideError();
        int score = CalculatePasswordStrength(_newReveal.GetPassword());
        ProgressStrength.Value = score;

        switch (score)
        {
            case 0:
            case 1:
                TxtStrengthLabel.Text = "Kekuatan: Lemah";
                TxtStrengthLabel.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28));
                ProgressStrength.Foreground = new SolidColorBrush(Color.FromRgb(220, 38, 38));
                break;
            case 2:
                TxtStrengthLabel.Text = "Kekuatan: Sedang";
                TxtStrengthLabel.Foreground = new SolidColorBrush(Color.FromRgb(154, 52, 18));
                ProgressStrength.Foreground = new SolidColorBrush(Color.FromRgb(234, 88, 12));
                break;
            case 3:
            case 4:
                TxtStrengthLabel.Text = "Kekuatan: Kuat";
                TxtStrengthLabel.Foreground = new SolidColorBrush(Color.FromRgb(21, 128, 61));
                ProgressStrength.Foreground = new SolidColorBrush(Color.FromRgb(22, 163, 74));
                break;
        }
    }

    private static int CalculatePasswordStrength(string password)
    {
        if (string.IsNullOrEmpty(password))
            return 0;

        int score = 0;
        if (password.Length >= 6) score++;
        if (password.Length >= 10) score++;
        if (Regex.IsMatch(password, @"\d")) score++;
        if (Regex.IsMatch(password, @"[A-Z]") && Regex.IsMatch(password, @"[^a-zA-Z0-9]")) score++;

        return Math.Min(score, 4);
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        SavePassword();
    }

    private void SavePassword()
    {
        if (_hasExistingPassword)
        {
            string currentPass = _currentReveal.GetPassword();
            if (string.IsNullOrEmpty(currentPass) ||
                !PasswordHasher.VerifyPassword(currentPass, _existingHash, _existingSalt))
            {
                ShowError("Password saat ini salah.");
                _currentReveal.FocusActive();
                return;
            }
        }

        string newPass = _newReveal.GetPassword();
        if (string.IsNullOrEmpty(newPass) || newPass.Length < 6)
        {
            ShowError("Password baru harus minimal 6 karakter.");
            _newReveal.FocusActive();
            return;
        }

        string confirmPass = _confirmReveal.GetPassword();
        if (newPass != confirmPass)
        {
            ShowError("Konfirmasi password baru tidak cocok.");
            _confirmReveal.FocusActive();
            return;
        }

        Result = PasswordHasher.HashPassword(newPass);
        DialogResult = true;
        Close();
    }

    private void HideError()
    {
        TxtError.Visibility = Visibility.Collapsed;
    }

    private void ShowError(string message)
    {
        TxtError.Text = message;
        TxtError.Visibility = Visibility.Visible;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
