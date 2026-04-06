using System.Text;
using System.Web;
using HPPAQDeploy.Core.Models;
using Serilog;

namespace HPPAQDeploy.Shared.Helpers;

public static class ReportGenerator
{
    public static async Task GenerateCsvReport(IEnumerable<Device> devices, string outputPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Hostname,IP Address,Manufacturer,Model,Serial Number,Product ID,Group,Status,OS Version,BIOS Version,Last Scanned,Last Analyzed,Pending Updates,Critical Updates,Update Names");

        foreach (var d in devices)
        {
            var recommendations = d.Recommendations?.ToList() ?? [];
            var criticalCount = recommendations.Count(r =>
                r.Severity?.Equals("Critical", StringComparison.OrdinalIgnoreCase) == true);
            var updateNames = string.Join("; ", recommendations.Select(r => r.Name));

            sb.AppendLine(string.Join(",",
                Esc(d.Hostname),
                Esc(d.IpAddress),
                Esc(d.Manufacturer),
                Esc(d.Model),
                Esc(d.SerialNumber),
                Esc(d.ProductId),
                Esc(d.GroupName ?? ""),
                d.Status.ToString(),
                Esc(d.OsVersion),
                Esc(d.BiosVersion),
                d.LastScanned.ToString("yyyy-MM-dd HH:mm"),
                d.LastAnalyzed?.ToString("yyyy-MM-dd HH:mm") ?? "",
                recommendations.Count,
                criticalCount,
                Esc(updateNames)));
        }

        try
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (dir != null) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(outputPath, sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to write CSV report to {OutputPath}", outputPath);
            throw;
        }
    }

