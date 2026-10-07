using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using LocalMediaAI.Infrastructure;

namespace LocalMediaAI.App;

/// <summary>Loads only realized Library thumbnails, with bounded off-UI decoding.</summary>
public static class LibraryThumbnailLoader
{
    private static readonly SemaphoreSlim Admission = new(4);
    private static readonly AppPaths Paths = AppPaths.Create(AppContext.BaseDirectory);
    private static readonly Dictionary<Image, CancellationTokenSource> Pending = [];

    public static readonly DependencyProperty RelativePathProperty = DependencyProperty.RegisterAttached(
        "RelativePath", typeof(string), typeof(LibraryThumbnailLoader), new PropertyMetadata(null, OnRelativePathChanged));
    public static readonly DependencyProperty DecodeWidthProperty = DependencyProperty.RegisterAttached(
        "DecodeWidth", typeof(int), typeof(LibraryThumbnailLoader), new PropertyMetadata(120));
    public static readonly DependencyProperty CacheRootProperty = DependencyProperty.RegisterAttached(
        "CacheRoot", typeof(string), typeof(LibraryThumbnailLoader), new PropertyMetadata(null, OnRelativePathChanged));
    public static readonly DependencyProperty LoadErrorProperty = DependencyProperty.RegisterAttached(
        "LoadError", typeof(string), typeof(LibraryThumbnailLoader), new PropertyMetadata(null));
    public static readonly DependencyProperty LoadStatusProperty = DependencyProperty.RegisterAttached(
        "LoadStatus", typeof(string), typeof(LibraryThumbnailLoader), new PropertyMetadata(null));

    public static string? GetRelativePath(Image image) => (string?)image.GetValue(RelativePathProperty);
    public static void SetRelativePath(Image image, string? value) => image.SetValue(RelativePathProperty, value);
    public static int GetDecodeWidth(Image image) => (int)image.GetValue(DecodeWidthProperty);
    public static void SetDecodeWidth(Image image, int value) => image.SetValue(DecodeWidthProperty, value);
    public static string GetCacheRoot(Image image) => (string?)image.GetValue(CacheRootProperty) ?? Paths.CacheRoot;
    public static void SetCacheRoot(Image image, string value) => image.SetValue(CacheRootProperty, value);
    public static string? GetLoadError(Image image) => (string?)image.GetValue(LoadErrorProperty);
    public static string? GetLoadStatus(Image image) => (string?)image.GetValue(LoadStatusProperty);

    private static void OnRelativePathChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not Image image) return;
        image.Loaded -= OnLoaded;
        image.Unloaded -= OnUnloaded;
        image.Loaded += OnLoaded;
        image.Unloaded += OnUnloaded;
        Start(image);
    }

    private static void OnLoaded(object sender, RoutedEventArgs args) => Start((Image)sender);
    private static void OnUnloaded(object sender, RoutedEventArgs args)
    {
        var image = (Image)sender;
        Cancel(image);
        image.Source = null;
        image.SetValue(LoadErrorProperty, null);
        image.SetValue(LoadStatusProperty, "Unloaded");
    }

    private static void Cancel(Image image)
    {
        if (!Pending.Remove(image, out var cancellation)) return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private static void Start(Image image)
    {
        Cancel(image);
        image.Source = null;
        image.SetValue(LoadErrorProperty, null);
        var relative = GetRelativePath(image);
        var cacheRoot = GetCacheRoot(image);
        if (!image.IsLoaded || string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
        {
            image.SetValue(LoadStatusProperty, image.IsLoaded ? "NoArtifact" : "WaitingForLoad");
            return;
        }
        image.SetValue(LoadStatusProperty, "Pending");
        var cancellation = new CancellationTokenSource();
        Pending[image] = cancellation;
        _ = LoadAsync(image, cacheRoot, relative, Math.Clamp(GetDecodeWidth(image), 32, 600), cancellation);
    }

    private static async Task LoadAsync(Image image, string cacheRoot, string relative, int width, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            var bitmap = await Task.Run(async () =>
            {
                await Admission.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var fullPath = Path.GetFullPath(Path.Combine(cacheRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
                    if (!PathPolicy.IsWithinRoot(cacheRoot, fullPath) || !File.Exists(fullPath)) return null;
                    using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var result = new BitmapImage();
                    result.BeginInit();
                    result.CacheOption = BitmapCacheOption.OnLoad;
                    result.DecodePixelWidth = width;
                    result.StreamSource = stream;
                    result.EndInit();
                    result.Freeze();
                    return result;
                }
                finally { Admission.Release(); }
            }, token);
            if (!token.IsCancellationRequested && image.IsLoaded && GetRelativePath(image) == relative && GetCacheRoot(image) == cacheRoot)
            {
                image.Source = bitmap;
                image.SetValue(LoadStatusProperty, bitmap is null ? "MissingArtifact" : "Loaded");
            }
        }
        catch (OperationCanceledException) { if (image.IsLoaded) image.SetValue(LoadStatusProperty, "Cancelled"); }
        catch (Exception exception)
        {
            if (!token.IsCancellationRequested && image.IsLoaded && GetRelativePath(image) == relative)
            {
                image.SetValue(LoadErrorProperty, exception.GetType().Name);
                image.SetValue(LoadStatusProperty, "Failed");
            }
        }
        finally
        {
            if (Pending.TryGetValue(image, out var current) && ReferenceEquals(current, cancellation))
            {
                Pending.Remove(image);
                cancellation.Dispose();
            }
        }
    }
}
