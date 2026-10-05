using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DriveTester.Models;

namespace DriveTester.Converters;

public class LogLevelToBrushConverter : IValueConverter
{
    private static readonly Brush InfoBrush = new SolidColorBrush(Color.FromRgb(148, 163, 184));    // #94a3b8
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(52, 211, 153));  // #34d399
    private static readonly Brush WarningBrush = new SolidColorBrush(Color.FromRgb(251, 191, 36));  // #fbbf24
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(248, 113, 113));   // #f87171

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is LogLevel level)
        {
            return level switch
            {
                LogLevel.Success => SuccessBrush,
                LogLevel.Warning => WarningBrush,
                LogLevel.Error => ErrorBrush,
                _ => InfoBrush
            };
        }
        return InfoBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b)
        {
            return b ? Visibility.Collapsed : Visibility.Visible;
        }
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class PassedToColorConverter : IValueConverter
{
    private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromRgb(34, 197, 94));
    private static readonly Brush RedBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool passed)
        {
            return passed ? GreenBrush : RedBrush;
        }
        return RedBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class EnumToDisplayNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is CapacityTargetMode targetMode)
        {
            return targetMode switch
            {
                CapacityTargetMode.SafeFreeSpace95Percent => "Safe Free Space (95% - Recommended)",
                CapacityTargetMode.FullFreeSpace => "Full Free Space (100%)",
                CapacityTargetMode.Fixed10GB => "Fixed 10 GB (Quick Smoke Test)",
                CapacityTargetMode.Fixed50GB => "Fixed 50 GB",
                CapacityTargetMode.Fixed100GB => "Fixed 100 GB",
                CapacityTargetMode.Fixed500GB => "Fixed 500 GB",
                CapacityTargetMode.Fixed1000GB => "Fixed 1000 GB (1 TB Full)",
                CapacityTargetMode.CustomGB => "Custom Target (Specify GB)",
                _ => targetMode.ToString()
            };
        }

        if (value is FileSizePreset preset)
        {
            return preset switch
            {
                FileSizePreset.Balanced => "Balanced Mix (1MB - 1GB, realistic)",
                FileSizePreset.FastSequential => "High Speed Sequential (512MB - 1GB)",
                FileSizePreset.DiverseStress => "Diverse Stress (128KB - 512MB)",
                _ => preset.ToString()
            };
        }

        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}
