using System.Net;
using HPPAQDeploy.Core.Interfaces;
using HPPAQDeploy.Shared.Configuration;
using Serilog;

namespace HPPAQDeploy.Infrastructure.Agent;

public sealed class AgentBootstrapper : IAgentBootstrapper
{
    private const string RemoteRoot = @"C:\ProgramData\HPPAQDeploy";
    private const string RemoteAgentPath = RemoteRoot + @"\Agent";
    private const string RemoteHpiaPath = RemoteRoot + @"\HPIA";
    private const string ServiceName = "HPPAQDeployAgent";

    private readonly IFileTransfer _fileTransfer;
    private readonly IRemoteExecutor _remoteExecutor;
    private readonly ILogger _logger = Log.ForContext<AgentBootstrapper>();

    public AgentBootstrapper(IFileTransfer fileTransfer, IRemoteExecutor remoteExecutor)
    {
        _fileTransfer = fileTransfer;
        _remoteExecutor = remoteExecutor;
    }

    public async Task BootstrapAsync(string hostname, NetworkCredential credential, CancellationToken ct)
    {
        var localAgentPath = ResolveLocalAgentPath();
        var localHpiaPath = AppSettings.HpiaExtractPath;

        if (!Directory.Exists(localAgentPath))
            throw new DirectoryNotFoundException($"Agent build output not found at {localAgentPath}.");

        if (!File.Exists(Path.Combine(localHpiaPath, "HPImageAssistant.exe")))
            throw new FileNotFoundException($"HPIA is not extracted at {localHpiaPath}. Extract HPIA before bootstrapping agents.");

        _logger.Information("Bootstrapping HPPAQDeploy agent on {Hostname}", hostname);

        await _remoteExecutor.ExecuteAsync(
            hostname,
            credential,
            $"cmd /c mkdir \"{RemoteRoot}\" \"{RemoteRoot}\\jobs\" \"{RemoteRoot}\\results\" \"{RemoteRoot}\\logs\" 2>nul",
            null,
            TimeSpan.FromSeconds(30),
            ct);

        await _fileTransfer.CopyToRemoteAsync(hostname, credential, localAgentPath, RemoteAgentPath, ct);
        await _fileTransfer.CopyToRemoteAsync(hostname, credential, localHpiaPath, RemoteHpiaPath, ct);

        var agentExe = RemoteAgentPath + @"\HPPAQDeploy.Agent.exe";
        var installCommand =
            $"cmd /c sc query \"{ServiceName}\" >nul 2>&1 " +
            $"&& sc config \"{ServiceName}\" binPath= \"\\\"{agentExe}\\\" watch\" start= auto " +
            $"|| sc create \"{ServiceName}\" binPath= \"\\\"{agentExe}\\\" watch\" start= auto DisplayName= \"HPPAQDeploy Agent\"";

        await _remoteExecutor.ExecuteAsync(
            hostname,
            credential,
            installCommand,
            null,
            TimeSpan.FromSeconds(30),
            ct);

        await _remoteExecutor.ExecuteAsync(
            hostname,
            credential,
            $"cmd /c sc start \"{ServiceName}\" || sc query \"{ServiceName}\" | find /i \"RUNNING\"",
            null,
            TimeSpan.FromSeconds(30),
            ct);

        _logger.Information("HPPAQDeploy agent bootstrap completed on {Hostname}", hostname);
    }

    private static string ResolveLocalAgentPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Agent"),
            Path.Combine(AppContext.BaseDirectory, "..", "src", "HPPAQDeploy.Agent", "bin", "Debug", "net8.0-windows"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "src", "HPPAQDeploy.Agent", "bin", "Debug", "net8.0-windows")
        };

        return candidates
            .Select(Path.GetFullPath)
            .FirstOrDefault(path => File.Exists(Path.Combine(path, "HPPAQDeploy.Agent.exe")))
            ?? Path.GetFullPath(candidates[0]);
    }
}
