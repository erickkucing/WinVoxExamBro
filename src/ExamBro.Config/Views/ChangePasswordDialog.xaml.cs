using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using ExamBro.Core.Security;

namespace ExamBro.Config.Views;

/// <summary>
/// Interaction logic for ChangePasswordDialog.xaml
/// </summary>
public partial class ChangePasswordDialog : Window
{
    private readonly string _existingHash;
    private readonly string _existingSalt;
    private readonly bool _hasExistingPassword;

    public (string Hash, string Salt) Result { get; private set; }

    public ChangePasswordDialog(string title, string existingHash = "", string existingSalt = "")
    {
        InitializeComponent();
        Title = title;
        TxtTitle.Text = title;
        _existingHash = existingHash;
        _existingSalt = existingSalt;
        _hasExistingPassword = !string.IsNullOrEmpty(existingHash) && !string.IsNullOrEmpty(existingSalt);

        if (!_hasExistingPassword)
        {
            CurrentPasswordPanel.Visibility = Visibility.Collapsed;
            Height = 370;
        }

        Loaded += (_, _) =>
        {
            if (_hasExistingPassword)
                TxtCurrentPassword.Focus();
            else
                TxtNewPassword.Focus();
        };
    }

    private void TxtNewPassword_PasswordChanged(object sender, RoutedEventArgs e)
    {
        TxtError.Visibility = Visibility.Collapsed;
        int score = CalculatePasswordStrength(TxtNewPassword.Password);
        ProgressStrength.Value = score;

        switch (score)
        {
            case 0:
            case 1:
                TxtStrengthLabel.Text = "Kekuatan: Lemah";
                TxtStrengthLabel.Foreground = new SolidColorBrush(Color.FromRgb(211, 47, 47)); // Red
                ProgressStrength.Foreground = new SolidColorBrush(Color.FromRgb(211, 47, 47));
                break;
            case 2:
                TxtStrengthLabel.Text = "Kekuatan: Sedang";
                TxtStrengthLabel.Foreground = new SolidColorBrush(Color.FromRgb(245, 127, 23)); // Orange
                ProgressStrength.Foreground = new SolidColorBrush(Color.FromRgb(245, 127, 23));
                break;
            case 3:
            case 4:
                TxtStrengthLabel.Text = "Kekuatan: Kuat";
                TxtStrengthLabel.Foreground = new SolidColorBrush(Color.FromRgb(56, 142, 60)); // Green
                ProgressStrength.Foreground = new SolidColorBrush(Color.FromRgb(56, 142, 60));
                break;
        }
    }

    private int CalculatePasswordStrength(string password)
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
        if (_hasExistingPassword)
        {
            string currentPass = TxtCurrentPassword.Password;
            if (string.IsNullOrEmpty(currentPass) ||
                !PasswordHasher.VerifyPassword(currentPass, _existingHash, _existingSalt))
            {
                ShowError("Password saat ini salah.");
                TxtCurrentPassword.Focus();
                return;
            }
        }

        string newPass = TxtNewPassword.Password;
        if (string.IsNullOrEmpty(newPass) || newPass.Length < 6)
        {
            ShowError("Password baru harus minimal 6 karakter.");
            TxtNewPassword.Focus();
            return;
        }

        string confirmPass = TxtConfirmPassword.Password;
        if (newPass != confirmPass)
        {
            ShowError("Konfirmasi password baru tidak cocok.");
            TxtConfirmPassword.Focus();
            return;
        }

        Result = PasswordHasher.HashPassword(newPass);
        DialogResult = true;
        Close();
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
