using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HPPAQDeploy.Core.Models;

namespace HPPAQDeploy.App.ViewModels;

public partial class MonitorSessionViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private int _groupId;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private int _scannedCount;

    [ObservableProperty]
    private int _totalToScan;

    public CancellationTokenSource Cts { get; } = new();

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _isDeploying;

    [ObservableProperty]
    private ObservableCollection<Device> _devices = [];

    [ObservableProperty]
    private ObservableCollection<DeploymentLogEntry> _logs = [];

    [ObservableProperty]
    private ObservableCollection<DeploymentLogEntry> _filteredLogs = [];

    [ObservableProperty]
    private string _logFilterText = "";

    [ObservableProperty]
    private string _logLevelFilter = "All";

    public MonitorSessionViewModel(string title)
    {
        Title = title;
    }

    public void AddLog(string deviceName, string message, string level)
    {
        var entry = new DeploymentLogEntry
        {
            Timestamp = DateTime.Now,
            DeviceName = deviceName,
            Message = message,
            Level = level
        };

        System.Windows.Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            Logs.Insert(0, entry);
            if (MatchesFilter(entry))
            {
                FilteredLogs.Insert(0, entry);
            }
        });
    }

    public bool MatchesFilter(DeploymentLogEntry entry)
    {
        if (!string.IsNullOrEmpty(LogLevelFilter) && LogLevelFilter != "All")
        {
            if (!string.Equals(entry.Level, LogLevelFilter, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (!string.IsNullOrWhiteSpace(LogFilterText))
        {
            var text = LogFilterText.Trim();
            return (entry.DeviceName?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (entry.Message?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        return true;
    }

    [RelayCommand]
    private void ClearLogs()
    {
        Logs.Clear();
        FilteredLogs.Clear();
    }
}
