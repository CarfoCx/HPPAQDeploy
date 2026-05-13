using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using HPPAQDeploy.Core.Models;

namespace HPPAQDeploy.App.Converters;

public class StatusToColorConverter : IValueConverter
{
    // Pre-allocated and frozen brushes — avoids GC pressure in DataGrids
    private static readonly SolidColorBrush OnlineBrush     = Freeze(new SolidColorBrush(Color.FromRgb(76, 175, 80)));
    private static readonly SolidColorBrush DiscoveredBrush  = Freeze(new SolidColorBrush(Color.FromRgb(0, 150, 214)));
    private static readonly SolidColorBrush InProgressBrush  = Freeze(new SolidColorBrush(Color.FromRgb(0, 180, 230)));   // HP Light Blue — distinct from warning orange
    private static readonly SolidColorBrush CompletedBrush   = Freeze(new SolidColorBrush(Color.FromRgb(76, 175, 80)));
    private static readonly SolidColorBrush RebootBrush      = Freeze(new SolidColorBrush(Color.FromRgb(255, 167, 38)));
    private static readonly SolidColorBrush ReadyBrush       = Freeze(new SolidColorBrush(Color.FromRgb(0, 180, 230)));
    private static readonly SolidColorBrush FailedBrush      = Freeze(new SolidColorBrush(Color.FromRgb(244, 67, 54)));
    private static readonly SolidColorBrush OfflineBrush     = Freeze(new SolidColorBrush(Color.FromRgb(158, 158, 158)));
    private static readonly SolidColorBrush CriticalBrush    = Freeze(new SolidColorBrush(Color.FromRgb(244, 67, 54)));
    private static readonly SolidColorBrush RecommendedBrush = Freeze(new SolidColorBrush(Color.FromRgb(255, 152, 0)));
    private static readonly SolidColorBrush OptionalBrush    = Freeze(new SolidColorBrush(Color.FromRgb(0, 150, 214)));
    private static readonly SolidColorBrush SuccessBrush     = Freeze(new SolidColorBrush(Color.FromRgb(76, 175, 80)));
    private static readonly SolidColorBrush WarningBrush     = Freeze(new SolidColorBrush(Color.FromRgb(255, 152, 0)));
    private static readonly SolidColorBrush InfoBrush        = Freeze(new SolidColorBrush(Color.FromRgb(0, 150, 214)));

    private static SolidColorBrush Freeze(SolidColorBrush brush) { brush.Freeze(); return brush; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is DeviceStatus status)
        {
            return status switch
            {
                DeviceStatus.Online => OnlineBrush,
                DeviceStatus.Discovered => DiscoveredBrush,
                DeviceStatus.Scanning or DeviceStatus.Analyzing or DeviceStatus.Deploying
                    => InProgressBrush,
                DeviceStatus.Completed => CompletedBrush,
                DeviceStatus.RebootRequired => RebootBrush,
                DeviceStatus.ReadyToDeploy => ReadyBrush,
                DeviceStatus.Failed => FailedBrush,
                DeviceStatus.Offline or DeviceStatus.Unreachable => OfflineBrush,
                _ => OfflineBrush
            };
        }

        if (value is string severity)
        {
            return severity.ToLowerInvariant() switch
            {
                "critical" => CriticalBrush,
                "recommended" => RecommendedBrush,
                "routine" => InfoBrush,
                "optional" => OptionalBrush,
                "success" => SuccessBrush,
                "error" => FailedBrush,
                "warning" => WarningBrush,
                "info" => InfoBrush,
                _ => OfflineBrush
            };
        }

        return OfflineBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is System.Windows.Visibility.Visible;
}

public class StringEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string text
           && parameter is string expected
           && string.Equals(text, expected, StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true && parameter is string expected ? expected : Binding.DoNothing;
}

public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is System.Windows.Visibility.Collapsed;
}

public class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int count = value is int i ? i : 0;
        return count == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class NonZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int count = value is int i ? i : 0;
        return count > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class ComplianceToColorConverter : IValueConverter
{
    private static readonly SolidColorBrush GreenBrush = Freeze(new SolidColorBrush(Color.FromRgb(76, 175, 80)));
    private static readonly SolidColorBrush YellowBrush = Freeze(new SolidColorBrush(Color.FromRgb(255, 193, 7)));
    private static readonly SolidColorBrush RedBrush = Freeze(new SolidColorBrush(Color.FromRgb(244, 67, 54)));

    private static SolidColorBrush Freeze(SolidColorBrush brush) { brush.Freeze(); return brush; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double percent = value is double d ? d : 0;
        if (percent > 90) return GreenBrush;
        if (percent >= 50) return YellowBrush;
        return RedBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;
}

public class BoolToTextWrappingConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? System.Windows.TextWrapping.Wrap : System.Windows.TextWrapping.NoWrap;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is System.Windows.TextWrapping.Wrap;
}

public class FileSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is long bytes)
        {
            string[] sizes = ["B", "KB", "MB", "GB"];
            int order = 0;
            double size = bytes;
            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }
            return $"{size:0.##} {sizes[order]}";
        }
        return "0 B";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class StringContainsToVisibilityConverter : IValueConverter
{
    public string SearchText { get; set; } = "";
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var contains = value is string s && !string.IsNullOrEmpty(SearchText)
            && s.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
        if (Invert) contains = !contains;
        return contains ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class CategoryToColorConverter : IValueConverter
{
    private static readonly SolidColorBrush DriverBrush     = Freeze(new SolidColorBrush(Color.FromRgb(66, 165, 245)));   // Blue
    private static readonly SolidColorBrush BiosBrush       = Freeze(new SolidColorBrush(Color.FromRgb(239, 83, 80)));    // Red
    private static readonly SolidColorBrush FirmwareBrush   = Freeze(new SolidColorBrush(Color.FromRgb(255, 167, 38)));   // Orange
    private static readonly SolidColorBrush SoftwareBrush   = Freeze(new SolidColorBrush(Color.FromRgb(102, 187, 106)));  // Green
    private static readonly SolidColorBrush DockBrush       = Freeze(new SolidColorBrush(Color.FromRgb(171, 71, 188)));   // Purple
    private static readonly SolidColorBrush UtilityBrush    = Freeze(new SolidColorBrush(Color.FromRgb(0, 150, 214)));    // HP Blue
    private static readonly SolidColorBrush DefaultBrush    = Freeze(new SolidColorBrush(Color.FromRgb(158, 158, 158)));  // Gray

    private static SolidColorBrush Freeze(SolidColorBrush brush) { brush.Freeze(); return brush; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string category)
        {
            return category.ToLowerInvariant() switch
            {
                "driver"       => DriverBrush,
                "bios"         => BiosBrush,
                "firmware"     => FirmwareBrush,
                "software"     => SoftwareBrush,
                "dock"         => DockBrush,
                "accessory"    => DockBrush,
                "utility"      => UtilityBrush,
                "diagnostic"   => UtilityBrush,
                "manageability" => UtilityBrush,
                _              => DefaultBrush
            };
        }
        return DefaultBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
