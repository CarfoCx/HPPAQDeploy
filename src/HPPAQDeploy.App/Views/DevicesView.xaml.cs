using System.Windows.Controls;
using HPPAQDeploy.App.ViewModels;
using HPPAQDeploy.Core.Models;

namespace HPPAQDeploy.App.Views;

public partial class DevicesView : UserControl
{
    public DevicesView()
    {
        InitializeComponent();
    }

    private void CredentialComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is DevicesViewModel vm && sender is ComboBox { SelectedItem: Credential cred })
            vm.SelectedCredential = cred;
    }
}
