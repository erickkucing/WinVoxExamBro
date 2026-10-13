using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ExamBro.Exam.Views;

/// <summary>
/// Interaction logic for CompletionOverlay.xaml.
/// Displays a completion screen with a countdown timer when the exam completion URL is reached.
/// </summary>
public partial class CompletionOverlay : UserControl
{
    private readonly DispatcherTimer _timer;
    private int _secondsRemaining = 5;

    public event Action? ExitRequested;

    public CompletionOverlay()
    {
        InitializeComponent();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += OnTimerTick;
    }

    public void ShowCompletion(int autoCloseSeconds = 5)
    {
        _secondsRemaining = autoCloseSeconds > 0 ? autoCloseSeconds : 5;
        txtCountdown.Text = $"Aplikasi akan ditutup otomatis dalam {_secondsRemaining} detik.";
        Visibility = Visibility.Visible;

        _timer.Stop();
        _timer.Start();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _secondsRemaining--;
        if (_secondsRemaining <= 0)
        {
            _timer.Stop();
            ExitRequested?.Invoke();
        }
        else
        {
            txtCountdown.Text = $"Aplikasi akan ditutup otomatis dalam {_secondsRemaining} detik.";
        }
    }

    private void BtnExitNow_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        ExitRequested?.Invoke();
    }
}
