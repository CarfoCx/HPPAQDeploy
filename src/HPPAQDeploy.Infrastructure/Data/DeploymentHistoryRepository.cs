using HPPAQDeploy.Core.Interfaces;
using HPPAQDeploy.Core.Models;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace HPPAQDeploy.Infrastructure.Data;

/// <summary>
/// Implements IDeploymentHistoryRepository using EF Core with AppDbContext.
/// </summary>
public class DeploymentHistoryRepository : IDeploymentHistoryRepository
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly ILogger _logger = Log.ForContext<DeploymentHistoryRepository>();

    public DeploymentHistoryRepository(IDbContextFactory<AppDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
    }

    public async Task AddAsync(DeploymentHistory entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        dbContext.DeploymentHistories.Add(entry);
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.Information("Recorded deployment history: {Action} {SoftPaqId} on {Hostname}",
            entry.Action, entry.SoftPaqId, entry.DeviceHostname);
    }

    public async Task AddRangeAsync(IEnumerable<DeploymentHistory> entries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var entryList = entries.ToList();
        if (entryList.Count == 0)
            return;

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        dbContext.DeploymentHistories.AddRange(entryList);
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.Information("Recorded {Count} deployment history entries", entryList.Count);
    }

    public async Task<IEnumerable<DeploymentHistory>> GetAllAsync(CancellationToken ct = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await dbContext.DeploymentHistories
            .AsNoTracking()
            .OrderByDescending(h => h.Timestamp)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<IEnumerable<DeploymentHistory>> GetByDeviceAsync(int deviceId, CancellationToken ct = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await dbContext.DeploymentHistories
            .AsNoTracking()
            .Where(h => h.DeviceId == deviceId)
            .OrderByDescending(h => h.Timestamp)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<IEnumerable<DeploymentHistory>> GetRecentAsync(int count = 100, CancellationToken ct = default)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await dbContext.DeploymentHistories
            .AsNoTracking()
            .OrderByDescending(h => h.Timestamp)
            .Take(count)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await dbContext.DeploymentHistories.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        _logger.Information("Cleared all deployment history records");
    }
}
