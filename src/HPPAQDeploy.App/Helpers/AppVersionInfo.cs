using System.Reflection;

namespace HPPAQDeploy.App.Helpers;

public static class AppVersionInfo
{
    public const string HpiaVersion = "5.3.4";

    public static string Version { get; } = ResolveVersion();

    public static string StatusText => $"v{Version} | HPIA {HpiaVersion}";

    private static string ResolveVersion()
    {
        var assembly = typeof(AppVersionInfo).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var version = informationalVersion.Split('+', 2)[0].Trim().TrimStart('v', 'V');
            if (version.Length > 0)
                return version;
        }

        var assemblyVersion = assembly.GetName().Version;
        return assemblyVersion is null
            ? "Unknown"
            : assemblyVersion.ToString(assemblyVersion.Build >= 0 ? 3 : 2);
    }
}
