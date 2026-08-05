using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using ApplicationUpdater.Helpers;

namespace ApplicationUpdater.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        AppIcon.ApplyTo(this);
        SourceInitialized += (_, _) =>
            WindowChromeHelper.ApplyTheme(this, ThemeManager.IsDarkEffective);
        ThemeManager.RefreshWindow(this);

        Title = $"About {AppInfo.ProductName}";
        TitleText.Text = AppInfo.ProductName;
        VersionText.Text = $"Version {AppInfo.Version}";
        DescriptionText.Text = AppInfo.Description;
        CompanyText.Text = AppInfo.Company;
        CopyrightText.Text = AppInfo.Copyright;
        GitHubLink.Text = "github.com/SparksSkywere/WindowsPatchManager";

        var icon = AppIcon.GetImageSource();
        if (icon is not null)
            AppIconImage.Source = icon;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();

    private void GitHubLink_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = AppInfo.GitHubUrl,
                UseShellExecute = true
            });
        }
        catch
        {
            MessageBox.Show(AppInfo.GitHubUrl, "GitHub", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
