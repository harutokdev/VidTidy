using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using LocalMediaAI.Core.Organize;

namespace LocalMediaAI.App.Organize;

/// <summary>For similar groups: 一致した場面 (matched frames) and 並べて再生 (side-by-side playback from a matched scene).</summary>
public partial class OrganizeView
{
    private readonly List<MediaElement> _sideBySide = [];
    private DispatcherTimer? _sideBySideTimer;

    private void AddScenesAndSideBySide(GroupRow group)
    {
        DetailPanel.Children.Add(H3(L.T("一致した場面")));
        var scenes = new UniformGrid { Columns = 3, Margin = new Thickness(0, 6, 0, 14) };
        DetailPanel.Children.Add(scenes);
        DetailPanel.Children.Add(H3(L.T("並べて再生")));
        var files = group.Files.Take(3).ToArray();
        var screens = new UniformGrid { Columns = files.Length };
        var players = files.Select(_ => new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform, ScrubbingEnabled = true, IsMuted = true }).ToArray();
        for (var i = 0; i < files.Length; i++)
        {
            var cell = new Grid { Margin = new Thickness(0, 0, 8, 0), Height = 120 };
            cell.Children.Add(new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(4) });
            cell.Children.Add(ThumbImage(files[i].ThumbnailRelativePath, 240));
            cell.Children.Add(players[i]);
            cell.Children.Add(new Border
            {
                Background = Hex("#8C000000"), CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(6, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = ((char)('A' + i)).ToString(), FontSize = 11, FontWeight = FontWeights.Bold }
            });
            screens.Children.Add(cell);
        }
        DetailPanel.Children.Add(screens);

        var play = new Button { Content = L.T("▶ 同時に再生"), Style = (Style)FindResource("Small") };
        var seek = new Slider { Minimum = 0, Maximum = 60, Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true };
        AutomationPropertiesHelper.SetName(seek, L.T("並べて再生の位置（一致した場面からの秒数）"));
        var from = new TextBlock { Text = L.T("一致した場面から"), FontSize = 12, Foreground = Res("Muted"), VerticalAlignment = VerticalAlignment.Center };
        var scrub = new DockPanel { Margin = new Thickness(0, 8, 0, 14) };
        DockPanel.SetDock(play, Dock.Left);
        DockPanel.SetDock(from, Dock.Right);
        scrub.Children.Add(play);
        scrub.Children.Add(from);
        scrub.Children.Add(seek);
        DetailPanel.Children.Add(scrub);

        var starts = files.Select(_ => 0d).ToArray();
        play.Click += (_, _) => ToggleSideBySide(files, players, starts, play, seek);
        seek.PreviewMouseLeftButtonUp += (_, _) => { for (var i = 0; i < players.Length; i++) players[i].Position = TimeSpan.FromSeconds(starts[i] + seek.Value); };
        _ = LoadScenesAsync(group, files, scenes, starts);
    }

    private async Task LoadScenesAsync(GroupRow group, OrganizeFile[] files, UniformGrid host, double[] starts)
    {
        IReadOnlyList<MatchedScene> scenes;
        try { scenes = Vm is null ? [] : await Vm.LoadMatchedScenesAsync(group, CancellationToken.None); }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException) { scenes = []; }
        if (!ReferenceEquals(Vm?.Selected, group)) return;
        if (scenes.Count == 0)
        {
            host.Columns = 1;
            host.Children.Add(Muted(L.T("一致した場面の画像はまだありません。解析が進むと表示されます。"), 12));
            return;
        }
        for (var i = 0; i < files.Length; i++) starts[i] = scenes[0].SecondsByFile.TryGetValue(files[i].MediaFileId, out var s) ? s : 0;
        foreach (var scene in scenes)
        {
            var cell = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            var frame = new Grid { Height = 90 };
            frame.Children.Add(new Border { Background = Hex("#2C3A4C"), CornerRadius = new CornerRadius(4) });
            frame.Children.Add(ThumbImage(scene.ImageRelativePath, 240));
            cell.Children.Add(frame);
            var times = files.Select((f, i) => scene.SecondsByFile.TryGetValue(f.MediaFileId, out var t) ? $"{(char)('A' + i)} {MediaDisplayFormat.Duration(t)}" : null).Where(t => t is not null);
            cell.Children.Add(new TextBlock { Text = string.Join(L.T(" ／ "), times), FontSize = 11, Foreground = Res("Muted"), Margin = new Thickness(0, 3, 0, 0) });
            host.Children.Add(cell);
        }
    }

    private Image ThumbImage(string? relativePath, int decodeWidth)
    {
        var image = new Image { Stretch = Stretch.UniformToFill };
        if (Vm?.ThumbnailCacheRoot is { } root) LibraryThumbnailLoader.SetCacheRoot(image, root);
        LibraryThumbnailLoader.SetDecodeWidth(image, decodeWidth);
        LibraryThumbnailLoader.SetRelativePath(image, relativePath);
        return image;
    }

    /// <summary>Starts every file at its matched scene and plays them together; A has sound, the others are muted.</summary>
    private void ToggleSideBySide(OrganizeFile[] files, MediaElement[] players, double[] starts, Button play, Slider seek)
    {
        if (_sideBySide.Count > 0)
        {
            var playing = Equals(play.Content, L.T("❚❚ 一時停止"));
            foreach (var p in players) { if (playing) p.Pause(); else p.Play(); }
            play.Content = playing ? L.T("▶ 同時に再生") : L.T("❚❚ 一時停止");
            return;
        }
        for (var i = 0; i < players.Length; i++)
        {
            var player = players[i];
            var start = starts[i];
            player.IsMuted = i != 0;
            player.MediaOpened += (_, _) => player.Position = TimeSpan.FromSeconds(start);
            player.MediaFailed += (_, _) => Toast(L.F("{0} はこのアプリでは再生できない形式です", (char)('A' + Array.IndexOf(players, player))));
            player.Source = new Uri(files[i].Path);
            player.Play();
            _sideBySide.Add(player);
        }
        play.Content = L.T("❚❚ 一時停止");
        _sideBySideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _sideBySideTimer.Tick += (_, _) =>
        {
            var first = players[0];
            if (first.NaturalDuration.HasTimeSpan) seek.Maximum = Math.Max(1, first.NaturalDuration.TimeSpan.TotalSeconds - starts[0]);
            if (!seek.IsMouseCaptureWithin) seek.Value = Math.Max(0, first.Position.TotalSeconds - starts[0]);
        };
        _sideBySideTimer.Start();
    }

    private void StopSideBySide()
    {
        _sideBySideTimer?.Stop();
        _sideBySideTimer = null;
        foreach (var player in _sideBySide) { player.Stop(); player.Close(); player.Source = null; }
        _sideBySide.Clear();
    }
}
