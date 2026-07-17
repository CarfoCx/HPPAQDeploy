using System.Diagnostics;
using HPPAQDeploy.Shared.Configuration;
using Serilog;

namespace HPPAQDeploy.Infrastructure.Hpia;

/// <summary>
/// Extracts the bundled HPIA SoftPaq silently to a local directory.
/// No elevation (UAC) is required — extraction targets the app's own folder.
/// </summary>
public class HpiaExtractor
{
    private readonly ILogger _logger = Log.ForContext<HpiaExtractor>();

    /// <summary>
    /// Extracts hp-hpia-5.3.4.exe to the configured extract path.
    /// Skips extraction if HPImageAssistant.exe already exists.
    /// </summary>
    public virtual async Task ExtractAsync(CancellationToken ct = default)
    {
        var extractPath = AppSettings.HpiaExtractPath;

        // Check if already extracted (look in root and one level of subfolders)
        var existingExe = FindHpiaExe(extractPath);
        if (existingExe != null)
        {
            _logger.Information("HPIA already extracted at {Path}, skipping", existingExe);
            // Make sure the extract path points to the right directory
            AppSettings.HpiaExtractPath = Path.GetDirectoryName(existingExe)!;
            return;
        }

        var installerPath = AppSettings.HpiaExePath;
        if (!File.Exists(installerPath))
        {
            throw new FileNotFoundException(
                $"HPIA installer not found at {installerPath}.",
                installerPath);
        }

        _logger.Information("Extracting HPIA from {Installer} to {ExtractPath}", installerPath, extractPath);
        Directory.CreateDirectory(extractPath);

        // HP SoftPaq self-extracting archives support:
        //   /s           = silent
        //   /e           = extract only (don't install)
        //   /f "path"    = extract to this folder
        // No elevation needed when extracting to the app's own directory.
        var psi = new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = $"/s /e /f \"{extractPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start HPIA extraction process from {installerPath}");

        var (stdout, stderr) = await WaitForExitAndCaptureAsync(process, ct).ConfigureAwait(false);
        _logger.Information("HPIA extraction process exited with code {ExitCode}. Output: {Output} {Error}",
            process.ExitCode, stdout.Trim(), stderr.Trim());

        // Exit code 0 = success; some SoftPaqs return other codes but still extract fine
        // Wait briefly for file system to finish writing
        await Task.Delay(1500, ct).ConfigureAwait(false);

        // Verify extraction succeeded by searching for HPImageAssistant.exe
        existingExe = FindHpiaExe(extractPath);
        if (existingExe != null)
        {
            AppSettings.HpiaExtractPath = Path.GetDirectoryName(existingExe)!;
            _logger.Information("HPIA extracted successfully to {Path}", AppSettings.HpiaExtractPath);
            return;
        }

        // If the standard flags didn't work, try alternative extraction approach
        // Some HP SoftPaqs need just /s /f with no /e flag
        _logger.Warning("First extraction attempt didn't produce HPImageAssistant.exe, retrying with alternate flags");

        var psi2 = new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = $"/s /f \"{extractPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process2 = Process.Start(psi2)
            ?? throw new InvalidOperationException("Failed to start HPIA extraction (retry).");

        await WaitForExitAndCaptureAsync(process2, ct).ConfigureAwait(false);

        await Task.Delay(1500, ct).ConfigureAwait(false);

        existingExe = FindHpiaExe(extractPath);
        if (existingExe != null)
        {
            AppSettings.HpiaExtractPath = Path.GetDirectoryName(existingExe)!;
            _logger.Information("HPIA extracted successfully (alternate) to {Path}", AppSettings.HpiaExtractPath);
            return;
        }

        throw new InvalidOperationException(
            $"HPIA extraction completed (exit codes: {process.ExitCode}, {process2.ExitCode}) " +
            $"but HPImageAssistant.exe was not found in {extractPath}. " +
            $"Check that {installerPath} is a valid HP Image Assistant SoftPaq.");
    }

    /// <summary>
    /// Searches for HPImageAssistant.exe in the given directory and its immediate subdirectories.
    /// </summary>
    private static string? FindHpiaExe(string basePath)
    {
        if (!Directory.Exists(basePath)) return null;

        var direct = Path.Combine(basePath, "HPImageAssistant.exe");
        if (File.Exists(direct)) return direct;

        // Search up to 2 levels deep (HP sometimes nests in a version subfolder)
        try
        {
            return Directory.GetFiles(basePath, "HPImageAssistant.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<(string Output, string Error)> WaitForExitAndCaptureAsync(
        Process process,
        CancellationToken ct)
    {
        // Drain both pipes while the process runs; waiting first can deadlock once
        // either redirected pipe reaches its OS buffer limit.
        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return (
                await outputTask.ConfigureAwait(false),
                await errorTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            try
            {
                await process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10))
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Cancellation must not hang indefinitely if Windows cannot reap the process.
            }
            throw;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited between HasExited and Kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Best effort during cancellation.
        }
    }
}
