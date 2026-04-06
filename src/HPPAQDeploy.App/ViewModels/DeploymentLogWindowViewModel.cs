using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HPPAQDeploy.Core.Models;

namespace HPPAQDeploy.App.ViewModels;

public partial class DeploymentLogWindowViewModel : ObservableObject
{
    private readonly DeployViewModel _deployViewModel;

    public DeploymentLogWindowViewModel(DeployViewModel deployViewModel)
    {
        _deployViewModel = deployViewModel;
    }

    // Delegate to DeployViewModel's log data
    public ObservableCollection<DeploymentLogEntry> FilteredDeploymentLog
        => _deployViewModel.FilteredDeploymentLog;

    public ObservableCollection<DeploymentLogEntry> DeploymentLog
        => _deployViewModel.DeploymentLog;

    public string LogFilterText
    {
        get => _deployViewModel.LogFilterText;
        set
        {
            _deployViewModel.LogFilterText = value;
            OnPropertyChanged();
        }
    }

    public string LogLevelFilter
    {
        get => _deployViewModel.LogLevelFilter;
        set
        {
            _deployViewModel.LogLevelFilter = value;
            OnPropertyChanged();
        }
    }

    public List<string> LogLevelOptions => _deployViewModel.LogLevelOptions;

    // Window-specific state
    [ObservableProperty]
    private bool _autoScroll = true;

    [ObservableProperty]
    private bool _wordWrap;

    [ObservableProperty]
    private bool _alwaysOnTop;

    [ObservableProperty]
    private double _logFontSize = 14.0;

    // Deployment state (forwarded for status display)
    public bool IsDeploying => _deployViewModel.IsDeploying;
    public double ProgressPercent => _deployViewModel.ProgressPercent;
    public string DeployStatus => _deployViewModel.DeployStatus;

    [RelayCommand]
    private void SetFontSmall() => LogFontSize = 12.0;

    [RelayCommand]
    private void SetFontMedium() => LogFontSize = 14.0;

    [RelayCommand]
    private void SetFontLarge() => LogFontSize = 16.0;

    [RelayCommand]
    private void ClearLog()
    {
        _deployViewModel.DeploymentLog.Clear();
    }

    public IRelayCommand ExportResultsCommand => _deployViewModel.ExportResultsCommand;

    public void Subscribe()
    {
        _deployViewModel.PropertyChanged += OnDeployViewModelPropertyChanged;
    }

    public void Unsubscribe()
    {
        _deployViewModel.PropertyChanged -= OnDeployViewModelPropertyChanged;
    }

    private void OnDeployViewModelPropertyChanged(object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DeployViewModel.FilteredDeploymentLog):
                OnPropertyChanged(nameof(FilteredDeploymentLog));
                break;
            case nameof(DeployViewModel.LogFilterText):
                OnPropertyChanged(nameof(LogFilterText));
                break;
            case nameof(DeployViewModel.LogLevelFilter):
                OnPropertyChanged(nameof(LogLevelFilter));
                break;
            case nameof(DeployViewModel.IsDeploying):
                OnPropertyChanged(nameof(IsDeploying));
                break;
            case nameof(DeployViewModel.ProgressPercent):
                OnPropertyChanged(nameof(ProgressPercent));
                break;
            case nameof(DeployViewModel.DeployStatus):
                OnPropertyChanged(nameof(DeployStatus));
                break;
        }
    }
}
