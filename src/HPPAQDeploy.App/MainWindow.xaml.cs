using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using HPPAQDeploy.App.ViewModels;
using Serilog;

namespace HPPAQDeploy.App;

public partial class MainWindow : Window
{
    private static readonly string WindowStateFile = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "Data", "windowstate.json");

    public MainWindow()
    {
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
        if (DataContext is MainViewModel mainVm && mainVm.CurrentView is DeployViewModel deployVm && deployVm.IsDeploying)
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

            // Validate the position is within screen bounds
            var screenWidth = SystemParameters.VirtualScreenWidth;
            var screenHeight = SystemParameters.VirtualScreenHeight;
            if (state.Left >= 0 && state.Top >= 0 &&
                state.Left + state.Width <= screenWidth + 50 &&
                state.Top + state.Height <= screenHeight + 50)
            {
                Left = state.Left;
                Top = state.Top;
                Width = state.Width;
                Height = state.Height;
                WindowStartupLocation = WindowStartupLocation.Manual;
            }

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
