using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HPPAQDeploy.App.Helpers;
using HPPAQDeploy.App.Services;
using HPPAQDeploy.Core.Interfaces;
using HPPAQDeploy.Core.Models;
using HPPAQDeploy.Shared.Configuration;
using HPPAQDeploy.Shared.Helpers;
using Serilog;

namespace HPPAQDeploy.App.ViewModels;

public partial class DevicesViewModel : ObservableObject
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly INetworkScanner _scanner;
    private readonly IDeviceDiscovery _discovery;
    private readonly ICredentialStore _credentialStore;
    private readonly IEmailService _emailService;
    private CancellationTokenSource? _cts;

    // ── Device list ──
    [ObservableProperty] private ObservableCollection<Device> _devices = [];
    [ObservableProperty] private ObservableCollection<Device> _filteredDevices = [];
    [ObservableProperty] private Device? _selectedDevice;
    [ObservableProperty] private ObservableCollection<HpiaRecommendation> _selectedDeviceRecommendations = [];
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private int _selectedDeviceCount;

    public bool HasDevices => FilteredDevices.Count > 0;
    public bool HasAnyDevices => Devices.Count > 0;
    public bool IsFilterEmpty => !HasDevices && HasAnyDevices;
    public bool HasSelectedDevice => SelectedDevice is not null;

    partial void OnSelectedDeviceChanged(Device? value)
    {
        OnPropertyChanged(nameof(HasSelectedDevice));
        SelectedDeviceRecommendations = value?.Recommendations is not null
            ? new ObservableCollection<HpiaRecommendation>(value.Recommendations)
            : [];
    }

    partial void OnDevicesChanged(ObservableCollection<Device> value)
    {
        foreach (var device in value)
        {
            device.PropertyChanged -= Device_PropertyChanged;
            device.PropertyChanged += Device_PropertyChanged;
        }

        UpdateSelectedDeviceCount();
    }

    // ── Scanning ──
    [ObservableProperty] private string _networkIp = "";
    [ObservableProperty] private string _selectedCidrPrefix = "/24";
    [ObservableProperty] private string _singleHostInput = "";
    [ObservableProperty] private bool _isAddingHost;
    [ObservableProperty] private string _singleHostStatus = "";
    [ObservableProperty] private ObservableCollection<Credential> _credentials = [];
    [ObservableProperty] private Credential? _selectedCredential;
    [ObservableProperty] private int _concurrency = AppSettings.DefaultPingConcurrency;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private int _totalIps;
    [ObservableProperty] private int _scannedIps;
    [ObservableProperty] private int _aliveHosts;
    [ObservableProperty] private int _hpDevicesFound;
    [ObservableProperty] private int _wmiFailures;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _scanStatus = "";
    [ObservableProperty] private bool _showScanPanel;

    public string CidrInput => $"{NetworkIp.Trim()}{SelectedCidrPrefix}";
    public ObservableCollection<string> CidrPrefixes { get; } = ["/16", "/20", "/21", "/22", "/23", "/24", "/25", "/26", "/27", "/28", "/29", "/30"];

    public DevicesViewModel(
        IDeviceRepository deviceRepository,
        INetworkScanner scanner,
        IDeviceDiscovery discovery,
        ICredentialStore credentialStore,
        IEmailService emailService)
    {
        _deviceRepository = deviceRepository;
        _scanner = scanner;
        _discovery = discovery;
        _credentialStore = credentialStore;
        _emailService = emailService;
        
        CredentialManagerViewModel.CredentialsChanged += (_, _) =>
            AsyncInitHelper.SafeFireAndForget(RefreshCredentialsAsync, nameof(DevicesViewModel));
            
        AsyncInitHelper.SafeFireAndForget(InitializeAsync, nameof(DevicesViewModel));
    }

    private async Task InitializeAsync()
    {
        await RefreshCredentialsAsync();
        await LoadDevicesAsync();
    }

    // ── Filtering ──
    partial void OnFilterTextChanged(string value) => ApplyFilter();

    private void Device_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Device.IsSelected))
        {
            UpdateSelectedDeviceCount();
        }
    }

    private void UpdateSelectedDeviceCount()
    {
        SelectedDeviceCount = Devices.Count(d => d.IsSelected);
    }

    private void ApplyFilter()
    {
        var source = Devices.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            source = source.Where(d =>
                (d.Hostname?.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (d.IpAddress?.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (d.Model?.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (d.SerialNumber?.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (d.GroupName?.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (d.ProductId?.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        // Preserve the currently selected device across collection replacement
        var previousSelection = SelectedDevice;
        FilteredDevices = new ObservableCollection<Device>(source.OrderBy(d => d.Hostname));
        if (previousSelection is not null)
            SelectedDevice = FilteredDevices.FirstOrDefault(d => d.Id == previousSelection.Id);
        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(HasAnyDevices));
        OnPropertyChanged(nameof(IsFilterEmpty));
    }

    // ── Load Devices ──
    [RelayCommand]
    private async Task LoadDevicesAsync()
    {
        IsLoading = true;
        try
        {
            var devices = await _deviceRepository.GetAllWithRecommendationsAsync();
            Devices = new ObservableCollection<Device>(devices);
            ApplyFilter();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load devices");
            StatusMessage = $"Error: {ex.Message}";
            SnackbarService.ShowError("Failed to load devices.");
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ── Load Credentials ──
    [RelayCommand]
    private async Task RefreshCredentialsAsync()
    {
        try
        {
            var currentSelection = SelectedCredential?.Id;
            var creds = await _credentialStore.GetAllAsync();
            Credentials = new ObservableCollection<Credential>(creds);
            
            if (currentSelection != null)
                SelectedCredential = Credentials.FirstOrDefault(c => c.Id == currentSelection);
                
            if (SelectedCredential == null)
                SelectedCredential = Credentials.FirstOrDefault(c => c.IsDefault) ?? Credentials.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load credentials");
            SnackbarService.ShowError("Failed to load credentials.");
        }
    }

    // ── Scanning ──
    [RelayCommand]
    private void ToggleScanPanel() => ShowScanPanel = !ShowScanPanel;

    [RelayCommand]
    private void ClearFilter() => FilterText = "";

    [RelayCommand]
    private void SelectAllFiltered()
    {
        foreach (var device in FilteredDevices)
        {
            device.IsSelected = true;
        }

        UpdateSelectedDeviceCount();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var device in Devices)
        {
            device.IsSelected = false;
        }

        UpdateSelectedDeviceCount();
    }

    [RelayCommand]
    private async Task StartScanAsync()
    {
        if (string.IsNullOrWhiteSpace(NetworkIp))
        {
            ScanStatus = "Please enter a network IP address (e.g., 192.168.1.0)";
            return;
        }

        if (SelectedCredential is null)
        {
            ScanStatus = "Please select credentials for WMI discovery";
            return;
        }

        CidrRange cidr;
        try
        {
            cidr = new CidrRange(CidrInput.Trim());
        }
        catch (Exception ex)
        {
            ScanStatus = $"Invalid CIDR: {ex.Message}";
            SnackbarService.ShowError($"Invalid CIDR: {ex.Message}");
            return;
        }

        IsScanning = true;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        ScannedIps = 0;
        AliveHosts = 0;
        HpDevicesFound = 0;
        WmiFailures = 0;
        TotalIps = cidr.TotalHosts;
        ScanStatus = $"Scanning {cidr.TotalHosts} IPs in {CidrInput}...";

        var networkCred = await _credentialStore.DecryptAsync(SelectedCredential);
        var progress = new Progress<(int completed, int total)>(p =>
        {
            ScannedIps = p.completed;
            ProgressPercent = TotalIps > 0 ? (double)p.completed / TotalIps * 100 : 0;
        });

        var wmiTasks = new ConcurrentBag<Task>();
        var wmiSemaphore = new SemaphoreSlim(AppSettings.DefaultWmiConcurrency);
        var dbSemaphore = new SemaphoreSlim(1); // Serialize DB writes to prevent DbContext threading issues
        var pendingDevices = new ConcurrentQueue<Device>();
        var pendingWmiFailures = 0;
        var pendingAliveHosts = 0;
        var nonHpDevices = 0;

        var flushTimer = new DispatcherTimer(DispatcherPriority.Background, Application.Current.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        flushTimer.Tick += (_, _) => FlushPendingDevicesToUi(pendingDevices, ref pendingWmiFailures, ref pendingAliveHosts);
        flushTimer.Start();

        try
        {
            Log.Information("Starting scan of {Cidr} with concurrency {Concurrency}", CidrInput, Concurrency);

            await foreach (var result in _scanner.PingSweepAsync(cidr, Concurrency, progress, _cts.Token))
            {
                if (!result.IsAlive) continue;
                Interlocked.Increment(ref pendingAliveHosts);

                var ip = result.IpAddress;
                var task = Task.Run(async () =>
                {
                    await wmiSemaphore.WaitAsync(_cts!.Token);
                    try
                    {
                        var device = await _discovery.IdentifyDeviceAsync(ip, networkCred, _cts!.Token);
                        if (device is not null)
                        {
                            device.Status = DeviceStatus.Online;
                            device.LastScanned = DateTime.Now;
                            await dbSemaphore.WaitAsync(_cts!.Token);
                            try { await _deviceRepository.UpsertAsync(device); }
                            finally { dbSemaphore.Release(); }
                            pendingDevices.Enqueue(device);
                            Log.Information("Discovered HP device: {Hostname} ({Model}) at {Ip}",
                                device.Hostname, device.Model, device.IpAddress);
                        }
                        else
                        {
                            // WMI connected but device is not HP
                            Interlocked.Increment(ref nonHpDevices);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref pendingWmiFailures);
                        Log.Debug("WMI failed for {Ip}: {Error}", ip, ex.Message);
                    }
                    finally
                    {
                        wmiSemaphore.Release();
                    }
                }, _cts.Token);
                wmiTasks.Add(task);
            }

            ScanStatus = $"Ping sweep done. Waiting for {wmiTasks.Count(t => !t.IsCompleted)} remaining WMI queries...";
            await Task.WhenAll(wmiTasks);
            flushTimer.Stop();
            FlushPendingDevicesToUi(pendingDevices, ref pendingWmiFailures, ref pendingAliveHosts);

            // Mark existing devices in the scanned range that didn't respond as Offline
            var existingDevices = await _deviceRepository.GetAllAsync();
            int offlineCount = 0;
            foreach (var device in existingDevices)
            {
                if (device.Status == DeviceStatus.Online || device.Status == DeviceStatus.Discovered)
                {
                    // If this device's IP is in the CIDR range we just scanned, and it wasn't found alive, mark offline
                    if (!string.IsNullOrEmpty(device.IpAddress) && cidr.Contains(device.IpAddress))
                    {
                        // Check if this device was found in the current scan (it would have been upserted with Online status)
                        var wasFound = device.LastScanned > DateTime.Now.AddMinutes(-5);
                        if (!wasFound)
                        {
                            device.Status = DeviceStatus.Offline;
                            await _deviceRepository.UpdateAsync(device);
                            offlineCount++;
                        }
                    }
                }
            }

            ScanStatus = $"Scan complete. Found {HpDevicesFound} HP devices from {AliveHosts} alive hosts ({TotalIps} IPs scanned).";
            if (nonHpDevices > 0) ScanStatus += $" {nonHpDevices} non-HP.";
            if (WmiFailures > 0) ScanStatus += $" {WmiFailures} unreachable.";
            if (offlineCount > 0) ScanStatus += $" {offlineCount} marked offline.";
            SnackbarService.Show($"Scan complete: {HpDevicesFound} HP devices found");

            Log.Information("Scan complete: {HpDevices} HP devices, {Offline} offline, from {Alive} alive in {Total} IPs",
                HpDevicesFound, offlineCount, AliveHosts, TotalIps);

            _ = Task.Run(async () =>
            {
                try { await _emailService.SendScanCompleteNotificationAsync(HpDevicesFound, AliveHosts, TotalIps); }
                catch (Exception ex) { Log.Warning(ex, "Failed to send scan complete email notification"); }
            });

            await LoadDevicesAsync();
        }
        catch (OperationCanceledException)
        {
            try { await Task.WhenAll(wmiTasks); } catch (Exception ex) { Log.Debug(ex, "Scan cancellation cleanup"); }
            flushTimer.Stop();
            FlushPendingDevicesToUi(pendingDevices, ref pendingWmiFailures, ref pendingAliveHosts);
            ScanStatus = $"Scan cancelled. Found {HpDevicesFound} HP devices so far.";
            await LoadDevicesAsync();
        }
        catch (Exception ex)
        {
            flushTimer.Stop();
            ScanStatus = $"Scan error: {ex.Message}";
            Log.Error(ex, "Scan failed");
        }
        finally
        {
            wmiSemaphore.Dispose();
            dbSemaphore.Dispose();
            IsScanning = false;
        }
    }

    private void FlushPendingDevicesToUi(ConcurrentQueue<Device> pendingDevices, ref int pendingWmiFailures, ref int pendingAliveHosts)
    {
        var failures = Interlocked.Exchange(ref pendingWmiFailures, 0);
        if (failures > 0) WmiFailures += failures;

        var alive = Interlocked.Exchange(ref pendingAliveHosts, 0);
        if (alive > 0) AliveHosts += alive;

        while (pendingDevices.TryDequeue(out _))
        {
            HpDevicesFound++;
        }

        if (failures > 0 || alive > 0)
        {
            ScanStatus = $"Scanning... {ScannedIps}/{TotalIps} | Alive: {AliveHosts} | HP: {HpDevicesFound}";
        }
    }

    [RelayCommand]
    private void StopScan()
    {
        _cts?.Cancel();
        ScanStatus = "Cancelling scan...";
    }

    // ── Single Host Add ──
    [RelayCommand]
    private async Task AddSingleHostAsync()
    {
        if (string.IsNullOrWhiteSpace(SingleHostInput))
        {
            SingleHostStatus = "Please enter a hostname or IP address.";
            return;
        }

        if (SelectedCredential is null)
        {
            SingleHostStatus = "Please select credentials.";
            return;
        }

        IsAddingHost = true;
        SingleHostStatus = $"Connecting to {SingleHostInput.Trim()}...";
        var networkCred = await _credentialStore.DecryptAsync(SelectedCredential);
        var target = SingleHostInput.Trim();

        try
        {
            var device = await _discovery.IdentifyDeviceAsync(target, networkCred, CancellationToken.None);
            if (device is not null)
            {
                device.Status = DeviceStatus.Online;
                device.LastScanned = DateTime.Now;
                await _deviceRepository.UpsertAsync(device);
                await LoadDevicesAsync();
                SingleHostStatus = $"Added {device.Hostname} ({device.Model})";
                SingleHostInput = "";
            }
            else
            {
                SingleHostStatus = $"'{target}' is not an HP device or could not be identified.";
            }
        }
        catch (Exception ex)
        {
            SingleHostStatus = $"Failed to connect: {ex.Message}";
            Log.Error(ex, "Single host add failed for {Target}", target);
            SnackbarService.ShowError($"Failed to connect: {ex.Message}");
        }
        finally
        {
            IsAddingHost = false;
        }
    }

    // ── Device Actions ──
    [RelayCommand]
    private async Task DeleteDeviceAsync(Device? device)
    {
        if (device is null) return;
        if (!DialogHelper.Confirm(
            $"Delete device '{device.Hostname}' ({device.IpAddress})?\nThis cannot be undone.",
            "Delete Device"))
            return;

        await _deviceRepository.DeleteAsync(device.Id);
        Devices.Remove(device);
        FilteredDevices.Remove(device);
        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(HasAnyDevices));
        OnPropertyChanged(nameof(IsFilterEmpty));
        UpdateSelectedDeviceCount();
        StatusMessage = $"Device '{device.Hostname}' deleted.";
        SnackbarService.Show($"Device '{device.Hostname}' deleted");
    }

    [RelayCommand]
    private async Task ClearAllDevicesAsync()
    {
        if (Devices.Count == 0) return;
        if (!DialogHelper.Confirm($"Are you sure you want to delete all {Devices.Count} devices?\nThis cannot be undone.", "Clear All Devices"))
            return;

        await _deviceRepository.DeleteAllAsync();
        Devices.Clear();
        FilteredDevices.Clear();
        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(HasAnyDevices));
        OnPropertyChanged(nameof(IsFilterEmpty));
        UpdateSelectedDeviceCount();
        StatusMessage = "All devices cleared.";
        SnackbarService.Show("All devices cleared");
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        if (FilteredDevices.Count == 0) { StatusMessage = "No devices to export."; return; }
        var path = DialogHelper.SaveFileDialog($"devices-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        if (path is null) return;
        try
        {
            await ReportGenerator.GenerateCsvReport(FilteredDevices, path);
            StatusMessage = $"Exported {FilteredDevices.Count} devices to {path}";
            SnackbarService.Show($"Exported {FilteredDevices.Count} devices");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed: {ex.Message}";
            SnackbarService.ShowError($"Export failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ExportHtmlAsync()
    {
        if (FilteredDevices.Count == 0) { StatusMessage = "No devices to export."; return; }
        var path = DialogHelper.SaveFileDialog(
            $"devices-report-{DateTime.Now:yyyyMMdd-HHmmss}.html",
            "HTML Files (*.html)|*.html|All Files (*.*)|*.*");
        if (path is null) return;
        try
        {
            await ReportGenerator.GenerateHtmlReport(FilteredDevices, path);
            StatusMessage = $"HTML report saved to {path}";
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = $"HTML export failed: {ex.Message}";
            SnackbarService.ShowError($"HTML export failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task RescanDeviceAsync()
    {
        if (SelectedDevice is null) return;
        StatusMessage = $"Re-scanning {SelectedDevice.Hostname}...";
        await LoadDevicesAsync();
        StatusMessage = "Device list refreshed.";
    }

    [RelayCommand]
    private void CopyHostname() => ClipboardHelper.CopyToClipboard(SelectedDevice?.Hostname);

    [RelayCommand]
    private void CopyIpAddress() => ClipboardHelper.CopyToClipboard(SelectedDevice?.IpAddress);

    [RelayCommand]
    private async Task DeleteSelectedDevicesAsync()
    {
        // Use IsSelected or fall back to the single SelectedDevice
        var selected = Devices.Where(d => d.IsSelected).ToList();
        if (selected.Count == 0 && SelectedDevice is not null)
            selected = [SelectedDevice];

        if (selected.Count == 0)
        {
            StatusMessage = "No devices selected.";
            return;
        }

        if (!DialogHelper.Confirm(
            $"Delete {selected.Count} selected device(s)?\nThis cannot be undone.",
            "Delete Selected Devices"))
            return;

        foreach (var device in selected)
        {
            await _deviceRepository.DeleteAsync(device.Id);
            Devices.Remove(device);
        }

        ApplyFilter();
        UpdateSelectedDeviceCount();
        StatusMessage = $"Deleted {selected.Count} device(s).";
        SnackbarService.ShowSuccess($"Deleted {selected.Count} device(s)");
    }
}
