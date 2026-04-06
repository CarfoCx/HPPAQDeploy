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
        var channel = Channel.CreateBounded<ScanResult>(new BoundedChannelOptions(1024)
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        var hosts = range.GetAllHosts().ToList();
        int total = hosts.Count;
        int completed = 0;

        _logger.Information("Starting ping sweep of {Range} ({Total} hosts, concurrency={Concurrency})",
            range.Network, total, maxConcurrency);

        var producerTask = Task.Run(async () =>
        {
            var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
            var tasks = new List<Task>();

            foreach (var ip in hosts)
            {
                ct.ThrowIfCancellationRequested();
                await semaphore.WaitAsync(ct).ConfigureAwait(false);

                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var result = await PingHostAsync(ip, ct).ConfigureAwait(false);
                        await channel.Writer.WriteAsync(result, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        semaphore.Release();
                        var current = Interlocked.Increment(ref completed);
                        progress?.Report((current, total));
                    }
                }, ct));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            channel.Writer.Complete();
        }, ct);

        await foreach (var result in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return result;
        }

        await producerTask.ConfigureAwait(false);
    }

    private async Task<ScanResult> PingHostAsync(IPAddress ip, CancellationToken ct)
    {
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(ip, AppSettings.PingTimeoutMs).ConfigureAwait(false);

            string? hostname = null;
            if (reply.Status == IPStatus.Success)
            {
                try
                {
                    var hostEntry = await Dns.GetHostEntryAsync(ip).ConfigureAwait(false);
                    hostname = hostEntry.HostName;
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
