namespace HPPAQDeploy.Shared.Helpers;

/// <summary>
/// Maps HP Image Assistant (HPIA) exit codes to human-readable messages.
/// </summary>
public static class HpiaExitCodes
{
    private static readonly Dictionary<int, string> ExitCodeMessages = new()
    {
        [0] = "Success",
        [1] = "General error",
        [2] = "Invalid argument",
        [3] = "Missing prerequisite",
        [5] = "Access denied",
        [10] = "BIOS password required or incorrect",
        [11] = "Unsupported platform - HPIA does not support this hardware model",
        [256] = "Analysis complete - no recommendations",
        [257] = "Success - updates were applied",
        [3010] = "Success - reboot required",
        [3020] = "Success - reboot required for some updates",
        [4096] = "Failed - general failure",
        [4097] = "Failed - invalid command line",
        [4098] = "Failed - platform not supported or HP.com unreachable",
        [16384] = "Failed - could not download reference file",
        [16385] = "Failed - could not download SoftPaq",
        [16386] = "Failed - failed to install SoftPaq",
    };

    private static readonly HashSet<int> SuccessCodes = [0, 256, 257, 3010, 3020];
    private static readonly HashSet<int> RebootCodes = [3010, 3020];

    /// <summary>
    /// Returns a human-readable description for the given HPIA exit code.
    /// </summary>
    public static string GetMessage(int exitCode)
    {
        return ExitCodeMessages.TryGetValue(exitCode, out var message)
            ? message
            : $"Unknown exit code ({exitCode})";
    }

    /// <summary>
    /// Returns true if the exit code indicates a successful operation.
    /// </summary>
    public static bool IsSuccess(int exitCode) => SuccessCodes.Contains(exitCode);

    /// <summary>
    /// Returns true if the exit code indicates a reboot is required.
    /// </summary>
    public static bool RequiresReboot(int exitCode) => RebootCodes.Contains(exitCode);
}
