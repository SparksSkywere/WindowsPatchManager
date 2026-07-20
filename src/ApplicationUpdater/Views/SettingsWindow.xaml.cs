using System.Windows;
using ApplicationUpdater.Helpers;
using ApplicationUpdater.ViewModels;

namespace ApplicationUpdater.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        AppIcon.ApplyTo(this);
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SaveCommand.Execute(null);
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
