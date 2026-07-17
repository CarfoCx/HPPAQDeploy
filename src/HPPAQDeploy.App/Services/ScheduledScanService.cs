using System.ComponentModel;
using System.Collections.Concurrent;
using System.Net;
using HPPAQDeploy.Core.Interfaces;
using HPPAQDeploy.Core.Models;
using HPPAQDeploy.Shared.Configuration;
using Serilog;

namespace HPPAQDeploy.App.Services;

public class ScheduledScanService : INotifyPropertyChanged, IDisposable
{
    private readonly INetworkScanner _networkScanner;
    private readonly IDeviceDiscovery _deviceDiscovery;
    private readonly ICredentialStore _credentialStore;
    private readonly IDeviceRepository _deviceRepository;
    private readonly SynchronizationContext? _synchronizationContext;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly object _lifecycleLock = new();
    private Timer? _timer;
    private CancellationTokenSource? _scheduleCts;
    private DateTime _scheduleBaseline = DateTime.Now;
    private bool _disposed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private DateTime? _lastScanTime;
    public DateTime? LastScanTime
    {
        get => _lastScanTime;
        private set
        {
            _lastScanTime = value;
            OnPropertyChanged(nameof(LastScanTime));
            OnPropertyChanged(nameof(NextScanTime));
            OnPropertyChanged(nameof(ScheduleStatusText));
        }
    }

    public DateTime? NextScanTime
    {
        get
        {
            if (!AppSettings.ScheduledScanEnabled)
                return null;

            if (LastScanTime.HasValue)
                return LastScanTime.Value + AppSettings.ScheduledScanInterval;

            return _scheduleBaseline + AppSettings.ScheduledScanInterval;
        }
    }

    public string ScheduleStatusText
    {
        get
        {
            if (!AppSettings.ScheduledScanEnabled)
                return "Scheduled scans: Disabled";

            if (NextScanTime.HasValue)
                return $"Next scheduled scan: {NextScanTime.Value:g}";

            return "Scheduled scans: Enabled";
        }
    }

    public ScheduledScanService(
        INetworkScanner networkScanner,
        IDeviceDiscovery deviceDiscovery,
        ICredentialStore credentialStore,
        IDeviceRepository deviceRepository)
    {
        _networkScanner = networkScanner;
        _deviceDiscovery = deviceDiscovery;
        _credentialStore = credentialStore;
        _deviceRepository = deviceRepository;
        _synchronizationContext = SynchronizationContext.Current;
        _lastScanTime = AppSettings.LastScheduledScan;
    }

    public void Start()
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _timer?.Dispose();
            _scheduleCts?.Cancel();
            _scheduleCts?.Dispose();
            _scheduleCts = new CancellationTokenSource();

            // Check every 60 seconds if a scan is due.
            _timer = new Timer(
                OnTimerElapsed,
                _scheduleCts.Token,
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(60));
        }

        Log.Information("Scheduled scan service started. Enabled={Enabled}, Interval={Interval}h, CIDR={Cidr}",
            AppSettings.ScheduledScanEnabled, AppSettings.ScheduledScanInterval.TotalHours, AppSettings.ScheduledScanCidr);
    }

    public void Restart()
    {
        _lastScanTime = AppSettings.LastScheduledScan;
        _scheduleBaseline = DateTime.Now;
        OnPropertyChanged(nameof(LastScanTime));
        OnPropertyChanged(nameof(NextScanTime));
        OnPropertyChanged(nameof(ScheduleStatusText));
        Start();
    }

    private void OnTimerElapsed(object? state)
    {
        if (state is not CancellationToken scheduleToken || scheduleToken.IsCancellationRequested)
            return;

        _ = CheckAndRunScanAsync(scheduleToken);
    }

    private async Task CheckAndRunScanAsync(CancellationToken scheduleToken)
    {
        if (!AppSettings.ScheduledScanEnabled || string.IsNullOrWhiteSpace(AppSettings.ScheduledScanCidr))
            return;

        var now = DateTime.Now;
        var nextScan = NextScanTime;
        if (nextScan.HasValue && now < nextScan.Value)
            return;

        if (!await _runGate.WaitAsync(0).ConfigureAwait(false))
            return;

        try
        {
            Log.Information("Scheduled scan starting for CIDR {Cidr}", AppSettings.ScheduledScanCidr);
            await RunScanAsync(scheduleToken).ConfigureAwait(false);
            LastScanTime = DateTime.Now;
            AppSettings.LastScheduledScan = LastScanTime;
            AppSettings.Save();
            Log.Information("Scheduled scan completed at {Time}", LastScanTime);
        }
        catch (OperationCanceledException) when (scheduleToken.IsCancellationRequested)
        {
            Log.Information("Scheduled scan cancelled");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Scheduled scan failed");
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async Task RunScanAsync(CancellationToken scheduleToken)
    {
        var cidr = new CidrRange(AppSettings.ScheduledScanCidr);
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(scheduleToken, timeoutCts.Token);
        var progress = new Progress<(int completed, int total)>();

        // Phase 1: Ping sweep
        var aliveHosts = new List<string>();
        await foreach (var result in _networkScanner.PingSweepAsync(
            cidr, AppSettings.DefaultPingConcurrency, progress, cts.Token))
        {
            if (result.IsAlive)
                aliveHosts.Add(result.IpAddress);
        }

        Log.Information("Scheduled scan ping sweep found {Count} alive hosts", aliveHosts.Count);

        if (aliveHosts.Count == 0)
            return;

        // Get default credential
        var credentials = await _credentialStore.GetAllAsync(cts.Token);
        var defaultCred = credentials.FirstOrDefault(c => c.IsDefault) ?? credentials.FirstOrDefault();
        if (defaultCred == null)
        {
            Log.Warning("Scheduled scan aborted: no credentials configured");
            return;
        }

        var networkCred = await _credentialStore.DecryptAsync(defaultCred);

        // Phase 2: WMI discovery
        using var discoverySemaphore = new SemaphoreSlim(AppSettings.DefaultWmiConcurrency);
        var discoveredDevices = new ConcurrentBag<Device>();
        var tasks = aliveHosts.Select(async ip =>
        {
            await discoverySemaphore.WaitAsync(cts.Token).ConfigureAwait(false);
            try
            {
                var device = await _deviceDiscovery.IdentifyDeviceAsync(ip, networkCred, cts.Token).ConfigureAwait(false);
                if (device != null)
                {
                    device.LastScanned = DateTime.Now;
                    discoveredDevices.Add(device);
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Scheduled scan WMI discovery failed for {Ip}", ip);
            }
            finally
            {
                discoverySemaphore.Release();
            }
        });

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!discoveredDevices.IsEmpty)
                await _deviceRepository.BatchUpsertAsync(discoveredDevices, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        if (!discoveredDevices.IsEmpty)
            await _deviceRepository.BatchUpsertAsync(discoveredDevices, cts.Token).ConfigureAwait(false);
    }

    private void OnPropertyChanged(string propertyName)
    {
        var args = new PropertyChangedEventArgs(propertyName);
        if (_synchronizationContext is not null && SynchronizationContext.Current != _synchronizationContext)
        {
            _synchronizationContext.Post(_ => PropertyChanged?.Invoke(this, args), null);
            return;
        }

        PropertyChanged?.Invoke(this, args);
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            _scheduleCts?.Cancel();
            _scheduleCts?.Dispose();
            _scheduleCts = null;
        }
    }
}
