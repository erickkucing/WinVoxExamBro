using System.Windows;
using System.Windows.Threading;
using ExamBro.WpfHelpers;

namespace ExamBro.Config.Views;

/// <summary>
/// Interaction logic for PasswordPromptDialog.xaml
/// Prompts the user for a password, optionally requiring confirmation.
/// </summary>
public partial class PasswordPromptDialog : Window
{
    private readonly bool _requireConfirm;
    private readonly PasswordRevealBinding _passwordReveal;
    private readonly PasswordRevealBinding _confirmReveal;

    public string Password { get; private set; } = string.Empty;

    public PasswordPromptDialog(string title, string message, bool requireConfirm = false)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        PromptMessageText.Text = message;
        _requireConfirm = requireConfirm;

        Action hideErr = () => TxtError.Visibility = Visibility.Collapsed;
        _passwordReveal = PasswordRevealBinding.Bind(TxtPassword, TxtPasswordVisible, BtnTogglePassword, hideErr, Submit);
        _confirmReveal = PasswordRevealBinding.Bind(TxtConfirmPassword, TxtConfirmPasswordVisible, BtnToggleConfirmPassword, hideErr, Submit);

        if (_requireConfirm)
        {
            ConfirmPanel.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) =>
        {
            Activate();
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => _passwordReveal.FocusActive());
        };
        Closing += (_, _) =>
        {
            _passwordReveal.Reset();
            _confirmReveal.Reset();
        };
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        Submit();
    }

    private void Submit()
    {
        string pass = _passwordReveal.GetPassword();
        if (string.IsNullOrWhiteSpace(pass))
        {
            TxtError.Text = "Password tidak boleh kosong.";
            TxtError.Visibility = Visibility.Visible;
            _passwordReveal.FocusActive();
            return;
        }

        if (_requireConfirm)
        {
            string confirm = _confirmReveal.GetPassword();
            if (pass != confirm)
            {
                TxtError.Text = "Konfirmasi password tidak cocok.";
                TxtError.Visibility = Visibility.Visible;
                _confirmReveal.FocusActive();
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
