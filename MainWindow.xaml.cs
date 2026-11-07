using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using FinancialTerminal.App.ViewModels;

namespace FinancialTerminal.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<MainViewModel>();
    }
}
