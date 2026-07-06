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

    public ObservableCollection<MonitorSessionViewModel> ActiveSessions => _deployViewModel.ActiveSessions;

    public MonitorSessionViewModel? SelectedSession
    {
        get => _deployViewModel.SelectedSession;
        set => _deployViewModel.SelectedSession = value;
    }

    [ObservableProperty]
    private bool _autoScroll = true;

    [ObservableProperty]
    private bool _wordWrap;

    [ObservableProperty]
    private bool _alwaysOnTop;

    [ObservableProperty]
    private double _logFontSize = 14.0;

    public List<string> LogLevelOptions => _deployViewModel.LogLevelOptions;

    [RelayCommand]
    private void SetFontSmall() => LogFontSize = 12.0;

    [RelayCommand]
    private void SetFontMedium() => LogFontSize = 14.0;

    [RelayCommand]
    private void SetFontLarge() => LogFontSize = 16.0;

    public void Subscribe() { }
    public void Unsubscribe() { }
}