    public static async Task GenerateHtmlReport(IEnumerable<Device> devices, string outputPath)
    {
        var deviceList = devices.ToList();
        var totalDevices = deviceList.Count;
        var devicesUpToDate = deviceList.Count(d => (d.Recommendations?.Count ?? 0) == 0);
        var devicesWithUpdates = deviceList.Count(d => (d.Recommendations?.Count ?? 0) > 0);
        var compliancePercent = totalDevices > 0
            ? Math.Round((double)devicesUpToDate / totalDevices * 100, 1)
            : 0;

        var allRecommendations = deviceList.SelectMany(d => d.Recommendations ?? []).ToList();
        var criticalCount = allRecommendations
            .Count(r => r.Severity?.Equals("Critical", StringComparison.OrdinalIgnoreCase) == true);
        var devicesWithCritical = deviceList.Count(d =>
            d.Recommendations?.Any(r => r.Severity?.Equals("Critical", StringComparison.OrdinalIgnoreCase) == true) == true);

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"UTF-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine("<title>HPPAQDeploy Compliance Report</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(GetReportCss());
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        // Header
        sb.AppendLine("<div class=\"header\">");
        sb.AppendLine("<h1>HPPAQDeploy Compliance Report</h1>");
        sb.AppendLine($"<p class=\"generated\">Generated on {DateTime.UtcNow:MMMM dd, yyyy} at {DateTime.UtcNow:HH:mm:ss} UTC</p>");
        sb.AppendLine("</div>");

        // Summary cards
        sb.AppendLine("<div class=\"summary\">");
        sb.AppendLine($"<div class=\"card\"><div class=\"card-value\">{totalDevices}</div><div class=\"card-label\">Total Devices</div></div>");
        sb.AppendLine($"<div class=\"card\"><div class=\"card-value compliance\">{compliancePercent}%</div><div class=\"card-label\">Compliant</div></div>");
        sb.AppendLine($"<div class=\"card\"><div class=\"card-value warning\">{devicesWithUpdates}</div><div class=\"card-label\">Need Updates</div></div>");
        sb.AppendLine($"<div class=\"card\"><div class=\"card-value critical\">{devicesWithCritical}</div><div class=\"card-label\">Critical</div></div>");
        sb.AppendLine("</div>");

        // Device table
        sb.AppendLine("<h2>Device Overview</h2>");
        sb.AppendLine("<table>");
        sb.AppendLine("<thead><tr>");
        sb.AppendLine("<th>Hostname</th><th>IP Address</th><th>Model</th><th>Status</th>");
        sb.AppendLine("<th>OS Version</th><th>BIOS Version</th><th>Last Scanned</th>");
        sb.AppendLine("<th>Pending</th><th>Critical</th>");
        sb.AppendLine("</tr></thead>");
        sb.AppendLine("<tbody>");

        foreach (var d in deviceList)
        {
            var recs = d.Recommendations?.ToList() ?? [];
            var devCritical = recs.Count(r =>
                r.Severity?.Equals("Critical", StringComparison.OrdinalIgnoreCase) == true);
            var statusClass = GetStatusClass(d.Status, recs.Count, devCritical);

            sb.AppendLine("<tr>");
            sb.AppendLine($"<td>{H(d.Hostname)}</td>");
            sb.AppendLine($"<td>{H(d.IpAddress)}</td>");
            sb.AppendLine($"<td>{H(d.Model)}</td>");
            sb.AppendLine($"<td class=\"{statusClass}\">{d.Status}</td>");
            sb.AppendLine($"<td>{H(d.OsVersion)}</td>");
            sb.AppendLine($"<td>{H(d.BiosVersion)}</td>");
            sb.AppendLine($"<td>{d.LastScanned:yyyy-MM-dd HH:mm}</td>");
            sb.AppendLine($"<td>{recs.Count}</td>");
            sb.AppendLine($"<td class=\"{(devCritical > 0 ? "critical" : "")}\">{devCritical}</td>");
            sb.AppendLine("</tr>");
        }

        sb.AppendLine("</tbody></table>");

        // Per-device update details
        var devicesWithRecs = deviceList.Where(d => (d.Recommendations?.Count ?? 0) > 0).ToList();
        if (devicesWithRecs.Count > 0)
        {
            sb.AppendLine("<h2>Pending Updates by Device</h2>");

            foreach (var d in devicesWithRecs)
            {
                var recs = d.Recommendations!.ToList();
                sb.AppendLine($"<div class=\"device-section\">");
                sb.AppendLine($"<h3>{H(d.Hostname)} <span class=\"model\">({H(d.Model)})</span></h3>");
                sb.AppendLine("<table class=\"updates-table\">");
                sb.AppendLine("<thead><tr><th>SoftPaq</th><th>Name</th><th>Category</th><th>Severity</th><th>Version</th></tr></thead>");
                sb.AppendLine("<tbody>");

                foreach (var r in recs.OrderByDescending(r => r.Severity == "Critical").ThenBy(r => r.Name))
                {
                    var sevClass = r.Severity?.ToLowerInvariant() switch
                    {
                        "critical" => "critical",
                        "recommended" => "warning",
                        _ => ""
                    };
                    sb.AppendLine("<tr>");
                    sb.AppendLine($"<td>{H(r.SoftPaqId)}</td>");
                    sb.AppendLine($"<td>{H(r.Name)}</td>");
                    sb.AppendLine($"<td>{H(r.Category)}</td>");
                    sb.AppendLine($"<td class=\"{sevClass}\">{H(r.Severity)}</td>");
                    sb.AppendLine($"<td>{H(r.Version)}</td>");
                    sb.AppendLine("</tr>");
                }

                sb.AppendLine("</tbody></table>");
                sb.AppendLine("</div>");
            }
        }

        // Footer
        sb.AppendLine("<div class=\"footer\">");
        sb.AppendLine($"<p>HPPAQDeploy &mdash; {totalDevices} devices &bull; {allRecommendations.Count} pending updates &bull; {criticalCount} critical</p>");
        sb.AppendLine("</div>");

        sb.AppendLine("</body></html>");

        try
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (dir != null) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(outputPath, sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to write HTML report to {OutputPath}", outputPath);
            throw;
        }
    }

