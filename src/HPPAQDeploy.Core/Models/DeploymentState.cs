namespace HPPAQDeploy.Core.Models;

public enum DeploymentState
{
    Pending,
    Downloading,
    Copying,
    Installing,
    Completed,
    Failed,
    Skipped
}
