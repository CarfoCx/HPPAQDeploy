using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HPPAQDeploy.Shared.Configuration;

public static class AppSettings
{
    private static readonly string AppRoot = AppDomain.CurrentDomain.BaseDirectory;

    public static string HpiaExePath { get; set; } = Path.Combine(AppRoot, "hp-hpia-5.3.4.exe");
    public static string HpiaExtractPath { get; set; } = Path.Combine(AppRoot, "HPIA");
    public static string DatabasePath { get; set; } = Path.Combine(AppRoot, "Data", "hppaq.db");
    public static string LogPath { get; set; } = Path.Combine(AppRoot, "Logs");
    public static string RemoteTempPath { get; set; } = @"C:\Temp\HPIA";
    public static string RepositoryPath { get; set; } = Path.Combine(AppRoot, "Repository");
    public static string RepositorySharePath { get; set; } = ""; // Optional: UNC path to shared HPIA repository for offline mode

    // BIOS passwords — HPIA will try each in order until one works (or skip if device has no BIOS password)
    public static List<string> BiosPasswords { get; set; } = new();

    public static int DefaultPingConcurrency { get; set; } = 256;
    public static int DefaultWmiConcurrency { get; set; } = 64;
    public static int DefaultScanConcurrency { get; set; } = 5;
    public static int DefaultDeployConcurrency { get; set; } = 10;

    public static int PingTimeoutMs { get; set; } = 1000;
    public static int WmiTimeoutSeconds { get; set; } = 20;
    public static int AnalysisTimeoutMinutes { get; set; } = 30;
    public static int DeployTimeoutMinutes { get; set; } = 120;
    public static int FileTransferTimeoutMinutes { get; set; } = 15;

    // Retry settings
    public static int RetryMaxAttempts { get; set; } = 3;
    public static int RetryBaseDelayMs { get; set; } = 2000;

    // Scheduled scan settings
    public static bool ScheduledScanEnabled { get; set; }
    public static TimeSpan ScheduledScanInterval { get; set; } = TimeSpan.FromHours(24);
    public static DateTime? LastScheduledScan { get; set; }
    public static string ScheduledScanCidr { get; set; } = string.Empty;

    // Email notification settings
    public static bool EmailNotificationsEnabled { get; set; } = false;
    public static string SmtpServer { get; set; } = "";
    public static int SmtpPort { get; set; } = 587;
    public static bool SmtpUseSsl { get; set; } = true;
    public static string SmtpUsername { get; set; } = "";
    public static string SmtpPassword { get; set; } = "";
    public static string EmailFrom { get; set; } = "";
    public static string EmailTo { get; set; } = "";
    public static bool NotifyOnScanComplete { get; set; } = true;
    public static bool NotifyOnCriticalUpdates { get; set; } = true;
    public static bool NotifyOnDeployComplete { get; set; } = true;
    public static bool NotifyOnDeployFailure { get; set; } = true;

    private static readonly string SettingsFilePath = Path.Combine(AppRoot, "Data", "settings.json");

