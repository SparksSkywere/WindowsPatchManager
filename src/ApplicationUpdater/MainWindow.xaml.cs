using System.Windows;
using ApplicationUpdater.Helpers;
using ApplicationUpdater.Services;
using ApplicationUpdater.ViewModels;
using ApplicationUpdater.Views;

namespace ApplicationUpdater;

public partial class MainWindow : Window
{
    private readonly ConfigService _config;

    public MainWindow(MainViewModel viewModel, ConfigService config)
    {
        InitializeComponent();
        Title = AppInfo.ProductName;
        AppIcon.ApplyTo(this);
        DataContext = viewModel;
        _config = config;
        Loaded += async (_, _) =>
        {
            if (viewModel.LoadedCommand.CanExecute(null))
                await viewModel.LoadedCommand.ExecuteAsync(null);
        };
    }

    private void Options_Click(object sender, RoutedEventArgs e)
    {
        var vm = new SettingsViewModel(_config);
        var window = new SettingsWindow(vm) { Owner = this };
        window.ShowDialog();
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var about = new AboutWindow { Owner = this };
        about.ShowDialog();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
}
