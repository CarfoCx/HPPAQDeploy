using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HPPAQDeploy.App.Constants;
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
    private readonly ConcurrentQueue<SnackbarMessage> _snackbarQueue = new();
    private volatile bool _isProcessingSnackbar;

    [ObservableProperty]
    private ObservableObject? _currentView;

    [ObservableProperty]
    private string _currentViewName = ViewNames.Dashboard;

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

    [ObservableProperty]
    private string _snackbarIconKind = "CheckCircle";

    [ObservableProperty]
    private SolidColorBrush _snackbarIconBrush = new(Color.FromRgb(76, 175, 80));

    [ObservableProperty]
    private string? _snackbarActionLabel;

    [ObservableProperty]
    private bool _hasSnackbarAction;

    private Action? _snackbarAction;

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

        // Freeze brushes for performance
        _snackbarIconBrush.Freeze();

        SnackbarService.MessagePosted += msg =>
            Application.Current?.Dispatcher?.BeginInvoke(() => EnqueueSnackbar(msg));

        DashboardViewModel.NavigationRequested += (_, viewName) =>
            Application.Current?.Dispatcher?.BeginInvoke(() => NavigateTo(viewName));
    }

    [RelayCommand]
    private void NavigateTo(string viewName)
    {
        CurrentViewName = viewName;
        var vm = viewName switch
        {
            ViewNames.Dashboard => _serviceProvider.GetRequiredService<DashboardViewModel>() as ObservableObject,
            ViewNames.Devices => _serviceProvider.GetRequiredService<DevicesViewModel>(),
            ViewNames.Groups => _serviceProvider.GetRequiredService<GroupsViewModel>(),
            ViewNames.Deploy => _serviceProvider.GetRequiredService<DeployViewModel>(),
            ViewNames.History => _serviceProvider.GetRequiredService<HistoryViewModel>(),
            ViewNames.Credentials => _serviceProvider.GetRequiredService<CredentialManagerViewModel>(),
            ViewNames.Logs => _serviceProvider.GetRequiredService<LogViewModel>(),
            ViewNames.Settings => _serviceProvider.GetRequiredService<SettingsViewModel>(),
            ViewNames.About => _serviceProvider.GetRequiredService<AboutViewModel>(),
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
            ViewNames.Dashboard => "Dashboard",
            ViewNames.Devices => $"Devices — {DeviceCount} total",
            ViewNames.Groups => "Groups",
            ViewNames.Deploy => "Deploy Updates",
            ViewNames.History => "Deployment History",
            ViewNames.Credentials => "Credential Manager",
            ViewNames.Logs => "Application Logs",
            ViewNames.Settings => "Settings",
            ViewNames.About => "About",
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

    // ── Snackbar with severity, queuing, and action support ──

    private static readonly SolidColorBrush SuccessBrush = Freeze(new SolidColorBrush(Color.FromRgb(76, 175, 80)));
    private static readonly SolidColorBrush InfoBrush = Freeze(new SolidColorBrush(Color.FromRgb(0, 150, 214)));
    private static readonly SolidColorBrush WarningBrush = Freeze(new SolidColorBrush(Color.FromRgb(255, 167, 38)));
    private static readonly SolidColorBrush ErrorBrush = Freeze(new SolidColorBrush(Color.FromRgb(244, 67, 54)));

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    private void EnqueueSnackbar(SnackbarMessage msg)
    {
        _snackbarQueue.Enqueue(msg);
        if (!_isProcessingSnackbar)
            _ = ProcessSnackbarQueueAsync();
    }

    private async Task ProcessSnackbarQueueAsync()
    {
        _isProcessingSnackbar = true;
        try
        {
            while (_snackbarQueue.TryDequeue(out var msg))
            {
                ShowSnackbarInternal(msg);
                await Task.Delay(3500);
                IsSnackbarVisible = false;
                await Task.Delay(200); // Brief gap between messages
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error processing snackbar queue");
        }
        finally
        {
            _isProcessingSnackbar = false;
        }
    }

    private void ShowSnackbarInternal(SnackbarMessage msg)
    {
        SnackbarMessage = msg.Text;
        _snackbarAction = msg.Action;
        SnackbarActionLabel = msg.ActionLabel;
        HasSnackbarAction = msg.ActionLabel != null;

        var (icon, brush) = msg.Severity switch
        {
            SnackbarSeverity.Success => ("CheckCircle", SuccessBrush),
            SnackbarSeverity.Warning => ("AlertCircleOutline", WarningBrush),
            SnackbarSeverity.Error => ("CloseCircle", ErrorBrush),
            _ => ("InformationOutline", InfoBrush)
        };

        SnackbarIconKind = icon;
        SnackbarIconBrush = brush;
        IsSnackbarVisible = true;
    }

    [RelayCommand]
    private void ExecuteSnackbarAction()
    {
        _snackbarAction?.Invoke();
        IsSnackbarVisible = false;
    }

    // Legacy compat: existing callers that use ShowSnackbar(string)
    public async void ShowSnackbar(string message)
    {
        try
        {
            EnqueueSnackbar(new SnackbarMessage(message, SnackbarSeverity.Success));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in ShowSnackbar");
        }
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
