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

    public MainWindow(ExamConfig config)
    {
        InitializeComponent();
        ViewModel = new MainViewModel(config);
        DataContext = ViewModel;
    }

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = ViewModel;
    }
}
