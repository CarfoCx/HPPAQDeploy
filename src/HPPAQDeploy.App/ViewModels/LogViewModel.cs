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
    private const int MaxLoadedLines = 10_000;
    private const int MaxLoadedBytes = 8 * 1024 * 1024;
    private string[] _loadedLines = [];
    private CancellationTokenSource? _loadCts;
    private readonly SemaphoreSlim _loadFilesGate = new(1, 1);
    private bool _isUpdatingLogFiles;

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

    [ObservableProperty]
    private string _truncationLabel = "";

    public LogViewModel()
    {
        AsyncInitHelper.SafeFireAndForget(LoadLogFilesAsync, nameof(LogViewModel));
    }

    partial void OnSelectedLogFileChanged(string value)
    {
        if (!_isUpdatingLogFiles)
            AsyncInitHelper.SafeFireAndForget(LoadSelectedLogAsync, nameof(LogViewModel));
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplySearchFilter();
    }

    [RelayCommand]
    private async Task LoadLogFilesAsync()
    {
        if (!await _loadFilesGate.WaitAsync(0))
            return;

        try
        {
            var logDir = AppSettings.LogPath;
            if (!Directory.Exists(logDir))
            {
                LogFiles = [];
                SelectedLogFile = "";
                return;
            }

            var files = Directory.GetFiles(logDir, "*.log")
                .OrderByDescending(f => File.GetLastWriteTime(f))
                .ToList();

            _isUpdatingLogFiles = true;
            try
            {
                LogFiles = new ObservableCollection<string>(files.Select(Path.GetFileName)!);

                var firstFile = LogFiles.FirstOrDefault();
                if (firstFile is null)
                    SelectedLogFile = string.Empty;
                else if (!LogFiles.Contains(SelectedLogFile))
                    SelectedLogFile = firstFile;
            }
            finally
            {
                _isUpdatingLogFiles = false;
            }

            await LoadSelectedLogAsync();
        }
        finally
        {
            _loadFilesGate.Release();
        }
    }

    [RelayCommand]
    private async Task LoadSelectedLogAsync()
    {
        if (string.IsNullOrEmpty(SelectedLogFile))
        {
            var cancelledCts = Interlocked.Exchange(ref _loadCts, null);
            cancelledCts?.Cancel();
            cancelledCts?.Dispose();
            _loadedLines = [];
            TotalLines = 0;
            TruncationLabel = "";
            LogEntries = [];
            return;
        }

        if (!string.Equals(Path.GetFileName(SelectedLogFile), SelectedLogFile, StringComparison.Ordinal))
        {
            _loadedLines = [];
            TotalLines = 0;
            TruncationLabel = "";
            LogEntries = [];
            return;
        }

        var path = Path.Combine(AppSettings.LogPath, SelectedLogFile);
        if (!File.Exists(path))
        {
            _loadedLines = [];
            TotalLines = 0;
            TruncationLabel = "";
            LogEntries = [];
            return;
        }

        var loadCts = new CancellationTokenSource();
        var previousCts = Interlocked.Exchange(ref _loadCts, loadCts);
        previousCts?.Cancel();
        previousCts?.Dispose();

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 81920,
                useAsync: true);
            var startedMidFile = stream.Length > MaxLoadedBytes;
            if (startedMidFile)
                stream.Seek(-MaxLoadedBytes, SeekOrigin.End);

            using var reader = new StreamReader(stream);
            if (startedMidFile)
                _ = await reader.ReadLineAsync(loadCts.Token); // Discard the partial first line.

            var content = await reader.ReadToEndAsync(loadCts.Token);
            loadCts.Token.ThrowIfCancellationRequested();
            var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var trimmedLineCount = Math.Max(0, lines.Length - MaxLoadedLines);
            _loadedLines = trimmedLineCount > 0 ? lines[^MaxLoadedLines..] : lines;
            TotalLines = _loadedLines.Length;
            TruncationLabel = startedMidFile || trimmedLineCount > 0 ? " (recent)" : "";
            ApplySearchFilter();
        }
        catch (OperationCanceledException) when (loadCts.IsCancellationRequested)
        {
            return;
        }
        catch (IOException)
        {
            _loadedLines = [];
            TotalLines = 0;
            TruncationLabel = "";
            LogEntries = new ObservableCollection<string>(["(Log file is locked by another process)"]);
        }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _loadCts, null, loadCts), loadCts))
                loadCts.Dispose();
        }
    }

    private void ApplySearchFilter()
    {
        IEnumerable<string> filtered = _loadedLines.Reverse();
        if (!string.IsNullOrWhiteSpace(SearchText))
            filtered = filtered.Where(line => line.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

        LogEntries = new ObservableCollection<string>(filtered.Take(1000));
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
