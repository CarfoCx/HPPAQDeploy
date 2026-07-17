using HPPAQDeploy.Core.Interfaces;
using HPPAQDeploy.Core.Models;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace HPPAQDeploy.Infrastructure.Data;

public class DeviceGroupRepository : IDeviceGroupRepository
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly ILogger _logger = Log.ForContext<DeviceGroupRepository>();

    public DeviceGroupRepository(IDbContextFactory<AppDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
    }

    public async Task<IReadOnlyList<DeviceGroup>> GetAllAsync(CancellationToken ct = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await dbContext.DeviceGroups
            .AsNoTracking()
            .OrderBy(g => g.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<DeviceGroup?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await dbContext.DeviceGroups
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Name == name, ct)
            .ConfigureAwait(false);
    }

    public async Task<DeviceGroup> CreateAsync(string name, string? description = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var group = new DeviceGroup { Name = name.Trim(), Description = description?.Trim() };
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        dbContext.DeviceGroups.Add(group);
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.Information("Created group: {Name}", name);
        return group;
    }

    public async Task UpdateAsync(DeviceGroup group, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        dbContext.DeviceGroups.Update(group);
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.Information("Updated group: {Name}", group.Name);
    }

    public async Task DeleteAsync(string name, CancellationToken ct = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var group = await dbContext.DeviceGroups.FirstOrDefaultAsync(g => g.Name == name, ct).ConfigureAwait(false);
        if (group != null)
        {
            dbContext.DeviceGroups.Remove(group);
            var unassignedCount = await dbContext.Devices
                .Where(d => d.GroupName == name)
                .ExecuteUpdateAsync(setters => setters.SetProperty(d => d.GroupName, (string?)null), ct)
                .ConfigureAwait(false);
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            _logger.Information("Deleted group: {Name}, unassigned {Count} devices", name, unassignedCount);
        }
    }

    public async Task<bool> ExistsAsync(string name, CancellationToken ct = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await dbContext.DeviceGroups
            .AnyAsync(g => g.Name == name, ct)
            .ConfigureAwait(false);
    }
}
