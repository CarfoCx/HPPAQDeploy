namespace HPPAQDeploy.Core.Models;

public enum DeviceStatus
{
    Discovered,
    Online,
    Offline,
    Scanning,
    Analyzing,
    ReadyToDeploy,
    Deploying,
    Completed,
    RebootRequired,
    Failed,
    Unreachable
}
