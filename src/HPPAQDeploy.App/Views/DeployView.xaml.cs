using System.Windows.Controls;
using HPPAQDeploy.App.ViewModels;
using HPPAQDeploy.Core.Models;

namespace HPPAQDeploy.App.Views;

public partial class DeployView : UserControl
{
    public DeployView()
    {
        InitializeComponent();
    }

    private void CredentialComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is DeployViewModel vm && sender is ComboBox { SelectedItem: Credential cred })
            vm.SelectedCredential = cred;
    }

    private void GroupComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is DeployViewModel vm && sender is ComboBox { SelectedItem: DeviceGroup group })
            vm.SelectedGroup = group;
    }
}
