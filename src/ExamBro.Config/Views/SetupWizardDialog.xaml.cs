using System.Windows;
using ExamBro.Core.Models;
using ExamBro.Core.Security;
using Serilog;

namespace ExamBro.Config.Views;

/// <summary>
/// Interaction logic for SetupWizardDialog.xaml
/// First-run step-by-step setup wizard to configure credentials and Start URL.
/// </summary>
public partial class SetupWizardDialog : Window
{
    private int _currentStep;

    public ExamConfig? Config { get; private set; }

    public SetupWizardDialog()
    {
        InitializeComponent();
        UpdateStepView();
    }

    private void UpdateStepView()
    {
        PanelStep1.Visibility = _currentStep == 0 ? Visibility.Visible : Visibility.Collapsed;
        PanelStep2.Visibility = _currentStep == 1 ? Visibility.Visible : Visibility.Collapsed;
        PanelStep3.Visibility = _currentStep == 2 ? Visibility.Visible : Visibility.Collapsed;
        PanelStep4.Visibility = _currentStep == 3 ? Visibility.Visible : Visibility.Collapsed;
        PanelStep5.Visibility = _currentStep == 4 ? Visibility.Visible : Visibility.Collapsed;

        BtnBack.IsEnabled = _currentStep > 0;
        BtnNext.Visibility = _currentStep < 4 ? Visibility.Visible : Visibility.Collapsed;
        BtnFinish.Visibility = _currentStep == 4 ? Visibility.Visible : Visibility.Collapsed;

        switch (_currentStep)
        {
            case 0:
                TxtHeaderSubtitle.Text = "Langkah 1 dari 5: Selamat Datang";
                break;
            case 1:
                TxtHeaderSubtitle.Text = "Langkah 2 dari 5: Password Administrator";
                TxtAdminPassword.Focus();
                break;
            case 2:
                TxtHeaderSubtitle.Text = "Langkah 3 dari 5: Password Pengawas (Proctor)";
                TxtProctorPassword.Focus();
                break;
            case 3:
                TxtHeaderSubtitle.Text = "Langkah 4 dari 5: Alamat URL Ujian";
                TxtStartUrl.Focus();
                break;
            case 4:
                TxtHeaderSubtitle.Text = "Langkah 5 dari 5: Ringkasan & Konfirmasi";
                TxtSummaryUrl.Text = TxtStartUrl.Text.Trim();
                break;
        }
    }

    private void BtnNext_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateCurrentStep())
            return;

        if (_currentStep < 4)
        {
            _currentStep++;
            UpdateStepView();
        }
    }

    private void BtnBack_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStep > 0)
        {
            _currentStep--;
            UpdateStepView();
        }
    }

    private bool ValidateCurrentStep()
    {
        switch (_currentStep)
        {
            case 0:
                return true;

            case 1:
                string adminPass = TxtAdminPassword.Password;
                string adminConfirm = TxtAdminPasswordConfirm.Password;

                if (string.IsNullOrEmpty(adminPass) || adminPass.Length < 6)
                {
                    TxtAdminError.Text = "Password administrator harus minimal 6 karakter.";
                    TxtAdminError.Visibility = Visibility.Visible;
                    TxtAdminPassword.Focus();
                    return false;
                }

                if (adminPass != adminConfirm)
                {
                    TxtAdminError.Text = "Konfirmasi password administrator tidak cocok.";
                    TxtAdminError.Visibility = Visibility.Visible;
                    TxtAdminPasswordConfirm.Focus();
                    return false;
                }

                TxtAdminError.Visibility = Visibility.Collapsed;
                return true;

            case 2:
                string proctorPass = TxtProctorPassword.Password;
                string proctorConfirm = TxtProctorPasswordConfirm.Password;

                if (string.IsNullOrEmpty(proctorPass) || proctorPass.Length < 6)
                {
                    TxtProctorError.Text = "Password pengawas harus minimal 6 karakter.";
                    TxtProctorError.Visibility = Visibility.Visible;
                    TxtProctorPassword.Focus();
                    return false;
                }

                if (proctorPass != proctorConfirm)
                {
                    TxtProctorError.Text = "Konfirmasi password pengawas tidak cocok.";
                    TxtProctorError.Visibility = Visibility.Visible;
                    TxtProctorPasswordConfirm.Focus();
                    return false;
                }

                TxtProctorError.Visibility = Visibility.Collapsed;
                return true;

            case 3:
                string rawUrl = TxtStartUrl.Text.Trim();
                if (string.IsNullOrEmpty(rawUrl))
                {
                    TxtUrlError.Text = "Start URL tidak boleh kosong.";
                    TxtUrlError.Visibility = Visibility.Visible;
                    TxtStartUrl.Focus();
                    return false;
                }

                if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out Uri? uri) ||
                    (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) &&
                     !uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)))
                {
                    TxtUrlError.Text = "Format URL tidak valid. Gunakan format 'https://domain.com/path'.";
                    TxtUrlError.Visibility = Visibility.Visible;
                    TxtStartUrl.Focus();
                    return false;
                }

                TxtUrlError.Visibility = Visibility.Collapsed;
                return true;

            default:
                return true;
        }
    }

    private void BtnFinish_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var config = ExamConfig.CreateDefault();

            // Setup credentials
            var (adminHash, adminSalt) = PasswordHasher.HashPassword(TxtAdminPassword.Password);
            config.Admin.AdminPasswordHash = adminHash;
            config.Admin.AdminPasswordSalt = adminSalt;

            var (proctorHash, proctorSalt) = PasswordHasher.HashPassword(TxtProctorPassword.Password);
            config.Admin.ProctorPasswordHash = proctorHash;
            config.Admin.ProctorPasswordSalt = proctorSalt;

            // Setup URL
            string startUrl = TxtStartUrl.Text.Trim();
            config.General.StartUrl = startUrl;
            bool autoAllow = ChkAutoAllowHost.IsChecked == true;
            config.Navigation.AutoAllowStartUrlHost = autoAllow;

            if (autoAllow && Uri.TryCreate(startUrl, UriKind.Absolute, out Uri? uri))
            {
                string httpsPattern = $"https://{uri.Host}/*";
                string httpPattern = $"http://{uri.Host}/*";
                if (!config.Navigation.AllowedUrlPatterns.Contains(httpsPattern))
                    config.Navigation.AllowedUrlPatterns.Add(httpsPattern);
                if (!config.Navigation.AllowedUrlPatterns.Contains(httpPattern))
                    config.Navigation.AllowedUrlPatterns.Add(httpPattern);
            }

            Config = config;
            Log.Information("Wisaya konfigurasi awal berhasil diselesaikan");

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Gagal menyelesaikan wisaya konfigurasi awal");
            MessageBox.Show(
                $"Terjadi kesalahan saat memproses konfigurasi: {ex.Message}",
                "Kesalahan",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
