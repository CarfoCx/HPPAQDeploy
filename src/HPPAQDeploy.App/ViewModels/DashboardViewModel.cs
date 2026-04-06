using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HPPAQDeploy.App.Services;
using HPPAQDeploy.Core.Interfaces;
using HPPAQDeploy.Core.Models;
using HPPAQDeploy.Shared.Configuration;
using HPPAQDeploy.Shared.Helpers;
using Serilog;
using static HPPAQDeploy.Shared.Helpers.AsyncInitHelper;

namespace HPPAQDeploy.App.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly ScheduledScanService _scheduledScanService;

    // Quick action navigation event — MainViewModel subscribes to this
    public static event EventHandler<string>? NavigationRequested;

    [ObservableProperty]
    private int _totalDevices;

    [ObservableProperty]
    private int _onlineDevices;

    [ObservableProperty]
    private int _pendingUpdates;

    [ObservableProperty]
    private int _criticalUpdates;

    [ObservableProperty]
    private double _compliancePercent;

    [ObservableProperty]
    private int _devicesUpToDate;

    [ObservableProperty]
    private int _devicesWithCritical;

    [ObservableProperty]
    private int _devicesWithUpdates;

    [ObservableProperty]
    private int _totalPendingUpdates;

    [ObservableProperty]
    private int _neverScannedCount;

    [ObservableProperty]
    private int _recommendedUpdates;

    [ObservableProperty]
    private int _optionalUpdates;

    [ObservableProperty]
    private int _offlineDevices;

    [ObservableProperty]
    private ObservableCollection<Device> _recentDevices = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _exportStatus = "";

    [ObservableProperty]
    private Device? _selectedRecentDevice;

    [ObservableProperty]
    private ObservableCollection<HpiaRecommendation> _selectedDeviceRecommendations = [];

    [ObservableProperty]
    private bool _hasSelectedDevice;

    partial void OnSelectedRecentDeviceChanged(Device? value)
    {
        HasSelectedDevice = value is not null;
        SelectedDeviceRecommendations = value?.Recommendations is not null
            ? new ObservableCollection<HpiaRecommendation>(value.Recommendations)
            : [];
    }

    [ObservableProperty]
    private string _scheduledScanStatusText = "";

    private List<Device> _allDevices = [];

    public bool HasDevices => TotalDevices > 0;

    public DashboardViewModel(IDeviceRepository deviceRepository, ScheduledScanService scheduledScanService)
    {
        _deviceRepository = deviceRepository;
        _scheduledScanService = scheduledScanService;
        ScheduledScanStatusText = _scheduledScanService.ScheduleStatusText;

        _scheduledScanService.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ScheduledScanService.ScheduleStatusText))
            {
                ScheduledScanStatusText = _scheduledScanService.ScheduleStatusText;
            }
        };

        SafeFireAndForget(LoadDataAsync, nameof(DashboardViewModel));
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadDataAsync();
    }

    private async Task LoadDataAsync()
    {
        IsLoading = true;
        try
        {
            var deviceList = (await _deviceRepository.GetAllWithRecommendationsAsync()).ToList();
            _allDevices = deviceList;

            TotalDevices = deviceList.Count;
            OnlineDevices = deviceList.Count(d => d.Status == DeviceStatus.Online || d.Status == DeviceStatus.ReadyToDeploy);
            OfflineDevices = deviceList.Count(d => d.Status == DeviceStatus.Offline || d.Status == DeviceStatus.Unreachable);

            var allRecommendations = deviceList.SelectMany(d => d.Recommendations ?? []).ToList();

            PendingUpdates = allRecommendations.Count;
            TotalPendingUpdates = allRecommendations.Count;
            CriticalUpdates = allRecommendations
                .Count(r => r.Severity?.Equals("Critical", StringComparison.OrdinalIgnoreCase) == true);
            RecommendedUpdates = allRecommendations
                .Count(r => r.Severity?.Equals("Recommended", StringComparison.OrdinalIgnoreCase) == true);
            OptionalUpdates = allRecommendations
                .Count(r => r.Severity?.Equals("Optional", StringComparison.OrdinalIgnoreCase) == true);

            var analyzedDevices = deviceList.Where(d => d.LastAnalyzed.HasValue).ToList();
            NeverScannedCount = deviceList.Count - analyzedDevices.Count;
            DevicesUpToDate = analyzedDevices.Count(d => (d.Recommendations?.Count ?? 0) == 0);
            DevicesWithUpdates = deviceList.Count(d => (d.Recommendations?.Count ?? 0) > 0);
            DevicesWithCritical = deviceList.Count(d =>
                d.Recommendations?.Any(r => r.Severity?.Equals("Critical", StringComparison.OrdinalIgnoreCase) == true) == true);

            CompliancePercent = analyzedDevices.Count > 0
                ? Math.Round((double)DevicesUpToDate / analyzedDevices.Count * 100, 1)
                : 0;

            RecentDevices = new ObservableCollection<Device>(
                deviceList.OrderByDescending(d => d.LastScanned).Take(10));

            OnPropertyChanged(nameof(HasDevices));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load dashboard data");
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ── Quick Actions ──

    [RelayCommand]
    private void GoToDeployView() => NavigationRequested?.Invoke(this, "Deploy");

    [RelayCommand]
    private void GoToDevicesView() => NavigationRequested?.Invoke(this, "Devices");

    [RelayCommand]
    private void GoToHistoryView() => NavigationRequested?.Invoke(this, "History");

    // ── Exports ──

    [RelayCommand]
    private async Task ExportCsvReportAsync()
    {
        if (_allDevices.Count == 0) { ExportStatus = "No devices to export."; return; }
        var path = Helpers.DialogHelper.SaveFileDialog($"compliance-report-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        if (path is null) return;
        try
        {
            await ReportGenerator.GenerateCsvReport(_allDevices, path);
            ExportStatus = $"CSV exported to {path}";
            SnackbarService.ShowSuccess("Compliance CSV exported");
            Log.Information("Compliance CSV report exported to {Path}", path);
        }
        catch (Exception ex)
        {
            ExportStatus = $"CSV export failed: {ex.Message}";
            Log.Error(ex, "Failed to export CSV compliance report");
        }
    }

    [RelayCommand]
    private async Task ExportHtmlReportAsync()
    {
        if (_allDevices.Count == 0) { ExportStatus = "No devices to export."; return; }
        var path = Helpers.DialogHelper.SaveFileDialog(
            $"compliance-report-{DateTime.Now:yyyyMMdd-HHmmss}.html",
            "HTML Files (*.html)|*.html|All Files (*.*)|*.*");
        if (path is null) return;
        try
        {
            await ReportGenerator.GenerateHtmlReport(_allDevices, path);
            ExportStatus = $"HTML report saved to {path}";
            SnackbarService.ShowSuccess("Compliance HTML report exported");
            Log.Information("Compliance HTML report exported to {Path}", path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ExportStatus = $"HTML export failed: {ex.Message}";
            Log.Error(ex, "Failed to export HTML compliance report");
        }
    }
}
