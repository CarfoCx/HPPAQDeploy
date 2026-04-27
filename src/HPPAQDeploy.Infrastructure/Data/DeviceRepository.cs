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
    private readonly AppDbContext _dbContext;
    private readonly ILogger _logger = Log.ForContext<DeviceRepository>();

    public DeviceRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<IReadOnlyList<Device>> GetAllAsync(CancellationToken ct = default)
    {
        return await _dbContext.Devices
            .AsNoTracking()
            .OrderBy(d => d.Hostname)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Device>> GetAllWithRecommendationsAsync(CancellationToken ct = default)
    {
        return await _dbContext.Devices
            .Include(d => d.Recommendations)
            .AsNoTracking()
            .OrderBy(d => d.Hostname)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<Device?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        return await _dbContext.Devices
            .FindAsync(new object[] { id }, ct)
            .ConfigureAwait(false);
    }

    public async Task AddAsync(Device device, CancellationToken ct = default)
    {
        _dbContext.Devices.Add(device);
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.Information("Added device {Hostname} ({IpAddress})", device.Hostname, device.IpAddress);
    }

    public async Task UpsertAsync(Device device, CancellationToken ct = default)
    {
        var existing = await _dbContext.Devices
            .FirstOrDefaultAsync(d => d.Hostname == device.Hostname, ct)
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
            _dbContext.Devices.Add(device);
            _logger.Information("Added new device {Hostname} ({IpAddress})", device.Hostname, device.IpAddress);
        }

        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(Device device, CancellationToken ct = default)
    {
        // Detach any already-tracked instance with the same Id to avoid tracking conflicts
        var trackedEntries = _dbContext.ChangeTracker.Entries<Device>()
            .Where(e => e.Entity.Id == device.Id).ToList();
        foreach (var entry in trackedEntries)
            entry.State = EntityState.Detached;

        // Also detach any tracked recommendations for this device
        var trackedRecs = _dbContext.ChangeTracker.Entries<HpiaRecommendation>()
            .Where(e => e.Entity.DeviceId == device.Id).ToList();
        foreach (var entry in trackedRecs)
            entry.State = EntityState.Detached;

        // Remove old recommendations before saving new ones to prevent duplicates
        var existingRecs = await _dbContext.Recommendations
            .Where(r => r.DeviceId == device.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        if (existingRecs.Count > 0)
            _dbContext.Recommendations.RemoveRange(existingRecs);

        _dbContext.Devices.Update(device);
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.Information("Updated device {Hostname} ({IpAddress})", device.Hostname, device.IpAddress);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var device = await _dbContext.Devices.FindAsync(new object[] { id }, ct).ConfigureAwait(false);
        if (device != null)
        {
            _dbContext.Devices.Remove(device);
            await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.Information("Deleted device {Hostname} (Id={Id})", device.Hostname, id);
        }
    }

    public async Task DeleteAllAsync(CancellationToken ct = default)
    {
        _dbContext.Devices.RemoveRange(_dbContext.Devices);
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.Information("Deleted all devices");
    }

    public async Task BatchUpsertAsync(IEnumerable<Device> devices, CancellationToken ct = default)
    {
        var deviceList = devices.ToList();
        if (deviceList.Count == 0) return;

        var hostnames = deviceList.Select(d => d.Hostname).ToList();
        var existing = await _dbContext.Devices
            .Where(d => hostnames.Contains(d.Hostname))
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
                _dbContext.Devices.Add(device);
            }
        }

        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.Information("Batch upserted {Count} devices", deviceList.Count);
    }

    /// <summary>
    /// Gets a device with its recommendations eagerly loaded.
    /// </summary>
    public async Task<Device?> GetWithRecommendationsAsync(int id, CancellationToken ct = default)
    {
        return await _dbContext.Devices
            .Include(d => d.Recommendations)
            .FirstOrDefaultAsync(d => d.Id == id, ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Device>> GetByGroupAsync(string groupName, CancellationToken ct = default)
    {
        return await _dbContext.Devices
            .Include(d => d.Recommendations)
            .Where(d => d.GroupName == groupName)
            .AsNoTracking()
            .OrderBy(d => d.Hostname)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task AssignGroupAsync(IEnumerable<int> deviceIds, string? groupName, CancellationToken ct = default)
    {
        var ids = deviceIds.ToList();
        var devices = await _dbContext.Devices
            .Where(d => ids.Contains(d.Id))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var device in devices)
        {
            device.GroupName = groupName;
        }

        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.Information("Assigned {Count} devices to group '{Group}'", devices.Count, groupName ?? "(none)");
    }
}
