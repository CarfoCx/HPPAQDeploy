using System.Net;
using HPPAQDeploy.Core.Models;

namespace HPPAQDeploy.Core.Interfaces;

public interface IHpiaManager
{
    Task ExtractLocallyAsync(CancellationToken ct);

    Task StageToRemoteAsync(
        string hostname,
        NetworkCredential credential,
        CancellationToken ct);

    Task<List<HpiaRecommendation>> RunAnalysisAsync(
        Device device,
        NetworkCredential credential,
        CancellationToken ct,
        IProgress<string>? progress = null);

    Task DeployUpdatesAsync(
        Device device,
        NetworkCredential credential,
        IReadOnlyList<HpiaRecommendation> selectedRecommendations,
        IProgress<string> progress,
        CancellationToken ct);

    Task CleanupRemoteAsync(
        string hostname,
        NetworkCredential credential,
        CancellationToken ct);
}
