using HPPAQDeploy.Core.Models;
using HPPAQDeploy.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HPPAQDeploy.Tests;

public sealed class InfrastructureRepositoryTests
{
    [Fact]
    public async Task BatchUpsertAsync_DeduplicatesHostnamesCaseInsensitively()
    {
        await using var database = new RepositoryTestDatabase();
        var repository = new DeviceRepository(database);

        await repository.BatchUpsertAsync(
        [
            new Device { Hostname = "MULTIHOST", IpAddress = "10.0.0.1" },
            new Device { Hostname = "multihost", IpAddress = "10.0.0.2" }
        ]);

        var devices = await repository.GetAllAsync();
        var device = Assert.Single(devices);
        Assert.Equal("10.0.0.2", device.IpAddress);
    }

    [Fact]
    public async Task Repository_AllowsConcurrentOperationsOnOneServiceInstance()
    {
        await using var database = new RepositoryTestDatabase();
        var repository = new DeviceRepository(database);

        await Task.WhenAll(Enumerable.Range(1, 12).Select(index =>
            repository.AddAsync(new Device
            {
                Hostname = $"host-{index}",
                IpAddress = $"10.0.0.{index}"
            })));

        Assert.Equal(12, (await repository.GetAllAsync()).Count);
    }

    [Fact]
    public async Task ConcurrentBatchUpserts_WithSharedHostname_PreserveBothBatches()
    {
        await using var database = new RepositoryTestDatabase();
        var repository = new DeviceRepository(database);

        await Task.WhenAll(
            repository.BatchUpsertAsync(
            [
                new Device { Hostname = "shared-host", IpAddress = "10.0.0.1" },
                new Device { Hostname = "batch-a", IpAddress = "10.0.0.2" }
            ]),
            repository.BatchUpsertAsync(
            [
                new Device { Hostname = "SHARED-HOST", IpAddress = "10.0.0.3" },
                new Device { Hostname = "batch-b", IpAddress = "10.0.0.4" }
            ]));

        var devices = await repository.GetAllAsync();
        Assert.Equal(3, devices.Count);
        Assert.Single(devices, device =>
            device.Hostname.Equals("shared-host", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UpdateAsync_ReplacesDetachedRecommendations()
    {
        await using var database = new RepositoryTestDatabase();
        var repository = new DeviceRepository(database);
        var device = new Device
        {
            Hostname = "host1",
            IpAddress = "10.0.0.1",
            Recommendations = [new HpiaRecommendation { SoftPaqId = "SP1", Name = "Old" }]
        };
        await repository.AddAsync(device);

        var detached = Assert.Single(await repository.GetAllWithRecommendationsAsync());
        detached.Recommendations = [new HpiaRecommendation { SoftPaqId = "SP2", Name = "New" }];
        await repository.UpdateAsync(detached);

        var saved = Assert.Single(await repository.GetAllWithRecommendationsAsync());
        var recommendation = Assert.Single(saved.Recommendations);
        Assert.Equal("SP2", recommendation.SoftPaqId);
        Assert.NotEqual(0, recommendation.Id);
    }

    [Fact]
    public async Task Initialize_EnablesWalJournalMode()
    {
        await using var database = new RepositoryTestDatabase();
        await using var context = await database.CreateDbContextAsync();
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";

        Assert.Equal("wal", (await command.ExecuteScalarAsync())?.ToString());
    }

    private sealed class RepositoryTestDatabase : IDbContextFactory<AppDbContext>, IAsyncDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"hppaq-db-tests-{Guid.NewGuid():N}.db");
        private readonly DbContextOptions<AppDbContext> _options;

        public RepositoryTestDatabase()
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _path,
                DefaultTimeout = 30,
                Pooling = true
            }.ToString();
            _options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connectionString)
                .Options;

            using var context = CreateDbContext();
            context.Initialize();
        }

        public AppDbContext CreateDbContext() => new(_options);

        public ValueTask<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(CreateDbContext());
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(_path); } catch { }
            try { File.Delete(_path + "-wal"); } catch { }
            try { File.Delete(_path + "-shm"); } catch { }
            return ValueTask.CompletedTask;
        }
    }
}
