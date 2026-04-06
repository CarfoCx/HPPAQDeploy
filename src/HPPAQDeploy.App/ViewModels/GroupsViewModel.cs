using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HPPAQDeploy.App.Helpers;
using HPPAQDeploy.App.Services;
using HPPAQDeploy.Core.Interfaces;
using HPPAQDeploy.Core.Models;
using HPPAQDeploy.Shared.Helpers;
using Serilog;

namespace HPPAQDeploy.App.ViewModels;

public partial class GroupsViewModel : ObservableObject
{
    public static event EventHandler? GroupsChanged;

    private readonly IDeviceGroupRepository _groupRepository;
    private readonly IDeviceRepository _deviceRepository;

    [ObservableProperty] private ObservableCollection<GroupItem> _groups = [];
    [ObservableProperty] private GroupItem? _selectedGroup;
    [ObservableProperty] private ObservableCollection<Device> _groupDevices = [];
    [ObservableProperty] private ObservableCollection<Device> _availableDevices = [];
    [ObservableProperty] private ObservableCollection<Device> _filteredAvailableDevices = [];
    [ObservableProperty] private Device? _selectedAvailableDevice;
    [ObservableProperty] private string _newGroupName = "";
    [ObservableProperty] private string _newGroupDescription = "";
    [ObservableProperty] private string _deviceSearchText = "";
    [ObservableProperty] private bool _showCreateForm;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private ObservableCollection<string> _availableModels = [];
    [ObservableProperty] private string? _selectedModel;

    private List<Device> _allDevices = [];

    partial void OnDeviceSearchTextChanged(string value)
    {
        FilterAvailableDevices();
    }

