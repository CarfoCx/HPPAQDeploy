using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HPPAQDeploy.App.Helpers;
using HPPAQDeploy.Core.Interfaces;
using HPPAQDeploy.Core.Models;
using HPPAQDeploy.Shared.Configuration;
using HPPAQDeploy.Shared.Helpers;
using Serilog;

namespace HPPAQDeploy.App.ViewModels;

public partial class HistoryViewModel : ObservableObject
{
    private readonly IDeploymentHistoryRepository _historyRepository;

    [ObservableProperty]
    private ObservableCollection<DeploymentHistory> _allHistory = [];

    [ObservableProperty]
    private ObservableCollection<DeploymentHistory> _filteredHistory = [];

    [ObservableProperty]
    private string _filterText = "";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "";

    // ── Date Range Filter ──
    [ObservableProperty]
    private string _selectedDateRange = "All Time";

    public List<string> DateRangeOptions { get; } = ["All Time", "Today", "Last 7 Days", "Last 30 Days", "Last 90 Days"];

    // ── Action Filter ──
    [ObservableProperty]
    private string _selectedActionFilter = "All";

    public List<string> ActionFilterOptions { get; } = ["All", "Deployed", "Failed", "Cancelled"];

    // ── Summary Statistics ──
    [ObservableProperty]
    private int _totalDeployed;

    [ObservableProperty]
    private int _totalFailed;

    [ObservableProperty]
    private int _totalCancelled;

    [ObservableProperty]
    private double _successRate;

    [ObservableProperty]
    private int _uniqueDevices;

    [ObservableProperty]
    private int _uniqueUpdates;

    public bool HasHistory => FilteredHistory.Count > 0;
    public bool HasAnyHistory => AllHistory.Count > 0;
    public bool IsFilterEmpty => !HasHistory && HasAnyHistory;
    public bool HasSummaryStats => FilteredHistory.Count > 0;

    public HistoryViewModel(IDeploymentHistoryRepository historyRepository)
    {
        _historyRepository = historyRepository;
        AsyncInitHelper.SafeFireAndForget(LoadHistoryAsync, nameof(HistoryViewModel));
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();
    partial void OnSelectedDateRangeChanged(string value) => ApplyFilter();
    partial void OnSelectedActionFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var source = AllHistory.AsEnumerable();

        // Date range filter
        if (SelectedDateRange != "All Time")
        {
            var cutoff = SelectedDateRange switch
            {
                "Today" => DateTime.UtcNow.Date,
                "Last 7 Days" => DateTime.UtcNow.AddDays(-7),
                "Last 30 Days" => DateTime.UtcNow.AddDays(-30),
                "Last 90 Days" => DateTime.UtcNow.AddDays(-90),
                _ => DateTime.MinValue
            };
            source = source.Where(h => h.Timestamp >= cutoff);
        }

        // Action filter
        if (SelectedActionFilter != "All")
        {
            source = source.Where(h =>
                h.Action?.Equals(SelectedActionFilter, StringComparison.OrdinalIgnoreCase) == true);
        }

        // Text filter
        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            var filter = FilterText;
            source = source.Where(h =>
                (h.DeviceHostname?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (h.DeviceIpAddress?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (h.UpdateName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (h.SoftPaqId?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (h.Category?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (h.Action?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var filtered = source.ToList();
        FilteredHistory = new ObservableCollection<DeploymentHistory>(filtered);

        // Update summary stats
        TotalDeployed = filtered.Count(h => h.Action?.Equals("Deployed", StringComparison.OrdinalIgnoreCase) == true);
        TotalFailed = filtered.Count(h => h.Action?.Equals("Failed", StringComparison.OrdinalIgnoreCase) == true);
        TotalCancelled = filtered.Count(h => h.Action?.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) == true);
        SuccessRate = (TotalDeployed + TotalFailed) > 0
            ? Math.Round((double)TotalDeployed / (TotalDeployed + TotalFailed) * 100, 1)
            : 0;
        UniqueDevices = filtered
            .Where(h => !string.IsNullOrEmpty(h.DeviceHostname))
            .Select(h => h.DeviceHostname)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        UniqueUpdates = filtered
            .Where(h => !string.IsNullOrEmpty(h.SoftPaqId))
            .Select(h => h.SoftPaqId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(HasAnyHistory));
        OnPropertyChanged(nameof(IsFilterEmpty));
        OnPropertyChanged(nameof(HasSummaryStats));
    }

    [RelayCommand]
    private async Task LoadHistoryAsync()
    {
        IsLoading = true;
        try
        {
            var history = await _historyRepository.GetAllAsync();
            AllHistory = new ObservableCollection<DeploymentHistory>(history);
            ApplyFilter();
            StatusMessage = $"{AllHistory.Count} deployment records loaded.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading history: {ex.Message}";
            Log.Error(ex, "Failed to load deployment history");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ExportHistoryCsvAsync()
    {
        if (FilteredHistory.Count == 0) { StatusMessage = "No history to export."; return; }
        var path = DialogHelper.SaveFileDialog($"deployment-history-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        if (path is null) return;
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Timestamp,Hostname,IP,Update,SoftPaq,Category,Action,Error");
            foreach (var h in FilteredHistory)
                sb.AppendLine($"\"{h.Timestamp:yyyy-MM-dd HH:mm:ss}\",\"{h.DeviceHostname}\",\"{h.DeviceIpAddress}\",\"{h.UpdateName?.Replace("\"", "\"\"")}\",\"{h.SoftPaqId}\",\"{h.Category}\",\"{h.Action}\",\"{h.ErrorMessage?.Replace("\"", "\"\"")}\"");
            await File.WriteAllTextAsync(path, sb.ToString());
            StatusMessage = $"Exported {FilteredHistory.Count} records to {path}";
            Log.Information("Deployment history exported to {Path}", path);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        if (AllHistory.Count == 0) return;
        if (!DialogHelper.Confirm(
            $"Clear all {AllHistory.Count} deployment history records?\nThis cannot be undone.",
            "Clear History"))
            return;
        try
        {
            await _historyRepository.ClearAllAsync();
            AllHistory.Clear();
            FilteredHistory.Clear();
            TotalDeployed = 0;
            TotalFailed = 0;
            TotalCancelled = 0;
            SuccessRate = 0;
            UniqueDevices = 0;
            UniqueUpdates = 0;
            OnPropertyChanged(nameof(HasHistory));
            OnPropertyChanged(nameof(HasAnyHistory));
            OnPropertyChanged(nameof(IsFilterEmpty));
            OnPropertyChanged(nameof(HasSummaryStats));
            StatusMessage = "History cleared.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            Log.Error(ex, "Failed to clear deployment history");
        }
    }
}
