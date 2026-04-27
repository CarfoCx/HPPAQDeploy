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
    private readonly AppDbContext _dbContext;
    private readonly ILogger _logger = Log.ForContext<DeploymentHistoryRepository>();

    public DeploymentHistoryRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task AddAsync(DeploymentHistory entry, CancellationToken ct = default)
    {
        _dbContext.DeploymentHistories.Add(entry);
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.Information("Recorded deployment history: {Action} {SoftPaqId} on {Hostname}",
            entry.Action, entry.SoftPaqId, entry.DeviceHostname);
    }

    public async Task AddRangeAsync(IEnumerable<DeploymentHistory> entries, CancellationToken ct = default)
    {
        _dbContext.DeploymentHistories.AddRange(entries);
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.Information("Recorded {Count} deployment history entries", entries.Count());
    }

    public async Task<IEnumerable<DeploymentHistory>> GetAllAsync(CancellationToken ct = default)
    {
        return await _dbContext.DeploymentHistories
            .AsNoTracking()
            .OrderByDescending(h => h.Timestamp)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<IEnumerable<DeploymentHistory>> GetByDeviceAsync(int deviceId, CancellationToken ct = default)
    {
        return await _dbContext.DeploymentHistories
            .AsNoTracking()
            .Where(h => h.DeviceId == deviceId)
            .OrderByDescending(h => h.Timestamp)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<IEnumerable<DeploymentHistory>> GetRecentAsync(int count = 100, CancellationToken ct = default)
    {
        return await _dbContext.DeploymentHistories
            .AsNoTracking()
            .OrderByDescending(h => h.Timestamp)
            .Take(count)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        _dbContext.DeploymentHistories.RemoveRange(_dbContext.DeploymentHistories);
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.Information("Cleared all deployment history records");
    }
}
