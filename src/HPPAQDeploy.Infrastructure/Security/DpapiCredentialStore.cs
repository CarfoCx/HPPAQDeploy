using System.Net;
using System.Security.Cryptography;
using System.Text;
using HPPAQDeploy.Core.Interfaces;
using HPPAQDeploy.Core.Models;
using HPPAQDeploy.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace HPPAQDeploy.Infrastructure.Security;

/// <summary>
/// Implements ICredentialStore using DPAPI (DataProtectionScope.CurrentUser)
/// to encrypt/decrypt passwords. Stores credentials in SQLite via AppDbContext.
/// </summary>
public class DpapiCredentialStore : ICredentialStore
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly ILogger _logger = Log.ForContext<DpapiCredentialStore>();

    public DpapiCredentialStore(IDbContextFactory<AppDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
    }

    public async Task<IReadOnlyList<Credential>> GetAllAsync(CancellationToken ct = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await dbContext.Credentials
            .AsNoTracking()
            .OrderByDescending(c => c.IsDefault)
            .ThenBy(c => c.Label)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task SaveAsync(Credential credential, string plainTextPassword, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (string.IsNullOrEmpty(plainTextPassword))
            throw new ArgumentException("Password cannot be null or empty.", nameof(plainTextPassword));

        // Generate entropy (stored as IV field)
        var entropy = RandomNumberGenerator.GetBytes(16);

        // Encrypt using DPAPI with CurrentUser scope
        var passwordBytes = Encoding.UTF8.GetBytes(plainTextPassword);
        var encryptedBytes = ProtectedData.Protect(passwordBytes, entropy, DataProtectionScope.CurrentUser);

        credential.EncryptedPassword = encryptedBytes;
        credential.IV = entropy;
        credential.Created = DateTime.UtcNow;

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (credential.Id == 0)
        {
            dbContext.Credentials.Add(credential);
        }
        else
        {
            dbContext.Credentials.Update(credential);
        }

        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.Information("Saved credential '{Label}' for {Domain}\\{Username}",
            credential.Label, credential.Domain, credential.Username);
    }

    public Task<NetworkCredential> DecryptAsync(Credential credential)
    {
        if (credential.EncryptedPassword == null || credential.EncryptedPassword.Length == 0)
            throw new InvalidOperationException("Credential has no encrypted password data.");

        try
        {
            // Decrypt using DPAPI with entropy from IV field
            // DPAPI is CPU-bound and fast, so we use Task.FromResult rather than Task.Run
            var decryptedBytes = ProtectedData.Unprotect(
                credential.EncryptedPassword,
                credential.IV,
                DataProtectionScope.CurrentUser);

            var password = Encoding.UTF8.GetString(decryptedBytes);

            return Task.FromResult(new NetworkCredential(credential.Username, password, credential.Domain));
        }
        catch (CryptographicException ex)
        {
            _logger.Error(ex, "Failed to decrypt credential '{Label}'. This may occur if the credential " +
                "was encrypted by a different user account.", credential.Label);
            throw;
        }
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var credential = await dbContext.Credentials
            .FindAsync(new object[] { id }, ct)
            .ConfigureAwait(false);

        if (credential != null)
        {
            dbContext.Credentials.Remove(credential);
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.Information("Deleted credential '{Label}' (Id={Id})", credential.Label, id);
        }
    }
}