    /// <summary>
    /// Encrypts a string using DPAPI (CurrentUser scope).
    /// Returns a Base64-encoded string, or empty string if input is empty.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static string ProtectString(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return "";
        var bytes = Encoding.UTF8.GetBytes(plainText);
        var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    /// <summary>
    /// Decrypts a DPAPI-protected Base64 string. Returns empty string on failure.
    /// Falls back to treating value as plaintext for backward compatibility.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static string UnprotectString(string protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return "";
        try
        {
            var encrypted = Convert.FromBase64String(protectedBase64);
            var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            // Backward compatibility: if it's not valid Base64/DPAPI, treat as plaintext
            return protectedBase64;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void Save()
    {
        var data = new SettingsData
        {
            DefaultPingConcurrency = DefaultPingConcurrency,
            DefaultWmiConcurrency = DefaultWmiConcurrency,
            DefaultScanConcurrency = DefaultScanConcurrency,
            DefaultDeployConcurrency = DefaultDeployConcurrency,
            PingTimeoutMs = PingTimeoutMs,
            WmiTimeoutSeconds = WmiTimeoutSeconds,
            AnalysisTimeoutMinutes = AnalysisTimeoutMinutes,
            DeployTimeoutMinutes = DeployTimeoutMinutes,
            FileTransferTimeoutMinutes = FileTransferTimeoutMinutes,
            RetryMaxAttempts = RetryMaxAttempts,
            RetryBaseDelayMs = RetryBaseDelayMs,
            ScheduledScanEnabled = ScheduledScanEnabled,
            ScheduledScanIntervalHours = ScheduledScanInterval.TotalHours,
            LastScheduledScan = LastScheduledScan,
            ScheduledScanCidr = ScheduledScanCidr,
            RepositoryPath = RepositoryPath,
            EmailNotificationsEnabled = EmailNotificationsEnabled,
            SmtpServer = SmtpServer,
            SmtpPort = SmtpPort,
            SmtpUseSsl = SmtpUseSsl,
            SmtpUsername = SmtpUsername,
            SmtpPasswordProtected = ProtectString(SmtpPassword),
            EmailFrom = EmailFrom,
            EmailTo = EmailTo,
            NotifyOnScanComplete = NotifyOnScanComplete,
            NotifyOnCriticalUpdates = NotifyOnCriticalUpdates,
            NotifyOnDeployComplete = NotifyOnDeployComplete,
            NotifyOnDeployFailure = NotifyOnDeployFailure,
            BiosPasswordsProtected = BiosPasswords.Select(p => ProtectString(p)).ToList()
        };
        var dir = Path.GetDirectoryName(SettingsFilePath);
        if (dir != null) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsFilePath, json);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void Load()
    {
        if (!File.Exists(SettingsFilePath)) return;
        try
        {
            var json = File.ReadAllText(SettingsFilePath);
            var data = JsonSerializer.Deserialize<SettingsData>(json);
            if (data == null) return;

            if (!string.IsNullOrEmpty(data.RepositoryPath)) RepositoryPath = data.RepositoryPath;

            DefaultPingConcurrency = data.DefaultPingConcurrency;
            DefaultWmiConcurrency = data.DefaultWmiConcurrency;
            DefaultScanConcurrency = data.DefaultScanConcurrency;
            DefaultDeployConcurrency = data.DefaultDeployConcurrency;
            PingTimeoutMs = data.PingTimeoutMs;
            WmiTimeoutSeconds = data.WmiTimeoutSeconds;
            AnalysisTimeoutMinutes = data.AnalysisTimeoutMinutes;
            DeployTimeoutMinutes = data.DeployTimeoutMinutes;
            FileTransferTimeoutMinutes = data.FileTransferTimeoutMinutes;
            RetryMaxAttempts = data.RetryMaxAttempts;
            RetryBaseDelayMs = data.RetryBaseDelayMs;
            ScheduledScanEnabled = data.ScheduledScanEnabled;
            ScheduledScanInterval = TimeSpan.FromHours(data.ScheduledScanIntervalHours);
            LastScheduledScan = data.LastScheduledScan;
            ScheduledScanCidr = data.ScheduledScanCidr;
            EmailNotificationsEnabled = data.EmailNotificationsEnabled;
            SmtpServer = data.SmtpServer;
            SmtpPort = data.SmtpPort;
            SmtpUseSsl = data.SmtpUseSsl;
            SmtpUsername = data.SmtpUsername;

            // Load SMTP password: prefer DPAPI-protected, fall back to legacy plaintext
            if (!string.IsNullOrEmpty(data.SmtpPasswordProtected))
                SmtpPassword = UnprotectString(data.SmtpPasswordProtected);
            else if (!string.IsNullOrEmpty(data.SmtpPassword))
                SmtpPassword = data.SmtpPassword; // Legacy plaintext, will be re-encrypted on next Save()

            EmailFrom = data.EmailFrom;
            EmailTo = data.EmailTo;
            NotifyOnScanComplete = data.NotifyOnScanComplete;
            NotifyOnCriticalUpdates = data.NotifyOnCriticalUpdates;
            NotifyOnDeployComplete = data.NotifyOnDeployComplete;
            NotifyOnDeployFailure = data.NotifyOnDeployFailure;

            if (data.BiosPasswordsProtected?.Count > 0)
                BiosPasswords = data.BiosPasswordsProtected.Select(p => UnprotectString(p)).ToList();
        }
        catch (Exception ex)
        {
            // Log the error so admins know the settings file is corrupt
            Console.Error.WriteLine($"[HPPAQDeploy] Failed to load settings from {SettingsFilePath}: {ex.Message}");
            // Fall through to defaults
        }
    }

    private class SettingsData
    {
        public int DefaultPingConcurrency { get; set; } = 256;
        public int DefaultWmiConcurrency { get; set; } = 64;
        public int DefaultScanConcurrency { get; set; } = 5;
        public int DefaultDeployConcurrency { get; set; } = 10;
        public int PingTimeoutMs { get; set; } = 1000;
        public int WmiTimeoutSeconds { get; set; } = 15;
        public int AnalysisTimeoutMinutes { get; set; } = 30;
        public int DeployTimeoutMinutes { get; set; } = 120;
        public int FileTransferTimeoutMinutes { get; set; } = 15;
        public int RetryMaxAttempts { get; set; } = 3;
        public int RetryBaseDelayMs { get; set; } = 2000;
        public bool ScheduledScanEnabled { get; set; }
        public double ScheduledScanIntervalHours { get; set; } = 24;
        public DateTime? LastScheduledScan { get; set; }
        public string ScheduledScanCidr { get; set; } = string.Empty;
        public string RepositoryPath { get; set; } = string.Empty;
        public bool EmailNotificationsEnabled { get; set; } = false;
        public string SmtpServer { get; set; } = "";
        public int SmtpPort { get; set; } = 587;
        public bool SmtpUseSsl { get; set; } = true;
        public string SmtpUsername { get; set; } = "";
        public string SmtpPassword { get; set; } = ""; // Legacy plaintext (read-only for migration)
        public string SmtpPasswordProtected { get; set; } = ""; // DPAPI-encrypted
        public string EmailFrom { get; set; } = "";
        public string EmailTo { get; set; } = "";
        public bool NotifyOnScanComplete { get; set; } = true;
        public bool NotifyOnCriticalUpdates { get; set; } = true;
        public bool NotifyOnDeployComplete { get; set; } = true;
        public bool NotifyOnDeployFailure { get; set; } = true;
        public List<string> BiosPasswordsProtected { get; set; } = new();
    }
}
