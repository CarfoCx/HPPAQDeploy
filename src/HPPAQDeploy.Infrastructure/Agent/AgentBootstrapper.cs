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
    private const string AgentTaskName = "HPPAQDeployAgent";

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

        await _remoteExecutor.ExecuteAsync(
            hostname,
            credential,
            $"cmd /c sc stop \"{AgentTaskName}\" >nul 2>&1 & sc delete \"{AgentTaskName}\" >nul 2>&1 & exit /b 0",
            null,
            TimeSpan.FromSeconds(30),
            ct);

        var agentExe = RemoteAgentPath + @"\HPPAQDeploy.Agent.exe";
        var createTaskCommand =
            $"cmd /c schtasks /Create /TN \"{AgentTaskName}\" " +
            $"/TR \"{agentExe} run-once\" /SC ONSTART /RU SYSTEM /RL HIGHEST /F";

        await _remoteExecutor.ExecuteAsync(
            hostname,
            credential,
            createTaskCommand,
            null,
            TimeSpan.FromSeconds(30),
            ct);

        _logger.Information("HPPAQDeploy agent bootstrap completed on {Hostname}", hostname);
    }

    public async Task RunOnceAsync(string hostname, NetworkCredential credential, CancellationToken ct)
    {
        _logger.Information("Starting HPPAQDeploy agent task on {Hostname}", hostname);

        await _remoteExecutor.ExecuteAsync(
            hostname,
            credential,
            $"cmd /c schtasks /Run /TN \"{AgentTaskName}\"",
            null,
            TimeSpan.FromSeconds(30),
            ct);
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
