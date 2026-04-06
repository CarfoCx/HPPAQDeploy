using System.Net;
using HPPAQDeploy.Core.Models;

namespace HPPAQDeploy.Core.Interfaces;

public interface ICredentialStore
{
    Task<IReadOnlyList<Credential>> GetAllAsync(CancellationToken ct = default);
    Task SaveAsync(Credential credential, string plainTextPassword, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
    Task<NetworkCredential> DecryptAsync(Credential credential);
}
