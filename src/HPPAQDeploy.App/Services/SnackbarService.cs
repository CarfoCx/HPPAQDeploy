namespace HPPAQDeploy.App.Services;

/// <summary>
/// Lightweight notification service for showing transient toast messages.
/// </summary>
public static class SnackbarService
{
    public static event Action<string>? MessagePosted;

    public static void Show(string message)
    {
        MessagePosted?.Invoke(message);
    }
}
