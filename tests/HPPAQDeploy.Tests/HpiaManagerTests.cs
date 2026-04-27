using System.Net;
using HPPAQDeploy.Core.Interfaces;
using HPPAQDeploy.Core.Models;
using HPPAQDeploy.Infrastructure.Hpia;
using HPPAQDeploy.Shared.Configuration;
using Moq;

namespace HPPAQDeploy.Tests;

public class HpiaManagerTests : IDisposable
{
    private readonly Mock<HpiaExtractor> _extractorMock;
    private readonly Mock<HpiaReportParser> _parserMock;
    private readonly Mock<IFileTransfer> _fileTransferMock;
    private readonly Mock<IRemoteExecutor> _remoteExecutorMock;
    private readonly HpiaManager _manager;
    private readonly NetworkCredential _cred = new("user", "pass", "domain");
    private readonly string _tempExtractPath;

    public HpiaManagerTests()
    {
        _extractorMock = new Mock<HpiaExtractor>();
        _parserMock = new Mock<HpiaReportParser>();
        _fileTransferMock = new Mock<IFileTransfer>();
        _remoteExecutorMock = new Mock<IRemoteExecutor>();

        _manager = new HpiaManager(
            _extractorMock.Object,
            _parserMock.Object,
            _fileTransferMock.Object,
            _remoteExecutorMock.Object);

        // Create temp extract path to satisfy directory existence checks
        _tempExtractPath = Path.Combine(Path.GetTempPath(), "HPPAQTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempExtractPath);
        AppSettings.HpiaExtractPath = _tempExtractPath;
        AppSettings.UseOfflineRepository = false;
        AppSettings.RepositorySharePath = "";

        // Mock extractor to be a no-op
        _extractorMock.Setup(e => e.ExtractAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempExtractPath))
            Directory.Delete(_tempExtractPath, true);
        AppSettings.UseOfflineRepository = false;
        AppSettings.RepositorySharePath = "";
    }

    [Fact]
    public async Task StageToRemoteAsync_CopiesHpiaToRemote()
    {
        await _manager.StageToRemoteAsync("stagehost1", _cred, CancellationToken.None);

        _fileTransferMock.Verify(f => f.CopyToRemoteAsync(
            "stagehost1",
            _cred,
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task StageToRemoteAsync_ThrowsIfNotExtracted()
    {
        AppSettings.HpiaExtractPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _manager.StageToRemoteAsync("stagehost2", _cred, CancellationToken.None));
    }

    [Fact]
    public async Task DeployUpdatesAsync_CallsRemoteExecutorWithInstall()
    {
        var device = new Device { Id = 1, Hostname = "deployhost1" };
        var recs = new List<HpiaRecommendation>
        {
            new() { SoftPaqId = "SP001", Name = "Test Update", DeviceId = 1 }
        };

        _remoteExecutorMock.Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<NetworkCredential>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<IProgress<string>?>()))
            .ReturnsAsync(new RemoteProcessResult(0, "", ""));

        await _manager.DeployUpdatesAsync(device, _cred, recs, new Progress<string>(), CancellationToken.None);

        _remoteExecutorMock.Verify(r => r.ExecuteAsync(
            "deployhost1",
            _cred,
            It.Is<string>(cmd => cmd.Contains("/Operation:Analyze") && cmd.Contains("/Action:Install")),
            It.IsAny<string>(),
            It.IsAny<TimeSpan>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<IProgress<string>?>()), Times.Once);
    }

    [Fact]
    public async Task RunAnalysisAsync_UsesOfflineMode_WhenRepositoryShareConfigured()
    {
        AppSettings.UseOfflineRepository = true;
        AppSettings.RepositorySharePath = @"\\server\share\Repository";

        var device = new Device { Id = 1, Hostname = "offlinehost1" };
        _remoteExecutorMock.Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<NetworkCredential>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<IProgress<string>?>()))
            .ReturnsAsync(new RemoteProcessResult(0, "", ""));

        await _manager.RunAnalysisAsync(device, _cred, CancellationToken.None);

        _remoteExecutorMock.Verify(r => r.ExecuteAsync(
            "offlinehost1",
            _cred,
            It.Is<string>(cmd => cmd.Contains(@"/Offlinemode:""\\server\share\Repository""")),
            It.IsAny<string>(),
            It.IsAny<TimeSpan>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<IProgress<string>?>()), Times.Once);
    }

    [Fact]
    public async Task DeployUpdatesAsync_ThrowsOnFailureExitCode()
    {
        var device = new Device { Id = 1, Hostname = "failhost1" };
        var recs = new List<HpiaRecommendation>
        {
            new() { SoftPaqId = "SP001", Name = "Test Update", DeviceId = 1 }
        };

        _remoteExecutorMock.Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<NetworkCredential>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<IProgress<string>?>()))
            .ReturnsAsync(new RemoteProcessResult(4096, "", "General failure"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _manager.DeployUpdatesAsync(device, _cred, recs, new Progress<string>(), CancellationToken.None));
    }

    [Fact]
    public async Task CleanupRemoteAsync_DeletesAndClearsStagingCache()
    {
        // First stage so the cache has an entry
        await _manager.StageToRemoteAsync("cleanhost1", _cred, CancellationToken.None);

        // Now clean up
        await _manager.CleanupRemoteAsync("cleanhost1", _cred, CancellationToken.None);

        _fileTransferMock.Verify(f => f.DeleteRemoteDirectoryAsync(
            "cleanhost1",
            _cred,
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeployUpdatesAsync_SetsRebootRequired_WhenExitCodeIndicates()
    {
        var device = new Device { Id = 1, Hostname = "reboothost1" };
        var recs = new List<HpiaRecommendation>
        {
            new() { SoftPaqId = "SP001", Name = "BIOS Update", DeviceId = 1 }
        };

        // Exit code 3010 = success + reboot required
        _remoteExecutorMock.Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<NetworkCredential>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<IProgress<string>?>()))
            .ReturnsAsync(new RemoteProcessResult(3010, "", ""));

        await _manager.DeployUpdatesAsync(device, _cred, recs, new Progress<string>(), CancellationToken.None);

        Assert.Equal(DeviceStatus.RebootRequired, device.Status);
        Assert.True(device.NeedsReboot);
    }
}
