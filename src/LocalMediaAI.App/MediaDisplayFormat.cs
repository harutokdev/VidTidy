using System.Globalization;
using System.Windows.Data;

namespace LocalMediaAI.App;

/// <summary>Human-readable size and duration text for list columns. Sorting keeps using the raw numeric binding path.</summary>
public static class MediaDisplayFormat
{
    private static readonly string[] SizeUnits = ["KB", "MB", "GB", "TB"];

    public static string FileSize(long bytes)
    {
        if (bytes < 1024) return string.Format(CultureInfo.InvariantCulture, "{0:N0} B", Math.Max(0, bytes));
        var value = (double)bytes;
        var unit = -1;
        while (value >= 1024 && unit < SizeUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var format = value < 10 ? "{0:0.0} {1}" : "{0:N0} {1}";
        return string.Format(CultureInfo.InvariantCulture, format, value, SizeUnits[unit]);
    }

    public static string Duration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return string.Empty;
        var time = TimeSpan.FromSeconds(Math.Round(seconds, MidpointRounding.AwayFromZero));
        return time.TotalHours >= 1
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", (int)time.TotalHours, time.Minutes, time.Seconds)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", time.Minutes, time.Seconds);
    }
}

public sealed class FileSizeDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is long bytes ? MediaDisplayFormat.FileSize(bytes) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class DurationDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is double seconds ? MediaDisplayFormat.Duration(seconds) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
