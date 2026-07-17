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
    private readonly SemaphoreSlim _loadGate = new(1, 1);

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
        if (!await _loadGate.WaitAsync(0))
            return;

        IsLoading = true;
        try
        {
            var deviceList = (await _deviceRepository.GetAllWithRecommendationsAsync()).ToList();
            _allDevices = deviceList;

            TotalDevices = deviceList.Count;
            var analyzedCount = 0;
            var onlineCount = 0;
            var offlineCount = 0;
            var pendingCount = 0;
            var criticalCount = 0;
            var recommendedCount = 0;
            var optionalCount = 0;
            var upToDateCount = 0;
            var devicesWithUpdatesCount = 0;
            var devicesWithCriticalCount = 0;

            foreach (var device in deviceList)
            {
                if (device.Status is DeviceStatus.Online or DeviceStatus.ReadyToDeploy)
                    onlineCount++;
                else if (device.Status is DeviceStatus.Offline or DeviceStatus.Unreachable)
                    offlineCount++;

                var recommendations = device.Recommendations ?? [];
                if (device.LastAnalyzed.HasValue)
                {
                    analyzedCount++;
                    if (recommendations.Count == 0)
                        upToDateCount++;
                }

                if (recommendations.Count > 0)
                    devicesWithUpdatesCount++;

                var deviceHasCritical = false;
                foreach (var recommendation in recommendations)
                {
                    pendingCount++;
                    if (recommendation.Severity?.Equals("Critical", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        criticalCount++;
                        deviceHasCritical = true;
                    }
                    else if (recommendation.Severity?.Equals("Recommended", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        recommendedCount++;
                    }
                    else if (recommendation.Severity?.Equals("Optional", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        optionalCount++;
                    }
                }

                if (deviceHasCritical)
                    devicesWithCriticalCount++;
            }

            OnlineDevices = onlineCount;
            OfflineDevices = offlineCount;
            PendingUpdates = pendingCount;
            TotalPendingUpdates = pendingCount;
            CriticalUpdates = criticalCount;
            RecommendedUpdates = recommendedCount;
            OptionalUpdates = optionalCount;
            NeverScannedCount = deviceList.Count - analyzedCount;
            DevicesUpToDate = upToDateCount;
            DevicesWithUpdates = devicesWithUpdatesCount;
            DevicesWithCritical = devicesWithCriticalCount;
            CompliancePercent = analyzedCount > 0
                ? Math.Round((double)upToDateCount / analyzedCount * 100, 1)
                : 0;

            RecentDevices = new ObservableCollection<Device>(
                deviceList.OrderByDescending(d => d.LastScanned).Take(10));

            OnPropertyChanged(nameof(HasDevices));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load dashboard data");
            SnackbarService.ShowError("Failed to load dashboard data.");
        }
        finally
        {
            IsLoading = false;
            _loadGate.Release();
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
            SnackbarService.ShowError($"Export failed: {ex.Message}");
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
            SnackbarService.ShowError($"Export failed: {ex.Message}");
        }
    }
}
