using HPPAQDeploy.Core.Models;

namespace HPPAQDeploy.Core.Interfaces;

public interface IDeviceGroupRepository
{
    Task<IReadOnlyList<DeviceGroup>> GetAllAsync(CancellationToken ct = default);
    Task<DeviceGroup?> GetByNameAsync(string name, CancellationToken ct = default);
    Task<DeviceGroup> CreateAsync(string name, string? description = null, CancellationToken ct = default);
    Task UpdateAsync(DeviceGroup group, CancellationToken ct = default);
    Task DeleteAsync(string name, CancellationToken ct = default);
    Task<bool> ExistsAsync(string name, CancellationToken ct = default);
}
