using System.Windows;
using Microsoft.Win32;

namespace HPPAQDeploy.App.Helpers;

public static class DialogHelper
{
    public static bool Confirm(string message, string title = "Confirm")
    {
        var result = MessageBox.Show(
            message, title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        return result == MessageBoxResult.Yes;
    }

    /// <summary>
    /// Shows a Save File dialog and returns the selected path, or null if cancelled.
    /// </summary>
    public static string? SaveFileDialog(string defaultFileName, string filter = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*", string? initialDirectory = null)
    {
        var dialog = new SaveFileDialog
        {
            FileName = defaultFileName,
            Filter = filter,
            InitialDirectory = initialDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            OverwritePrompt = true
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
