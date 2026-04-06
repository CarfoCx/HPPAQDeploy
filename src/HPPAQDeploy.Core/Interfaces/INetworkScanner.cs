using HPPAQDeploy.Core.Models;

namespace HPPAQDeploy.Core.Interfaces;

public interface INetworkScanner
{
    IAsyncEnumerable<ScanResult> PingSweepAsync(
        CidrRange range,
        int maxConcurrency,
        IProgress<(int completed, int total)> progress,
        CancellationToken ct);
}
