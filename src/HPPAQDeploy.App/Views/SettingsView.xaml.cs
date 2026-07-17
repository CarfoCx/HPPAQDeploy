using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using HPPAQDeploy.App.ViewModels;

namespace HPPAQDeploy.App.Views;

public partial class SettingsView : UserControl
{
    private SettingsViewModel? _viewModel;

    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += SettingsView_DataContextChanged;
        Unloaded += SettingsView_Unloaded;
    }

    private void SmtpPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
            vm.SmtpPassword = ((PasswordBox)sender).Password;
    }

    private void BiosPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
            vm.NewBiosPassword = ((PasswordBox)sender).Password;
    }

    private void SettingsView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
            PropertyChangedEventManager.RemoveHandler(_viewModel, SettingsViewModel_PropertyChanged, string.Empty);

        _viewModel = e.NewValue as SettingsViewModel;
        if (_viewModel is not null)
            PropertyChangedEventManager.AddHandler(_viewModel, SettingsViewModel_PropertyChanged, string.Empty);

        ClearBiosPasswordBox();
    }

    private void SettingsViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if ((string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(SettingsViewModel.NewBiosPassword)) &&
            sender is SettingsViewModel vm && string.IsNullOrEmpty(vm.NewBiosPassword))
        {
            ClearBiosPasswordBox();
        }
    }

    private void SettingsView_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.NewBiosPassword = string.Empty;

        ClearBiosPasswordBox();
    }

    private void ClearBiosPasswordBox()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ClearBiosPasswordBox));
            return;
        }

        if (BiosPasswordBox.Password.Length > 0)
            BiosPasswordBox.Clear();
    }
}
