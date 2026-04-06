using HPPAQDeploy.Core.Models;

namespace HPPAQDeploy.Core.Interfaces;

public interface IDeviceRepository
{
    Task<IReadOnlyList<Device>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Device>> GetAllWithRecommendationsAsync(CancellationToken ct = default);
    Task<Device?> GetByIdAsync(int id, CancellationToken ct = default);
    Task AddAsync(Device device, CancellationToken ct = default);
    Task UpsertAsync(Device device, CancellationToken ct = default);
    Task UpdateAsync(Device device, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
    Task DeleteAllAsync(CancellationToken ct = default);
    Task BatchUpsertAsync(IEnumerable<Device> devices, CancellationToken ct = default);
    Task<Device?> GetWithRecommendationsAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<Device>> GetByGroupAsync(string groupName, CancellationToken ct = default);
    Task AssignGroupAsync(IEnumerable<int> deviceIds, string? groupName, CancellationToken ct = default);
}
