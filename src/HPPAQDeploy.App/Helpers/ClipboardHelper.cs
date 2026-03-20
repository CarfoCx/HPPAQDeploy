using System.Windows;

namespace HPPAQDeploy.App.Helpers;

public static class ClipboardHelper
{
    public static void CopyToClipboard(string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            Clipboard.SetText(text);
        }
    }
}
