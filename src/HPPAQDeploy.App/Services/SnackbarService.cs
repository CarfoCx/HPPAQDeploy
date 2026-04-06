namespace HPPAQDeploy.App.Services;

/// <summary>
/// Notification service for showing transient toast messages with severity levels.
/// Supports message queuing and action callbacks.
/// </summary>
public static class SnackbarService
{
    public static event Action<SnackbarMessage>? MessagePosted;

    /// <summary>Show a simple informational message.</summary>
    public static void Show(string message)
        => MessagePosted?.Invoke(new SnackbarMessage(message, SnackbarSeverity.Info));

    /// <summary>Show a success message.</summary>
    public static void ShowSuccess(string message)
        => MessagePosted?.Invoke(new SnackbarMessage(message, SnackbarSeverity.Success));

    /// <summary>Show a warning message.</summary>
    public static void ShowWarning(string message)
        => MessagePosted?.Invoke(new SnackbarMessage(message, SnackbarSeverity.Warning));

    /// <summary>Show an error message.</summary>
    public static void ShowError(string message)
        => MessagePosted?.Invoke(new SnackbarMessage(message, SnackbarSeverity.Error));

    /// <summary>Show a message with an action button.</summary>
    public static void ShowWithAction(string message, string actionLabel, Action action, SnackbarSeverity severity = SnackbarSeverity.Info)
        => MessagePosted?.Invoke(new SnackbarMessage(message, severity, actionLabel, action));
}

public enum SnackbarSeverity { Info, Success, Warning, Error }

public class SnackbarMessage
{
    public string Text { get; }
    public SnackbarSeverity Severity { get; }
    public string? ActionLabel { get; }
    public Action? Action { get; }
    public DateTime Timestamp { get; } = DateTime.UtcNow;

    public SnackbarMessage(string text, SnackbarSeverity severity, string? actionLabel = null, Action? action = null)
    {
        Text = text;
        Severity = severity;
        ActionLabel = actionLabel;
        Action = action;
    }
}
