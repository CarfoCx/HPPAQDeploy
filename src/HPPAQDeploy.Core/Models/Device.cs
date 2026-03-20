using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using System.Runtime.CompilerServices;

namespace HPPAQDeploy.Core.Models;

public class Device : INotifyPropertyChanged
{
    private DeviceStatus _status;
    private bool _isSelected;
    private bool _needsReboot;

    public int Id { get; set; }
    public string Hostname { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string ProductId { get; set; } = string.Empty;
    public string OsVersion { get; set; } = string.Empty;
    public string BiosVersion { get; set; } = string.Empty;

    public DeviceStatus Status
    {
        get => _status;
        set { if (_status != value) { _status = value; OnPropertyChanged(); } }
    }

    public DateTime LastScanned { get; set; }
    public DateTime? LastAnalyzed { get; set; }
    public string? GroupName { get; set; }

    /// <summary>
    /// Indicates HPIA returned exit code 3010/3020, meaning a reboot is needed to complete updates.
    /// </summary>
    public bool NeedsReboot
    {
        get => _needsReboot;
        set { if (_needsReboot != value) { _needsReboot = value; OnPropertyChanged(); } }
    }

    /// <summary>
    /// UI-only selection flag, not persisted to DB.
    /// </summary>
    [NotMapped]
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    public ICollection<HpiaRecommendation> Recommendations { get; set; } = new List<HpiaRecommendation>();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public override string ToString() => Hostname;

    public override bool Equals(object? obj)
    {
        if (obj is not Device other) return false;
        return Id == other.Id;
    }

    public override int GetHashCode() => Id.GetHashCode();
}
