using HPPAQDeploy.Infrastructure.Hpia;

namespace HPPAQDeploy.Tests;

public class HpiaReportParserTests
{
    private readonly HpiaReportParser _parser = new();

    [Fact]
    public void ParseJsonReport_WithRecommendations_ExtractsCorrectly()
    {
        var dir = CreateTempDir();
        var json = """
        {
            "Recommendations": [
                {
                    "SoftPaqID": "sp123456",
                    "Name": "Intel UHD Graphics Driver",
                    "Version": "31.0.101.4502",
                    "Category": "Drivers",
                    "Severity": "Critical",
                    "Url": "https://ftp.hp.com/pub/sp123456.exe",
                    "Size": "512000"
                },
                {
                    "SoftPaqID": "sp789012",
                    "Name": "BIOS Update",
                    "Version": "F.70",
                    "Category": "BIOS",
                    "Severity": "Recommended"
                }
            ]
        }
        """;
        File.WriteAllText(Path.Combine(dir, "report.json"), json);

        var results = _parser.ParseReportDirectory(dir, deviceId: 42);

        Assert.Equal(2, results.Count);
        Assert.Equal("sp123456", results[0].SoftPaqId);
        Assert.Equal("Intel UHD Graphics Driver", results[0].Name);
        Assert.Equal("Critical", results[0].Severity);
        Assert.Equal(42, results[0].DeviceId);
        Assert.Equal(512000, results[0].SizeBytes);
        Assert.Equal("Driver", results[0].Category); // "Drivers" normalized to "Driver"
        Assert.Equal("sp789012", results[1].SoftPaqId);
        Assert.Equal("BIOS", results[1].Category);
    }

    [Fact]
    public void ParseJsonReport_HpiaStructure_ExtractsRecommendations()
    {
        var dir = CreateTempDir();
        var json = """
        {
            "HPIA": {
                "ExitCode": "0",
                "Recommendations": [
                    {
                        "SoftPaqID": "sp111111",
                        "Name": "Audio Driver",
                        "Category": "Drivers",
                        "Severity": "Optional"
                    }
                ]
            }
        }
        """;
        File.WriteAllText(Path.Combine(dir, "report.json"), json);

        var results = _parser.ParseReportDirectory(dir, deviceId: 1);

        Assert.Single(results);
        Assert.Equal("sp111111", results[0].SoftPaqId);
    }

    [Fact]
    public void ParseJsonReport_SolutionsStructure_Works()
    {
        var dir = CreateTempDir();
        var json = """
        {
            "Solutions": [
                {
                    "SoftPaqID": "sp222222",
                    "Name": "Network Driver",
                    "Category": "Drivers",
                    "Severity": "Recommended"
                }
            ]
        }
        """;
        File.WriteAllText(Path.Combine(dir, "report.json"), json);

        var results = _parser.ParseReportDirectory(dir, deviceId: 1);

        Assert.Single(results);
        Assert.Equal("sp222222", results[0].SoftPaqId);
    }

    [Fact]
    public void ParseJsonReport_ReleaseType_NormalizesToFriendlyName()
    {
        var dir = CreateTempDir();
        var json = """
        {
            "Recommendations": [
                {
                    "SoftPaqID": "sp100001",
                    "Name": "Routine Update",
                    "ReleaseType": "RELEASE_TYPE_ROUTINE"
                },
                {
                    "SoftPaqID": "sp100002",
                    "Name": "Critical Update",
                    "ReleaseType": "RELEASE_TYPE_CRITICAL"
                },
                {
                    "SoftPaqID": "sp100003",
                    "Name": "Recommended Update",
                    "ReleaseType": "RELEASE_TYPE_RECOMMENDED"
                }
            ]
        }
        """;
        File.WriteAllText(Path.Combine(dir, "report.json"), json);

        var results = _parser.ParseReportDirectory(dir, deviceId: 1);

        Assert.Equal(3, results.Count);
        Assert.Equal("Routine", results[0].Severity);
        Assert.Equal("Critical", results[1].Severity);
        Assert.Equal("Recommended", results[2].Severity);
    }

