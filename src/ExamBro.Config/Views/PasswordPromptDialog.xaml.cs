using System.Windows;

namespace ExamBro.Config.Views;

/// <summary>
/// Interaction logic for PasswordPromptDialog.xaml
/// Prompts the user for a password, optionally requiring confirmation.
/// </summary>
public partial class PasswordPromptDialog : Window
{
    private readonly bool _requireConfirm;

    public string Password { get; private set; } = string.Empty;

    public PasswordPromptDialog(string title, string message, bool requireConfirm = false)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        PromptMessageText.Text = message;
        _requireConfirm = requireConfirm;

        if (_requireConfirm)
        {
            ConfirmPanel.Visibility = Visibility.Visible;
            Height = 330;
        }

        Loaded += (_, _) => TxtPassword.Focus();
    }

    private void TxtPassword_PasswordChanged(object sender, RoutedEventArgs e)
    {
        TxtError.Visibility = Visibility.Collapsed;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        string pass = TxtPassword.Password;
        if (string.IsNullOrWhiteSpace(pass))
        {
            TxtError.Text = "Password tidak boleh kosong.";
            TxtError.Visibility = Visibility.Visible;
            TxtPassword.Focus();
            return;
        }

        if (_requireConfirm)
        {
            string confirm = TxtConfirmPassword.Password;
            if (pass != confirm)
            {
                TxtError.Text = "Konfirmasi password tidak cocok.";
                TxtError.Visibility = Visibility.Visible;
                TxtConfirmPassword.Focus();
                return;
            }
        }

        Password = pass;
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
