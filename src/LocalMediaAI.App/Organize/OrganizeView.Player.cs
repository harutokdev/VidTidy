using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace LocalMediaAI.App.Organize;

/// <summary>
/// In-app playback for checking a file before cleanup. Read-only: the file is only opened for playing.
/// Files of the same group can be switched at the same position to compare them.
/// </summary>
public partial class OrganizeView
{
    private MediaElement? _player;
    private static string PlayLabel => L.T("▶ 再生");
    private static string PauseLabel => L.T("❚❚ 一時停止");
    private DispatcherTimer? _playerTimer;
    private Button? _playerToggle;

    private void Play_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: OrganizeFile file }) ShowPlayer(file, TimeSpan.Zero);
        e.Handled = true;
    }

    private void PlayMenu_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: OrganizeFile file }) ShowPlayer(file, TimeSpan.Zero);
    }

    private void RevealMenu_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: OrganizeFile file }) RevealInExplorer(file.Path);
    }

    private void ShowPlayer(OrganizeFile file, TimeSpan start)
    {
        OpenModal(file.FileName, L.F("{0}・{1}", file.Folder, file.FactsText));
        var failure = UnsupportedBox(file);
        failure.Visibility = Visibility.Collapsed;
        var player = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform, Height = 300, Volume = 0.6 };
        AutomationPropertiesHelper.SetName(player, L.F("{0} の再生", file.FileName));
        var screen = new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(6), Child = player, ClipToBounds = true };
        OverlayBody.Children.Add(screen);
        OverlayBody.Children.Add(failure);

        var seek = new Slider { Minimum = 0, Maximum = 1, Margin = new Thickness(10, 0, 10, 0), IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        AutomationPropertiesHelper.SetName(seek, L.T("再生位置"));
        var time = Muted("0:00 / " + file.DurationText, 12, new Thickness(0));
        time.VerticalAlignment = VerticalAlignment.Center;
        var toggle = new Button { Content = PauseLabel, Style = (Style)FindResource("Small"), MinWidth = 100 };
        var dragging = false;

        player.MediaOpened += (_, _) =>
        {
            if (player.NaturalDuration.HasTimeSpan) seek.Maximum = Math.Max(1, player.NaturalDuration.TimeSpan.TotalSeconds);
            if (start > TimeSpan.Zero) player.Position = start;
        };
        player.MediaFailed += (_, _) => { screen.Visibility = Visibility.Collapsed; failure.Visibility = Visibility.Visible; toggle.IsEnabled = false; };
        player.MediaEnded += (_, _) => { player.Pause(); toggle.Content = PlayLabel; };
        seek.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => dragging = true));
        seek.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => { dragging = false; player.Position = TimeSpan.FromSeconds(seek.Value); }));
        seek.PreviewMouseLeftButtonUp += (_, _) => { if (!dragging) player.Position = TimeSpan.FromSeconds(seek.Value); };
        toggle.Click += (_, _) => TogglePlayback(player, toggle);

        _playerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _playerTimer.Tick += (_, _) =>
        {
            if (!dragging) seek.Value = Math.Min(seek.Maximum, player.Position.TotalSeconds);
            var length = player.NaturalDuration.HasTimeSpan ? MediaDisplayFormat.Duration(player.NaturalDuration.TimeSpan.TotalSeconds) : file.DurationText;
            time.Text = $"{MediaDisplayFormat.Duration(player.Position.TotalSeconds)} / {length}";
        };
        _playerTimer.Start();

        var scrub = new DockPanel { Margin = new Thickness(0, 8, 0, 14) };
        DockPanel.SetDock(toggle, Dock.Left);
        DockPanel.SetDock(time, Dock.Right);
        scrub.Children.Add(toggle);
        scrub.Children.Add(time);
        scrub.Children.Add(seek);
        OverlayBody.Children.Add(scrub);
        AddGroupSwitcher(file, player);

        _player = player;
        _playerToggle = toggle;
        player.Source = new Uri(file.Path);
        player.Play();
        SetButtons((L.T("既定のプレーヤーで開く"), "Ghost", () => OpenWithDefault(file.Path)), (L.T("エクスプローラーで表示"), "Ghost", () => RevealInExplorer(file.Path)), (L.T("閉じる"), "BtnBase", ClosePlayer));
    }

    /// <summary>Buttons for the other files of the same group; switching keeps the current position.</summary>
    private void AddGroupSwitcher(OrganizeFile current, MediaElement player)
    {
        var group = Vm?.GroupOf(current.MediaFileId);
        if (group is null || group.Files.Count < 2) return;
        OverlayBody.Children.Add(Muted(L.T("同じ組のファイルに切り替える（同じ位置から再生します）"), 12, new Thickness(0, 12, 0, 4), bold: true));
        var row = new WrapPanel();
        foreach (var choice in group.Choices)
        {
            var label = $"{choice.Letter}  {choice.File.Location}{(choice.IsKeep ? L.T("（残す）") : string.Empty)}";
            var button = new Button { Content = label, Style = (Style)FindResource(choice.File.MediaFileId == current.MediaFileId ? "Primary" : "Small"), Margin = new Thickness(0, 0, 8, 6), ToolTip = choice.File.Path };
            var file = choice.File;
            button.Click += (_, _) => { if (file.MediaFileId != current.MediaFileId) ShowPlayer(file, player.Position); };
            row.Children.Add(button);
        }
        OverlayBody.Children.Add(row);
    }

    private static void TogglePlayback(MediaElement player, Button toggle)
    {
        if (Equals(toggle.Content, PauseLabel)) { player.Pause(); toggle.Content = PlayLabel; }
        else { player.Play(); toggle.Content = PauseLabel; }
    }

    private void ClosePlayer()
    {
        StopPlayer();
        HideOverlay();
    }

    private void StopPlayer()
    {
        _playerTimer?.Stop();
        _playerTimer = null;
        if (_player is null) return;
        _player.Stop();
        _player.Close();
        _player.Source = null;
        _player = null;
    }

    /// <summary>Space toggles playback and Esc closes while the player is open.</summary>
    private bool HandlePlayerKey(KeyEventArgs e)
    {
        if (_player is null || Overlay.Visibility != Visibility.Visible) return false;
        if (e.Key == Key.Escape) { ClosePlayer(); return true; }
        if (e.Key == Key.Space && _playerToggle is { } toggle)
        {
            TogglePlayback(_player, toggle);
            return true;
        }
        return false;
    }

    /// <summary>Shown instead of the video when Windows cannot decode it (for example HEVC without the extension).</summary>
    private Border UnsupportedBox(OrganizeFile file)
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 460 };
        panel.Children.Add(new TextBlock { Text = L.T("このアプリでは再生できない形式です"), FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock
        {
            Text = L.T("HEVC（H.265）などの動画は、Windowsに「HEVC ビデオ拡張機能」（Microsoft Store）を入れると再生できます。既定のプレーヤーで開いて確認することもできます。"),
            FontSize = 13, Foreground = Res("Muted"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 8, 0, 8)
        });
        var open = Action(L.T("既定のプレーヤーで開く"), () => OpenWithDefault(file.Path), "Primary");
        open.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(open);
        return new Border { Child = panel, Height = 300, Background = Hex("#0E1217"), BorderBrush = Res("Line2"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(20) };
    }

    private static void OpenWithDefault(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static void RevealInExplorer(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
