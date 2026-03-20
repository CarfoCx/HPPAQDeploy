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

    public bool HasHistory => FilteredHistory.Count > 0;
    public bool HasAnyHistory => AllHistory.Count > 0;
    public bool IsFilterEmpty => !HasHistory && HasAnyHistory;

    public HistoryViewModel(IDeploymentHistoryRepository historyRepository)
    {
        _historyRepository = historyRepository;
        AsyncInitHelper.SafeFireAndForget(LoadHistoryAsync, nameof(HistoryViewModel));
    }

    partial void OnFilterTextChanged(string value)
    {
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (string.IsNullOrWhiteSpace(FilterText))
        {
            FilteredHistory = new ObservableCollection<DeploymentHistory>(AllHistory);
        }
        else
        {
            var filter = FilterText.ToLowerInvariant();
            FilteredHistory = new ObservableCollection<DeploymentHistory>(
                AllHistory.Where(h =>
                    (h.DeviceHostname?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (h.DeviceIpAddress?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (h.UpdateName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (h.SoftPaqId?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (h.Category?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (h.Action?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)));
        }
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(HasAnyHistory));
        OnPropertyChanged(nameof(IsFilterEmpty));
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
            OnPropertyChanged(nameof(HasHistory));
            OnPropertyChanged(nameof(HasAnyHistory));
            OnPropertyChanged(nameof(IsFilterEmpty));
            StatusMessage = "History cleared.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            Log.Error(ex, "Failed to clear deployment history");
        }
    }
}
