using System.IO;
using System.Windows;
using System.Windows.Input;
using ExamBro.Core;
using Microsoft.Web.WebView2.Core;
using Serilog;

namespace ExamBro.Config.Views;

/// <summary>
/// Interaction logic for TestUrlWindow.xaml
/// Provides a sandbox WebView2 browser window to test reachability of the Start URL.
/// </summary>
public partial class TestUrlWindow : Window
{
    private bool _isInitialized;

    public TestUrlWindow(string initialUrl)
    {
        InitializeComponent();
        TxtUrl.Text = initialUrl;
        Loaded += TestUrlWindow_Loaded;
        Closing += TestUrlWindow_Closing;
    }

    private async void TestUrlWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await InitializeWebViewAsync();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            TxtStatus.Text = "Menginisialisasi komponen WebView2...";
            LoadProgress.Visibility = Visibility.Visible;

            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Constants.AppName,
                "WebView2TestProfile");

            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await webView.EnsureCoreWebView2Async(env);

            _isInitialized = true;
            webView.NavigationStarting += WebView_NavigationStarting;
            webView.NavigationCompleted += WebView_NavigationCompleted;

            TxtStatus.Text = "WebView2 siap.";
            LoadProgress.Visibility = Visibility.Collapsed;

            if (!string.IsNullOrWhiteSpace(TxtUrl.Text))
            {
                NavigateToUrl(TxtUrl.Text);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Gagal menginisialisasi WebView2 untuk uji URL");
            TxtStatus.Text = $"Gagal memuat WebView2 runtime: {ex.Message}";
            LoadProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void NavigateToUrl(string rawUrl)
    {
        if (!_isInitialized)
        {
            TxtStatus.Text = "Browser belum selesai diinisialisasi.";
            return;
        }

        string target = rawUrl.Trim();
        if (!target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            target = "https://" + target;
            TxtUrl.Text = target;
        }

        if (Uri.TryCreate(target, UriKind.Absolute, out Uri? validUri))
        {
            LoadProgress.Visibility = Visibility.Visible;
            TxtStatus.Text = $"Membuka: {validUri.AbsoluteUri}...";
            webView.Source = validUri;
        }
        else
        {
            TxtStatus.Text = "URL tidak valid. Harap periksa format URL.";
            LoadProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void WebView_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        LoadProgress.Visibility = Visibility.Visible;
        TxtStatus.Text = $"Memuat: {e.Uri}...";
    }

    private void WebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        LoadProgress.Visibility = Visibility.Collapsed;
        if (e.IsSuccess)
        {
            TxtStatus.Text = $"Halaman berhasil dimuat (Status: OK).";
            Log.Information("Uji Start URL berhasil: {Url}", webView.Source);
        }
        else
        {
            TxtStatus.Text = $"Gagal memuat halaman. Error: {e.WebErrorStatus}";
            Log.Warning("Uji Start URL gagal: {Url}, Error: {ErrorStatus}", webView.Source, e.WebErrorStatus);
        }
    }

    private void BtnNavigate_Click(object sender, RoutedEventArgs e)
    {
        NavigateToUrl(TxtUrl.Text);
    }

    private void TxtUrl_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            NavigateToUrl(TxtUrl.Text);
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void TestUrlWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            webView.Dispose();
        }
        catch
        {
            // Ignore disposal errors on closing
        }
    }
}
