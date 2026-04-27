using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HPPAQDeploy.App.Services;
using HPPAQDeploy.Core.Interfaces;
using HPPAQDeploy.Infrastructure.Hpia;
using HPPAQDeploy.Shared.Configuration;
using Serilog;

namespace HPPAQDeploy.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ScheduledScanService _scheduledScanService;
    private readonly IEmailService _emailService;
    private readonly RepositorySyncer _repoSyncer;
    private readonly IDeviceRepository _deviceRepository;

    public CredentialManagerViewModel CredentialManager { get; }

    [ObservableProperty]
    private int _pingConcurrency = AppSettings.DefaultPingConcurrency;

    [ObservableProperty]
    private int _wmiConcurrency = AppSettings.DefaultWmiConcurrency;

    [ObservableProperty]
    private int _deployConcurrency = AppSettings.DefaultDeployConcurrency;

    [ObservableProperty]
    private int _pingTimeoutMs = AppSettings.PingTimeoutMs;

    [ObservableProperty]
    private int _analysisTimeoutMinutes = AppSettings.AnalysisTimeoutMinutes;

    [ObservableProperty]
    private int _deployTimeoutMinutes = AppSettings.DeployTimeoutMinutes;

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
        UpdateRepoStatus();

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
        AppSettings.DefaultPingConcurrency = PingConcurrency;
        AppSettings.DefaultWmiConcurrency = WmiConcurrency;
        AppSettings.DefaultDeployConcurrency = DeployConcurrency;
        AppSettings.PingTimeoutMs = PingTimeoutMs;
        AppSettings.AnalysisTimeoutMinutes = AnalysisTimeoutMinutes;
        AppSettings.DeployTimeoutMinutes = DeployTimeoutMinutes;
        AppSettings.RetryMaxAttempts = RetryMaxAttempts;
        AppSettings.RetryBaseDelayMs = RetryBaseDelayMs;

        // Save scheduled scan settings
        AppSettings.ScheduledScanEnabled = ScheduledScanEnabled;
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
        DeployConcurrency = 10;
        PingTimeoutMs = 1000;
        AnalysisTimeoutMinutes = 30;
        DeployTimeoutMinutes = 120;
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
        // Apply current UI values to AppSettings temporarily for the test
        AppSettings.EmailNotificationsEnabled = true; // Force enabled for test
        AppSettings.SmtpServer = SmtpServer;
        AppSettings.SmtpPort = SmtpPort;
        AppSettings.SmtpUseSsl = SmtpUseSsl;
        AppSettings.SmtpUsername = SmtpUsername;
        AppSettings.SmtpPassword = SmtpPassword;
        AppSettings.EmailFrom = EmailFrom;
        AppSettings.EmailTo = EmailTo;

        IsTestingEmail = true;
        TestEmailStatus = "Sending test email...";

        try
        {
            await _emailService.TestConnectionAsync();
            TestEmailStatus = "Test email sent successfully!";
        }
        catch (Exception ex)
        {
            TestEmailStatus = $"Failed: {ex.Message}";
        }
        finally
        {
            // Restore the actual enabled state
            AppSettings.EmailNotificationsEnabled = EmailNotificationsEnabled;
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
            UpdateRepoStatus();
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

    private void UpdateRepoStatus()
    {
        var repoPath = AppSettings.RepositoryPath;
        if (Directory.Exists(repoPath))
        {
            var files = Directory.GetFiles(repoPath, "*", SearchOption.AllDirectories);
            RepoStatus = files.Length > 0
                ? $"{files.Length} files in local repository"
                : "Repository empty";
        }
        else
        {
            RepoStatus = "Not synced yet";
        }
    }
}
