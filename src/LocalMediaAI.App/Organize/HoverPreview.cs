using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace LocalMediaAI.App.Organize;

/// <summary>
/// Plays a few seconds of a video, muted, over its thumbnail while the pointer rests on it: after a short pause it starts
/// at about 10% of the video and repeats about 3 seconds. Only one preview plays at a time; formats Windows cannot play
/// simply keep the thumbnail.
/// </summary>
public static class HoverPreview
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan Clip = TimeSpan.FromSeconds(3);

    public static readonly DependencyProperty FilePathProperty =
        DependencyProperty.RegisterAttached("FilePath", typeof(string), typeof(HoverPreview), new PropertyMetadata(null, OnFilePathChanged));

    private static Panel? _host;
    private static MediaElement? _media;
    private static DispatcherTimer? _delay;
    private static DispatcherTimer? _loop;

    public static string? GetFilePath(DependencyObject element) => (string?)element?.GetValue(FilePathProperty);

    public static void SetFilePath(DependencyObject element, string? value) => element?.SetValue(FilePathProperty, value);

    private static void OnFilePathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Panel panel || e.OldValue is not null) return;
        panel.MouseEnter += (_, _) => Arm(panel);
        panel.MouseLeave += (_, _) => { if (ReferenceEquals(_host, panel)) Stop(); };
        panel.Unloaded += (_, _) => { if (ReferenceEquals(_host, panel)) Stop(); };
    }

    private static void Arm(Panel panel)
    {
        Stop();
        _host = panel;
        _delay = new DispatcherTimer { Interval = Delay };
        _delay.Tick += (_, _) => { _delay?.Stop(); Start(panel); };
        _delay.Start();
    }

    private static void Start(Panel panel)
    {
        var path = GetFilePath(panel);
        if (!ReferenceEquals(_host, panel) || string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return;
        var media = new MediaElement
        {
            LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, IsMuted = true, Volume = 0,
            Stretch = Stretch.UniformToFill, IsHitTestVisible = false
        };
        media.MediaOpened += (_, _) =>
        {
            if (!ReferenceEquals(_media, media)) return;
            var from = media.NaturalDuration.HasTimeSpan ? TimeSpan.FromTicks(media.NaturalDuration.TimeSpan.Ticks / 10) : TimeSpan.Zero;
            media.Position = from;
            _loop = new DispatcherTimer { Interval = Clip };
            _loop.Tick += (_, _) => { if (ReferenceEquals(_media, media)) media.Position = from; };
            _loop.Start();
        };
        media.MediaFailed += (_, _) => { if (ReferenceEquals(_media, media)) Stop(); };
        _media = media;
        panel.Children.Insert(Math.Min(1, panel.Children.Count), media); // over the thumbnail, under the ▶ badge
        try
        {
            media.Source = new Uri(path);
            media.Play();
        }
        catch (Exception ex) when (ex is UriFormatException or InvalidOperationException)
        {
            Stop();
        }
    }

    private static void Stop()
    {
        _delay?.Stop();
        _delay = null;
        _loop?.Stop();
        _loop = null;
        if (_media is { } media)
        {
            _media = null;
            media.Stop();
            media.Close();
            (VisualTreeHelper.GetParent(media) as Panel)?.Children.Remove(media);
        }
        _host = null;
    }
}
