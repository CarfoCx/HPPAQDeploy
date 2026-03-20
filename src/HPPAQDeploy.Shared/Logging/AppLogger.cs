using Serilog;
using HPPAQDeploy.Shared.Configuration;

namespace HPPAQDeploy.Shared.Logging;

public static class AppLogger
{
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized)
            return;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                path: Path.Combine(AppSettings.LogPath, "hppaq-.log"),
                rollingInterval: RollingInterval.Day,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        _initialized = true;
    }

    public static ILogger GetLogger()
    {
        if (!_initialized)
            Initialize();

        return Log.Logger;
    }
}