    private void FilterAvailableDevices()
    {
        var search = DeviceSearchText?.Trim() ?? "";
        var ungrouped = _allDevices.Where(d => string.IsNullOrEmpty(d.GroupName));
        if (!string.IsNullOrEmpty(search))
        {
            ungrouped = ungrouped.Where(d =>
                (d.Hostname?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (d.IpAddress?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (d.Model?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        FilteredAvailableDevices = new ObservableCollection<Device>(ungrouped);
    }

    public bool HasSelectedGroup => SelectedGroup is not null;
    public int TotalDevicesInGroups => Groups.Sum(g => g.DeviceCount);

    public GroupsViewModel(IDeviceGroupRepository groupRepository, IDeviceRepository deviceRepository)
    {
        _groupRepository = groupRepository;
        _deviceRepository = deviceRepository;
        AsyncInitHelper.SafeFireAndForget(InitializeAsync, nameof(GroupsViewModel));
    }

    private async Task InitializeAsync()
    {
        await LoadGroupsAsync();
        await LoadAvailableDevicesAsync();
    }

    partial void OnSelectedGroupChanged(GroupItem? value)
    {
        OnPropertyChanged(nameof(HasSelectedGroup));
        AsyncInitHelper.SafeFireAndForget(LoadGroupDevicesAsync, nameof(GroupsViewModel));
    }

    [RelayCommand]
    private async Task LoadGroupsAsync()
    {
        IsLoading = true;
        try
        {
            var groups = await _groupRepository.GetAllAsync();
            var items = new ObservableCollection<GroupItem>();
            foreach (var g in groups)
            {
                var devices = await _deviceRepository.GetByGroupAsync(g.Name);
                items.Add(new GroupItem
                {
                    Name = g.Name,
                    Description = g.Description,
                    Created = g.Created,
                    DeviceCount = devices.Count
                });
            }
            Groups = items;
            OnPropertyChanged(nameof(TotalDevicesInGroups));

            if (SelectedGroup is not null)
            {
                var match = Groups.FirstOrDefault(g => g.Name == SelectedGroup.Name);
                SelectedGroup = match;
            }

            // Also refresh available devices list
            await LoadAvailableDevicesAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading groups: {ex.Message}";
            Log.Error(ex, "Failed to load groups");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task LoadGroupDevicesAsync()
    {
        if (SelectedGroup is null)
        {
            GroupDevices = [];
            return;
        }

        try
        {
            var devices = await _deviceRepository.GetByGroupAsync(SelectedGroup.Name);
            GroupDevices = new ObservableCollection<Device>(devices);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading devices: {ex.Message}";
            Log.Error(ex, "Failed to load group devices");
        }
    }

    [RelayCommand]
    private async Task LoadAvailableDevicesAsync()
    {
        try
        {
            _allDevices = (await _deviceRepository.GetAllAsync()).ToList();
            FilterAvailableDevices();

            // Build unique model list from ALL devices (not just ungrouped)
            var models = _allDevices
                .Select(d => d.Model)
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(m => m)
                .ToList();
            AvailableModels = new ObservableCollection<string>(models);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load available devices");
        }
    }

    [RelayCommand]
    private async Task CreateGroupAsync()
    {
        if (string.IsNullOrWhiteSpace(NewGroupName))
        {
            StatusMessage = "Group name is required.";
            return;
        }

        var name = NewGroupName.Trim();
        try
        {
            if (await _groupRepository.ExistsAsync(name))
            {
                StatusMessage = $"Group '{name}' already exists.";
                return;
            }

            await _groupRepository.CreateAsync(name, NewGroupDescription);
            NewGroupName = "";
            NewGroupDescription = "";
            ShowCreateForm = false;
            await LoadGroupsAsync();
            StatusMessage = $"Group '{name}' created.";

            SelectedGroup = Groups.FirstOrDefault(g => g.Name == name);
            GroupsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error creating group: {ex.Message}";
            Log.Error(ex, "Failed to create group");
        }
    }

    [RelayCommand]
    private async Task DeleteGroupAsync()
    {
        if (SelectedGroup is null) return;

        var name = SelectedGroup.Name;
        if (!DialogHelper.Confirm(
            $"Delete group '{name}'?\nDevices will be unassigned but not deleted.",
            "Delete Group"))
            return;

        try
        {
            await _groupRepository.DeleteAsync(name);
            SelectedGroup = null;
            await LoadGroupsAsync();
            await LoadAvailableDevicesAsync();
            StatusMessage = $"Group '{name}' deleted.";
            GroupsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error deleting group: {ex.Message}";
            Log.Error(ex, "Failed to delete group");
        }
    }

    [RelayCommand]
    private async Task AssignToGroupAsync()
    {
        if (SelectedGroup is null || SelectedAvailableDevice is null) return;

        try
        {
            var groupName = SelectedGroup.Name;
            await _deviceRepository.AssignGroupAsync([SelectedAvailableDevice.Id], groupName);
            StatusMessage = $"{SelectedAvailableDevice.Hostname} assigned to '{groupName}'.";
            SelectedAvailableDevice = null;
            DeviceSearchText = "";
            await LoadGroupDevicesAsync();
            await LoadAvailableDevicesAsync();

            // Update device count on current group without replacing the collection
            SelectedGroup.DeviceCount = GroupDevices.Count;
            OnPropertyChanged(nameof(TotalDevicesInGroups));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error assigning device: {ex.Message}";
            Log.Error(ex, "Failed to assign device to group");
        }
    }

    [RelayCommand]
    private async Task AssignByModelAsync()
    {
        if (SelectedGroup is null)
        {
            StatusMessage = "Select a group first.";
            return;
        }
        if (string.IsNullOrEmpty(SelectedModel))
        {
            StatusMessage = "Select a model from the dropdown.";
            return;
        }

        var matchingDevices = _allDevices
            .Where(d => string.Equals(d.Model, SelectedModel, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matchingDevices.Count == 0)
        {
            StatusMessage = $"No devices found with model '{SelectedModel}'.";
            return;
        }

        // Include devices already in other groups if user confirms
        var alreadyGrouped = matchingDevices.Where(d => !string.IsNullOrEmpty(d.GroupName) && d.GroupName != SelectedGroup.Name).ToList();
        var confirmMsg = $"Add {matchingDevices.Count} '{SelectedModel}' device(s) to '{SelectedGroup.Name}'?";
        if (alreadyGrouped.Count > 0)
            confirmMsg += $"\n\n{alreadyGrouped.Count} device(s) will be moved from their current group.";

        if (!DialogHelper.Confirm(confirmMsg, "Add by Model"))
            return;

        try
        {
            var ids = matchingDevices.Select(d => d.Id).ToList();
            await _deviceRepository.AssignGroupAsync(ids, SelectedGroup.Name);
            StatusMessage = $"{matchingDevices.Count} '{SelectedModel}' device(s) added to '{SelectedGroup.Name}'.";
            SnackbarService.Show($"{matchingDevices.Count} {SelectedModel} devices added");
            SelectedModel = null;
            await LoadGroupDevicesAsync();
            await LoadAvailableDevicesAsync();
            SelectedGroup.DeviceCount = GroupDevices.Count;
            OnPropertyChanged(nameof(TotalDevicesInGroups));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            Log.Error(ex, "Failed to assign devices by model");
        }
    }

    [RelayCommand]
    private async Task AssignAllToGroupAsync()
    {
        if (SelectedGroup is null) return;
        var ungrouped = _allDevices.Where(d => string.IsNullOrEmpty(d.GroupName)).ToList();
        if (ungrouped.Count == 0)
        {
            StatusMessage = "No ungrouped devices available.";
            return;
        }

        if (!DialogHelper.Confirm(
            $"Add all {ungrouped.Count} ungrouped device(s) to '{SelectedGroup.Name}'?",
            "Add All Devices"))
            return;

        try
        {
            var ids = ungrouped.Select(d => d.Id).ToList();
            await _deviceRepository.AssignGroupAsync(ids, SelectedGroup.Name);
            StatusMessage = $"{ungrouped.Count} device(s) assigned to '{SelectedGroup.Name}'.";
            SnackbarService.Show($"{ungrouped.Count} devices added to {SelectedGroup.Name}");
            DeviceSearchText = "";
            await LoadGroupDevicesAsync();
            await LoadAvailableDevicesAsync();
            SelectedGroup.DeviceCount = GroupDevices.Count;
            OnPropertyChanged(nameof(TotalDevicesInGroups));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error assigning devices: {ex.Message}";
            Log.Error(ex, "Failed to assign all devices to group");
        }
    }

    [RelayCommand]
    private async Task RemoveFromGroupAsync(Device? device)
    {
        if (device is null) return;

        if (!DialogHelper.Confirm(
            $"Remove '{device.Hostname}' from group '{SelectedGroup?.Name}'?\n\nThe device will not be deleted, only unassigned from this group.",
            "Remove from Group"))
            return;

        try
        {
            await _deviceRepository.AssignGroupAsync([device.Id], null);
            StatusMessage = $"{device.Hostname} removed from group.";
            await LoadGroupDevicesAsync();
            await LoadAvailableDevicesAsync();
            await LoadGroupsAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error removing device: {ex.Message}";
            Log.Error(ex, "Failed to remove device from group");
        }
    }

    [RelayCommand]
    private void ToggleCreateForm()
    {
        ShowCreateForm = !ShowCreateForm;
        if (!ShowCreateForm)
        {
            NewGroupName = "";
            NewGroupDescription = "";
        }
    }
}

/// <summary>
/// Display model for groups with computed device count.
/// </summary>
public partial class GroupItem : ObservableObject
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public DateTime Created { get; set; }

    [ObservableProperty]
    private int _deviceCount;

    public override string ToString() => $"{Name} ({DeviceCount} devices)";
}
