using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HPPAQDeploy.Shared.Configuration;

namespace HPPAQDeploy.App.ViewModels;

public partial class AboutViewModel : ObservableObject
{
    public string AppName => "HPPAQDeploy";
    public string Version => "1.0.0";
    public string HpiaVersion => "5.3.4";
    public string Runtime => $".NET {Environment.Version.Major}.{Environment.Version.Minor}";
    public string DatabasePath => AppSettings.DatabasePath;
    public string AppVersion => "v1.1.0";
    public string GitHubUrl => "https://github.com/CarfoCx/HPPAQDeploy";
    public string Description => "A network deployment tool for HP driver, BIOS, and firmware updates. " +
        "Uses HP Image Assistant (HPIA) to scan HP devices for missing updates and deploy them remotely via WMI/DCOM and SMB.";

    public string Features => "- Network scanning with CIDR range support\n" +
        "- WMI-based HP device discovery\n" +
        "- HPIA-powered update analysis\n" +
        "- Remote driver, BIOS, and firmware deployment\n" +
        "- Device grouping and batch operations\n" +
        "- Deployment history and logging\n" +
        "- Email notifications\n" +
        "- Scheduled scans\n" +
        "- DPAPI-encrypted credential storage";

    [RelayCommand]
    private void OpenGitHub()
    {
        Process.Start(new ProcessStartInfo(GitHubUrl) { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        if (System.IO.Directory.Exists(LogPath))
            Process.Start(new ProcessStartInfo(LogPath) { UseShellExecute = true });
    }
}
