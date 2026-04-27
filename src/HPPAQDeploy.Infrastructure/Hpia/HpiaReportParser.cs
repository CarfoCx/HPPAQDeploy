using System.Text.Json;
using System.Xml.Linq;
using HPPAQDeploy.Core.Models;
using Serilog;

namespace HPPAQDeploy.Infrastructure.Hpia;

/// <summary>
/// Parses HPIA report files (JSON or XML) to extract software/driver recommendations.
/// </summary>
public class HpiaReportParser
{
    private readonly ILogger _logger = Log.ForContext<HpiaReportParser>();

    /// <summary>
    /// Parses HPIA report(s) from a directory and extracts recommendations.
    /// HPIA 5.x generates JSON reports by default.
    /// </summary>
    public virtual List<HpiaRecommendation> ParseReportDirectory(string reportDirectory, int deviceId)
    {
        var recommendations = new List<HpiaRecommendation>();

        if (!Directory.Exists(reportDirectory))
        {
            _logger.Warning("Report directory does not exist: {Directory}", reportDirectory);
            return recommendations;
        }

        // HPIA 5.x generates JSON reports
        var reportFiles = Directory.GetFiles(reportDirectory, "*.json", SearchOption.AllDirectories);

        // Fallback to XML for older HPIA versions
        if (reportFiles.Length == 0)
            reportFiles = Directory.GetFiles(reportDirectory, "*.xml", SearchOption.AllDirectories);

        if (reportFiles.Length == 0)
        {
            _logger.Warning("No report files found in {Directory}", reportDirectory);
            return recommendations;
        }

        foreach (var reportFile in reportFiles)
        {
            try
            {
                _logger.Information("Parsing HPIA report: {ReportFile}", reportFile);

                if (reportFile.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    recommendations.AddRange(ParseJsonReport(reportFile, deviceId));
                else
                    recommendations.AddRange(ParseXmlReport(reportFile, deviceId));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to parse HPIA report: {ReportFile}", reportFile);
            }
        }

        _logger.Information("Parsed {Count} recommendations from {Directory}",
            recommendations.Count, reportDirectory);

        return recommendations;
    }

    /// <summary>
    /// Parses a JSON report file from HPIA 5.x.
    /// </summary>
    private List<HpiaRecommendation> ParseJsonReport(string filePath, int deviceId)
    {
        var recommendations = new List<HpiaRecommendation>();
        var json = File.ReadAllText(filePath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Check for HPIA error status
        if (root.TryGetProperty("HPIA", out var hpiaNode))
        {
            var exitCode = GetJsonString(hpiaNode, "ExitCode");
            var lastOp = GetJsonString(hpiaNode, "LastOperation");
            var lastStatus = GetJsonString(hpiaNode, "LastOperationStatus");

            if (exitCode != null && exitCode != "0")
            {
                _logger.Warning("HPIA internal exit code {ExitCode}: {LastOp} - {Status}",
                    exitCode, lastOp, lastStatus);

                if (exitCode == "4098")
                    _logger.Warning("HPIA exit 4098: Platform not supported or unable to reach HP.com for reference catalog. " +
                        "Ensure remote machines have internet access or use offline repository mode.");
            }
        }

        // Look for recommendations in various JSON structures HPIA may produce
        // Structure 1: root.Recommendations[] array
        if (root.TryGetProperty("Recommendations", out var recsArray) && recsArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var rec in recsArray.EnumerateArray())
                TryAddJsonRecommendation(rec, deviceId, recommendations);
        }

        // Structure 2: root.HPIA.Recommendations[]
        if (root.TryGetProperty("HPIA", out var hpia2) &&
            hpia2.TryGetProperty("Recommendations", out var hpiaRecs) &&
            hpiaRecs.ValueKind == JsonValueKind.Array)
        {
            foreach (var rec in hpiaRecs.EnumerateArray())
                TryAddJsonRecommendation(rec, deviceId, recommendations);
        }

        // Structure 3: root.Solutions[] (some HPIA versions)
        if (root.TryGetProperty("Solutions", out var solutions) && solutions.ValueKind == JsonValueKind.Array)
        {
            foreach (var rec in solutions.EnumerateArray())
                TryAddJsonRecommendation(rec, deviceId, recommendations);
        }

        // Deep search: find any array property that contains objects with SoftPaqID
        if (recommendations.Count == 0)
        {
            SearchForRecommendations(root, deviceId, recommendations);
        }

        return recommendations;
    }

    private void SearchForRecommendations(JsonElement element, int deviceId, List<HpiaRecommendation> results)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in prop.Value.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object &&
                            (item.TryGetProperty("SoftPaqID", out _) ||
                             item.TryGetProperty("SoftpaqID", out _) ||
                             item.TryGetProperty("Id", out _)))
                        {
                            TryAddJsonRecommendation(item, deviceId, results);
                        }
                    }
                }
                else if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    SearchForRecommendations(prop.Value, deviceId, results);
                }
            }
        }
    }

    private void TryAddJsonRecommendation(JsonElement rec, int deviceId, List<HpiaRecommendation> results)
    {
        var softPaqId = GetJsonString(rec, "SoftPaqID", "SoftpaqID", "softpaqId", "Id", "id") ?? string.Empty;
        if (string.IsNullOrEmpty(softPaqId)) return;

        var rawSeverity = GetJsonString(rec, "Severity", "severity", "ReleaseType", "Importance", "importance") ?? string.Empty;
        var rawCategory = GetJsonString(rec, "Category", "category", "CategoryType", "categoryType", "Type", "type", "SSMCompliance", "DeviceClass") ?? string.Empty;

        // Try SizeMB first (HPIA 5.x often reports size in MB), then fall back to byte-based fields
        var sizeMbStr = GetJsonString(rec, "SizeMB", "sizeMB", "SizeMb");
        long sizeBytes;
        if (sizeMbStr != null && double.TryParse(sizeMbStr, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var mb))
        {
            sizeBytes = (long)(mb * 1024 * 1024);
        }
        else
        {
            sizeBytes = ParseFileSize(GetJsonString(rec, "Size", "size", "DownloadSize", "downloadSize", "FileSize"));
        }

        var name = GetJsonString(rec, "Name", "name", "Title", "title") ?? string.Empty;
        var category = !string.IsNullOrEmpty(rawCategory) ? NormalizeCategory(rawCategory) : InferCategoryFromName(name);

        // Check if HPIA can install this silently (SSMCompliant = Silent System Management)
        // DPBCompliant is about HP's Device Policy Block tool and does NOT affect remote install capability
        var ssmCompliant = GetJsonString(rec, "SSMCompliant", "ssmCompliant");
        var silentInstallable = ssmCompliant == null || ssmCompliant.Equals("True", StringComparison.OrdinalIgnoreCase);

        results.Add(new HpiaRecommendation
        {
            DeviceId = deviceId,
            SoftPaqId = softPaqId,
            Name = name,
            Version = GetJsonString(rec, "Version", "version", "ReleaseVersion") ?? string.Empty,
            Category = category,
            Severity = NormalizeSeverity(rawSeverity),
            SilentInstallable = silentInstallable,
            DownloadUrl = GetJsonString(rec, "Url", "url", "DownloadUrl", "ReleaseNotesUrl") ?? string.Empty,
            SizeBytes = sizeBytes,
            DeployState = DeploymentState.Pending
        });
    }

    private static string? GetJsonString(JsonElement element, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (element.TryGetProperty(name, out var prop))
            {
                var val = prop.ValueKind == JsonValueKind.Number
                    ? prop.GetRawText()
                    : prop.GetString();
                if (!string.IsNullOrWhiteSpace(val))
                    return val.Trim();
            }
        }
        return null;
    }

    /// <summary>
    /// Parses a single HPIA XML report file.
    /// HPIA XML structure: HPIA > Recommendations > [CategoryName] > Recommendation > Solution > Softpaq
    /// </summary>
    private List<HpiaRecommendation> ParseXmlReport(string filePath, int deviceId)
    {
        var recommendations = new List<HpiaRecommendation>();
        var doc = XDocument.Load(filePath);

        // HPIA report: Recommendations > CategoryGroup > Recommendation > Solution > Softpaq
        var recsRoot = doc.Descendants("Recommendations").FirstOrDefault();
        if (recsRoot == null)
        {
            _logger.Warning("No <Recommendations> element found in {File}", filePath);
            return recommendations;
        }

        // Category groups: Drivers, Software, BIOS, Firmware, Accessories
        foreach (var categoryGroup in recsRoot.Elements())
        {
            var category = categoryGroup.Name.LocalName; // e.g., "Drivers", "Software"

            foreach (var recElem in categoryGroup.Elements("Recommendation"))
            {
                try
                {
                    var softpaq = recElem.Element("Solution")?.Element("Softpaq");
                    if (softpaq == null) continue;

                    var softpaqId = softpaq.Element("Id")?.Value?.Trim() ?? string.Empty;
                    if (string.IsNullOrEmpty(softpaqId)) continue;

                    var comments = recElem.Element("Comments")?.Value ?? string.Empty;
                    var severity = comments.Contains("Critical", StringComparison.OrdinalIgnoreCase) ? "Critical"
                        : comments.Contains("recommended", StringComparison.OrdinalIgnoreCase) ? "Recommended"
                        : "Optional";

                    var sizeStr = softpaq.Element("Size")?.Value?.Trim();
                    long xmlSizeBytes = 0;
                    if (!string.IsNullOrEmpty(sizeStr))
                        xmlSizeBytes = ParseFileSize(sizeStr);

                    recommendations.Add(new HpiaRecommendation
                    {
                        DeviceId = deviceId,
                        SoftPaqId = softpaqId,
                        Name = softpaq.Element("Name")?.Value?.Trim() ?? recElem.Element("TargetComponent")?.Value?.Trim() ?? string.Empty,
                        Version = softpaq.Element("Version")?.Value?.Trim() ?? string.Empty,
                        Category = NormalizeCategory(category),
                        Severity = severity,
                        DownloadUrl = softpaq.Element("Url")?.Value?.Trim() ?? string.Empty,
                        SizeBytes = xmlSizeBytes,
                        DeployState = DeploymentState.Pending
                    });
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to parse recommendation element");
                }
            }
        }

        return recommendations;
    }

    private static string? GetXmlValue(XElement parent, params string[] elementNames)
    {
        foreach (var name in elementNames)
        {
            var element = parent.Element(name);
            if (element != null && !string.IsNullOrWhiteSpace(element.Value))
                return element.Value.Trim();

            var attribute = parent.Attribute(name);
            if (attribute != null && !string.IsNullOrWhiteSpace(attribute.Value))
                return attribute.Value.Trim();
        }
        return null;
    }

    private static long ParseFileSize(string? sizeString)
    {
        if (string.IsNullOrWhiteSpace(sizeString))
            return 0;

        if (long.TryParse(sizeString.Trim(), out var bytes))
            return bytes;

        var parts = sizeString.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && double.TryParse(parts[0],
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return parts[1].ToUpperInvariant() switch
            {
                "KB" => (long)(value * 1024),
                "MB" => (long)(value * 1024 * 1024),
                "GB" => (long)(value * 1024 * 1024 * 1024),
                _ => 0
            };
        }

        return 0;
    }

    /// <summary>
    /// Maps raw HPIA ReleaseType / Severity values to user-friendly names.
    /// HPIA 5.x uses values like RELEASE_TYPE_ROUTINE, RELEASE_TYPE_CRITICAL, etc.
    /// </summary>
    internal static string NormalizeSeverity(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Unknown";

        // Normalize: strip prefix, lowercase compare
        var key = raw.Trim().Replace("RELEASE_TYPE_", "", StringComparison.OrdinalIgnoreCase)
                     .Replace("_", " ");

        return key.ToLowerInvariant() switch
        {
            "critical" => "Critical",
            "recommended" => "Recommended",
            "routine" => "Routine",
            "optional" => "Optional",
            // Already friendly — pass through capitalized
            _ when raw.Equals("Critical", StringComparison.OrdinalIgnoreCase) => "Critical",
            _ when raw.Equals("Recommended", StringComparison.OrdinalIgnoreCase) => "Recommended",
            _ when raw.Equals("Optional", StringComparison.OrdinalIgnoreCase) => "Optional",
            _ when raw.Equals("Routine", StringComparison.OrdinalIgnoreCase) => "Routine",
            _ => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(key.ToLowerInvariant())
        };
    }

    /// <summary>
    /// Normalizes HPIA category strings to consistent display names.
    /// </summary>
    /// <summary>
    /// Infers the update category from the SoftPaq name when HPIA doesn't provide an explicit category.
    /// </summary>
    internal static string InferCategoryFromName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Other";

        var lower = name.ToLowerInvariant();

        // BIOS
        if (lower.Contains("bios") || lower.Contains("system firmware"))
            return "BIOS";

        // Firmware (check before driver since some firmware names contain "driver"-like words)
        if (lower.Contains("firmware") || lower.Contains("thunderbolt") && lower.Contains("controller"))
            return "Firmware";

        // Dock-related
        if (lower.Contains("dock") || lower.Contains("usb-c g5") || lower.Contains("usb-c dock"))
            return "Dock";

        // Driver keywords
        if (lower.Contains("driver") || lower.Contains("wlan") || lower.Contains("wifi") ||
            lower.Contains("bluetooth") || lower.Contains("ethernet") || lower.Contains("lan ") ||
            lower.Contains("audio") || lower.Contains("graphics") || lower.Contains("gpu") ||
            lower.Contains("chipset") || lower.Contains("touchpad") || lower.Contains("card reader") ||
            lower.Contains("fingerprint") || lower.Contains("ir camera") || lower.Contains("sensor") ||
            lower.Contains("serial port") || lower.Contains("usb ") || lower.Contains("nfc"))
            return "Driver";

        // Software / Management
        if (lower.Contains("hotkey") || lower.Contains("support assistant") ||
            lower.Contains("notifications") || lower.Contains("client security") ||
            lower.Contains("manageability") || lower.Contains("client management") ||
            lower.Contains("sure ") || lower.Contains("wolf "))
            return "Software";

        // Utility / Framework
        if (lower.Contains("framework") || lower.Contains("platform") || lower.Contains("utility") ||
            lower.Contains("diagnostic") || lower.Contains("power manager"))
            return "Utility";

        return "Driver"; // Default to Driver since most HPIA recommendations are driver updates
    }

    internal static string NormalizeCategory(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Other";

        var lower = raw.Trim().ToLowerInvariant();

        // Exact matches first
        var result = lower switch
        {
            "driver" or "drivers" => "Driver",
            "bios" => "BIOS",
            "firmware" => "Firmware",
            "software" => "Software",
            "dock" or "docking" => "Dock",
            "accessories" or "accessory" => "Accessory",
            "utility" or "utilities" => "Utility",
            "diagnostic" or "diagnostics" => "Diagnostic",
            "manageability" => "Manageability",
            _ => null
        };
        if (result != null) return result;

        // Partial/prefix matches for HPIA compound categories like "Driver - Network"
        if (lower.StartsWith("driver")) return "Driver";
        if (lower.StartsWith("bios")) return "BIOS";
        if (lower.StartsWith("firmware")) return "Firmware";
        if (lower.StartsWith("software")) return "Software";
        if (lower.StartsWith("dock")) return "Dock";
        if (lower.StartsWith("utility") || lower.StartsWith("diagnostic")) return "Utility";
        if (lower.Contains("driver")) return "Driver";
        if (lower.Contains("bios")) return "BIOS";
        if (lower.Contains("firmware")) return "Firmware";

        return System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(lower);
    }
}
