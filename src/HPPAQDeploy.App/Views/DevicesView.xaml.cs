using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    private void DevicesGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || e.OriginalSource is not DependencyObject source)
            return;

        if (ItemsControl.ContainerFromElement(grid, source) is DataGridRow row)
            grid.SelectedItem = row.Item;
    }
}
