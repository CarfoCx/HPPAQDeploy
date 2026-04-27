using HPPAQDeploy.Core.Interfaces;
using HPPAQDeploy.Core.Models;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace HPPAQDeploy.Infrastructure.Data;

public class DeviceGroupRepository : IDeviceGroupRepository
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger _logger = Log.ForContext<DeviceGroupRepository>();

    public DeviceGroupRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<IReadOnlyList<DeviceGroup>> GetAllAsync(CancellationToken ct = default)
    {
        return await _dbContext.DeviceGroups
            .AsNoTracking()
            .OrderBy(g => g.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<DeviceGroup?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        return await _dbContext.DeviceGroups
            .FirstOrDefaultAsync(g => g.Name == name, ct)
            .ConfigureAwait(false);
    }

    public async Task<DeviceGroup> CreateAsync(string name, string? description = null, CancellationToken ct = default)
    {
        var group = new DeviceGroup { Name = name.Trim(), Description = description?.Trim() };
        _dbContext.DeviceGroups.Add(group);
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.Information("Created group: {Name}", name);
        return group;
    }

    public async Task UpdateAsync(DeviceGroup group, CancellationToken ct = default)
    {
        _dbContext.DeviceGroups.Update(group);
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.Information("Updated group: {Name}", group.Name);
    }

    public async Task DeleteAsync(string name, CancellationToken ct = default)
    {
        var group = await _dbContext.DeviceGroups.FirstOrDefaultAsync(g => g.Name == name, ct);
        if (group != null)
        {
            _dbContext.DeviceGroups.Remove(group);
            var devices = await _dbContext.Devices.Where(d => d.GroupName == name).ToListAsync(ct);
            foreach (var d in devices) d.GroupName = null;
            await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.Information("Deleted group: {Name}, unassigned {Count} devices", name, devices.Count);
        }
    }

    public async Task<bool> ExistsAsync(string name, CancellationToken ct = default)
    {
        return await _dbContext.DeviceGroups
            .AnyAsync(g => g.Name == name, ct)
            .ConfigureAwait(false);
    }
}
