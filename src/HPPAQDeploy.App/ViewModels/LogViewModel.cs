using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HPPAQDeploy.Shared.Configuration;
using HPPAQDeploy.Shared.Helpers;

namespace HPPAQDeploy.App.ViewModels;

public partial class LogViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<string> _logEntries = [];

    [ObservableProperty]
    private string _selectedLogFile = "";

    [ObservableProperty]
    private ObservableCollection<string> _logFiles = [];

    [ObservableProperty]
    private bool _autoScroll = true;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private int _totalLines;

    public LogViewModel()
    {
        AsyncInitHelper.SafeFireAndForget(LoadLogFilesAsync, nameof(LogViewModel));
    }

    partial void OnSelectedLogFileChanged(string value)
    {
        AsyncInitHelper.SafeFireAndForget(LoadSelectedLogAsync, nameof(LogViewModel));
    }

    partial void OnSearchTextChanged(string value)
    {
        AsyncInitHelper.SafeFireAndForget(LoadSelectedLogAsync, nameof(LogViewModel));
    }

    [RelayCommand]
    private async Task LoadLogFilesAsync()
    {
        var logDir = AppSettings.LogPath;
        if (!Directory.Exists(logDir)) return;

        var files = Directory.GetFiles(logDir, "*.log")
            .OrderByDescending(f => File.GetLastWriteTime(f))
            .ToList();

        LogFiles = new ObservableCollection<string>(files.Select(Path.GetFileName)!);

        var firstFile = LogFiles.FirstOrDefault();
        if (firstFile is not null)
        {
            SelectedLogFile = firstFile;
            await LoadSelectedLogAsync();
        }
    }

    [RelayCommand]
    private async Task LoadSelectedLogAsync()
    {
        if (string.IsNullOrEmpty(SelectedLogFile)) return;

        var path = Path.Combine(AppSettings.LogPath, SelectedLogFile);
        if (!File.Exists(path)) return;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync();
            var lines = content.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            TotalLines = lines.Length;

            IEnumerable<string> filtered = lines.Reverse();

            // Apply search filter
            if (!string.IsNullOrWhiteSpace(SearchText))
                filtered = filtered.Where(l => l.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

            LogEntries = new ObservableCollection<string>(filtered.Take(1000));
        }
        catch (IOException)
        {
            LogEntries = new ObservableCollection<string>(["(Log file is locked by another process)"]);
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadLogFilesAsync();
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        var logDir = AppSettings.LogPath;
        if (Directory.Exists(logDir))
            Process.Start(new ProcessStartInfo(logDir) { UseShellExecute = true });
    }
}
