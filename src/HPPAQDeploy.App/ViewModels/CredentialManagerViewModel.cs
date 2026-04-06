using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HPPAQDeploy.App.Helpers;
using HPPAQDeploy.App.Services;
using HPPAQDeploy.Core.Interfaces;
using HPPAQDeploy.Core.Models;
using Serilog;

namespace HPPAQDeploy.App.ViewModels;

public partial class CredentialManagerViewModel : ObservableObject
{
    private readonly ICredentialStore _credentialStore;
    private readonly IRemoteExecutor _remoteExecutor;
    private readonly IFileTransfer _fileTransfer;
    private readonly DispatcherTimer _statusClearTimer;

    [ObservableProperty]
    private ObservableCollection<Credential> _credentials = [];

    [ObservableProperty]
    private Credential? _selectedCredential;

    [ObservableProperty]
    private string _label = "";

    [ObservableProperty]
    private string _domain = "";

    [ObservableProperty]
    private string _username = "";

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private bool _isDefault;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private string _testHostInput = "";

    [ObservableProperty]
    private bool _isTestingCredential;

    [ObservableProperty]
    private string _testResult = "";

    // New properties for improved UI
    [ObservableProperty]
    private string _wmiResult = "";

    [ObservableProperty]
    private string _smbResult = "";

    [ObservableProperty]
    private bool _hasCredentials;

    [ObservableProperty]
    private bool _hasNoCredentials = true;

    [ObservableProperty]
    private bool _hasSelectedCredential;

    [ObservableProperty]
    private string _selectedCredentialLabel = "No credential selected";

    [ObservableProperty]
    private bool _isStatusVisible;

    [ObservableProperty]
    private bool _canSetAsDefault;

    public static event EventHandler? CredentialsChanged;

    public CredentialManagerViewModel(
        ICredentialStore credentialStore,
        IRemoteExecutor remoteExecutor,
        IFileTransfer fileTransfer)
    {
        _credentialStore = credentialStore;
        _remoteExecutor = remoteExecutor;
        _fileTransfer = fileTransfer;

        _statusClearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _statusClearTimer.Tick += (_, _) =>
        {
            _statusClearTimer.Stop();
            IsStatusVisible = false;
        };

        _ = LoadCredentialsAsync();
    }

    partial void OnSelectedCredentialChanged(Credential? value)
    {
        HasSelectedCredential = value is not null;
        SelectedCredentialLabel = value?.Label ?? "No credential selected";
        CanSetAsDefault = value is not null && !value.IsDefault;
    }

    partial void OnCredentialsChanged(ObservableCollection<Credential> value)
    {
        HasCredentials = value.Count > 0;
        HasNoCredentials = value.Count == 0;
    }

    private void ShowStatus(string message)
    {
        StatusMessage = message;
        IsStatusVisible = true;
        _statusClearTimer.Stop();
        _statusClearTimer.Start();
    }

    [RelayCommand]
    private async Task LoadCredentialsAsync()
    {
        try
        {
            var creds = await _credentialStore.GetAllAsync();
            Credentials = new ObservableCollection<Credential>(creds);
            CredentialsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ShowStatus($"Error loading credentials: {ex.Message}");
            Log.Error(ex, "Failed to load credentials");
        }
    }

    [RelayCommand]
    private async Task SaveCredentialAsync()
    {
        if (string.IsNullOrWhiteSpace(Label) || string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            ShowStatus("Label, Username, and Password are required.");
            return;
        }

        try
        {
            var credential = new Credential
            {
                Label = Label,
                Domain = Domain,
                Username = Username,
                IsDefault = IsDefault,
                Created = DateTime.UtcNow
            };

            await _credentialStore.SaveAsync(credential, Password);

            ShowStatus($"Credential '{Label}' saved successfully.");
            SnackbarService.Show($"Credential '{Label}' saved");
            Log.Information("Credential saved: {Label} ({Domain}\\{Username})", Label, Domain, Username);

            // Clear form
            Label = "";
            Domain = "";
            Username = "";
            Password = "";
            IsDefault = false;
            IsEditing = false;

            await LoadCredentialsAsync();
        }
        catch (Exception ex)
        {
            ShowStatus($"Error saving credential: {ex.Message}");
            Log.Error(ex, "Failed to save credential");
        }
    }

