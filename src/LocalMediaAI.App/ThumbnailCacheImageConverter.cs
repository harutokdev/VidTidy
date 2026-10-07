using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using LocalMediaAI.Infrastructure;

namespace LocalMediaAI.App;

/// <summary>
/// DBに保存された相対cache pathを、原本ディレクトリを参照せずWPFの表示用ImageSourceへ変換します。
/// 壊れた/未生成のcacheはnullにして、XAML上のplaceholderを残します。
/// </summary>
public sealed class ThumbnailCacheImageConverter : IValueConverter
{
    // Resolved once: the data location (portable, data-location.json or per-user) does not change while the app runs.
    private static readonly Lazy<string> CacheRoot = new(() => AppPaths.Create(AppContext.BaseDirectory).CacheRoot);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string relativePath || string.IsNullOrWhiteSpace(relativePath)) return null;
        try
        {
            var fullPath = Path.Combine(CacheRoot.Value, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath)) return null;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = parameter is "Evidence" ? 440 : 120;
            image.UriSource = new Uri(fullPath, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
