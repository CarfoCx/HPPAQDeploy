using System.Windows.Controls;
using HPPAQDeploy.App.ViewModels;

namespace HPPAQDeploy.App.Views;

public partial class CredentialManagerView : UserControl
{
    public CredentialManagerView()
    {
        InitializeComponent();
    }

    private void PasswordBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is CredentialManagerViewModel vm && sender is PasswordBox pb)
        {
            vm.Password = pb.Password;
        }
    }
}
