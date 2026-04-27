using HPPAQDeploy.Core.Models;
using HPPAQDeploy.Shared.Configuration;
using Microsoft.EntityFrameworkCore;

namespace HPPAQDeploy.Infrastructure.Data;

/// <summary>
/// EF Core DbContext for the HPPAQDeploy application.
/// Uses SQLite for local data storage.
/// </summary>
public class AppDbContext : DbContext
{
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Credential> Credentials => Set<Credential>();
    public DbSet<HpiaRecommendation> Recommendations => Set<HpiaRecommendation>();
    public DbSet<DeploymentJob> DeploymentJobs => Set<DeploymentJob>();
    public DbSet<DeploymentHistory> DeploymentHistories => Set<DeploymentHistory>();
    public DbSet<DeviceGroup> DeviceGroups => Set<DeviceGroup>();

    public AppDbContext()
    {
    }

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            var dbPath = AppSettings.DatabasePath;
            var dbDirectory = Path.GetDirectoryName(dbPath)!;

            if (!Directory.Exists(dbDirectory))
                Directory.CreateDirectory(dbDirectory);

            optionsBuilder.UseSqlite($"Data Source={dbPath}");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Device configuration
        modelBuilder.Entity<Device>(entity =>
        {
            entity.HasKey(d => d.Id);
            entity.Property(d => d.Hostname).IsRequired().HasMaxLength(256);
            entity.Property(d => d.IpAddress).IsRequired().HasMaxLength(45);
            entity.Property(d => d.Manufacturer).HasMaxLength(256);
            entity.Property(d => d.Model).HasMaxLength(256);
            entity.Property(d => d.SerialNumber).HasMaxLength(128);
            entity.Property(d => d.ProductId).HasMaxLength(128);
            entity.Property(d => d.OsVersion).HasMaxLength(512);
            entity.Property(d => d.BiosVersion).HasMaxLength(256);

            entity.HasMany(d => d.Recommendations)
                  .WithOne()
                  .HasForeignKey(r => r.DeviceId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(d => d.Hostname).IsUnique();
            entity.HasIndex(d => d.IpAddress);
        });

        // Credential configuration
        modelBuilder.Entity<Credential>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Label).IsRequired().HasMaxLength(256);
            entity.Property(c => c.Domain).HasMaxLength(256);
            entity.Property(c => c.Username).IsRequired().HasMaxLength(256);
            entity.Property(c => c.EncryptedPassword).IsRequired();
            entity.Property(c => c.IV).IsRequired();
        });

        // HpiaRecommendation configuration
        modelBuilder.Entity<HpiaRecommendation>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.Property(r => r.SoftPaqId).IsRequired().HasMaxLength(64);
            entity.Property(r => r.Name).HasMaxLength(512);
            entity.Property(r => r.Version).HasMaxLength(128);
            entity.Property(r => r.Category).HasMaxLength(128);
            entity.Property(r => r.Severity).HasMaxLength(64);
            entity.Property(r => r.DownloadUrl).HasMaxLength(2048);

            // Ignore the Selected property - it is UI-only (NotMapped + ObservableProperty)
            entity.Ignore(r => r.Selected);

            entity.HasIndex(r => r.DeviceId);
        });

        // DeploymentJob configuration
        modelBuilder.Entity<DeploymentJob>(entity =>
        {
            entity.HasKey(j => j.Id);
            entity.Property(j => j.Status).HasMaxLength(64);
            entity.Property(j => j.LogOutput).HasColumnType("TEXT");

            entity.HasIndex(j => j.DeviceId);
        });

        // DeploymentHistory configuration
        modelBuilder.Entity<DeploymentHistory>(entity =>
        {
            entity.HasKey(h => h.Id);
            entity.Property(h => h.DeviceHostname).HasMaxLength(256);
            entity.Property(h => h.DeviceIpAddress).HasMaxLength(45);
            entity.Property(h => h.UpdateName).HasMaxLength(512);
            entity.Property(h => h.SoftPaqId).HasMaxLength(64);
            entity.Property(h => h.Category).HasMaxLength(128);
            entity.Property(h => h.Action).IsRequired().HasMaxLength(64);
            entity.Property(h => h.ErrorMessage).HasColumnType("TEXT");

            entity.HasIndex(h => h.DeviceId);
            entity.HasIndex(h => h.Timestamp);
        });

        // DeviceGroup configuration
        modelBuilder.Entity<DeviceGroup>(entity =>
        {
            entity.HasKey(g => g.Id);
            entity.Property(g => g.Name).IsRequired().HasMaxLength(128);
            entity.Property(g => g.Description).HasMaxLength(512);
            entity.HasIndex(g => g.Name).IsUnique();
        });
    }

    /// <summary>
    /// Ensures the database and tables are created.
    /// Also applies lightweight schema migrations for columns added after initial release.
    /// Call this during application startup.
    /// </summary>
    public void Initialize()
    {
        Database.EnsureCreated();
        ApplyColumnMigrations();
    }

    /// <summary>
    /// Adds any columns that were introduced after the initial schema.
    /// SQLite's EnsureCreated() doesn't add columns to existing tables, so we do it manually.
    /// </summary>
    private void ApplyColumnMigrations()
    {
        var conn = Database.GetDbConnection();
        conn.Open();
        try
        {
            // Collect existing column names for the Devices table
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(Devices);";
            var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    existingColumns.Add(reader.GetString(1)); // column name is at index 1
                }
            }

            // Add NeedsReboot if missing (added for reboot-pending detection)
            if (!existingColumns.Contains("NeedsReboot"))
            {
                using var alter = conn.CreateCommand();
                alter.CommandText = "ALTER TABLE Devices ADD COLUMN NeedsReboot INTEGER NOT NULL DEFAULT 0;";
                alter.ExecuteNonQuery();
            }

            // Add LastAnalyzed if missing
            if (!existingColumns.Contains("LastAnalyzed"))
            {
                using var alter = conn.CreateCommand();
                alter.CommandText = "ALTER TABLE Devices ADD COLUMN LastAnalyzed TEXT;";
                alter.ExecuteNonQuery();
            }

            // Add GroupName if missing (Groups feature)
            if (!existingColumns.Contains("GroupName"))
            {
                using var alter = conn.CreateCommand();
                alter.CommandText = "ALTER TABLE Devices ADD COLUMN GroupName TEXT;";
                alter.ExecuteNonQuery();
            }

            // Ensure DeploymentHistories table exists (added after initial release)
            using var createHistory = conn.CreateCommand();
            createHistory.CommandText = @"
                CREATE TABLE IF NOT EXISTS DeploymentHistories (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    DeviceId INTEGER NOT NULL,
                    DeviceHostname TEXT,
                    DeviceIpAddress TEXT,
                    UpdateName TEXT,
                    SoftPaqId TEXT,
                    Category TEXT,
                    Action TEXT NOT NULL,
                    ErrorMessage TEXT,
                    Timestamp TEXT NOT NULL DEFAULT (datetime('now')),
                    RebootRequired INTEGER NOT NULL DEFAULT 0
                );";
            createHistory.ExecuteNonQuery();

            // Create indexes for DeploymentHistories if they don't exist
            using var createHistIdx1 = conn.CreateCommand();
            createHistIdx1.CommandText = "CREATE INDEX IF NOT EXISTS IX_DeploymentHistories_DeviceId ON DeploymentHistories(DeviceId);";
            createHistIdx1.ExecuteNonQuery();

            using var createHistIdx2 = conn.CreateCommand();
            createHistIdx2.CommandText = "CREATE INDEX IF NOT EXISTS IX_DeploymentHistories_Timestamp ON DeploymentHistories(Timestamp);";
            createHistIdx2.ExecuteNonQuery();

            // Add SilentInstallable column to Recommendations if missing
            {
                using var pragmaCmd = conn.CreateCommand();
                pragmaCmd.CommandText = "PRAGMA table_info(Recommendations);";
                var recColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var reader2 = pragmaCmd.ExecuteReader())
                {
                    while (reader2.Read())
                        recColumns.Add(reader2.GetString(1));
                }
                if (!recColumns.Contains("SilentInstallable"))
                {
                    using var alter = conn.CreateCommand();
                    alter.CommandText = "ALTER TABLE Recommendations ADD COLUMN SilentInstallable INTEGER NOT NULL DEFAULT 1;";
                    alter.ExecuteNonQuery();
                }
            }

            // Ensure DeviceGroups table exists
            using var createGroups = conn.CreateCommand();
            createGroups.CommandText = @"
                CREATE TABLE IF NOT EXISTS DeviceGroups (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL UNIQUE,
                    Description TEXT,
                    Created TEXT NOT NULL DEFAULT (datetime('now'))
                );";
            createGroups.ExecuteNonQuery();
        }
        finally
        {
            conn.Close();
        }
    }
}
