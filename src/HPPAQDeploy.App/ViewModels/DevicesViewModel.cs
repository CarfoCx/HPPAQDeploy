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
    private readonly HashSet<Device> _subscribedDevices = [];
    private readonly SemaphoreSlim _loadGate = new(1, 1);
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
        foreach (var device in _subscribedDevices.Except(value).ToList())
        {
            device.PropertyChanged -= Device_PropertyChanged;
            _subscribedDevices.Remove(device);
        }

        foreach (var device in value)
        {
            if (_subscribedDevices.Add(device))
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
        if (!await _loadGate.WaitAsync(0))
            return;

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
            _loadGate.Release();
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

        NetworkCredential networkCred;
        try
        {
            networkCred = await _credentialStore.DecryptAsync(SelectedCredential);
        }
        catch (Exception ex)
        {
            ScanStatus = $"Unable to decrypt the selected credential: {ex.Message}";
            Log.Error(ex, "Failed to decrypt credentials for network scan");
            SnackbarService.ShowError("Unable to use the selected credential.");
            return;
        }

        IsScanning = true;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        ScannedIps = 0;
        AliveHosts = 0;
        HpDevicesFound = 0;
        WmiFailures = 0;
        TotalIps = cidr.UsableHostCount;
        ScanStatus = $"Scanning {cidr.UsableHostCount} IPs in {CidrInput}...";

        var progress = new Progress<(int completed, int total)>(p =>
        {
            ScannedIps = p.completed;
            ProgressPercent = TotalIps > 0 ? (double)p.completed / TotalIps * 100 : 0;
        });

        var wmiTasks = new ConcurrentBag<Task>();
        var wmiSemaphore = new SemaphoreSlim(AppSettings.DefaultWmiConcurrency);
        var discoveredDevices = new ConcurrentBag<Device>();
        var pendingDevices = new ConcurrentQueue<Device>();
        var pendingWmiFailures = 0;
        var pendingAliveHosts = 0;
        var nonHpDevices = 0;
        var discoveredDevicesPersisted = false;
        var respondingHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                respondingHosts.Add(result.IpAddress);

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
                            discoveredDevices.Add(device);
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
            await PersistDiscoveredDevicesAsync(discoveredDevices, pendingDevices, _cts.Token);
            discoveredDevicesPersisted = true;
            flushTimer.Stop();
            FlushPendingDevicesToUi(pendingDevices, ref pendingWmiFailures, ref pendingAliveHosts);

            // Mark existing devices in the scanned range that didn't respond as Offline
            var existingDevices = await _deviceRepository.GetAllAsync();
            var offlineDevices = new List<Device>();
            foreach (var device in existingDevices)
            {
                if (device.Status == DeviceStatus.Online || device.Status == DeviceStatus.Discovered)
                {
                    // If this device's IP is in the CIDR range we just scanned, and it wasn't found alive, mark offline
                    if (!string.IsNullOrEmpty(device.IpAddress) && cidr.Contains(device.IpAddress))
                    {
                        // A device can answer ping even when WMI discovery fails; only mark it
                        // offline when it did not respond during this specific sweep.
                        if (!respondingHosts.Contains(device.IpAddress))
                        {
                            device.Status = DeviceStatus.Offline;
                            offlineDevices.Add(device);
                        }
                    }
                }
            }

            var batchableOfflineDevices = offlineDevices
                .Where(device => !string.IsNullOrWhiteSpace(device.Hostname))
                .ToList();
            if (batchableOfflineDevices.Count > 0)
                await _deviceRepository.BatchUpsertAsync(batchableOfflineDevices, _cts.Token);

            // Hostname-less legacy rows cannot be upserted safely. Reload their
            // recommendations before updating so marking them offline preserves data.
            foreach (var device in offlineDevices.Except(batchableOfflineDevices))
            {
                var persisted = await _deviceRepository.GetWithRecommendationsAsync(device.Id, _cts.Token);
                if (persisted is null) continue;
                persisted.Status = DeviceStatus.Offline;
                await _deviceRepository.UpdateAsync(persisted, _cts.Token);
            }

            var offlineCount = offlineDevices.Count;

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
            if (!discoveredDevicesPersisted)
            {
                try { await PersistDiscoveredDevicesAsync(discoveredDevices, pendingDevices, CancellationToken.None); }
                catch (Exception ex) { Log.Warning(ex, "Failed to persist partial scan results during cancellation"); }
            }
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
            flushTimer.Stop();
            wmiSemaphore.Dispose();
            _cts?.Dispose();
            _cts = null;
            IsScanning = false;
        }
    }

    private async Task PersistDiscoveredDevicesAsync(
        ConcurrentBag<Device> discoveredDevices,
        ConcurrentQueue<Device> pendingDevices,
        CancellationToken ct)
    {
        var devices = discoveredDevices.ToList();
        if (devices.Count == 0)
            return;

        await _deviceRepository.BatchUpsertAsync(devices, ct);
        foreach (var device in devices)
            pendingDevices.Enqueue(device);
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
        var target = SingleHostInput.Trim();

        try
        {
            var networkCred = await _credentialStore.DecryptAsync(SelectedCredential);
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
        if (SelectedCredential is null)
        {
            StatusMessage = "Please select credentials to rescan.";
            return;
        }

        var host = SelectedDevice.Hostname ?? SelectedDevice.IpAddress;
        StatusMessage = $"Re-scanning {host}...";
        IsLoading = true;

        try
        {
            // Ping check first
            using var ping = new System.Net.NetworkInformation.Ping();
            var pingReply = await ping.SendPingAsync(host!, AppSettings.PingTimeoutMs);

            if (pingReply.Status != System.Net.NetworkInformation.IPStatus.Success)
            {
                SelectedDevice.Status = DeviceStatus.Offline;
                await _deviceRepository.UpdateAsync(SelectedDevice);
                StatusMessage = $"{host} is offline (ping failed).";
                SnackbarService.ShowWarning($"{host} is offline.");
                await LoadDevicesAsync();
                return;
            }

            // WMI re-identification
            var networkCred = await _credentialStore.DecryptAsync(SelectedCredential);
            var refreshed = await _discovery.IdentifyDeviceAsync(host!, networkCred, CancellationToken.None);

            if (refreshed is not null)
            {
                SelectedDevice.Model = refreshed.Model;
                SelectedDevice.SerialNumber = refreshed.SerialNumber;
                SelectedDevice.ProductId = refreshed.ProductId;
                SelectedDevice.OsVersion = refreshed.OsVersion;
                SelectedDevice.IpAddress = refreshed.IpAddress;
                SelectedDevice.Status = DeviceStatus.Online;
                SelectedDevice.LastScanned = DateTime.Now;
                await _deviceRepository.UpdateAsync(SelectedDevice);
                StatusMessage = $"Re-scanned {host}: {refreshed.Model} - Online.";
                SnackbarService.ShowSuccess($"Re-scanned {host} successfully");
            }
            else
            {
                SelectedDevice.Status = DeviceStatus.Online;
                SelectedDevice.LastScanned = DateTime.Now;
                await _deviceRepository.UpdateAsync(SelectedDevice);
                StatusMessage = $"{host} is online but could not be re-identified.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Re-scan failed for {host}: {ex.Message}";
            Log.Error(ex, "Rescan failed for {Host}", host);
            SnackbarService.ShowError($"Re-scan failed: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
            await LoadDevicesAsync();
        }
    }

    [RelayCommand]
    private void CopyHostname(Device? device) => ClipboardHelper.CopyToClipboard(device?.Hostname);

    [RelayCommand]
    private void CopyIpAddress(Device? device) => ClipboardHelper.CopyToClipboard(device?.IpAddress);

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
