using HPPAQDeploy.Core.Interfaces;
using HPPAQDeploy.Core.Models;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace HPPAQDeploy.Infrastructure.Data;

/// <summary>
/// Implements IDeviceRepository using EF Core with AppDbContext.
/// </summary>
public class DeviceRepository : IDeviceRepository
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly SemaphoreSlim _upsertGate = new(1, 1);
    private readonly ILogger _logger = Log.ForContext<DeviceRepository>();

    public DeviceRepository(IDbContextFactory<AppDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
    }

    public async Task<IReadOnlyList<Device>> GetAllAsync(CancellationToken ct = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await dbContext.Devices
            .AsNoTracking()
            .OrderBy(d => d.Hostname)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Device>> GetAllWithRecommendationsAsync(CancellationToken ct = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await dbContext.Devices
            .Include(d => d.Recommendations)
            .AsNoTracking()
            .OrderBy(d => d.Hostname)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<Device?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await dbContext.Devices
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id, ct)
            .ConfigureAwait(false);
    }

    public async Task AddAsync(Device device, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        dbContext.Devices.Add(device);
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.Information("Added device {Hostname} ({IpAddress})", device.Hostname, device.IpAddress);
    }

    public async Task UpsertAsync(Device device, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (string.IsNullOrWhiteSpace(device.Hostname))
            throw new ArgumentException("Device must have a hostname.", nameof(device));

        await _upsertGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var normalizedHostname = device.Hostname.ToUpperInvariant();
            var existing = await dbContext.Devices
                .FirstOrDefaultAsync(d => d.Hostname.ToUpper() == normalizedHostname, ct)
                .ConfigureAwait(false);

            if (existing != null)
            {
                existing.IpAddress = device.IpAddress;
                existing.Manufacturer = device.Manufacturer;
                existing.Model = device.Model;
                existing.SerialNumber = device.SerialNumber;
                existing.ProductId = device.ProductId;
                existing.OsVersion = device.OsVersion;
                existing.BiosVersion = device.BiosVersion;
                existing.Status = device.Status;
                existing.LastScanned = device.LastScanned;
                device.Id = existing.Id;
                _logger.Information("Updated existing device {Hostname} ({IpAddress})", device.Hostname, device.IpAddress);
            }
            else
            {
                dbContext.Devices.Add(device);
                _logger.Information("Added new device {Hostname} ({IpAddress})", device.Hostname, device.IpAddress);
            }

            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _upsertGate.Release();
        }
    }

    public async Task UpdateAsync(Device device, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var persisted = await dbContext.Devices
            .FirstOrDefaultAsync(d => d.Id == device.Id, ct)
            .ConfigureAwait(false);

        if (persisted is null)
            throw new InvalidOperationException($"Device with Id {device.Id} no longer exists.");

        dbContext.Entry(persisted).CurrentValues.SetValues(device);
        await dbContext.Recommendations
            .Where(r => r.DeviceId == device.Id)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        foreach (var recommendation in device.Recommendations)
        {
            recommendation.Id = 0;
            recommendation.DeviceId = device.Id;
            dbContext.Recommendations.Add(recommendation);
        }

        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        _logger.Information("Updated device {Hostname} ({IpAddress})", device.Hostname, device.IpAddress);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var device = await dbContext.Devices.FindAsync(new object[] { id }, ct).ConfigureAwait(false);
        if (device != null)
        {
            dbContext.Devices.Remove(device);
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.Information("Deleted device {Hostname} (Id={Id})", device.Hostname, id);
        }
    }

    public async Task DeleteAllAsync(CancellationToken ct = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await dbContext.Devices.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        _logger.Information("Deleted all devices");
    }

    public async Task BatchUpsertAsync(IEnumerable<Device> devices, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var inputDevices = devices.ToList();
        if (inputDevices.Any(d => string.IsNullOrWhiteSpace(d.Hostname)))
            throw new ArgumentException("Every device must have a hostname.", nameof(devices));

        // A multihomed machine can be discovered more than once in one sweep.
        // Keep the last observation and issue only one insert/update per hostname.
        var deviceList = inputDevices
            .GroupBy(d => d.Hostname, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();
        if (deviceList.Count == 0) return;

        await _upsertGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var hostnames = deviceList.Select(d => d.Hostname.ToUpperInvariant()).ToList();
            var existing = await dbContext.Devices
                .Where(d => hostnames.Contains(d.Hostname.ToUpper()))
                .ToDictionaryAsync(d => d.Hostname!, StringComparer.OrdinalIgnoreCase, ct)
                .ConfigureAwait(false);

            foreach (var device in deviceList)
            {
                if (device.Hostname != null && existing.TryGetValue(device.Hostname, out var existingDevice))
                {
                    existingDevice.IpAddress = device.IpAddress;
                    existingDevice.Manufacturer = device.Manufacturer;
                    existingDevice.Model = device.Model;
                    existingDevice.SerialNumber = device.SerialNumber;
                    existingDevice.ProductId = device.ProductId;
                    existingDevice.OsVersion = device.OsVersion;
                    existingDevice.BiosVersion = device.BiosVersion;
                    existingDevice.Status = device.Status;
                    existingDevice.LastScanned = device.LastScanned;
                    device.Id = existingDevice.Id;
                }
                else
                {
                    dbContext.Devices.Add(device);
                }
            }

            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.Information("Batch upserted {Count} devices", deviceList.Count);
        }
        finally
        {
            _upsertGate.Release();
        }
    }

    /// <summary>
    /// Gets a device with its recommendations eagerly loaded.
    /// </summary>
    public async Task<Device?> GetWithRecommendationsAsync(int id, CancellationToken ct = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await dbContext.Devices
            .Include(d => d.Recommendations)
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id, ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Device>> GetByGroupAsync(string groupName, CancellationToken ct = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await dbContext.Devices
            .Include(d => d.Recommendations)
            .Where(d => d.GroupName == groupName)
            .AsNoTracking()
            .OrderBy(d => d.Hostname)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task AssignGroupAsync(IEnumerable<int> deviceIds, string? groupName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        var ids = deviceIds.ToList();
        if (ids.Count == 0)
            return;

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var updatedCount = await dbContext.Devices
            .Where(d => ids.Contains(d.Id))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(d => d.GroupName, groupName),
                ct)
            .ConfigureAwait(false);
        _logger.Information("Assigned {Count} devices to group '{Group}'", updatedCount, groupName ?? "(none)");
    }
}
