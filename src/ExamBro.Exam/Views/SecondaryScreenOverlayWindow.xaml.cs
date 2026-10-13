using System.Windows;

namespace ExamBro.Exam.Views;

/// <summary>
/// Interaction logic for SecondaryScreenOverlayWindow.xaml.
/// Blackout window placed over secondary monitors to prevent multi-monitor cheating.
/// </summary>
public partial class SecondaryScreenOverlayWindow : Window
{
    public SecondaryScreenOverlayWindow()
    {
        InitializeComponent();
    }
}