    [Fact]
    public void ParseJsonReport_SizeMB_ConvertsToBytes()
    {
        var dir = CreateTempDir();
        var json = """
        {
            "Recommendations": [
                {
                    "SoftPaqID": "sp200001",
                    "Name": "Large Driver",
                    "SizeMB": "123.45"
                }
            ]
        }
        """;
        File.WriteAllText(Path.Combine(dir, "report.json"), json);

        var results = _parser.ParseReportDirectory(dir, deviceId: 1);

        Assert.Single(results);
        Assert.Equal((long)(123.45 * 1024 * 1024), results[0].SizeBytes);
    }

    [Fact]
    public void ParseJsonReport_DownloadSize_ParsesBytes()
    {
        var dir = CreateTempDir();
        var json = """
        {
            "Recommendations": [
                {
                    "SoftPaqID": "sp300001",
                    "Name": "Small Utility",
                    "DownloadSize": "5242880"
                }
            ]
        }
        """;
        File.WriteAllText(Path.Combine(dir, "report.json"), json);

        var results = _parser.ParseReportDirectory(dir, deviceId: 1);

        Assert.Single(results);
        Assert.Equal(5242880, results[0].SizeBytes);
    }

    [Fact]
    public void ParseXmlReport_ExtractsRecommendations()
    {
        var dir = CreateTempDir();
        var xml = """
        <?xml version="1.0" encoding="utf-8"?>
        <HPIA>
            <Recommendations>
                <Drivers>
                    <Recommendation>
                        <TargetComponent>Intel Graphics</TargetComponent>
                        <Comments>Critical driver update</Comments>
                        <Solution>
                            <Softpaq>
                                <Id>sp333333</Id>
                                <Name>Intel Graphics Driver</Name>
                                <Version>30.0.100.1234</Version>
                                <Url>https://ftp.hp.com/sp333333.exe</Url>
                                <Size>104857600</Size>
                            </Softpaq>
                        </Solution>
                    </Recommendation>
                </Drivers>
            </Recommendations>
        </HPIA>
        """;
        File.WriteAllText(Path.Combine(dir, "report.xml"), xml);

        var results = _parser.ParseReportDirectory(dir, deviceId: 5);

        Assert.Single(results);
        Assert.Equal("sp333333", results[0].SoftPaqId);
        Assert.Equal("Driver", results[0].Category); // "Drivers" normalized to "Driver"
        Assert.Equal("Critical", results[0].Severity);
        Assert.Equal(104857600, results[0].SizeBytes);
    }

    [Fact]
    public void ParseReportDirectory_EmptyDir_ReturnsEmpty()
    {
        var dir = CreateTempDir();
        var results = _parser.ParseReportDirectory(dir, deviceId: 1);
        Assert.Empty(results);
    }

    [Fact]
    public void ParseReportDirectory_NonexistentDir_ReturnsEmpty()
    {
        var results = _parser.ParseReportDirectory(@"C:\nonexistent_path_12345", deviceId: 1);
        Assert.Empty(results);
    }

    [Theory]
    [InlineData("RELEASE_TYPE_ROUTINE", "Routine")]
    [InlineData("RELEASE_TYPE_CRITICAL", "Critical")]
    [InlineData("RELEASE_TYPE_RECOMMENDED", "Recommended")]
    [InlineData("Critical", "Critical")]
    [InlineData("Recommended", "Recommended")]
    [InlineData("Optional", "Optional")]
    [InlineData("", "Unknown")]
    [InlineData("  ", "Unknown")]
    public void NormalizeSeverity_MapsCorrectly(string input, string expected)
    {
        Assert.Equal(expected, HpiaReportParser.NormalizeSeverity(input));
    }

    [Theory]
    [InlineData("Drivers", "Driver")]
    [InlineData("Driver", "Driver")]
    [InlineData("BIOS", "BIOS")]
    [InlineData("bios", "BIOS")]
    [InlineData("Firmware", "Firmware")]
    [InlineData("Software", "Software")]
    [InlineData("Dock", "Dock")]
    [InlineData("", "Other")]
    [InlineData("  ", "Other")]
    public void NormalizeCategory_MapsCorrectly(string input, string expected)
    {
        Assert.Equal(expected, HpiaReportParser.NormalizeCategory(input));
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "HPPAQDeploy_Tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        return dir;
    }
}
