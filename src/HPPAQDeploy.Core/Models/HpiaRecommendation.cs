using System.ComponentModel.DataAnnotations.Schema;
using CommunityToolkit.Mvvm.ComponentModel;

namespace HPPAQDeploy.Core.Models;

public partial class HpiaRecommendation : ObservableObject
{
    public int Id { get; set; }
    public int DeviceId { get; set; }
    public string SoftPaqId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public long SizeBytes { get; set; }

    /// <summary>
    /// Whether HPIA can install this update silently. False means it requires user interaction.
    /// </summary>
    public bool SilentInstallable { get; set; } = true;

    [NotMapped]
    [ObservableProperty]
    private bool _selected;

    /// <summary>
    /// UI-only: hostname of the device this recommendation belongs to.
    /// </summary>
    [NotMapped]
    public string DeviceHostname { get; set; } = string.Empty;

    public DeploymentState DeployState { get; set; }
}
