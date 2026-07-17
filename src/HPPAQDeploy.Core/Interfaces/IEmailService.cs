namespace HPPAQDeploy.Core.Interfaces;

public sealed record EmailConnectionOptions(
    bool Enabled,
    string SmtpServer,
    int SmtpPort,
    bool UseSsl,
    string Username,
    string Password,
    string From,
    string To);

public interface IEmailService
{
    Task SendAsync(string subject, string body, CancellationToken ct = default);
    Task<bool> TestConnectionAsync(EmailConnectionOptions options, CancellationToken ct = default);
    Task SendScanCompleteNotificationAsync(int devicesFound, int aliveHosts, int totalIps);
    Task SendCriticalUpdatesFoundAsync(int criticalCount, IEnumerable<string> deviceNames);
    Task SendDeployCompleteNotificationAsync(int successCount, int failCount);
}
