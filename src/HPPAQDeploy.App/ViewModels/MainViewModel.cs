using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HPPAQDeploy.App.Services;
using HPPAQDeploy.Core.Interfaces;
using HPPAQDeploy.Shared.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using System.Diagnostics;

namespace HPPAQDeploy.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IServiceProvider _serviceProvider;

    [ObservableProperty]
    private ObservableObject? _currentView;

    [ObservableProperty]
    private string _currentViewName = "Dashboard";

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private int _deviceCount;

    [ObservableProperty]
    private int _onlineCount;

    [ObservableProperty]
    private string _snackbarMessage = "";

    [ObservableProperty]
    private bool _isSnackbarVisible;

    public bool HasOnlineDevices => OnlineCount > 0;

    partial void OnOnlineCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasOnlineDevices));
    }

    public MainViewModel(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        CurrentView = _serviceProvider.GetRequiredService<DashboardViewModel>();
        AsyncInitHelper.SafeFireAndForget(RefreshStatusBarAsync(), nameof(MainViewModel));

        SnackbarService.MessagePosted += msg =>
            Application.Current?.Dispatcher?.BeginInvoke(() => ShowSnackbar(msg));
    }

    [RelayCommand]
    private void NavigateTo(string viewName)
    {
        CurrentViewName = viewName;
        var vm = viewName switch
        {
            "Dashboard" => _serviceProvider.GetRequiredService<DashboardViewModel>() as ObservableObject,
            "Devices" => _serviceProvider.GetRequiredService<DevicesViewModel>(),
            "Groups" => _serviceProvider.GetRequiredService<GroupsViewModel>(),
            "Deploy" => _serviceProvider.GetRequiredService<DeployViewModel>(),
            "History" => _serviceProvider.GetRequiredService<HistoryViewModel>(),
            "Credentials" => _serviceProvider.GetRequiredService<CredentialManagerViewModel>(),
            "Logs" => _serviceProvider.GetRequiredService<LogViewModel>(),
            "Settings" => _serviceProvider.GetRequiredService<SettingsViewModel>(),
            "About" => _serviceProvider.GetRequiredService<AboutViewModel>(),
            _ => CurrentView
        };
        CurrentView = vm;

        // Auto-refresh data when navigating to key tabs
        switch (vm)
        {
            case DashboardViewModel dashboard:
                AsyncInitHelper.SafeFireAndForget(dashboard.RefreshCommand.ExecuteAsync(null), nameof(DashboardViewModel));
                AsyncInitHelper.SafeFireAndForget(RefreshStatusBarAsync(), nameof(MainViewModel));
                break;
            case DevicesViewModel:
                AsyncInitHelper.SafeFireAndForget(RefreshStatusBarAsync(), nameof(MainViewModel));
                break;
            case GroupsViewModel groups:
                AsyncInitHelper.SafeFireAndForget(groups.LoadGroupsCommand.ExecuteAsync(null), nameof(GroupsViewModel));
                break;
            case DeployViewModel deploy:
                AsyncInitHelper.SafeFireAndForget(deploy.RefreshGroupsAsync(), nameof(DeployViewModel));
                break;
            case HistoryViewModel history:
                AsyncInitHelper.SafeFireAndForget(history.LoadHistoryCommand.ExecuteAsync(null), nameof(HistoryViewModel));
                break;
        }

        // Update status bar operation
        StatusMessage = viewName switch
        {
            "Dashboard" => "Dashboard",
            "Devices" => $"Devices — {DeviceCount} total",
            "Groups" => "Groups",
            "Deploy" => "Deploy Updates",
            "History" => "Deployment History",
            "Credentials" => "Credential Manager",
            "Logs" => "Application Logs",
            "Settings" => "Settings",
            "About" => "About",
            _ => "Ready"
        };
    }

    [RelayCommand]
    private void ShowKeyboardShortcuts()
    {
        MessageBox.Show(
            "Keyboard Shortcuts\n" +
            "─────────────────────────\n\n" +
            "Ctrl+1    Dashboard\n" +
            "Ctrl+2    Devices\n" +
            "Ctrl+3    Groups\n" +
            "Ctrl+4    Deploy Updates\n" +
            "Ctrl+5    History\n" +
            "Ctrl+6    Logs\n" +
            "Ctrl+7    Settings\n" +
            "Ctrl+8    Credentials\n\n" +
            "F1           Show this help\n" +
            "F5           Refresh current view",
            "HPPAQDeploy — Keyboard Shortcuts",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    [RelayCommand]
    private void OpenSupportLink()
    {
        var result = MessageBox.Show(
            "This will open an external link to ko-fi.com/carfo in your default web browser to support the developer.\n\nDo you want to continue?",
            "Support HPPAQDeploy",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://ko-fi.com/carfo") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to open support link.");
            }
        }
    }

    public async Task RefreshStatusBarAsync()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
            var devices = (await repo.GetAllAsync()).ToList();
            DeviceCount = devices.Count;
            OnlineCount = devices.Count(d =>
                d.Status == Core.Models.DeviceStatus.Online ||
                d.Status == Core.Models.DeviceStatus.ReadyToDeploy);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to refresh status bar counts");
        }
    }

    public async void ShowSnackbar(string message)
    {
        SnackbarMessage = message;
        IsSnackbarVisible = true;
        await Task.Delay(3000);
        IsSnackbarVisible = false;
    }

    public void UpdateStatus(string message)
    {
        StatusMessage = message;
    }

    public void UpdateCounts(int total, int online)
    {
        DeviceCount = total;
        OnlineCount = online;
    }
}
