using System.Windows;
using ApplicationUpdater.Helpers;

namespace ApplicationUpdater.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        AppIcon.ApplyTo(this);

        Title = $"About {AppInfo.ProductName}";
        TitleText.Text = AppInfo.ProductName;
        VersionText.Text = $"Version {AppInfo.Version}";
        DescriptionText.Text = AppInfo.Description;
        CompanyText.Text = AppInfo.Company;
        CopyrightText.Text = AppInfo.Copyright;

        var icon = AppIcon.GetImageSource();
        if (icon is not null)
            AppIconImage.Source = icon;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();
}
