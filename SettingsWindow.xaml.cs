using System.Windows;
using FinancialTerminal.App.ViewModels;
using FinancialTerminal.Core.Configuration;

namespace FinancialTerminal.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        DataContext = new SettingsViewModel(settings);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
