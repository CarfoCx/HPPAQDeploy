using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using HPPAQDeploy.Core.Interfaces;
using HPPAQDeploy.Core.Models;
using HPPAQDeploy.Shared.Configuration;
using Serilog;

namespace HPPAQDeploy.Infrastructure.Network;

/// <summary>
/// Implements INetworkScanner using ICMP ping sweeps with throttled concurrency.
/// </summary>
public class PingSweeper : INetworkScanner
{
    private readonly ILogger _logger = Log.ForContext<PingSweeper>();

    public async IAsyncEnumerable<ScanResult> PingSweepAsync(
        CidrRange range,
        int maxConcurrency,
        IProgress<(int completed, int total)> progress,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        if (maxConcurrency <= 0 || maxConcurrency > 4096)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency));

        var channel = Channel.CreateBounded<ScanResult>(new BoundedChannelOptions(Math.Max(1, maxConcurrency * 2))
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        int total = range.UsableHostCount;
        int completed = 0;

        _logger.Information("Starting ping sweep of {Range} ({Total} hosts, concurrency={Concurrency})",
            range.Network, total, maxConcurrency);

        using var producerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var producerTask = ProduceResultsAsync(producerCts.Token);

        async Task ProduceResultsAsync(CancellationToken producerToken)
        {
            try
            {
                await Parallel.ForEachAsync(
                    range.GetAllHosts(),
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = maxConcurrency,
                        CancellationToken = producerToken
                    },
                    async (ip, token) =>
                    {
                        try
                        {
                            var result = await PingHostAsync(ip, token).ConfigureAwait(false);
                            await channel.Writer.WriteAsync(result, token).ConfigureAwait(false);
                        }
                        finally
                        {
                            var current = Interlocked.Increment(ref completed);
                            progress?.Report((current, total));
                        }
                    }).ConfigureAwait(false);
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }

        try
        {
            await foreach (var result in channel.Reader.ReadAllAsync(producerCts.Token).ConfigureAwait(false))
            {
                yield return result;
            }

            await producerTask.ConfigureAwait(false);
        }
        finally
        {
            if (!producerTask.IsCompleted)
            {
                producerCts.Cancel();
                try
                {
                    await producerTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (producerCts.IsCancellationRequested)
                {
                    // The consumer stopped enumerating before the sweep completed.
                }
            }
        }
    }

    private async Task<ScanResult> PingHostAsync(IPAddress ip, CancellationToken ct)
    {
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(ip, AppSettings.PingTimeoutMs)
                .WaitAsync(ct)
                .ConfigureAwait(false);

            string? hostname = null;
            if (reply.Status == IPStatus.Success)
            {
                try
                {
                    var hostEntry = await Dns.GetHostEntryAsync(ip)
                        .WaitAsync(ct)
                        .ConfigureAwait(false);
                    hostname = hostEntry.HostName;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // DNS resolution failure is non-critical
                }
            }

            return new ScanResult
            {
                IpAddress = ip.ToString(),
                IsAlive = reply.Status == IPStatus.Success,
                ResponseTimeMs = reply.Status == IPStatus.Success ? (int)reply.RoundtripTime : 0,
                Hostname = hostname
            };
        }
        catch (PingException)
        {
            return new ScanResult
            {
                IpAddress = ip.ToString(),
                IsAlive = false,
                ResponseTimeMs = 0,
                Hostname = null
            };
        }
    }
}