    [RelayCommand]
    private async Task DeleteCredentialAsync()
    {
        if (SelectedCredential is null) return;

        if (!DialogHelper.Confirm(
            $"Delete credential '{SelectedCredential.Label}'?\nThis cannot be undone.",
            "Delete Credential"))
            return;

        try
        {
            var label = SelectedCredential.Label;
            await _credentialStore.DeleteAsync(SelectedCredential.Id);
            ShowStatus($"Credential '{label}' deleted.");
            Log.Information("Credential deleted: {Label}", label);
            await LoadCredentialsAsync();
        }
        catch (Exception ex)
        {
            ShowStatus($"Error deleting credential: {ex.Message}");
            Log.Error(ex, "Failed to delete credential");
        }
    }

    [RelayCommand]
    private async Task SetAsDefaultAsync()
    {
        if (SelectedCredential is null || SelectedCredential.IsDefault) return;

        try
        {
            // Clear default on all, set on selected
            // We need to reload all, update flags, and save them via existing store
            var allCreds = await _credentialStore.GetAllAsync();
            foreach (var cred in allCreds)
            {
                if (cred.IsDefault && cred.Id != SelectedCredential.Id)
                {
                    cred.IsDefault = false;
                    // Save with a dummy password approach won't work — we need to update just the flag.
                    // Since SaveAsync requires a plain text password, we use the decrypt/re-encrypt approach.
                    var netCred = await _credentialStore.DecryptAsync(cred);
                    await _credentialStore.SaveAsync(cred, netCred.Password);
                }
            }

            // Set the selected one as default
            var selectedCred = allCreds.FirstOrDefault(c => c.Id == SelectedCredential.Id);
            if (selectedCred != null)
            {
                selectedCred.IsDefault = true;
                var netCred = await _credentialStore.DecryptAsync(selectedCred);
                await _credentialStore.SaveAsync(selectedCred, netCred.Password);
            }

            ShowStatus($"'{SelectedCredential.Label}' set as default credential.");
            SnackbarService.Show($"'{SelectedCredential.Label}' is now the default");
            Log.Information("Default credential changed to: {Label}", SelectedCredential.Label);

            await LoadCredentialsAsync();
        }
        catch (Exception ex)
        {
            ShowStatus($"Error setting default: {ex.Message}");
            Log.Error(ex, "Failed to set default credential");
        }
    }

    [RelayCommand]
    private void NewCredential()
    {
        IsEditing = true;
        Label = "";
        Domain = "";
        Username = "";
        Password = "";
        IsDefault = false;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        Label = "";
        Domain = "";
        Username = "";
        Password = "";
        IsDefault = false;
    }

    [RelayCommand]
    private async Task TestCredentialAsync()
    {
        if (SelectedCredential is null)
        {
            TestResult = "Please select a credential to test.";
            WmiResult = "";
            SmbResult = "";
            return;
        }

        if (string.IsNullOrWhiteSpace(TestHostInput))
        {
            TestResult = "Please enter a hostname or IP address to test against.";
            WmiResult = "";
            SmbResult = "";
            return;
        }

        IsTestingCredential = true;
        TestResult = "";
        WmiResult = "Testing";
        SmbResult = "Testing";

        try
        {
            var networkCred = await _credentialStore.DecryptAsync(SelectedCredential);
            var host = TestHostInput.Trim();

            var wmiTask = Task.Run(async () =>
            {
                try
                {
                    var ok = await _remoteExecutor.TestConnectionAsync(host, networkCred, CancellationToken.None);
                    return ok ? "OK" : "Failed";
                }
                catch (Exception ex)
                {
                    return $"Failed ({ex.Message})";
                }
            });

            var smbTask = Task.Run(async () =>
            {
                try
                {
                    var ok = await _fileTransfer.TestConnectionAsync(host, networkCred, CancellationToken.None);
                    return ok ? "OK" : "Failed";
                }
                catch (Exception ex)
                {
                    return $"Failed ({ex.Message})";
                }
            });

            await Task.WhenAll(wmiTask, smbTask);

            // Normalize badge values for DataTrigger matching; keep detail in TestResult
            WmiResult = wmiTask.Result.StartsWith("Failed") ? "Failed" : wmiTask.Result;
            SmbResult = smbTask.Result.StartsWith("Failed") ? "Failed" : smbTask.Result;
            TestResult = $"WMI: {wmiTask.Result}, SMB: {smbTask.Result}";
            Log.Information("Credential test for {Label} against {Host}: WMI={Wmi}, SMB={Smb}",
                SelectedCredential.Label, host, wmiTask.Result, smbTask.Result);
        }
        catch (Exception ex)
        {
            TestResult = $"Test error: {ex.Message}";
            WmiResult = "Error";
            SmbResult = "Error";
            Log.Error(ex, "Credential test failed");
        }
        finally
        {
            IsTestingCredential = false;
        }
    }
}
