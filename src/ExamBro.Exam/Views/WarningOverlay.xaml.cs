using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ExamBro.Exam.Views;

/// <summary>
/// Interaction logic for WarningOverlay.xaml.
/// Displays a temporary semi-transparent violation warning that auto-dismisses after 5 seconds
/// or when acknowledged by the student.
/// </summary>
public partial class WarningOverlay : UserControl
{
    private readonly DispatcherTimer _timer;
    private int _secondsRemaining = 5;

    public event Action? Dismissed;

    public WarningOverlay()
    {
        InitializeComponent();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += OnTimerTick;
    }

    public void ShowWarning(string message, int occurrenceCount)
    {
        txtMessage.Text = message;
        _secondsRemaining = 5;
        txtCountdown.Text = $"Peringatan ini akan ditutup otomatis dalam {_secondsRemaining} detik.";
        Visibility = Visibility.Visible;

        _timer.Stop();
        _timer.Start();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _secondsRemaining--;
        if (_secondsRemaining <= 0)
        {
            Dismiss();
        }
        else
        {
            txtCountdown.Text = $"Peringatan ini akan ditutup otomatis dalam {_secondsRemaining} detik.";
        }
    }

    private void BtnDismiss_Click(object sender, RoutedEventArgs e)
    {
        Dismiss();
    }

    public void Dismiss()
    {
        _timer.Stop();
        Visibility = Visibility.Collapsed;
        Dismissed?.Invoke();
    }
}
