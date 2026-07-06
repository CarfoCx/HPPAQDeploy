using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HPPAQDeploy.App.Helpers;
using HPPAQDeploy.App.Services;
using HPPAQDeploy.Core.Interfaces;
using HPPAQDeploy.Core.Models;
using HPPAQDeploy.Infrastructure.Hpia;
using HPPAQDeploy.Shared.Configuration;
using HPPAQDeploy.Shared.Helpers;
using Serilog;

namespace HPPAQDeploy.App.ViewModels;

public partial class DeployViewModel : ObservableObject
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly IDeviceGroupRepository _groupRepository;
    private readonly IHpiaManager _hpiaManager;
    private readonly ICredentialStore _credentialStore;
    private readonly IRemoteExecutor _remoteExecutor;
    private readonly IFileTransfer _fileTransfer;
    private readonly IAgentClient _agentClient;
    private readonly IAgentBootstrapper _agentBootstrapper;
    private readonly IDeploymentHistoryRepository _historyRepository;
    private readonly IEmailService _emailService;
    private readonly RepositorySyncer _repoSyncer;
    private CancellationTokenSource? _scanCts;
    private volatile bool _softCancelled;
    private System.Diagnostics.Stopwatch? _scanStopwatch;
    private System.Diagnostics.Stopwatch? _deployStopwatch;
    private System.Windows.Threading.DispatcherTimer? _elapsedTimer;

    [ObservableProperty]
    private ObservableCollection<Device> _devices = [];

    [ObservableProperty]
    private ObservableCollection<Device> _filteredDevices = [];

    [ObservableProperty]
    private string _deviceFilterText = "";

    [ObservableProperty]
    private ObservableCollection<Credential> _credentials = [];

    [ObservableProperty]
    private Credential? _selectedCredential;

    [ObservableProperty]
    private Device? _selectedDevice;

    [ObservableProperty]
    private ObservableCollection<HpiaRecommendation> _selectedDeviceRecommendations = [];

    [ObservableProperty]
    private bool _hasSelectedDevice;

    [ObservableProperty]
    private bool _isDeploying;

    [ObservableProperty]
    private int _deployedCount;

    [ObservableProperty]
    private int _totalToDeploy;

    [ObservableProperty]
    private int _successCount;

    [ObservableProperty]
    private int _failCount;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _deployStatus = "Loading devices...";

    [ObservableProperty]
    private int _concurrency = AppSettings.DefaultDeployConcurrency;

    [ObservableProperty]
    private ObservableCollection<DeploymentLogEntry> _deploymentLog = [];

    [ObservableProperty]
    private ObservableCollection<DeploymentLogEntry> _filteredDeploymentLog = [];

    [ObservableProperty]
    private ObservableCollection<DeploymentLogEntry> _scanLog = [];

    [ObservableProperty]
    private string _logFilterText = "";

    [ObservableProperty]
    private string _logLevelFilter = "All";

    public List<string> LogLevelOptions { get; } = ["All", "Info", "Success", "Warning", "Error"];

    private const int MaxLogEntries = 1000;

    [ObservableProperty]
    private int _totalPendingUpdates;

    [ObservableProperty]
    private int _selectedUpdateCount;

    [ObservableProperty]
    private int _rebootPendingCount;

    private WeakReference<Views.DeploymentLogWindow>? _logWindowRef;

    // ── Group selection & scanning ──
    [ObservableProperty]
    private ObservableCollection<DeviceGroup> _availableGroups = [];

    [ObservableProperty]
    private DeviceGroup? _selectedGroup;

    [ObservableProperty]
    private ObservableCollection<Device> _groupDevices = [];

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private int _scannedCount;

    [ObservableProperty]
    private int _totalToScan;

    [ObservableProperty]
    private double _scanProgress;

    [ObservableProperty]
    private string _scanStatus = "";

    [ObservableProperty]
    private bool _hasScannedGroup;

    [ObservableProperty]
    private string _scanElapsedText = "";

    [ObservableProperty]
    private string _deployElapsedText = "";

    [ObservableProperty]
    private ObservableCollection<MonitorSessionViewModel> _activeSessions = [];

    [ObservableProperty]
    private MonitorSessionViewModel? _selectedSession;

    public bool IsSelectedGroupScanning => SelectedGroup != null && ActiveSessions.Any(s => s.GroupId == SelectedGroup.Id && s.IsScanning);
    public bool IsSelectedGroupDeploying => SelectedGroup != null && ActiveSessions.Any(s => s.GroupId == SelectedGroup.Id && s.IsDeploying);

    public bool HasSelectedGroup => SelectedGroup is not null;

    partial void OnSelectedGroupChanged(DeviceGroup? value)
    {
        OnPropertyChanged(nameof(HasSelectedGroup));
        OnPropertyChanged(nameof(IsSelectedGroupScanning));
        OnPropertyChanged(nameof(IsSelectedGroupDeploying));
        HasScannedGroup = false;
        Devices = [];
        SelectedDevice = null;
        if (value is not null)
            AsyncInitHelper.SafeFireAndForget(LoadGroupDevicesAsync, nameof(DeployViewModel));
        else
            GroupDevices = [];
    }

    public DeployViewModel(
        IDeviceRepository deviceRepository,
        IDeviceGroupRepository groupRepository,
        IHpiaManager hpiaManager,
        ICredentialStore credentialStore,
        IRemoteExecutor remoteExecutor,
        IFileTransfer fileTransfer,
        IAgentClient agentClient,
        IAgentBootstrapper agentBootstrapper,
        IDeploymentHistoryRepository historyRepository,
        IEmailService emailService,
        RepositorySyncer repoSyncer)
    {
        _deviceRepository = deviceRepository;
        _groupRepository = groupRepository;
        _hpiaManager = hpiaManager;
        _credentialStore = credentialStore;
        _remoteExecutor = remoteExecutor;
        _fileTransfer = fileTransfer;
        _agentClient = agentClient;
        _agentBootstrapper = agentBootstrapper;
        _historyRepository = historyRepository;
        _emailService = emailService;
        _repoSyncer = repoSyncer;
        DeploymentLog.CollectionChanged += OnDeploymentLogCollectionChanged;
        GroupsViewModel.GroupsChanged += (_, _) =>
            AsyncInitHelper.SafeFireAndForget(RefreshGroupsAsync, nameof(DeployViewModel));
        CredentialManagerViewModel.CredentialsChanged += (_, _) =>
            AsyncInitHelper.SafeFireAndForget(RefreshCredentialsAsync, nameof(DeployViewModel));
            
        AsyncInitHelper.SafeFireAndForget(LoadAsync, nameof(DeployViewModel));
    }

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
            Log.Error(ex, "Failed to refresh credentials in deploy view");
        }
    }

    public async Task RefreshGroupsAsync()
    {
        try
        {
            var currentName = SelectedGroup?.Name;
            var groups = await _groupRepository.GetAllAsync();
            AvailableGroups = new ObservableCollection<DeviceGroup>(groups);
            if (currentName is not null)
                SelectedGroup = AvailableGroups.FirstOrDefault(g => g.Name == currentName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to refresh groups in deploy view");
        }
    }

    private void OnDeploymentLogCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ApplyLogFilter();
    }

    partial void OnLogFilterTextChanged(string value)
    {
        ApplyLogFilter();
    }

    partial void OnLogLevelFilterChanged(string value)
    {
        ApplyLogFilter();
    }

    private void ApplyLogFilter()
    {
        var filtered = DeploymentLog.AsEnumerable();

        if (!string.IsNullOrEmpty(LogLevelFilter) && LogLevelFilter != "All")
        {
            filtered = filtered.Where(e => string.Equals(e.Level, LogLevelFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(LogFilterText))
        {
            var text = LogFilterText.Trim();
            filtered = filtered.Where(e =>
                (e.DeviceName?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (e.Message?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        FilteredDeploymentLog = new ObservableCollection<DeploymentLogEntry>(filtered);
    }

    partial void OnDeviceFilterTextChanged(string value) => ApplyDeviceFilter();

    private void ApplyDeviceFilter()
    {
        if (string.IsNullOrWhiteSpace(DeviceFilterText))
        {
            FilteredDevices = new ObservableCollection<Device>(Devices);
        }
        else
        {
            var text = DeviceFilterText.Trim();
            FilteredDevices = new ObservableCollection<Device>(
                Devices.Where(d =>
                    (d.Hostname?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (d.Model?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (d.IpAddress?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)));
        }
    }

    partial void OnDevicesChanged(ObservableCollection<Device> value) => ApplyDeviceFilter();
    // ── Elapsed time tracking ──

    private void StartElapsedTimer(bool isScan)
    {
        var sw = new System.Diagnostics.Stopwatch();
        sw.Start();

        if (isScan) _scanStopwatch = sw;
        else _deployStopwatch = sw;

        _elapsedTimer?.Stop();
        _elapsedTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _elapsedTimer.Tick += (_, _) => UpdateElapsedText(isScan);
        _elapsedTimer.Start();
    }

    private void StopElapsedTimer(bool isScan)
    {
        _elapsedTimer?.Stop();
        _elapsedTimer = null;
        if (isScan) _scanStopwatch?.Stop();
        else _deployStopwatch?.Stop();
    }

    private void UpdateElapsedText(bool isScan)
    {
        var sw = isScan ? _scanStopwatch : _deployStopwatch;
        if (sw is null) return;

        var elapsed = sw.Elapsed;
        var elapsedStr = elapsed.TotalHours >= 1
            ? $"{elapsed:h\\:mm\\:ss}"
            : $"{elapsed:m\\:ss}";

        var completed = isScan ? ScannedCount : DeployedCount;
        var total = isScan ? TotalToScan : TotalToDeploy;

        if (completed > 0 && total > 0)
        {
            var avgPerDevice = elapsed.TotalSeconds / completed;
            var remaining = TimeSpan.FromSeconds(avgPerDevice * (total - completed));
            var remainStr = remaining.TotalHours >= 1
                ? $"{remaining:h\\:mm\\:ss}"
                : $"{remaining:m\\:ss}";

            if (isScan)
                ScanElapsedText = $"{elapsedStr} elapsed, ~{remainStr} remaining";
            else
                DeployElapsedText = $"{elapsedStr} elapsed, ~{remainStr} remaining";
        }
        else
        {
            if (isScan)
                ScanElapsedText = $"{elapsedStr} elapsed";
            else
                DeployElapsedText = $"{elapsedStr} elapsed";
        }
    }

    // ── Deploy pre-flight ping check ──

    private async Task<List<Device>> PingFilterDevicesAsync(List<Device> devices, CancellationToken ct)
    {
        var reachable = new System.Collections.Concurrent.ConcurrentBag<Device>();
        int offlineCount = 0;

        var pingTasks = devices.Select(async device =>
        {
            var host = device.Hostname ?? device.IpAddress;
            try
            {
                using var ping = new System.Net.NetworkInformation.Ping();
                var reply = await ping.SendPingAsync(host!, AppSettings.PingTimeoutMs);
                if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
                {
                    reachable.Add(device);
                }
                else
                {
                    Interlocked.Increment(ref offlineCount);
                    _ = Application.Current.Dispatcher.BeginInvoke(() =>
                        AddLog(device, "Device is offline (ping failed), skipping deployment", "Warning"));
                }
            }
            catch
            {
                Interlocked.Increment(ref offlineCount);
                _ = Application.Current.Dispatcher.BeginInvoke(() =>
                    AddLog(device, "Device unreachable, skipping deployment", "Warning"));
            }
        });

        await Task.WhenAll(pingTasks);

        if (offlineCount > 0)
        {
            _ = Application.Current.Dispatcher.BeginInvoke(() =>
                DeployStatus = $"{offlineCount} device(s) offline, deploying to {reachable.Count} reachable device(s)...");
        }

        return reachable.ToList();
    }

    partial void OnSelectedDeviceChanged(Device? value)
    {
        HasSelectedDevice = value is not null;

        if (value is null)
        {
            SelectedDeviceRecommendations = [];
            return;
        }

        var recs = value.Recommendations?.ToList() ?? [];
        SelectedDeviceRecommendations = new ObservableCollection<HpiaRecommendation>(recs);
    }

    [RelayCommand]
    private void CopyHostname()
    {
        Helpers.ClipboardHelper.CopyToClipboard(SelectedDevice?.Hostname);
    }

    [RelayCommand]
    private void CopyIpAddress()
    {
        Helpers.ClipboardHelper.CopyToClipboard(SelectedDevice?.IpAddress);
    }

    [RelayCommand]
    private void CopySoftPaq(string? softPaqId)
    {
        Helpers.ClipboardHelper.CopyToClipboard(softPaqId);
    }

    [RelayCommand]
    private void CopyUpdateName(string? name)
    {
        Helpers.ClipboardHelper.CopyToClipboard(name);
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        try
        {
            // Load groups
            var groups = await _groupRepository.GetAllAsync();
            AvailableGroups = new ObservableCollection<DeviceGroup>(groups);

            // Restore selection or auto-select first group
            var currentGroup = SelectedGroup;
            if (currentGroup is not null)
            {
                var match = AvailableGroups.FirstOrDefault(g => g.Name == currentGroup.Name);
                if (match is not null) SelectedGroup = match;
            }
            else if (AvailableGroups.Count > 0)
            {
                SelectedGroup = AvailableGroups[0];
            }

            // Sync concurrency from settings
            Concurrency = AppSettings.DefaultDeployConcurrency;

            // Load credentials
            await RefreshCredentialsAsync();

            // If a group is selected and was scanned, reload its devices
            if (SelectedGroup is not null && HasScannedGroup)
            {
                await LoadScannedDevicesAsync();
            }
            else if (SelectedGroup is null)
            {
                DeployStatus = "Select a group and scan it to check for available updates.";
            }
        }
        catch (Exception ex)
        {
            DeployStatus = $"Error loading: {ex.Message}";
            Log.Error(ex, "Failed to load deploy view data");
            SnackbarService.ShowError($"Failed to load deploy view: {ex.Message}");
        }
    }

    private async Task LoadGroupDevicesAsync()
    {
        if (SelectedGroup is null) return;
        try
        {
            var devices = await _deviceRepository.GetByGroupAsync(SelectedGroup.Name);
            GroupDevices = new ObservableCollection<Device>(devices);
            Devices = new ObservableCollection<Device>(devices);
            DeployStatus = $"Group '{SelectedGroup.Name}' has {devices.Count} device(s). Click 'Scan Group' to check for updates.";
        }
        catch (Exception ex)
        {
            DeployStatus = $"Error loading group devices: {ex.Message}";
            Log.Error(ex, "Failed to load group devices");
            SnackbarService.ShowError($"Failed to load group devices: {ex.Message}");
        }
    }

    private async Task LoadScannedDevicesAsync()
    {
        if (SelectedGroup is null) return;
        var scannedDevices = (await _deviceRepository.GetByGroupAsync(SelectedGroup.Name))
            .Where(d => d.LastAnalyzed.HasValue ||
                        d.Status == DeviceStatus.ReadyToDeploy ||
                        d.Status == DeviceStatus.Online ||
                        d.Status == DeviceStatus.Failed ||
                        d.Status == DeviceStatus.RebootRequired ||
                        d.NeedsReboot ||
                        (d.Recommendations != null && d.Recommendations.Any()))
            .OrderByDescending(d => d.Recommendations?.Count > 0)
            .ThenBy(d => d.Hostname)
            .ToList();

        Devices = new ObservableCollection<Device>(scannedDevices);
        SelectedDevice = null;

        TotalPendingUpdates = scannedDevices
            .SelectMany(d => d.Recommendations ?? new List<HpiaRecommendation>())
            .Count();

        SelectedUpdateCount = scannedDevices
            .SelectMany(d => d.Recommendations ?? new List<HpiaRecommendation>())
            .Count(r => r.Selected);

        RebootPendingCount = scannedDevices.Count(d =>
            d.NeedsReboot || d.Status == DeviceStatus.RebootRequired);

        if (Devices.Count == 0)
            DeployStatus = $"No scanned devices found for '{SelectedGroup.Name}'.";
        else if (TotalPendingUpdates == 0)
            DeployStatus = $"All {Devices.Count} scanned device(s) in '{SelectedGroup.Name}' are up to date. No updates needed.";
        else
            DeployStatus = $"{Devices.Count} scanned device(s), {TotalPendingUpdates} update(s) ready to deploy.";
    }

    [RelayCommand]
    private void SelectAllDeviceUpdates()
    {
        foreach (var rec in SelectedDeviceRecommendations)
            rec.Selected = true;
        DeployStatus = $"Selected all {SelectedDeviceRecommendations.Count} updates for {SelectedDevice?.Hostname}.";
    }

    [RelayCommand]
    private void DeselectAllDeviceUpdates()
    {
        foreach (var rec in SelectedDeviceRecommendations)
            rec.Selected = false;
        DeployStatus = $"Cleared update selection for {SelectedDevice?.Hostname}.";
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task DeployAllAsync()
    {
        var toDeploy = Devices.Where(d =>
            d.Status == DeviceStatus.ReadyToDeploy ||
            (d.Recommendations != null && d.Recommendations.Any())).ToList();

        if (toDeploy.Count == 0)
        {
            DeployStatus = "No devices with pending updates.";
            return Task.CompletedTask;
        }

        return DeployDevicesAsync(toDeploy);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task DeploySelectedAsync()
    {
        if (SelectedDevice is null)
        {
            DeployStatus = "Click a device in the list first, then click 'Install on Selected'.";
            return Task.CompletedTask;
        }

        if (SelectedDevice.Recommendations is null || !SelectedDevice.Recommendations.Any())
        {
            DeployStatus = $"No updates available for {SelectedDevice.Hostname}.";
            return Task.CompletedTask;
        }

        return DeployDevicesAsync([SelectedDevice]);
    }

    private async Task DeployDevicesAsync(List<Device> toDeploy)
    {
        if (SelectedCredential is null)
        {
            DeployStatus = "Please select credentials";
            return;
        }

        if (!toDeploy.Any())
        {
            DeployStatus = "No devices with pending updates. Run analysis first.";
            return;
        }

        var allRecs = toDeploy.SelectMany(d => d.Recommendations ?? []).ToList();
        var selectedRecs = allRecs.Where(r => r.Selected).ToList();
        var deployingRecs = selectedRecs.Count > 0 ? selectedRecs : allRecs;
        var deployingCount = deployingRecs.Count;
        var totalSizeBytes = deployingRecs.Sum(r => r.SizeBytes);
        var sizeText = totalSizeBytes > 0
            ? $"\nEstimated download size: {totalSizeBytes / 1024.0 / 1024.0:F1} MB"
            : "";

        // List update names (max 8, then "...and X more")
        var updateNames = deployingRecs.Select(r => r.Name).Distinct().Take(8).ToList();
        var updateListText = string.Join("\n  • ", updateNames);
        if (deployingRecs.Select(r => r.Name).Distinct().Count() > 8)
            updateListText += $"\n  ...and {deployingRecs.Select(r => r.Name).Distinct().Count() - 8} more";

        if (!DialogHelper.Confirm(
            $"Deploy {deployingCount} update(s) to {toDeploy.Count} device(s)?\n\n" +
            $"Updates:\n  • {updateListText}\n\n" +
            (selectedRecs.Count > 0
                ? $"Only the {selectedRecs.Count} selected update(s) will be installed."
                : "All available updates will be installed.") +
            sizeText +
            "\n\nSome updates may require a reboot.",
            "Confirm Deployment"))
            return;

        _softCancelled = false;
        IsDeploying = true;
        
        var sessionTitle = $"Deployment: {(toDeploy.Count == 1 ? toDeploy[0].Hostname : SelectedGroup?.Name ?? "Selected Devices")} ({DateTime.Now:HH:mm:ss})";
        var session = new MonitorSessionViewModel(sessionTitle)
        {
            GroupId = SelectedGroup?.Id ?? 0,
            IsDeploying = true,
            Devices = new ObservableCollection<Device>(toDeploy),
            TotalToScan = toDeploy.Count // Reuse for progress tracking
        };

        _ = Application.Current.Dispatcher.BeginInvoke(() =>
        {
            ActiveSessions.Add(session);
            SelectedSession = session;
            OnPropertyChanged(nameof(IsSelectedGroupDeploying));
            OpenDeploymentLog();
        });

        var networkCred = await _credentialStore.DecryptAsync(SelectedCredential);

        // Pre-flight ping check: skip offline devices
        toDeploy = await PingFilterDevicesAsync(toDeploy, session.Cts.Token);
        if (toDeploy.Count == 0)
        {
            session.Status = "No devices are reachable.";
            session.AddLog("System", "No devices reachable for deployment. Aborted.", "Error");
            SnackbarService.ShowError("No devices are reachable for deployment");
            
            session.IsDeploying = false;
            IsDeploying = ActiveSessions.Any(s => s.IsDeploying);
            OnPropertyChanged(nameof(IsSelectedGroupDeploying));
            return;
        }

        TotalToDeploy = toDeploy.Count;
        session.Status = $"Deploying to {TotalToDeploy} device(s)...";
        StartElapsedTimer(isScan: false);

        try
        {
            var semaphore = new SemaphoreSlim(Concurrency);
            var tasks = toDeploy.Select(device => Task.Run(async () =>
            {
                await semaphore.WaitAsync(session.Cts.Token);
                
                // Soft cancel: skip this device if soft cancel was requested
                if (_softCancelled)
                {
                    semaphore.Release();
                    _ = Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        DeployedCount++;
                        ProgressPercent = TotalToDeploy > 0 ? (double)DeployedCount / TotalToDeploy * 100 : 0;
                        AddLog(device, "Skipped (soft cancel)", "Warning");
                    });
                    return;
                }

                // Resolve recs before try so it's accessible in catch
                var recs = device.Recommendations?.Where(r => r.Selected).ToList();
                if (recs == null || !recs.Any())
                    recs = device.Recommendations?.ToList() ?? [];

                try
                {
                    _ = Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        device.Status = DeviceStatus.Deploying;
                        session.AddLog(device.Hostname ?? device.IpAddress, "Starting deployment...", "Info");
                    });

                    var progress = new Progress<string>(msg =>
                    {
                        _ = Application.Current.Dispatcher.BeginInvoke(() =>
                        {
                            session.AddLog(device.Hostname ?? device.IpAddress, msg, "Info");

                            // Parse progress: "Processing sp123 (2 so far) — Friendly Name"
                            // or "Processing SoftPaqs (5 so far)"
                            var match = System.Text.RegularExpressions.Regex.Match(msg, @"\((\d+) so far\)");
                            if (match.Success && int.TryParse(match.Groups[1].Value, out int count) && recs.Count > 0)
                            {
                                device.Progress = Math.Min(100, (double)count / recs.Count * 100);
                            }
                        });
                    });

                    device.Progress = 0;
                    await _hpiaManager.DeployUpdatesAsync(
                        device,
                        networkCred,
                        recs,
                        progress,
                        session.Cts.Token);

                    // Preserve RebootRequired status set by HPIA if exit code was 3010/3020
                    if (device.Status != DeviceStatus.RebootRequired)
                        device.Status = DeviceStatus.Completed;
                    
                    device.Progress = 100;
                    // Clear deployed recommendations so they don't show as "still needed"
                    device.Recommendations = [];
                    await _deviceRepository.UpdateAsync(device);

                    var rebootRequired = device.Status == DeviceStatus.RebootRequired || device.NeedsReboot;

                    // Record deployment history for each update
                    var historyEntries = recs.Select(r => new DeploymentHistory
                    {
                        DeviceId = device.Id,
                        DeviceHostname = device.Hostname ?? "",
                        DeviceIpAddress = device.IpAddress ?? "",
                        UpdateName = r.Name ?? "",
                        SoftPaqId = r.SoftPaqId ?? "",
                        Category = r.Category ?? "",
                        Action = "Deployed",
                        Timestamp = DateTime.Now,
                        RebootRequired = rebootRequired
                    }).ToList();

                    try
                    {
                        await _historyRepository.AddRangeAsync(historyEntries);
                    }
                    catch (Exception histEx)
                    {
                        Log.Warning(histEx, "Failed to record deployment history for {Hostname}", device.Hostname);
                    }

                    _ = Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        session.ScannedCount++; // Use as DeployedCount
                        session.Progress = session.TotalToScan > 0 ? (double)session.ScannedCount / session.TotalToScan * 100 : 0;
                        session.Status = $"Deployed {session.ScannedCount}/{session.TotalToScan} | Success";
                        session.AddLog(device.Hostname ?? device.IpAddress, "Deployment completed successfully", "Success");
                    });

                    Log.Information("Deployment succeeded for {Hostname}", device.Hostname);
                }
                catch (OperationCanceledException)
                {
                    _ = Application.Current.Dispatcher.BeginInvoke(() =>
                        AddLog(device, "Deployment cancelled", "Warning"));
                }
                catch (Exception ex)
                {
                    device.Status = DeviceStatus.Failed;

                    // Attempt cleanup of remote HPIA files on failure
                    try
                    {
                        await _hpiaManager.CleanupRemoteAsync(
                            device.Hostname ?? device.IpAddress,
                            networkCred,
                            CancellationToken.None);
                    }
                    catch (Exception cleanupEx)
                    {
                        Log.Warning(cleanupEx, "Post-failure cleanup failed for {Hostname}", device.Hostname);
                    }

                    // Record failed deployment history for each update
                    var failedEntries = recs.Select(r => new DeploymentHistory
                    {
                        DeviceId = device.Id,
                        DeviceHostname = device.Hostname ?? "",
                        DeviceIpAddress = device.IpAddress ?? "",
                        UpdateName = r.Name ?? "",
                        SoftPaqId = r.SoftPaqId ?? "",
                        Category = r.Category ?? "",
                        Action = "Failed",
                        ErrorMessage = ex.Message,
                        Timestamp = DateTime.Now,
                        RebootRequired = false
                    }).ToList();

                    try
                    {
                        await _historyRepository.AddRangeAsync(failedEntries);
                    }
                    catch (Exception histEx)
                    {
                        Log.Warning(histEx, "Failed to record deployment failure history for {Hostname}", device.Hostname);
                    }

                    _ = Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        FailCount++;
                        DeployedCount++;
                        ProgressPercent = TotalToDeploy > 0 ? (double)DeployedCount / TotalToDeploy * 100 : 0;
                        session.Status = $"Deployed {DeployedCount}/{TotalToDeploy} | Success: {SuccessCount} | Failed: {FailCount}";
                        session.AddLog(device.Hostname ?? device.IpAddress, $"Deployment failed: {ex.Message}", "Error");
                    });

                    Log.Error(ex, "Deployment failed for {Hostname}", device.Hostname);
                }
                finally
                {
                    semaphore.Release();
                }
            }, session.Cts.Token)).ToList();

            await Task.WhenAll(tasks);
            DeployStatus = $"Deployment complete. Success: {SuccessCount} | Failed: {FailCount}";
            SnackbarService.ShowSuccess($"Deployment complete — {SuccessCount} succeeded, {FailCount} failed");

            UpdateRebootPendingCount();

            // Email notification (fire-and-forget with error handling)
            _ = Task.Run(async () =>
            {
                try { await _emailService.SendDeployCompleteNotificationAsync(SuccessCount, FailCount); }
                catch (Exception ex) { Log.Warning(ex, "Failed to send deploy complete email notification"); }
            });
        }
        catch (OperationCanceledException)
        {
            DeployStatus = $"Deployment cancelled. Success: {SuccessCount} | Failed: {FailCount}";
        }
        finally
        {
            StopElapsedTimer(isScan: false);
            session.IsDeploying = false;
            IsDeploying = ActiveSessions.Any(s => s.IsDeploying);
            OnPropertyChanged(nameof(IsSelectedGroupDeploying));
        }
    }

    [RelayCommand]
    private async Task RebootDeviceAsync()
    {
        if (SelectedDevice is null)
        {
            DeployStatus = "No device selected for reboot.";
            return;
        }

        if (SelectedCredential is null)
        {
            DeployStatus = "Please select credentials before rebooting.";
            return;
        }

        var host = SelectedDevice.Hostname ?? SelectedDevice.IpAddress;
        if (!DialogHelper.Confirm(
            $"Reboot {host}?\n\nThe device will restart in 60 seconds, giving users time to save their work.\n\nThis action cannot be undone once the timer expires.",
            "Confirm Reboot"))
            return;

        try
        {
            var networkCred = await _credentialStore.DecryptAsync(SelectedCredential);
            DeployStatus = $"Sending reboot command to {host}...";
            AddLog(SelectedDevice, "Sending reboot command (60-second delay)...", "Warning");

            var result = await _remoteExecutor.ExecuteAsync(
                host,
                networkCred,
                "shutdown /r /t 60 /f",
                null,
                TimeSpan.FromSeconds(30),
                CancellationToken.None);

            // Exit code 0 = success. Exit code -1 with "timed out" is also expected
            // because the machine starts shutting down before the exit code file is written.
            if (result.ExitCode == 0 || (result.ExitCode == -1 && result.ErrorOutput.Contains("timed out", StringComparison.OrdinalIgnoreCase)))
            {
                AddLog(SelectedDevice, "Reboot command sent successfully (60-second countdown)", "Success");
                DeployStatus = $"Reboot scheduled for {host} (60s countdown).";
                SelectedDevice.NeedsReboot = false;
                SelectedDevice.Status = DeviceStatus.Online;
                await _deviceRepository.UpdateAsync(SelectedDevice);
                UpdateRebootPendingCount();
                Log.Information("Reboot command sent to {Hostname}", host);
            }
            else
            {
                AddLog(SelectedDevice, $"Reboot command may have failed (exit code {result.ExitCode}): {result.ErrorOutput}", "Warning");
                DeployStatus = $"Reboot status uncertain for {host}. Check if the device is restarting.";
                Log.Warning("Reboot command returned unexpected result for {Hostname}: exit={ExitCode} err={Error}", host, result.ExitCode, result.ErrorOutput);
            }
        }
        catch (Exception ex)
        {
            AddLog(SelectedDevice, $"Reboot error: {ex.Message}", "Error");
            DeployStatus = $"Reboot failed for {host}: {ex.Message}";
            Log.Error(ex, "Failed to send reboot to {Hostname}", host);
            SnackbarService.ShowError($"Reboot failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task RebootAllPendingAsync()
    {
        if (SelectedCredential is null)
        {
            DeployStatus = "Please select credentials before rebooting.";
            return;
        }

        var pendingDevices = Devices.Where(d =>
            d.NeedsReboot || d.Status == DeviceStatus.RebootRequired).ToList();

        if (pendingDevices.Count == 0)
        {
            DeployStatus = "No devices need rebooting.";
            return;
        }

        if (!DialogHelper.Confirm(
            $"Reboot {pendingDevices.Count} device(s) that need a restart?\n\nEach device will restart in 60 seconds, giving users time to save their work.",
            "Confirm Reboot All Pending"))
            return;

        var networkCred = await _credentialStore.DecryptAsync(SelectedCredential);
        int successCount = 0;
        int failedCount = 0;

        var semaphore = new SemaphoreSlim(Concurrency);
        var tasks = pendingDevices.Select(device => Task.Run(async () =>
        {
            await semaphore.WaitAsync();
            var host = device.Hostname ?? device.IpAddress;
            try
            {
                _ = Application.Current.Dispatcher.BeginInvoke(() =>
                    AddLog(device, "Sending reboot command (60-second delay)...", "Warning"));

                var result = await _remoteExecutor.ExecuteAsync(
                    host,
                    networkCred,
                    "shutdown /r /t 60 /f",
                    null,
                    TimeSpan.FromSeconds(30),
                    CancellationToken.None);

                if (result.ExitCode == 0 || (result.ExitCode == -1 && result.ErrorOutput.Contains("timed out", StringComparison.OrdinalIgnoreCase)))
                {
                    Interlocked.Increment(ref successCount);
                    device.NeedsReboot = false;
                    device.Status = DeviceStatus.Online;
                    await _deviceRepository.UpdateAsync(device);
                    _ = Application.Current.Dispatcher.BeginInvoke(() =>
                        AddLog(device, "Reboot command sent successfully", "Success"));
                    Log.Information("Reboot command sent to {Hostname}", host);
                }
                else
                {
                    Interlocked.Increment(ref failedCount);
                    _ = Application.Current.Dispatcher.BeginInvoke(() =>
                        AddLog(device, $"Reboot status uncertain (exit code {result.ExitCode}): {result.ErrorOutput}", "Warning"));
                    Log.Warning("Reboot returned unexpected result for {Hostname}: {Error}", host, result.ErrorOutput);
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failedCount);
                _ = Application.Current.Dispatcher.BeginInvoke(() =>
                    AddLog(device, $"Reboot error: {ex.Message}", "Error"));
                Log.Error(ex, "Failed to send reboot to {Hostname}", host);
            }
            finally
            {
                semaphore.Release();
            }
        })).ToList();

        await Task.WhenAll(tasks);
        UpdateRebootPendingCount();
        DeployStatus = $"Reboot commands sent. Success: {successCount} | Failed: {failedCount}";
    }

    [RelayCommand]
    private async Task CancelRebootAsync()
    {
        if (SelectedDevice is null)
        {
            DeployStatus = "No device selected to cancel reboot.";
            return;
        }

        if (SelectedCredential is null)
        {
            DeployStatus = "Please select credentials.";
            return;
        }

        var host = SelectedDevice.Hostname ?? SelectedDevice.IpAddress;
        try
        {
            var networkCred = await _credentialStore.DecryptAsync(SelectedCredential);
            DeployStatus = $"Cancelling reboot on {host}...";
            AddLog(SelectedDevice, "Sending reboot cancellation...", "Info");

            var result = await _remoteExecutor.ExecuteAsync(
                host,
                networkCred,
                "shutdown /a",
                null,
                TimeSpan.FromSeconds(30),
                CancellationToken.None);

            if (result.ExitCode == 0)
            {
                AddLog(SelectedDevice, "Reboot cancelled successfully", "Success");
                DeployStatus = $"Reboot cancelled for {host}.";
                Log.Information("Reboot cancelled for {Hostname}", host);
            }
            else
            {
                AddLog(SelectedDevice, $"Cancel reboot failed (exit code {result.ExitCode}): {result.ErrorOutput}", "Error");
                DeployStatus = $"Cancel reboot failed for {host}: {result.ErrorOutput}";
            }
        }
        catch (Exception ex)
        {
            AddLog(SelectedDevice, $"Cancel reboot error: {ex.Message}", "Error");
            DeployStatus = $"Cancel reboot failed for {host}: {ex.Message}";
            Log.Error(ex, "Failed to cancel reboot for {Hostname}", host);
            SnackbarService.ShowError($"Cancel reboot failed: {ex.Message}");
        }
    }

    private void UpdateRebootPendingCount()
    {
        RebootPendingCount = Devices.Count(d =>
            d.NeedsReboot || d.Status == DeviceStatus.RebootRequired);
    }

    [RelayCommand]
    private void SoftCancelDeploy()
    {
        _softCancelled = true;
        DeployStatus = "Soft cancel: finishing in-flight deployments, no new devices will start...";
        AddLog(new Device { Hostname = "System" }, "Soft cancel requested — in-flight installs will finish, but no new devices will be started.", "Warning");
        SnackbarService.ShowWarning("Soft cancel: finishing in-flight deployments...");
        Log.Warning("Deployment soft-cancelled by user");
    }

    [RelayCommand]
    private void CancelDeploy()
    {
        if (!Helpers.DialogHelper.Confirm(
            "WARNING: Cancelling a deployment while it is running on remote endpoints is HIGHLY DANGEROUS.\n\n" +
            "If a system is currently flashing its BIOS or installing critical firmware, forcibly killing the process may result in a completely dead, unresponsive motherboard (bricking the device).\n\n" +
            "Are you absolutely sure you want to forcibly stop this deployment?", 
            "DANGER: Force Cancel Deployment"))
        {
            return;
        }

        foreach (var s in ActiveSessions.Where(s => s.IsDeploying))
            s.Cts.Cancel();
        DeployStatus = "Cancelling deployment...";

        // Safety: force-reset if tasks don't complete within 10 seconds
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(10000);
                _ = Application.Current?.Dispatcher?.BeginInvoke(() =>
                {
                    if (IsDeploying)
                    {
                        IsDeploying = false;
                        DeployStatus = "Deployment cancelled (forced).";
                        Log.Warning("Deployment cancellation forced after timeout");
                    }
                });
            }
            catch (Exception) { /* App may be closing */ }
        });
    }

    [RelayCommand]
    private void OpenDeploymentLog()
    {
        if (_logWindowRef != null && _logWindowRef.TryGetTarget(out var existingWindow))
        {
            if (existingWindow.WindowState == WindowState.Minimized)
                existingWindow.WindowState = WindowState.Normal;
            existingWindow.Activate();
            return;
        }

        var windowVm = new DeploymentLogWindowViewModel(this);
        var window = new Views.DeploymentLogWindow
        {
            DataContext = windowVm,
            Owner = Application.Current.MainWindow
        };

        _logWindowRef = new WeakReference<Views.DeploymentLogWindow>(window);
        window.Closed += (_, _) => _logWindowRef = null;
        window.Show();
    }

    [RelayCommand]
    private void SelectAllDevices()
    {
        foreach (var d in Devices) d.IsSelected = true;
        DeployStatus = $"Selected all {Devices.Count} device(s).";
    }

    [RelayCommand]
    private void DeselectAllDevices()
    {
        foreach (var d in Devices) d.IsSelected = false;
        DeployStatus = "Cleared device selection.";
    }

    // ── Group Scanning (HPIA Analysis) ──

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ScanGroupAsync()
    {
        if (SelectedGroup is null)
        {
            DeployStatus = "Please select a group first.";
            SnackbarService.ShowWarning("Select a group before scanning");
            return;
        }

        if (GroupDevices.Count == 0)
        {
            DeployStatus = $"Group '{SelectedGroup.Name}' has no devices. Assign devices in the Groups tab first.";
            SnackbarService.ShowWarning("No devices in this group");
            return;
        }

        if (SelectedCredential is null)
        {
            DeployStatus = "Please select credentials before scanning.";
            SnackbarService.ShowWarning("Select credentials first");
            return;
        }

        _softCancelled = false;
        IsScanning = true;
        HasScannedGroup = true;

        var sessionTitle = $"{SelectedGroup.Name} Scan ({DateTime.Now:HH:mm:ss})";
        var session = new MonitorSessionViewModel(sessionTitle)
        {
            GroupId = SelectedGroup.Id,
            IsScanning = true,
            Devices = new ObservableCollection<Device>(GroupDevices),
            TotalToScan = GroupDevices.Count
        };
        
        _ = Application.Current.Dispatcher.BeginInvoke(() =>
        {
            ActiveSessions.Add(session);
            SelectedSession = session;
            OnPropertyChanged(nameof(IsSelectedGroupScanning));
            OpenDeploymentLog(); // Open/focus the monitor window
        });

        var allDevices = GroupDevices.ToList();
        Devices = new ObservableCollection<Device>(allDevices);
        
        session.Status = "Checking device reachability...";
        session.AddLog("System", $"Checking reachability of {allDevices.Count} device(s)...", "Info");

        var reachable = new List<Device>();
        var unreachable = new List<Device>();

        await Task.Run(async () =>
        {
            var pingTasks = allDevices.Select(async device =>
            {
                var target = GetDeviceTarget(device);
                try
                {
                    using var ping = new System.Net.NetworkInformation.Ping();
                    var reply = await ping.SendPingAsync(target, AppSettings.PingTimeoutMs);
                    return (device, online: reply.Status == System.Net.NetworkInformation.IPStatus.Success);
                }
                catch { return (device, online: false); }
            });
            foreach (var (device, online) in await Task.WhenAll(pingTasks))
            {
                if (online) reachable.Add(device);
                else unreachable.Add(device);
            }
        }, session.Cts.Token);

        // Mark unreachable devices as failed immediately
        foreach (var device in unreachable)
        {
            device.Status = DeviceStatus.Failed;
            _ = Application.Current.Dispatcher.BeginInvoke(() =>
                AddScanLog(GetDeviceTarget(device), "Device is offline (ping failed), skipping", "Warning"));
        }

        if (reachable.Count == 0)
        {
            session.Status = $"All {allDevices.Count} device(s) are offline.";
            session.AddLog("System", "No devices responded to ping. Scan aborted.", "Error");
            SnackbarService.ShowError("No devices are reachable");
            
            session.IsScanning = false;
            IsScanning = ActiveSessions.Any(s => s.IsScanning);
            OnPropertyChanged(nameof(IsSelectedGroupScanning));
            return;
        }

        if (unreachable.Count > 0)
            session.AddLog("System", $"{unreachable.Count} device(s) offline, scanning {reachable.Count} reachable device(s)", "Warning");

        session.TotalToScan = reachable.Count;
        StartElapsedTimer(isScan: true);

        // Phase 2: Reset scan results
        session.Status = "Resetting previous scan results...";
        session.AddLog("System", "Clearing previous scan results for this group", "Info");
        await ResetScanResultsAsync(allDevices, session.Cts.Token);

        session.AddLog("System", $"Starting scan of {reachable.Count} reachable device(s) in group '{SelectedGroup.Name}'", "Info");
        SnackbarService.Show($"Scanning {reachable.Count} device(s) ({unreachable.Count} offline)...");

        var networkCred = await _credentialStore.DecryptAsync(SelectedCredential);

        try
        {
            session.Status = "Preparing local HPIA package...";
            try
            {
                await Task.Run(async () => await _hpiaManager.ExtractLocallyAsync(session.Cts.Token), session.Cts.Token);
            }
            catch (FileNotFoundException)
            {
                session.Status = "HPIA installer not found.";
                session.AddLog("System", "HPIA installer not found in application directory.", "Error");
                IsScanning = ActiveSessions.Any(s => s.IsScanning);
                return;
            }

            session.Status = $"Scanning {reachable.Count} device(s) for missing updates...";

            var semaphore = new SemaphoreSlim(AppSettings.DefaultScanConcurrency);
            var timeoutPerDevice = TimeSpan.FromMinutes(AppSettings.AnalysisTimeoutMinutes);
            var tasks = reachable.Select(device => Task.Run(async () =>
            {
                await semaphore.WaitAsync(session.Cts.Token);
                try
                {
                    var target = GetDeviceTarget(device);
                    using var deviceCts = CancellationTokenSource.CreateLinkedTokenSource(session.Cts.Token);
                    deviceCts.CancelAfter(timeoutPerDevice);

                    var recommendations = await ExecuteAgentScanAsync(
                        device,
                        target,
                        networkCred,
                        $"[{session.ScannedCount + 1}/{session.TotalToScan}]",
                        session,
                        deviceCts.Token);

                    _ = Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        session.ScannedCount++;
                        session.Progress = session.TotalToScan > 0 ? (double)session.ScannedCount / session.TotalToScan * 100 : 0;
                        var updateCount = recommendations?.Count ?? 0;
                        session.Status = $"Scanned {session.ScannedCount}/{session.TotalToScan} - {target}: {updateCount} update(s) found";
                        session.AddLog(target, $"Scan complete - {updateCount} update(s) found", updateCount > 0 ? "Warning" : "Success");
                    });

                    Log.Information("Scan complete for {Hostname}: {Count} updates",
                        target, recommendations?.Count ?? 0);
                }
                catch (OperationCanceledException) when (!session.Cts.IsCancellationRequested)
                {
                    var target = GetDeviceTarget(device);
                    device.Status = DeviceStatus.Failed;
                    await _deviceRepository.UpdateAsync(device);
                    Log.Warning("Scan timed out for {Hostname}", target);
                    _ = Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        session.ScannedCount++;
                        session.Progress = session.TotalToScan > 0 ? (double)session.ScannedCount / session.TotalToScan * 100 : 0;
                        session.Status = $"Scanned {session.ScannedCount}/{session.TotalToScan} (timed out: {target})";
                        session.AddLog(target, "Scan timed out", "Error");
                    });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var target = GetDeviceTarget(device);
                    device.Status = DeviceStatus.Failed;
                    await _deviceRepository.UpdateAsync(device);
                    Log.Error(ex, "Scan failed for {Hostname}", target);
                    _ = Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        session.ScannedCount++;
                        session.Progress = session.TotalToScan > 0 ? (double)session.ScannedCount / session.TotalToScan * 100 : 0;
                        session.Status = $"Scanned {session.ScannedCount}/{session.TotalToScan} (failed: {target})";
                        session.AddLog(target, $"Scan failed: {ex.Message}", "Error");
                    });
                }
                finally
                {
                    semaphore.Release();
                }
            }, session.Cts.Token)).ToList();

            await Task.WhenAll(tasks);

            HasScannedGroup = true;
            await LoadScannedDevicesAsync();

            // Persist scan results to deployment history for audit trail
            try
            {
                var scanHistoryEntries = new List<DeploymentHistory>();
                foreach (var device in Devices)
                {
                    var recs = device.Recommendations ?? [];
                    if (recs.Count > 0)
                    {
                        scanHistoryEntries.AddRange(recs.Select(r => new DeploymentHistory
                        {
                            DeviceId = device.Id,
                            DeviceHostname = device.Hostname ?? "",
                            DeviceIpAddress = device.IpAddress ?? "",
                            UpdateName = r.Name ?? "",
                            SoftPaqId = r.SoftPaqId ?? "",
                            Category = r.Category ?? "",
                            Action = "Scanned",
                            Timestamp = DateTime.Now
                        }));
                    }
                    else
                    {
                        scanHistoryEntries.Add(new DeploymentHistory
                        {
                            DeviceId = device.Id,
                            DeviceHostname = device.Hostname ?? "",
                            DeviceIpAddress = device.IpAddress ?? "",
                            UpdateName = "Up to date",
                            Action = "Scanned",
                            Timestamp = DateTime.Now
                        });
                    }
                }
                if (scanHistoryEntries.Count > 0)
                    await _historyRepository.AddRangeAsync(scanHistoryEntries);
            }
            catch (Exception histEx)
            {
                Log.Warning(histEx, "Failed to persist scan results to history");
            }

            var totalUpdates = Devices.Sum(d => d.Recommendations?.Count ?? 0);
            var summaryParts = new List<string> { $"{totalUpdates} updates found across {reachable.Count} devices" };
            if (unreachable.Count > 0) summaryParts.Add($"{unreachable.Count} offline");
            var summary = string.Join(", ", summaryParts);
            AddScanLog("System", $"Scan complete. {summary}.", "Success");
            ScanStatus = $"Scan complete. {summary}.";
            DeployStatus = totalUpdates > 0
                ? $"{Devices.Count} scanned device(s), {totalUpdates} update(s) ready to deploy."
                : $"All {Devices.Count} scanned device(s) in '{SelectedGroup.Name}' are up to date. No updates needed.";

            // Email notification for critical updates
            var criticalDevices = Devices
                .Where(d => d.Recommendations?.Any(r =>
                    r.Severity?.Equals("Critical", StringComparison.OrdinalIgnoreCase) == true) == true)
                .Select(d => d.Hostname ?? d.IpAddress)
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();
            if (criticalDevices.Count > 0)
                _ = Task.Run(async () =>
                {
                    try { await _emailService.SendCriticalUpdatesFoundAsync(criticalDevices.Count, criticalDevices!); }
                    catch (Exception ex) { Log.Warning(ex, "Failed to send critical updates email notification"); }
                });
        }
        catch (OperationCanceledException)
        {
            ScanStatus = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            ScanStatus = $"Scan error: {ex.Message}";
            DeployStatus = $"Scan failed: {ex.Message}";
            SnackbarService.ShowError($"Scan failed: {ex.Message}");
            Log.Error(ex, "Group scan failed");
        }
        finally
        {
            StopElapsedTimer(isScan: true);
            session.IsScanning = false;
            IsScanning = ActiveSessions.Any(s => s.IsScanning);
            OnPropertyChanged(nameof(IsSelectedGroupScanning));
        }
    }

    private async Task<IReadOnlyList<HpiaRecommendation>> ExecuteAgentScanAsync(
        Device device,
        string target,
        NetworkCredential networkCred,
        string statusPrefix,
        MonitorSessionViewModel session,
        CancellationToken ct)
    {
        _ = Application.Current.Dispatcher.BeginInvoke(() =>
        {
            device.Status = DeviceStatus.Analyzing;
            session.Status = $"{statusPrefix} Preparing agent on {target}...";
            session.AddLog(target, "Preparing HPPAQDeploy agent and local HPIA package...", "Info");
        });

        await _agentBootstrapper.BootstrapAsync(target, networkCred, ct);

        _ = Application.Current.Dispatcher.BeginInvoke(() =>
        {
            session.Status = $"{statusPrefix} Waiting for agent results from {target}...";
            session.AddLog(target, "Agent job active, waiting for HPIA result...", "Info");
        });

        var jobId = await _agentClient.SubmitScanAsync(target, new AgentJob
        {
            Type = AgentJobType.Scan,
            UseOfflineRepository = AppSettings.UseOfflineRepository,
            OfflineRepositoryPath = !string.IsNullOrWhiteSpace(AppSettings.RepositorySharePath)
                ? AppSettings.RepositorySharePath
                : Path.Combine(AppSettings.RemoteTempPath, "Repository")
        }, ct);
        await _agentBootstrapper.RunOnceAsync(target, networkCred, ct);

        var pollProgress = new Progress<string>(msg =>
            _ = Application.Current.Dispatcher.BeginInvoke(() => ScanStatus = $"{statusPrefix} {msg}"));

        var result = await WaitForAgentResultAsync(
            device,
            target,
            jobId,
            TimeSpan.FromMinutes(AppSettings.AnalysisTimeoutMinutes),
            ct,
            pollProgress);

        if (result.Status != AgentJobStatus.Succeeded)
        {
            var message = string.IsNullOrWhiteSpace(result.Message)
                ? "Endpoint agent scan failed without a message."
                : result.Message;
            throw new InvalidOperationException(message);
        }

        var recommendations = result.Recommendations.ToList();
        foreach (var recommendation in recommendations)
        {
            recommendation.Id = 0;
            recommendation.DeviceId = device.Id;
            recommendation.DeviceHostname = device.Hostname;
        }

        device.LastAnalyzed = DateTime.Now;
        device.NeedsReboot = false;
        device.Recommendations = recommendations;
        device.Status = recommendations.Count > 0 ? DeviceStatus.ReadyToDeploy : DeviceStatus.Online;
        await _deviceRepository.UpdateAsync(device, ct);

        return recommendations;
    }

    private async Task<AgentJobResult> WaitForAgentResultAsync(
        Device device,
        string target,
        string jobId,
        TimeSpan timeout,
        CancellationToken ct,
        IProgress<string>? progress = null)
    {
        var started = DateTimeOffset.UtcNow;
        var nextProgress = TimeSpan.Zero;
        var retriedAgentTrigger = false;

        while (DateTimeOffset.UtcNow - started < timeout)
        {
            ct.ThrowIfCancellationRequested();

            var result = await _agentClient.TryGetResultAsync(target, jobId, ct);
            if (result is not null)
                return result;

            var elapsed = DateTimeOffset.UtcNow - started;
            if (elapsed >= nextProgress)
            {
                var state = await _agentClient.GetJobStateAsync(target, jobId, ct);

                // If job is stuck queued after 45s, retry the agent trigger once
                if (!retriedAgentTrigger &&
                    elapsed > TimeSpan.FromSeconds(45) &&
                    (state.Equals("queued", StringComparison.OrdinalIgnoreCase) ||
                     state.Equals("job file not found", StringComparison.OrdinalIgnoreCase)))
                {
                    retriedAgentTrigger = true;
                    progress?.Report($"Agent on {target} not responding, retrying trigger...");
                    Log.Warning("Retrying agent trigger on {Hostname} — job {JobId} is still {State}", target, jobId, state);
                    try
                    {
                        var networkCred = await _credentialStore.DecryptAsync(SelectedCredential!);
                        await _agentBootstrapper.RunOnceAsync(target, networkCred, ct);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Agent re-trigger failed on {Hostname}", target);
                    }
                }

                // After retry + extra wait, give up
                if (elapsed > TimeSpan.FromSeconds(120) && retriedAgentTrigger &&
                    (state.Equals("queued", StringComparison.OrdinalIgnoreCase) ||
                     state.Equals("job file not found", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new TimeoutException(
                        $"Endpoint agent is not processing scan jobs on {target}. Job state is '{state}'. " +
                        "Confirm the HPPAQDeployAgent scheduled task exists and can run as SYSTEM on the endpoint.");
                }

                progress?.Report($"Endpoint scan is {state} on {target} ({elapsed.TotalSeconds:N0}s elapsed)...");
                nextProgress = elapsed + TimeSpan.FromSeconds(15);
            }

            // Update simulated progress for Scanning/Analyzing (average scan ~90s)
            if (device.Status == DeviceStatus.Scanning || device.Status == DeviceStatus.Analyzing)
            {
                elapsed = DateTimeOffset.UtcNow - started;
                _ = Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    device.Progress = Math.Min(95, (elapsed.TotalSeconds / 90.0) * 100);
                });
            }

            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }

        throw new TimeoutException($"Timed out waiting for endpoint scan result from {target} after {timeout.TotalMinutes:N0} minute(s).");
    }

    private async Task ResetScanResultsAsync(IReadOnlyList<Device> devices, CancellationToken ct)
    {
        // Reset in-memory state for all devices first
        foreach (var device in devices)
        {
            ct.ThrowIfCancellationRequested();
            device.Recommendations = new List<HpiaRecommendation>();
            device.LastAnalyzed = null;
            device.NeedsReboot = false;
            if (device.Status != DeviceStatus.Failed) // Preserve failed status from reachability check
                device.Status = DeviceStatus.Discovered;
        }

        // Batch DB updates in parallel (semaphore to limit DB contention)
        var dbSemaphore = new SemaphoreSlim(10);
        var updateTasks = devices.Select(async device =>
        {
            await dbSemaphore.WaitAsync(ct);
            try { await _deviceRepository.UpdateAsync(device, ct); }
            finally { dbSemaphore.Release(); }
        });
        await Task.WhenAll(updateTasks);

        TotalPendingUpdates = 0;
        SelectedUpdateCount = 0;
        UpdateRebootPendingCount();
    }

    [RelayCommand]
    private async Task CleanupRemoteFilesAsync()
    {
        if (SelectedGroup is null || GroupDevices.Count == 0)
        {
            DeployStatus = "Select a group with devices first.";
            return;
        }
        if (SelectedCredential is null)
        {
            DeployStatus = "Select credentials first.";
            return;
        }

        if (!Helpers.DialogHelper.Confirm(
            $"Remove HPIA temporary files (C:\\Temp\\HPIA) from {GroupDevices.Count} device(s)?",
            "Cleanup Remote Files"))
            return;

        var networkCred = await _credentialStore.DecryptAsync(SelectedCredential);
        int cleaned = 0, failed = 0;
        DeployStatus = "Cleaning up remote HPIA files...";

        var tasks = GroupDevices.Select(device => Task.Run(async () =>
        {
            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await _hpiaManager.CleanupRemoteAsync(
                    device.Hostname ?? device.IpAddress,
                    networkCred, timeoutCts.Token);
                Interlocked.Increment(ref cleaned);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Cleanup failed for device {Hostname}", device.Hostname ?? device.IpAddress);
                Interlocked.Increment(ref failed);
            }
        })).ToList();

        await Task.WhenAll(tasks);

        DeployStatus = $"Cleanup done. {cleaned} cleaned, {failed} failed.";
        SnackbarService.ShowSuccess($"Cleanup: {cleaned} devices cleaned");
    }

    [RelayCommand]
    private void CancelScan()
    {
        // Regular scans use each session's own token; retry-scans use the shared _scanCts.
        foreach (var s in ActiveSessions.Where(s => s.IsScanning))
            s.Cts.Cancel();
        _scanCts?.Cancel();
        ScanStatus = "Cancelling scan...";
    }

    // ── Retry Failed Scans ──
    [RelayCommand]
    private async Task RetryFailedScanAsync()
    {
        if (SelectedGroup is null)
        {
            DeployStatus = "Please select a group first.";
            return;
        }

        if (SelectedCredential is null)
        {
            DeployStatus = "Please select credentials before scanning.";
            return;
        }

        var failedDevices = Devices.Where(d => d.Status == DeviceStatus.Failed).ToList();
        if (failedDevices.Count == 0)
        {
            // Also check GroupDevices for devices that failed during initial scan
            failedDevices = GroupDevices.Where(d => d.Status == DeviceStatus.Failed).ToList();
        }

        if (failedDevices.Count == 0)
        {
            DeployStatus = "No failed devices to retry.";
            SnackbarService.Show("No failed devices to retry");
            return;
        }

        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        var sessionTitle = $"Retry: {SelectedGroup.Name} ({DateTime.Now:HH:mm:ss})";
        var session = new MonitorSessionViewModel(sessionTitle)
        {
            IsScanning = true,
            Devices = new ObservableCollection<Device>(failedDevices)
        };

        await Application.Current.Dispatcher.BeginInvoke(() => 
        {
            ActiveSessions.Add(session);
            SelectedSession = session;
            OpenDeploymentLog();
        });

        session.AddLog("System", $"Retrying scan for {failedDevices.Count} failed device(s)", "Info");
        SnackbarService.ShowWarning($"Retrying {failedDevices.Count} failed devices...");

        var networkCred = await _credentialStore.DecryptAsync(SelectedCredential);

        try
        {
            session.Status = "Preparing local HPIA package for endpoint agents...";
            await Task.Run(async () => await _hpiaManager.ExtractLocallyAsync(_scanCts.Token), _scanCts.Token);

            session.Status = $"Retrying {failedDevices.Count} failed device(s)...";
            var semaphore = new SemaphoreSlim(AppSettings.DefaultScanConcurrency);
            var timeoutPerDevice = TimeSpan.FromMinutes(AppSettings.AnalysisTimeoutMinutes);

            var tasks = failedDevices.Select(device => Task.Run(async () =>
            {
                await semaphore.WaitAsync(_scanCts.Token);
                try
                {
                    var target = GetDeviceTarget(device);
                    using var deviceCts = CancellationTokenSource.CreateLinkedTokenSource(_scanCts.Token);
                    deviceCts.CancelAfter(timeoutPerDevice);

                    var recommendations = await ExecuteAgentScanAsync(
                        device,
                        target,
                        networkCred,
                        $"[Retry {ScannedCount + 1}/{TotalToScan}]",
                        session,
                        deviceCts.Token);

                    _ = Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        ScannedCount++;
                        ScanProgress = TotalToScan > 0 ? (double)ScannedCount / TotalToScan * 100 : 0;
                        session.AddLog(target, $"Retry successful - {recommendations?.Count ?? 0} update(s) found", "Success");
                    });

                    Log.Information("Retry scan succeeded for {Hostname}: {Count} updates", target, recommendations?.Count ?? 0);
                }
                catch (OperationCanceledException) when (!_scanCts.IsCancellationRequested)
                {
                    var target = GetDeviceTarget(device);
                    device.Status = DeviceStatus.Failed;
                    await _deviceRepository.UpdateAsync(device);
                    _ = Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        ScannedCount++;
                        ScanProgress = TotalToScan > 0 ? (double)ScannedCount / TotalToScan * 100 : 0;
                        session.AddLog(target, "Retry timed out", "Error");
                    });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var target = GetDeviceTarget(device);
                    device.Status = DeviceStatus.Failed;
                    await _deviceRepository.UpdateAsync(device);
                    _ = Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        ScannedCount++;
                        ScanProgress = TotalToScan > 0 ? (double)ScannedCount / TotalToScan * 100 : 0;
                        session.AddLog(target, $"Retry failed: {ex.Message}", "Error");
                    });
                    Log.Error(ex, "Retry scan failed for {Hostname}", target);
                }
                finally
                {
                    semaphore.Release();
                }
            }, _scanCts.Token)).ToList();

            await Task.WhenAll(tasks);

            HasScannedGroup = true;
            await LoadScannedDevicesAsync();

            var stillFailed = failedDevices.Count(d => d.Status == DeviceStatus.Failed);
            var recovered = failedDevices.Count - stillFailed;
            session.AddLog("System", $"Retry complete. {recovered} recovered, {stillFailed} still failed.", recovered > 0 ? "Success" : "Warning");
            session.Status = $"Retry complete. {recovered} recovered, {stillFailed} still failed.";
            SnackbarService.Show($"Retry: {recovered} recovered, {stillFailed} still failed");
        }
        catch (OperationCanceledException)
        {
            session.Status = "Retry scan cancelled.";
        }
        catch (Exception ex)
        {
            session.Status = $"Retry error: {ex.Message}";
            Log.Error(ex, "Retry scan failed");
            SnackbarService.ShowError($"Retry scan failed: {ex.Message}");
        }
        finally
        {
            StopElapsedTimer(isScan: true);
            IsScanning = false;
            _scanCts?.Dispose();
            _scanCts = null;
        }
    }

    private static (string os, string osVer) DetectOsVersion(List<Device> devices)
    {
        foreach (var ov in devices.Select(d => d.OsVersion).Where(o => !string.IsNullOrWhiteSpace(o)))
        {
            if (ov.Contains("Windows 11", StringComparison.OrdinalIgnoreCase)) return ("Win11", "24H2");
            if (ov.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
            {
                if (ov.Contains("22H2")) return ("Win10", "22H2");
                if (ov.Contains("21H2")) return ("Win10", "21H2");
                return ("Win10", "22H2");
            }
        }
        return ("Win10", "22H2");
    }

    private static string GetDeviceTarget(Device device)
    {
        if (!string.IsNullOrWhiteSpace(device.Hostname))
            return device.Hostname.Trim();

        if (!string.IsNullOrWhiteSpace(device.IpAddress))
            return device.IpAddress.Trim();

        throw new InvalidOperationException("Device does not have a hostname or IP address.");
    }

    [RelayCommand]
    private async Task ExportResultsAsync()
    {
        if (DeploymentLog.Count == 0)
        {
            DeployStatus = "No deployment results to export.";
            return;
        }

        var path = Helpers.DialogHelper.SaveFileDialog($"deployment-results-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        if (path is null) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Timestamp,Device,Message,Level");
            foreach (var entry in DeploymentLog)
                sb.AppendLine($"\"{entry.Timestamp:yyyy-MM-dd HH:mm:ss}\",\"{entry.DeviceName}\",\"{entry.Message.Replace("\"", "\"\"")}\",\"{entry.Level}\"");
            await File.WriteAllTextAsync(path, sb.ToString());
            DeployStatus = $"Results exported to {path}";
            SnackbarService.ShowSuccess("Deployment log exported");
            Log.Information("Deployment results exported to {Path}", path);
        }
        catch (Exception ex)
        {
            DeployStatus = $"Export failed: {ex.Message}";
            SnackbarService.ShowError($"Export failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ExportScanLogAsync()
    {
        if (ScanLog.Count == 0) { DeployStatus = "No scan log to export."; return; }
        var path = Helpers.DialogHelper.SaveFileDialog($"scan-log-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        if (path is null) return;
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Timestamp,Device,Message,Level");
            foreach (var e in ScanLog)
                sb.AppendLine($"\"{e.Timestamp:yyyy-MM-dd HH:mm:ss}\",\"{e.DeviceName}\",\"{e.Message.Replace("\"", "\"\"")}\",\"{e.Level}\"");
            await File.WriteAllTextAsync(path, sb.ToString());
            DeployStatus = $"Scan log exported to {path}";
            SnackbarService.ShowSuccess("Scan log exported");
        }
        catch (Exception ex)
        {
            DeployStatus = $"Export failed: {ex.Message}";
            SnackbarService.ShowError($"Export failed: {ex.Message}");
        }
    }
    private void AddScanLog(string deviceName, string message, string level)
    {
        if (Application.Current?.Dispatcher == null) return;

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            while (ScanLog.Count >= MaxLogEntries)
                ScanLog.RemoveAt(ScanLog.Count - 1);

        var entry = new DeploymentLogEntry
        {
            Timestamp = DateTime.Now,
            DeviceName = deviceName,
            Message = message,
            Level = level
        };

            ScanLog.Insert(0, entry);
            AddLogEntryInternal(entry);
        });
    }

    private void AddLog(Device device, string message, string level)
    {
        if (Application.Current?.Dispatcher == null) return;

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            AddLogEntryInternal(new DeploymentLogEntry
            {
                Timestamp = DateTime.Now,
                DeviceName = device.Hostname ?? device.IpAddress,
                Message = message,
                Level = level
            });
        });
    }

    private void AddLogEntry(DeploymentLogEntry entry)
    {
        if (Application.Current?.Dispatcher == null) return;
        Application.Current.Dispatcher.BeginInvoke(() => AddLogEntryInternal(entry));
    }

    private void AddLogEntryInternal(DeploymentLogEntry entry)
    {
        // Insert(0) is O(n) on ObservableCollection, but necessary to display newest-first
        // without XAML-level sorting. At MaxLogEntries=1000 the cost is acceptable.
        while (DeploymentLog.Count >= MaxLogEntries)
        {
            DeploymentLog.RemoveAt(DeploymentLog.Count - 1);
        }

        DeploymentLog.Insert(0, entry);

        // Also update filtered collection if it matches current filter
        if (MatchesFilter(entry))
        {
            while (FilteredDeploymentLog.Count >= MaxLogEntries)
            {
                FilteredDeploymentLog.RemoveAt(FilteredDeploymentLog.Count - 1);
            }
            FilteredDeploymentLog.Insert(0, entry);
        }
    }

    private bool MatchesFilter(DeploymentLogEntry entry)
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
}
