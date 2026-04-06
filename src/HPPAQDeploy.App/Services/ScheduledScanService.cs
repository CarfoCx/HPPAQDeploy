using System.ComponentModel;
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
    private Timer? _timer;
    private bool _isRunning;
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

            // If never scanned, next scan is now + interval from app start
            return DateTime.Now + AppSettings.ScheduledScanInterval;
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
        _lastScanTime = AppSettings.LastScheduledScan;
    }

    public void Start()
    {
        _timer?.Dispose();
        // Check every 60 seconds if a scan is due
        _timer = new Timer(OnTimerElapsed, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60));
        Log.Information("Scheduled scan service started. Enabled={Enabled}, Interval={Interval}h, CIDR={Cidr}",
            AppSettings.ScheduledScanEnabled, AppSettings.ScheduledScanInterval.TotalHours, AppSettings.ScheduledScanCidr);
    }

    public void Restart()
    {
        _lastScanTime = AppSettings.LastScheduledScan;
        OnPropertyChanged(nameof(LastScanTime));
        OnPropertyChanged(nameof(NextScanTime));
        OnPropertyChanged(nameof(ScheduleStatusText));
        Start();
    }

    private async void OnTimerElapsed(object? state)
    {
        if (_isRunning || !AppSettings.ScheduledScanEnabled)
            return;

        if (string.IsNullOrWhiteSpace(AppSettings.ScheduledScanCidr))
            return;

        var now = DateTime.Now;
        if (LastScanTime.HasValue && (now - LastScanTime.Value) < AppSettings.ScheduledScanInterval)
            return;

        // A scan is due
        _isRunning = true;
        try
        {
            Log.Information("Scheduled scan starting for CIDR {Cidr}", AppSettings.ScheduledScanCidr);
            await RunScanAsync();
            LastScanTime = DateTime.Now;
            AppSettings.LastScheduledScan = LastScanTime;
            AppSettings.Save();
            Log.Information("Scheduled scan completed at {Time}", LastScanTime);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Scheduled scan failed");
        }
        finally
        {
            _isRunning = false;
        }
    }

    private async Task RunScanAsync()
    {
        var cidr = new CidrRange(AppSettings.ScheduledScanCidr);
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
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
        var semaphore = new SemaphoreSlim(AppSettings.DefaultWmiConcurrency);
        var tasks = aliveHosts.Select(async ip =>
        {
            await semaphore.WaitAsync(cts.Token);
            try
            {
                var device = await _deviceDiscovery.IdentifyDeviceAsync(ip, networkCred, cts.Token);
                if (device != null)
                {
                    device.LastScanned = DateTime.Now;
                    await _deviceRepository.UpsertAsync(device, cts.Token);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Scheduled scan WMI discovery failed for {Ip}", ip);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
    }
}
