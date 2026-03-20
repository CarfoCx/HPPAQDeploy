using HPPAQDeploy.Core.Models;

namespace HPPAQDeploy.Core.Interfaces;

public interface IDeploymentHistoryRepository
{
    Task AddAsync(DeploymentHistory entry);
    Task AddRangeAsync(IEnumerable<DeploymentHistory> entries);
    Task<IEnumerable<DeploymentHistory>> GetAllAsync();
    Task<IEnumerable<DeploymentHistory>> GetByDeviceAsync(int deviceId);
    Task<IEnumerable<DeploymentHistory>> GetRecentAsync(int count = 100);
    Task ClearAllAsync();
}
