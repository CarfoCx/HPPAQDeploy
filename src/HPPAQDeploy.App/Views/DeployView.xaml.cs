using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    private void DeviceList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        => SelectRightClickedItem(sender, e);

    private void UpdateList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        => SelectRightClickedItem(sender, e);

    private static void SelectRightClickedItem(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListView list || e.OriginalSource is not DependencyObject source)
            return;

        if (ItemsControl.ContainerFromElement(list, source) is ListViewItem item)
            list.SelectedItem = item.DataContext;
    }
}
