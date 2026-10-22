using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ExamBro.Exam.Views;

/// <summary>
/// Interaction logic for CompletionOverlay.xaml.
/// Displays a completion screen when the exam completion URL is reached.
/// The application does NOT close automatically; it stays locked until an authorized proctor exits.
/// </summary>
public partial class CompletionOverlay : UserControl
{
    public event Action? ExitRequested;

    public CompletionOverlay()
    {
        InitializeComponent();
    }

    public void ShowCompletion(int autoCloseSeconds = 5)
    {
        txtCountdown.Text = "Ujian telah selesai. Aplikasi terkunci dan hanya dapat ditutup oleh pengawas dengan password.";
        Visibility = Visibility.Visible;
    }

    private void BtnExitNow_Click(object sender, RoutedEventArgs e)
    {
        ExitRequested?.Invoke();
    }
}
