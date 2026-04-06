using HPPAQDeploy.Core.Models;

namespace HPPAQDeploy.Core.Interfaces;

public interface IDeploymentHistoryRepository
{
    Task AddAsync(DeploymentHistory entry, CancellationToken ct = default);
    Task AddRangeAsync(IEnumerable<DeploymentHistory> entries, CancellationToken ct = default);
    Task<IEnumerable<DeploymentHistory>> GetAllAsync(CancellationToken ct = default);
    Task<IEnumerable<DeploymentHistory>> GetByDeviceAsync(int deviceId, CancellationToken ct = default);
    Task<IEnumerable<DeploymentHistory>> GetRecentAsync(int count = 100, CancellationToken ct = default);
    Task ClearAllAsync(CancellationToken ct = default);
}
