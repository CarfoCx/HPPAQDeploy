using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HPPAQDeploy.App.Services;
using HPPAQDeploy.Core.Interfaces;
using HPPAQDeploy.Core.Models;
using HPPAQDeploy.Infrastructure.Hpia;
using HPPAQDeploy.Shared.Configuration;
using HPPAQDeploy.Shared.Helpers;
using Serilog;

namespace HPPAQDeploy.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ScheduledScanService _scheduledScanService;
    private readonly IEmailService _emailService;
    private readonly RepositorySyncer _repoSyncer;
    private readonly IDeviceRepository _deviceRepository;
    private readonly SemaphoreSlim _repoStatusGate = new(1, 1);

    public CredentialManagerViewModel CredentialManager { get; }

    [ObservableProperty]
    private int _pingConcurrency = AppSettings.DefaultPingConcurrency;

    [ObservableProperty]
    private int _wmiConcurrency = AppSettings.DefaultWmiConcurrency;

    [ObservableProperty]
    private int _scanConcurrency = AppSettings.DefaultScanConcurrency;

    [ObservableProperty]
    private int _deployConcurrency = AppSettings.DefaultDeployConcurrency;

    [ObservableProperty]
    private int _pingTimeoutMs = AppSettings.PingTimeoutMs;

    [ObservableProperty]
    private int _wmiTimeoutSeconds = AppSettings.WmiTimeoutSeconds;

    [ObservableProperty]
    private int _analysisTimeoutMinutes = AppSettings.AnalysisTimeoutMinutes;

    [ObservableProperty]
    private int _deployTimeoutMinutes = AppSettings.DeployTimeoutMinutes;

    [ObservableProperty]
    private int _fileTransferTimeoutMinutes = AppSettings.FileTransferTimeoutMinutes;

    [ObservableProperty]
    private int _retryMaxAttempts = AppSettings.RetryMaxAttempts;

    [ObservableProperty]
    private int _retryBaseDelayMs = AppSettings.RetryBaseDelayMs;

    [ObservableProperty]
    private string _hpiaPath = AppSettings.HpiaExePath;

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private string _repositoryPath = AppSettings.RepositoryPath;

    [ObservableProperty]
    private string _repositorySharePath = AppSettings.RepositorySharePath;

    [ObservableProperty]
    private bool _useOfflineRepository = AppSettings.UseOfflineRepository;

    public string DatabasePath => AppSettings.DatabasePath;

    // Scheduled scan properties
    [ObservableProperty]
    private bool _scheduledScanEnabled = AppSettings.ScheduledScanEnabled;

    [ObservableProperty]
    private string _scheduledScanCidr = AppSettings.ScheduledScanCidr;

    [ObservableProperty]
    private string _selectedInterval = HoursToIntervalLabel(AppSettings.ScheduledScanInterval.TotalHours);

    [ObservableProperty]
    private string _lastScheduledScanText = FormatLastScan(AppSettings.LastScheduledScan);

    [ObservableProperty]
    private string _nextScheduledScanText = "";

    // Email notification properties
    [ObservableProperty]
    private bool _emailNotificationsEnabled = AppSettings.EmailNotificationsEnabled;

    [ObservableProperty]
    private string _smtpServer = AppSettings.SmtpServer;

    [ObservableProperty]
    private int _smtpPort = AppSettings.SmtpPort;

    [ObservableProperty]
    private bool _smtpUseSsl = AppSettings.SmtpUseSsl;

    [ObservableProperty]
    private string _smtpUsername = AppSettings.SmtpUsername;

    [ObservableProperty]
    private string _smtpPassword = AppSettings.SmtpPassword;

    [ObservableProperty]
    private string _emailFrom = AppSettings.EmailFrom;

    [ObservableProperty]
    private string _emailTo = AppSettings.EmailTo;

    [ObservableProperty]
    private bool _notifyOnScanComplete = AppSettings.NotifyOnScanComplete;

    [ObservableProperty]
    private bool _notifyOnCriticalUpdates = AppSettings.NotifyOnCriticalUpdates;

    [ObservableProperty]
    private bool _notifyOnDeployComplete = AppSettings.NotifyOnDeployComplete;

    [ObservableProperty]
    private bool _notifyOnDeployFailure = AppSettings.NotifyOnDeployFailure;

    [ObservableProperty]
    private bool _isTestingEmail;

    [ObservableProperty]
    private string _testEmailStatus = "";

    // BIOS Passwords
    [ObservableProperty]
    private ObservableCollection<string> _biosPasswords = new(AppSettings.BiosPasswords.Select(MaskPassword));

    [ObservableProperty]
    private string _newBiosPassword = "";

    // Backing store for actual passwords (unmasked)
    private readonly List<string> _biosPasswordsRaw = new(AppSettings.BiosPasswords);

    // Repository sync
    [ObservableProperty]
    private bool _isSyncingRepo;

    [ObservableProperty]
    private string _repoStatus = "";

    [ObservableProperty]
    private string _repoSyncStatus = "";

    public ObservableCollection<string> IntervalOptions { get; } = new()
    {
        "Every 6 hours",
        "Every 12 hours",
        "Every 24 hours",
        "Every 48 hours",
        "Weekly"
    };

    public SettingsViewModel(ScheduledScanService scheduledScanService, IEmailService emailService,
        CredentialManagerViewModel credentialManager, RepositorySyncer repoSyncer, IDeviceRepository deviceRepository)
    {
        _scheduledScanService = scheduledScanService;
        _emailService = emailService;
        _repoSyncer = repoSyncer;
        _deviceRepository = deviceRepository;
        CredentialManager = credentialManager;
        UpdateNextScanText();
        AsyncInitHelper.SafeFireAndForget(UpdateRepoStatusAsync, nameof(SettingsViewModel));

        _scheduledScanService.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ScheduledScanService.LastScanTime))
            {
                LastScheduledScanText = FormatLastScan(_scheduledScanService.LastScanTime);
            }
            if (args.PropertyName == nameof(ScheduledScanService.NextScanTime))
            {
                UpdateNextScanText();
            }
        };
    }

    private void UpdateNextScanText()
    {
        var next = _scheduledScanService.NextScanTime;
        NextScheduledScanText = next.HasValue
            ? $"Next scan: {next.Value:g}"
            : "Next scan: --";
    }

    public async Task RefreshStatusAsync()
    {
        LastScheduledScanText = FormatLastScan(_scheduledScanService.LastScanTime);
        UpdateNextScanText();
        await UpdateRepoStatusAsync();
    }

    private static string FormatLastScan(DateTime? dt)
    {
        return dt.HasValue ? $"Last scan: {dt.Value:g}" : "Last scan: Never";
    }

    private static string HoursToIntervalLabel(double hours)
    {
        return hours switch
        {
            6 => "Every 6 hours",
            12 => "Every 12 hours",
            48 => "Every 48 hours",
            168 => "Weekly",
            _ => "Every 24 hours"
        };
    }

    private static double IntervalLabelToHours(string label)
    {
        return label switch
        {
            "Every 6 hours" => 6,
            "Every 12 hours" => 12,
            "Every 24 hours" => 24,
            "Every 48 hours" => 48,
            "Weekly" => 168,
            _ => 24
        };
    }

    [RelayCommand]
    private void Save()
    {
        PingConcurrency = Math.Clamp(PingConcurrency, 1, 4096);
        WmiConcurrency = Math.Clamp(WmiConcurrency, 1, 1024);
        ScanConcurrency = Math.Clamp(ScanConcurrency, 1, 50);
        DeployConcurrency = Math.Clamp(DeployConcurrency, 1, 1024);
        PingTimeoutMs = Math.Clamp(PingTimeoutMs, 100, 60_000);
        WmiTimeoutSeconds = Math.Clamp(WmiTimeoutSeconds, 1, 300);
        AnalysisTimeoutMinutes = Math.Clamp(AnalysisTimeoutMinutes, 1, 24 * 60);
        DeployTimeoutMinutes = Math.Clamp(DeployTimeoutMinutes, 1, 24 * 60);
        FileTransferTimeoutMinutes = Math.Clamp(FileTransferTimeoutMinutes, 1, 24 * 60);
        RetryMaxAttempts = Math.Clamp(RetryMaxAttempts, 1, 10);
        RetryBaseDelayMs = Math.Clamp(RetryBaseDelayMs, 0, 60_000);
        SmtpPort = Math.Clamp(SmtpPort, 1, 65_535);

        if (ScheduledScanEnabled)
        {
            try
            {
                _ = new CidrRange(ScheduledScanCidr.Trim());
            }
            catch (Exception ex)
            {
                StatusMessage = $"Scheduled scan CIDR is invalid: {ex.Message}";
                SnackbarService.ShowError("Enter a valid CIDR before enabling scheduled scans.");
                return;
            }
        }

        AppSettings.DefaultPingConcurrency = PingConcurrency;
        AppSettings.DefaultWmiConcurrency = WmiConcurrency;
        AppSettings.DefaultScanConcurrency = ScanConcurrency;
        AppSettings.DefaultDeployConcurrency = DeployConcurrency;
        AppSettings.PingTimeoutMs = PingTimeoutMs;
        AppSettings.WmiTimeoutSeconds = WmiTimeoutSeconds;
        AppSettings.AnalysisTimeoutMinutes = AnalysisTimeoutMinutes;
        AppSettings.DeployTimeoutMinutes = DeployTimeoutMinutes;
        AppSettings.FileTransferTimeoutMinutes = FileTransferTimeoutMinutes;
        AppSettings.RetryMaxAttempts = RetryMaxAttempts;
        AppSettings.RetryBaseDelayMs = RetryBaseDelayMs;

        // Save scheduled scan settings
        AppSettings.ScheduledScanEnabled = ScheduledScanEnabled;
        ScheduledScanCidr = ScheduledScanCidr.Trim();
        AppSettings.ScheduledScanCidr = ScheduledScanCidr;
        AppSettings.ScheduledScanInterval = TimeSpan.FromHours(IntervalLabelToHours(SelectedInterval));

        AppSettings.RepositoryPath = RepositoryPath;
        AppSettings.RepositorySharePath = RepositorySharePath?.Trim() ?? string.Empty;
        AppSettings.UseOfflineRepository = UseOfflineRepository;

        // Save email notification settings
        AppSettings.EmailNotificationsEnabled = EmailNotificationsEnabled;
        AppSettings.SmtpServer = SmtpServer;
        AppSettings.SmtpPort = SmtpPort;
        AppSettings.SmtpUseSsl = SmtpUseSsl;
        AppSettings.SmtpUsername = SmtpUsername;
        AppSettings.SmtpPassword = SmtpPassword;
        AppSettings.EmailFrom = EmailFrom;
        AppSettings.EmailTo = EmailTo;
        AppSettings.NotifyOnScanComplete = NotifyOnScanComplete;
        AppSettings.NotifyOnCriticalUpdates = NotifyOnCriticalUpdates;
        AppSettings.NotifyOnDeployComplete = NotifyOnDeployComplete;
        AppSettings.NotifyOnDeployFailure = NotifyOnDeployFailure;

        // Save BIOS passwords
        AppSettings.BiosPasswords = new List<string>(_biosPasswordsRaw);

        AppSettings.Save();
        _scheduledScanService.Restart();
        UpdateNextScanText();
        StatusMessage = "Settings saved to disk.";
        SnackbarService.Show("Settings saved");
    }

    [RelayCommand]
    private void Reset()
    {
        PingConcurrency = 256;
        WmiConcurrency = 64;
        ScanConcurrency = 5;
        DeployConcurrency = 10;
        PingTimeoutMs = 1000;
        WmiTimeoutSeconds = 20;
        AnalysisTimeoutMinutes = 30;
        DeployTimeoutMinutes = 120;
        FileTransferTimeoutMinutes = 15;
        RetryMaxAttempts = 3;
        RetryBaseDelayMs = 2000;
        ScheduledScanEnabled = false;
        ScheduledScanCidr = "";
        SelectedInterval = "Every 24 hours";
        RepositoryPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Repository");
        RepositorySharePath = "";
        UseOfflineRepository = false;
        EmailNotificationsEnabled = false;
        SmtpServer = "";
        SmtpPort = 587;
        SmtpUseSsl = true;
        SmtpUsername = "";
        SmtpPassword = "";
        EmailFrom = "";
        EmailTo = "";
        NotifyOnScanComplete = true;
        NotifyOnCriticalUpdates = true;
        NotifyOnDeployComplete = true;
        NotifyOnDeployFailure = true;
        _biosPasswordsRaw.Clear();
        BiosPasswords.Clear();
        NewBiosPassword = "";
        StatusMessage = "Settings reset to defaults.";
    }

    [RelayCommand]
    private async Task TestEmailAsync()
    {
        IsTestingEmail = true;
        TestEmailStatus = "Sending test email...";

        try
        {
            var options = new EmailConnectionOptions(
                true,
                SmtpServer.Trim(),
                Math.Clamp(SmtpPort, 1, 65_535),
                SmtpUseSsl,
                SmtpUsername.Trim(),
                SmtpPassword,
                EmailFrom.Trim(),
                EmailTo.Trim());

            var sent = await _emailService.TestConnectionAsync(options);
            TestEmailStatus = sent
                ? "Test email sent successfully!"
                : "Enter an SMTP server, sender, and at least one recipient.";
        }
        catch (Exception ex)
        {
            TestEmailStatus = $"Failed: {ex.Message}";
        }
        finally
        {
            IsTestingEmail = false;
        }
    }

    [RelayCommand]
    private async Task SyncRepositoryAsync()
    {
        IsSyncingRepo = true;
        RepoSyncStatus = "Syncing update catalog...";

        try
        {
            var devices = await _deviceRepository.GetAllAsync();
            var platformIds = devices
                .Select(d => d.ProductId)
                .Where(id => !string.IsNullOrWhiteSpace(id) && id.Trim().Length >= 4)
                .Select(id => id.Trim().Length == 4 ? id.Trim() : id.Trim()[..4])
                .Where(id => id.All(c => Uri.IsHexDigit(c)))
                .Distinct()
                .ToList();

            if (platformIds.Count == 0)
            {
                RepoSyncStatus = "No HP platform IDs found. Scan for devices first.";
                return;
            }

            // Detect OS from device data
            var osVersions = devices.Select(d => d.OsVersion).Where(o => !string.IsNullOrWhiteSpace(o)).ToList();
            var os = "Win10"; var osVer = "22H2";
            foreach (var ov in osVersions)
            {
                if (ov.Contains("Windows 11", StringComparison.OrdinalIgnoreCase)) { os = "Win11"; osVer = "24H2"; break; }
                if (ov.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
                {
                    if (ov.Contains("22H2")) { osVer = "22H2"; break; }
                    if (ov.Contains("21H2")) { osVer = "21H2"; break; }
                    break;
                }
            }

            var progress = new Progress<string>(msg =>
                _ = Application.Current?.Dispatcher?.BeginInvoke(() => RepoSyncStatus = msg));

            var fileCount = await _repoSyncer.SyncRepositoryViaScriptFileAsync(
                platformIds, os, osVer, progress, CancellationToken.None);

            RepoSyncStatus = fileCount > 0
                ? $"Sync complete! {fileCount} files downloaded."
                : "Sync completed but repository is empty. Check logs.";
            await UpdateRepoStatusAsync();
        }
        catch (Exception ex)
        {
            RepoSyncStatus = $"Sync failed: {ex.Message}";
            Log.Error(ex, "Manual repository sync failed");
        }
        finally
        {
            IsSyncingRepo = false;
        }
    }

    [RelayCommand]
    private void AddBiosPassword()
    {
        if (string.IsNullOrWhiteSpace(NewBiosPassword)) return;
        _biosPasswordsRaw.Add(NewBiosPassword);
        BiosPasswords.Add(MaskPassword(NewBiosPassword));
        NewBiosPassword = "";
    }

    [RelayCommand]
    private void RemoveBiosPassword(string masked)
    {
        var index = BiosPasswords.IndexOf(masked);
        if (index >= 0 && index < _biosPasswordsRaw.Count)
        {
            _biosPasswordsRaw.RemoveAt(index);
            BiosPasswords.RemoveAt(index);
        }
    }

    private static string MaskPassword(string password)
    {
        if (string.IsNullOrEmpty(password)) return "****";
        return password.Length <= 2 ? "****" : password[..2] + "****";
    }

    private async Task UpdateRepoStatusAsync()
    {
        if (!await _repoStatusGate.WaitAsync(0))
            return;

        try
        {
            var repoPath = AppSettings.RepositoryPath;
            if (Directory.Exists(repoPath))
            {
                var fileCount = await Task.Run(() =>
                    Directory.EnumerateFiles(repoPath, "*", SearchOption.AllDirectories).Count());
                RepoStatus = fileCount > 0
                    ? $"{fileCount} files in local repository"
                    : "Repository empty";
            }
            else
            {
                RepoStatus = "Not synced yet";
            }
        }
        catch (Exception ex)
        {
            RepoStatus = "Repository status unavailable";
            Log.Warning(ex, "Failed to inspect repository path {RepositoryPath}", AppSettings.RepositoryPath);
        }
        finally
        {
            _repoStatusGate.Release();
        }
    }
}
