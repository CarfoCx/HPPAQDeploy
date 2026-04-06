using System.Net;
using System.Net.Mail;
using HPPAQDeploy.Core.Interfaces;
using HPPAQDeploy.Shared.Configuration;
using HPPAQDeploy.Shared.Helpers;
using Serilog;

namespace HPPAQDeploy.Infrastructure.Services;

public class EmailService : IEmailService
{
    private bool IsConfigured =>
        AppSettings.EmailNotificationsEnabled &&
        !string.IsNullOrWhiteSpace(AppSettings.SmtpServer) &&
        !string.IsNullOrWhiteSpace(AppSettings.EmailFrom) &&
        !string.IsNullOrWhiteSpace(AppSettings.EmailTo);

    public async Task SendAsync(string subject, string body, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            Log.Debug("Email notifications not configured, skipping send");
            return;
        }

        try
        {
            await RetryHelper.RetryAsync(async () =>
            {
                using var client = CreateSmtpClient();
                var message = CreateMessage(subject, body);
                await client.SendMailAsync(message, ct);
            }, maxRetries: 2, baseDelayMs: 3000, ct: ct).ConfigureAwait(false);
            Log.Information("Email sent successfully: {Subject}", subject);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to send email after retries: {Subject}", subject);
        }
    }

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            return false;

        try
        {
            using var client = CreateSmtpClient();
            var message = CreateMessage(
                "HPPAQDeploy - Test Email",
                FormatHtml("Test Email",
                    "<p>This is a test email from HPPAQDeploy.</p>" +
                    "<p>If you received this message, email notifications are configured correctly.</p>"));
            await client.SendMailAsync(message, ct);
            Log.Information("Test email sent successfully");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Test email failed");
            throw;
        }
    }

    public async Task SendScanCompleteNotificationAsync(int devicesFound, int aliveHosts, int totalIps)
    {
        if (!AppSettings.NotifyOnScanComplete) return;

        var body = FormatHtml("Network Scan Complete",
            $"<table style='border-collapse:collapse;width:100%'>" +
            $"<tr><td style='padding:8px;border:1px solid #ddd'>Total IPs Scanned</td><td style='padding:8px;border:1px solid #ddd;font-weight:bold'>{totalIps}</td></tr>" +
            $"<tr><td style='padding:8px;border:1px solid #ddd'>Alive Hosts</td><td style='padding:8px;border:1px solid #ddd;font-weight:bold'>{aliveHosts}</td></tr>" +
            $"<tr><td style='padding:8px;border:1px solid #ddd'>HP Devices Found</td><td style='padding:8px;border:1px solid #ddd;font-weight:bold;color:#0096D6'>{devicesFound}</td></tr>" +
            $"</table>");

        await SendAsync($"HPPAQDeploy - Scan Complete: {devicesFound} HP devices found", body);
    }

    public async Task SendCriticalUpdatesFoundAsync(int criticalCount, IEnumerable<string> deviceNames)
    {
        if (!AppSettings.NotifyOnCriticalUpdates) return;

        var deviceList = string.Join("", deviceNames.Select(n => $"<li>{System.Net.WebUtility.HtmlEncode(n)}</li>"));
        var body = FormatHtml("Critical Updates Found",
            $"<p style='color:#d32f2f;font-weight:bold;font-size:18px'>{criticalCount} critical update(s) detected</p>" +
            $"<p>The following devices have critical updates available:</p>" +
            $"<ul>{deviceList}</ul>" +
            $"<p>Please review and deploy these updates as soon as possible.</p>");

        await SendAsync($"HPPAQDeploy - CRITICAL: {criticalCount} critical updates found", body);
    }

    public async Task SendDeployCompleteNotificationAsync(int successCount, int failCount)
    {
        if (failCount > 0 && !AppSettings.NotifyOnDeployFailure) return;
        if (failCount == 0 && !AppSettings.NotifyOnDeployComplete) return;

        var statusColor = failCount > 0 ? "#d32f2f" : "#388e3c";
        var statusText = failCount > 0 ? "Completed with Failures" : "Completed Successfully";

        var body = FormatHtml($"Deployment {statusText}",
            $"<table style='border-collapse:collapse;width:100%'>" +
            $"<tr><td style='padding:8px;border:1px solid #ddd'>Successful</td><td style='padding:8px;border:1px solid #ddd;font-weight:bold;color:#388e3c'>{successCount}</td></tr>" +
            $"<tr><td style='padding:8px;border:1px solid #ddd'>Failed</td><td style='padding:8px;border:1px solid #ddd;font-weight:bold;color:{statusColor}'>{failCount}</td></tr>" +
            $"<tr><td style='padding:8px;border:1px solid #ddd'>Total</td><td style='padding:8px;border:1px solid #ddd;font-weight:bold'>{successCount + failCount}</td></tr>" +
            $"</table>");

        var subjectPrefix = failCount > 0 ? "FAILED" : "Success";
        await SendAsync($"HPPAQDeploy - Deploy {subjectPrefix}: {successCount} succeeded, {failCount} failed", body);
    }

    private SmtpClient CreateSmtpClient()
    {
        var client = new SmtpClient(AppSettings.SmtpServer, AppSettings.SmtpPort)
        {
            EnableSsl = AppSettings.SmtpUseSsl,
            Timeout = 30000
        };

        if (!string.IsNullOrWhiteSpace(AppSettings.SmtpUsername))
        {
            client.Credentials = new NetworkCredential(
                AppSettings.SmtpUsername,
                AppSettings.SmtpPassword);
        }

        return client;
    }

    private MailMessage CreateMessage(string subject, string htmlBody)
    {
        var message = new MailMessage
        {
            From = new MailAddress(AppSettings.EmailFrom),
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true
        };

        foreach (var to in AppSettings.EmailTo.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            message.To.Add(to);
        }

        return message;
    }

    private static string FormatHtml(string title, string content)
    {
        return $@"<!DOCTYPE html>
<html>
<head><meta charset='utf-8'></head>
<body style='font-family:Segoe UI,Arial,sans-serif;margin:0;padding:20px;background:#f5f5f5'>
  <div style='max-width:600px;margin:0 auto;background:#fff;border-radius:8px;overflow:hidden;box-shadow:0 2px 4px rgba(0,0,0,0.1)'>
    <div style='background:#0096D6;color:#fff;padding:20px 24px'>
      <h1 style='margin:0;font-size:20px'>HPPAQDeploy</h1>
      <p style='margin:4px 0 0;opacity:0.9;font-size:14px'>{System.Net.WebUtility.HtmlEncode(title)}</p>
    </div>
    <div style='padding:24px'>
      {content}
      <hr style='border:none;border-top:1px solid #eee;margin:24px 0 12px' />
      <p style='color:#888;font-size:11px;margin:0'>Sent by HPPAQDeploy at {DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>
    </div>
  </div>
</body>
</html>";
    }
}
