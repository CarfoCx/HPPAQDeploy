namespace HPPAQDeploy.Core.Interfaces;

public interface IEmailService
{
    Task SendAsync(string subject, string body, CancellationToken ct = default);
    Task<bool> TestConnectionAsync(CancellationToken ct = default);
    Task SendScanCompleteNotificationAsync(int devicesFound, int aliveHosts, int totalIps);
    Task SendCriticalUpdatesFoundAsync(int criticalCount, IEnumerable<string> deviceNames);
    Task SendDeployCompleteNotificationAsync(int successCount, int failCount);
}
