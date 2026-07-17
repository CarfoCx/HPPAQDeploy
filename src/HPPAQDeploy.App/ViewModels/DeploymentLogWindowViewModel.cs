using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HPPAQDeploy.App.Helpers;
using HPPAQDeploy.App.Services;
using HPPAQDeploy.Core.Models;

namespace HPPAQDeploy.App.ViewModels;

public partial class DeploymentLogWindowViewModel : ObservableObject
{
    private readonly DeployViewModel _deployViewModel;
    private bool _isSubscribed;

    public DeploymentLogWindowViewModel(DeployViewModel deployViewModel)
    {
        _deployViewModel = deployViewModel;
        Subscribe();
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

    [RelayCommand]
    private async Task ExportSessionLogsAsync(MonitorSessionViewModel? session)
    {
        if (session is null || session.FilteredLogs.Count == 0)
        {
            SnackbarService.ShowWarning("There are no visible session logs to export.");
            return;
        }

        var safeTitle = string.Concat(session.Title.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '-' : character));
        var path = DialogHelper.SaveFileDialog(
            $"{safeTitle}-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        if (path is null)
            return;

        var entries = session.FilteredLogs.ToList();
        var csv = new StringBuilder("Timestamp,Device,Level,Message\r\n");
        foreach (var entry in entries)
        {
            csv.Append(EscapeCsv(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"))).Append(',')
               .Append(EscapeCsv(entry.DeviceName)).Append(',')
               .Append(EscapeCsv(entry.Level)).Append(',')
               .Append(EscapeCsv(entry.Message)).Append("\r\n");
        }

        await File.WriteAllTextAsync(path, csv.ToString());
        SnackbarService.ShowSuccess($"Exported {entries.Count} session log entries.");
    }

    private static string EscapeCsv(string? value)
    {
        var escaped = value ?? string.Empty;
        if (escaped.Length > 0 && escaped[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n')
            escaped = "'" + escaped;

        return $"\"{escaped.Replace("\"", "\"\"")}\"";
    }

    public void Subscribe()
    {
        if (_isSubscribed) return;
        _deployViewModel.PropertyChanged += OnDeployViewModelPropertyChanged;
        _isSubscribed = true;
    }

    public void Unsubscribe()
    {
        if (!_isSubscribed) return;
        _deployViewModel.PropertyChanged -= OnDeployViewModelPropertyChanged;
        _isSubscribed = false;
    }

    private void OnDeployViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(DeployViewModel.SelectedSession))
            OnPropertyChanged(nameof(SelectedSession));
    }
}
