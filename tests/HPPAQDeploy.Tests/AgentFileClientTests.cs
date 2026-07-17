using System.Net;
using System.Text.Json;
using HPPAQDeploy.Core.Models;
using HPPAQDeploy.Core.Interfaces;
using HPPAQDeploy.Infrastructure.Agent;
using Moq;

namespace HPPAQDeploy.Tests;

public sealed class AgentFileClientTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hppaq-agent-tests-{Guid.NewGuid():N}");
    private static readonly NetworkCredential TestCredential = new("operator", "password");

    [Fact]
    public async Task TryGetResultAsync_IncompleteJsonIsRetainedAndCanBeReadLater()
    {
        var jobsPath = Path.Combine(_root, "jobs");
        var resultsPath = Path.Combine(_root, "results");
        Directory.CreateDirectory(resultsPath);
        var client = CreateClient(jobsPath, resultsPath);
        var resultPath = Path.Combine(resultsPath, "job1.json");
        await File.WriteAllTextAsync(resultPath, "{\"jobId\":");

        Assert.Null(await client.TryGetResultAsync("host1", TestCredential, "job1", CancellationToken.None));
        Assert.True(File.Exists(resultPath));

        var expected = new AgentJobResult { JobId = "job1", Status = AgentJobStatus.Succeeded };
        await File.WriteAllTextAsync(
            resultPath,
            JsonSerializer.Serialize(expected, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var actual = await client.TryGetResultAsync("host1", TestCredential, "job1", CancellationToken.None);
        Assert.NotNull(actual);
        Assert.Equal(AgentJobStatus.Succeeded, actual.Status);
    }

    [Fact]
    public async Task SubmitScanAsync_PublishesFinalFileAndDoesNotOverwriteJobId()
    {
        var jobsPath = Path.Combine(_root, "jobs");
        var resultsPath = Path.Combine(_root, "results");
        var client = CreateClient(jobsPath, resultsPath);
        var job = new AgentJob { Id = "stable_job" };

        Assert.Equal("stable_job", await client.SubmitScanAsync("host1", TestCredential, job, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(jobsPath, "stable_job.json")));
        Assert.Empty(Directory.GetFiles(jobsPath, "*.tmp"));

        await Assert.ThrowsAsync<IOException>(() =>
            client.SubmitScanAsync("host1", TestCredential, new AgentJob { Id = "stable_job" }, CancellationToken.None));
    }

    [Fact]
    public async Task SubmitScanAsync_UsesAndReleasesSelectedCredentialSession()
    {
        var jobsPath = Path.Combine(_root, "jobs");
        var resultsPath = Path.Combine(_root, "results");
        var session = new Mock<IRemoteFileSession>();
        session.Setup(item => item.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var transfer = new Mock<IFileTransfer>();
        transfer
            .Setup(item => item.OpenAuthenticatedSessionAsync("host1", TestCredential, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session.Object);
        var client = new AgentFileClient(transfer.Object, _ => jobsPath, _ => resultsPath);

        await client.SubmitScanAsync(
            "host1",
            TestCredential,
            new AgentJob { Id = "authenticated_job" },
            CancellationToken.None);

        transfer.Verify(item => item.OpenAuthenticatedSessionAsync(
            "host1", TestCredential, It.IsAny<CancellationToken>()), Times.Once);
        session.Verify(item => item.DisposeAsync(), Times.Once);
    }

    [Theory]
    [InlineData("../job")]
    [InlineData("job.json")]
    [InlineData("job\\child")]
    public async Task JobId_PathTraversalIsRejected(string jobId)
    {
        var client = CreateClient(Path.Combine(_root, "jobs"), Path.Combine(_root, "results"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.TryGetResultAsync("host1", TestCredential, jobId, CancellationToken.None));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("server\\share")]
    [InlineData("server:123")]
    public async Task Hostname_PathInjectionIsRejected(string hostname)
    {
        var client = CreateClient(Path.Combine(_root, "jobs"), Path.Combine(_root, "results"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.TryGetResultAsync(hostname, TestCredential, "job1", CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static AgentFileClient CreateClient(string jobsPath, string resultsPath) =>
        new(_ => jobsPath, _ => resultsPath);
}
