using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using HPPAQDeploy.App.ViewModels;
using Serilog;

namespace HPPAQDeploy.App;

public partial class MainWindow : Window
{
    private readonly DeployViewModel _deployViewModel;
    private static readonly string WindowStateFile = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "Data", "windowstate.json");

    public MainWindow(DeployViewModel deployViewModel)
    {
        _deployViewModel = deployViewModel;
        InitializeComponent();
        Closing += OnClosing;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RestoreWindowState();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_deployViewModel.HasActiveDeployments)
        {
            var result = MessageBox.Show(
                "A deployment is currently in progress.\n\nClosing the application may leave devices in an inconsistent state.\n\nAre you sure you want to exit?",
                "Deployment In Progress",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.No)
            {
                e.Cancel = true;
                return;
            }
        }

        SaveWindowState();
    }

    private void SaveWindowState()
    {
        try
        {
            var state = new WindowStateData
            {
                Left = RestoreBounds.Left,
                Top = RestoreBounds.Top,
                Width = RestoreBounds.Width,
                Height = RestoreBounds.Height,
                IsMaximized = WindowState == WindowState.Maximized
            };
            var dir = Path.GetDirectoryName(WindowStateFile);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(WindowStateFile, JsonSerializer.Serialize(state));
        }
        catch (Exception ex) { Log.Debug(ex, "Failed to save window state"); }
    }

    private void RestoreWindowState()
    {
        try
        {
            if (!File.Exists(WindowStateFile)) return;
            var state = JsonSerializer.Deserialize<WindowStateData>(File.ReadAllText(WindowStateFile));
            if (state is null) return;

            if (!double.IsFinite(state.Left) || !double.IsFinite(state.Top) ||
                !double.IsFinite(state.Width) || !double.IsFinite(state.Height) ||
                state.Width <= 0 || state.Height <= 0)
                return;

            var virtualLeft = SystemParameters.VirtualScreenLeft;
            var virtualTop = SystemParameters.VirtualScreenTop;
            var virtualWidth = SystemParameters.VirtualScreenWidth;
            var virtualHeight = SystemParameters.VirtualScreenHeight;

            var width = Math.Clamp(state.Width, MinWidth, Math.Max(MinWidth, virtualWidth));
            var height = Math.Clamp(state.Height, MinHeight, Math.Max(MinHeight, virtualHeight));
            var maxLeft = virtualLeft + Math.Max(0, virtualWidth - width);
            var maxTop = virtualTop + Math.Max(0, virtualHeight - height);

            Width = width;
            Height = height;
            Left = Math.Clamp(state.Left, virtualLeft, maxLeft);
            Top = Math.Clamp(state.Top, virtualTop, maxTop);
            WindowStartupLocation = WindowStartupLocation.Manual;

            if (state.IsMaximized)
                WindowState = WindowState.Maximized;
        }
        catch (Exception ex) { Log.Debug(ex, "Failed to restore window state"); }
    }

    private class WindowStateData
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool IsMaximized { get; set; }
    }
}
