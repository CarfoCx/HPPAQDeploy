using System.Windows;
using System.Windows.Controls;

namespace HPPAQDeploy.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void SmtpPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.SettingsViewModel vm)
            vm.SmtpPassword = ((PasswordBox)sender).Password;
    }
}
