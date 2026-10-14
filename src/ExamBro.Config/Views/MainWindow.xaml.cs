using System.Windows;
using ExamBro.Config.ViewModels;
using ExamBro.Core.Models;

namespace ExamBro.Config.Views;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow(ExamConfig config) : this(new MainViewModel(config))
    {
    }

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = ViewModel;

        if (ViewModel.LogoPreview != null)
        {
            Icon = ViewModel.LogoPreview;
        }

        ViewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.LogoPreview) && ViewModel.LogoPreview != null)
            {
                Icon = ViewModel.LogoPreview;
            }
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Application.Current?.Shutdown(0);
        Environment.Exit(0);
    }
}