    private static string GetReportCss()
    {
        return """
            * { margin: 0; padding: 0; box-sizing: border-box; }
            body {
                font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif;
                background: #f5f6fa;
                color: #2d3436;
                padding: 24px;
                max-width: 1200px;
                margin: 0 auto;
            }
            .header {
                background: linear-gradient(135deg, #0096D6 0%, #004d80 100%);
                color: white;
                padding: 32px;
                border-radius: 12px;
                margin-bottom: 24px;
            }
            .header h1 { font-size: 28px; font-weight: 700; }
            .header .generated { margin-top: 8px; opacity: 0.85; font-size: 14px; }
            .summary {
                display: grid;
                grid-template-columns: repeat(4, 1fr);
                gap: 16px;
                margin-bottom: 32px;
            }
            .card {
                background: white;
                border-radius: 10px;
                padding: 24px;
                text-align: center;
                box-shadow: 0 2px 8px rgba(0,0,0,0.08);
                border-top: 4px solid #0096D6;
            }
            .card-value {
                font-size: 36px;
                font-weight: 700;
                color: #0096D6;
            }
            .card-value.compliance { color: #00b894; }
            .card-value.warning { color: #e17055; }
            .card-value.critical { color: #d63031; }
            .card-label {
                font-size: 13px;
                color: #636e72;
                margin-top: 4px;
                text-transform: uppercase;
                letter-spacing: 0.5px;
            }
            h2 {
                font-size: 20px;
                margin-bottom: 16px;
                color: #2d3436;
                border-left: 4px solid #0096D6;
                padding-left: 12px;
            }
            table {
                width: 100%;
                border-collapse: collapse;
                background: white;
                border-radius: 10px;
                overflow: hidden;
                box-shadow: 0 2px 8px rgba(0,0,0,0.08);
                margin-bottom: 32px;
            }
            th {
                background: #0096D6;
                color: white;
                padding: 12px 16px;
                text-align: left;
                font-size: 13px;
                text-transform: uppercase;
                letter-spacing: 0.5px;
            }
            td {
                padding: 10px 16px;
                border-bottom: 1px solid #eee;
                font-size: 14px;
            }
            tr:hover td { background: #f0f8ff; }
            tr:last-child td { border-bottom: none; }
            .critical { color: #d63031; font-weight: 600; }
            .warning { color: #e17055; font-weight: 600; }
            .device-section {
                margin-bottom: 24px;
            }
            .device-section h3 {
                font-size: 16px;
                margin-bottom: 8px;
                color: #2d3436;
            }
            .device-section .model {
                font-weight: 400;
                color: #636e72;
            }
            .updates-table { margin-bottom: 16px; }
            .footer {
                text-align: center;
                padding: 24px;
                color: #636e72;
                font-size: 13px;
                border-top: 1px solid #ddd;
                margin-top: 16px;
            }
            @media print {
                body { padding: 0; background: white; }
                .header { -webkit-print-color-adjust: exact; print-color-adjust: exact; }
                th { -webkit-print-color-adjust: exact; print-color-adjust: exact; }
                .card { box-shadow: none; border: 1px solid #ddd; }
                table { box-shadow: none; border: 1px solid #ddd; }
            }
            @media (max-width: 768px) {
                .summary { grid-template-columns: repeat(2, 1fr); }
            }
            """;
    }

    private static string GetStatusClass(DeviceStatus status, int pendingCount, int criticalCount)
    {
        if (criticalCount > 0) return "critical";
        if (pendingCount > 0) return "warning";
        return "";
    }

    private static string H(string? value) => HttpUtility.HtmlEncode(value ?? "");

    private static string Esc(string? val) =>
        val?.Contains(',') == true || val?.Contains('"') == true
            ? $"\"{val.Replace("\"", "\"\"")}\""
            : val ?? "";
}
