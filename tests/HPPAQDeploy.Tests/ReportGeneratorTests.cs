using HPPAQDeploy.Core.Models;
using HPPAQDeploy.Shared.Helpers;

namespace HPPAQDeploy.Tests;

public sealed class ReportGeneratorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hppaq-report-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task GenerateCsvReport_NeutralizesFormulasAndQuotesNewlines()
    {
        var path = Path.Combine(_root, "report.csv");
        await ReportGenerator.GenerateCsvReport(
        [
            new Device
            {
                Hostname = "=cmd|' /C calc'!A0",
                IpAddress = "10.0.0.1\r\nforged,row"
            }
        ], path);

        var csv = await File.ReadAllTextAsync(path);
        Assert.Contains("'=cmd|' /C calc'!A0", csv);
        Assert.Contains("\"10.0.0.1\r\nforged,row\"", csv);
    }

    [Fact]
    public async Task GenerateHtmlReport_NeverAnalyzedDevicesAreNotCompliant()
    {
        var path = Path.Combine(_root, "report.html");
        await ReportGenerator.GenerateHtmlReport(
            [new Device { Hostname = "new-host", LastAnalyzed = null }],
            path);

        var html = await File.ReadAllTextAsync(path);
        Assert.Contains("<div class=\"card-value compliance\">0%</div>", html);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
