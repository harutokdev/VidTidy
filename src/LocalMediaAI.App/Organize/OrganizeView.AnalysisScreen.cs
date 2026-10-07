using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using LocalMediaAI.Core.Organize;

namespace LocalMediaAI.App.Organize;

/// <summary>
/// The analysis screen (解析しています) and the change-check screen (前回からの変更を確認しています).
/// The screen is built once and then updated in place: bars glide to each new value, and the elapsed time and
/// countdown tick every half second between polls, so nothing jumps or blinks when new numbers arrive.
/// </summary>
public partial class OrganizeView
{
    private static readonly Duration Glide = new(TimeSpan.FromMilliseconds(900));
    private readonly List<Action> _live = [];
    private readonly List<Action> _clock = [];
    private string? _liveKey;

    private void RenderAnalysisScreen()
    {
        if (Vm is null) return;
        // Rebuilt only when the layout itself changes (another step, folders added or disconnected).
        var key = $"{_showAnalysis}|{Vm.IsChangeCheck}|{Vm.IsScanning}|{string.Join(",", Vm.Folders.Select(f => $"{f.Id}:{f.IsOnline}"))}";
        if (key != _liveKey)
        {
            _liveKey = key;
            _live.Clear();
            _clock.Clear();
            AnalysisPanel.Children.Clear();
            if (_showAnalysis) AnalysisPanel.Children.Add(Action(L.T("← 重複の一覧に戻る"), () => { _showAnalysis = false; RefreshChrome(); }, "Small"));
            if (Vm.IsChangeCheck) BuildCheckScreen(); else BuildScanScreen();
        }
        foreach (var update in _live) update();
        UpdateClock();
    }

    private void UpdateClock()
    {
        foreach (var update in _clock) update();
    }

    // ---------- 解析しています ----------
    private void BuildScanScreen()
    {
        AddHead(() => L.T("解析しています"));
        AddTimeLine(() => L.T("目安・ファイルの大きさで前後します"));
        AnalysisPanel.Children.Add(LiveBar(() => Vm!.ProgressPercent, 8));
        AddCurrentLine(() => L.T("いま解析中: "), () => Vm!.CurrentWorkText);

        var card = Card();
        foreach (var folder in Vm!.Folders) card.Children.Add(ScanFolderRow(folder));
        AnalysisPanel.Children.Add(CardBorder(card));
        AnalysisPanel.Children.Add(Reassure(
            L.T("大きな動画（4Kや長時間の録画）は1本に数分かかることがあります。数字が止まって見えても、左の点が点滅していれば処理は続いています。"),
            L.T("見つかった重複は、解析が終わるのを待たずにこの画面に表示され、すぐ片付けを始められます。"),
            L.T("アプリを閉じても大丈夫です。次に開いたときに続きから解析します。")));
    }

    private Grid ScanFolderRow(FolderRow folder)
    {
        FolderProgress? Row() => Vm?.FolderProgress.FirstOrDefault(f => f.Folder.Id == folder.Id);
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var glyph = FolderGlyph();
        glyph.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(glyph);
        var body = new StackPanel();
        body.Children.Add(FolderTitle(folder));
        var stages = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        _live.Add(() => FillStages(stages, Row()));
        body.Children.Add(stages);
        var bar = LiveBar(() => Row()?.Percent ?? 0, 6);
        bar.Margin = new Thickness(0, 4, 0, 0);
        body.Children.Add(bar);
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        var right = new TextBlock { FontSize = 12, Foreground = Res("Muted"), TextAlignment = TextAlignment.Right, Margin = new Thickness(12, 0, 0, 0) };
        _live.Add(() =>
        {
            if (Row() is not { } row) return;
            var count = row.Folder.Folder.FileCount;
            right.Inlines.Clear();
            right.Inlines.Add(new Run(L.F("{0:N0} / {1:N0}本", (int)(count * row.Percent / 100), count)));
            right.Inlines.Add(new LineBreak());
            right.Inlines.Add(row.State == "run" ? new Run(Vm!.EtaText) : new Run(L.T("完了")) { Foreground = Res("Keep") });
        });
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        return grid;
    }

    private void FillStages(WrapPanel stages, FolderProgress? row)
    {
        if (row is null) return;
        stages.Children.Clear();
        for (var i = 0; i < AnalysisStages.Names.Count; i++)
        {
            var done = i < row.Stage;
            var now = i == row.Stage && row.State == "run";
            stages.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 4), BorderThickness = new Thickness(1),
                BorderBrush = done ? System.Windows.Media.Brushes.Transparent : now ? Res("Accent") : Res("Line2"),
                Background = done ? Res("KeepBg") : System.Windows.Media.Brushes.Transparent,
                Child = new TextBlock { Text = (done ? "✓ " : string.Empty) + AnalysisStages.Names[i], FontSize = 12, Foreground = done ? Res("Keep") : now ? Res("Text") : Res("Faint") }
            });
        }
    }

    // ---------- 前回からの変更を確認しています ----------
    private void BuildCheckScreen()
    {
        var scanning = Vm!.IsScanning;
        AddHead(() => scanning ? L.T("前回からの変更を確認しています") : L.T("新しいファイルを解析しています"));
        AddTimeLine(() => scanning ? L.T("変更の確認が終わるまでの目安です。新しいファイルが見つかれば、続けてその解析をします") : L.T("目安・ファイルの大きさで前後します"));
        AnalysisPanel.Children.Add(LiveBar(() => Vm!.ProgressPercent, 8));
        AddCurrentLine(() => Vm!.IsScanning ? L.T("確認中：") : L.T("解析中："), () => Vm!.CurrentWorkText);

        AnalysisPanel.Children.Add(SectionTitle(L.T("1. 変更の確認"), L.T("ディスクに負担をかけないよう、フォルダーを1つずつ順番に確認します")));
        var folders = Card();
        foreach (var folder in Vm.Folders.Where(f => f.IsOnline)) folders.Children.Add(CheckFolderRow(folder));
        foreach (var off in Vm.Folders.Where(f => !f.IsOnline))
        {
            var line = new DockPanel { Margin = new Thickness(0, 4, 0, 4), Opacity = 0.6 };
            var note = new TextBlock { Text = L.T("接続されていないので確認できません"), FontSize = 12, Foreground = Res("Danger") };
            DockPanel.SetDock(note, Dock.Right);
            line.Children.Add(note);
            line.Children.Add(FolderGlyph(0.4));
            line.Children.Add(new TextBlock { Text = off.Name, FontWeight = FontWeights.Bold });
            folders.Children.Add(line);
        }
        AnalysisPanel.Children.Add(CardBorder(folders));

        var filesTitle = SectionTitle(L.T("2. 新しい・変わったファイルの解析"), string.Empty);
        _live.Add(() =>
        {
            var remaining = Vm!.FolderProgress.Where(f => f.Stage > 0).Sum(f => f.Remaining);
            ((Run)filesTitle.Inlines.LastInline).Text = "  " + (scanning ? L.T("変更の確認が終わると始まります") : L.F("あと {0:N0}件・1本ずつ", remaining));
        });
        AnalysisPanel.Children.Add(filesTitle);
        var files = Card();
        var filesText = new TextBlock { FontSize = 12, Foreground = Res("Muted"), TextWrapping = TextWrapping.Wrap };
        _live.Add(() => filesText.Text = scanning ? L.T("変更の確認が終わると始まります") : Vm!.CurrentWorkText.Length > 0 ? L.F("解析中：{0}", Vm.CurrentWorkText) : L.T("順番に解析しています"));
        files.Children.Add(filesText);
        AnalysisPanel.Children.Add(CardBorder(files));

        AnalysisPanel.Children.Add(Reassure(
            L.T("変わっていないファイルは、前回の解析結果をそのまま使うので、確認だけで済みます。"),
            Vm.IsAutoCheck ? L.T("起動時に自動で確認しています。設定で「手動で確認する」に変えられます。") : L.T("「変更を確認」を押したので確認しています。")));
    }

    private Grid CheckFolderRow(FolderRow folder)
    {
        FolderProgress? Row() => Vm?.FolderProgress.FirstOrDefault(f => f.Folder.Id == folder.Id);
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        var glyph = FolderGlyph();
        _live.Add(() => glyph.Opacity = Row()?.State == "wait" ? 0.4 : 1);
        grid.Children.Add(glyph);
        var body = new StackPanel();
        body.Children.Add(FolderTitle(folder));
        var bar = LiveBar(
            () => Row() is not { } row ? 0 : row.State == "done" ? 100 : row.Stage == 0 ? row.ScanPercent : row.Percent,
            4, () => Row()?.State == "done" ? "Keep" : "Accent");
        bar.Margin = new Thickness(0, 5, 0, 0);
        body.Children.Add(bar);
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        var right = new TextBlock { FontSize = 12, Foreground = Res("Muted"), TextAlignment = TextAlignment.Right };
        _live.Add(() =>
        {
            if (Row() is not { } row) return;
            right.Inlines.Clear();
            foreach (var inline in CheckFolderStatus(row)) right.Inlines.Add(inline);
        });
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>State on the first line, then how many files, then what was found.</summary>
    private IEnumerable<Inline> CheckFolderStatus(FolderProgress row)
    {
        var expected = row.Folder.Folder.FileCount;
        if (row.State == "wait")
        {
            yield return new Run(L.T("順番待ち")) { Foreground = Res("Faint") };
            yield return new LineBreak();
            yield return new Run(L.F("前回 {0:N0}本", expected));
            yield break;
        }
        if (row.State == "run" && row.Stage == 0)
        {
            yield return new Run(L.F("確認中 {0:0}%", row.ScanPercent)) { Foreground = Res("AccentHi") };
            yield return new LineBreak();
            yield return new Run(expected > 0 ? L.F("{0:N0} / 約{1:N0}本", row.ScanSeen, expected) : L.F("{0:N0}本", row.ScanSeen));
            yield break;
        }
        yield return row.State == "done" ? new Run(L.T("✓ 確認済み")) { Foreground = Res("Keep") } : new Run(L.F("新しいファイルを解析中・あと {0:N0}件", row.Remaining)) { Foreground = Res("AccentHi") };
        if (row.Scan is not { } scan) yield break;
        yield return new LineBreak();
        yield return new Run(L.F("{0:N0}本", scan.Seen));
        // The scanner counts every re-seen file as updated, so only new and missing files are reported.
        var found = string.Join(L.T("・"), new[] { scan.Added > 0 ? L.F("新しい {0}", scan.Added) : null, scan.Unavailable > 0 ? L.F("消えた {0}", scan.Unavailable) : null }.Where(x => x is not null));
        yield return new LineBreak();
        yield return new Run(found.Length > 0 ? found : row.State == "done" ? L.T("変更なし") : string.Empty);
    }

    // ---------- shared parts ----------

    /// <summary>Title with the big percent next to it.</summary>
    private void AddHead(Func<string> title)
    {
        var head = new WrapPanel { Margin = new Thickness(0, 10, 0, 4) };
        var titleText = new TextBlock { FontSize = 22, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 14, 0) };
        var percent = new TextBlock { FontSize = 22, Foreground = Res("AccentHi"), VerticalAlignment = VerticalAlignment.Bottom };
        _live.Add(() =>
        {
            titleText.Text = title();
            percent.Text = $"{Vm!.ProgressPercent:0}%";
        });
        head.Children.Add(titleText);
        head.Children.Add(percent);
        AnalysisPanel.Children.Add(head);
    }

    /// <summary>経過 1分23秒・残り 約3分20秒・14:52 ごろ終わる予定 — refreshed every tick, not only on polls.</summary>
    private void AddTimeLine(Func<string> hint)
    {
        var line = new TextBlock { FontSize = 13, Foreground = Res("Muted"), Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap };
        var elapsed = new Run();
        var left = new Bold(new Run()) { Foreground = Res("Text") };
        var finish = new Run();
        var note = new Run { FontSize = 12, Foreground = Res("Faint") };
        line.Inlines.Add(elapsed);
        line.Inlines.Add(new Run(L.T("　・　")));
        line.Inlines.Add(left);
        line.Inlines.Add(finish);
        line.Inlines.Add(new LineBreak());
        line.Inlines.Add(note);
        _live.Add(() => note.Text = hint());
        _clock.Add(() =>
        {
            if (Vm is null) return;
            var now = DateTimeOffset.UtcNow;
            elapsed.Text = Vm.AnalysisStartedAt is { } started ? L.F("経過 {0}", Clock(now - started)) : L.T("経過 —");
            var remaining = Vm.TimeLeft(now);
            ((Run)left.Inlines.FirstInline).Text = remaining switch
            {
                null => L.T("残り時間を計算しています…"),
                { TotalSeconds: < 1 } => L.T("まもなく終わります"),
                { } time => L.F("残り {0}", FinishClock.Format(time))
            };
            finish.Text = remaining is { TotalSeconds: >= 1 } && Vm.FinishAt is { } at ? L.F("　・　{0:HH:mm} ごろ終わる予定", at.ToLocalTime()) : string.Empty;
        });
        AnalysisPanel.Children.Add(line);
    }

    private static string Clock(TimeSpan span) =>
        span.TotalHours >= 1 ? L.F("{0}時間{1}分", (int)span.TotalHours, span.Minutes) : L.F("{0}分{1:00}秒", (int)span.TotalMinutes, span.Seconds);

    /// <summary>A bar that glides to each new value. It jumps only when the value goes back (a new step began).</summary>
    private Border LiveBar(Func<double> value, double height, Func<string>? fill = null)
    {
        var bar = Bar(0, height);
        var progress = (ProgressBar)bar.Child;
        double? target = null;
        _live.Add(() =>
        {
            var to = value();
            if (fill is not null) progress.Foreground = Res(fill());
            if (target is null || to < target)
            {
                target = to;
                progress.BeginAnimation(RangeBase.ValueProperty, null);
                progress.Value = to;
                return;
            }
            if (Math.Abs(to - target.Value) < 0.01) return;
            target = to;
            progress.BeginAnimation(RangeBase.ValueProperty, new DoubleAnimation(to, Glide) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        });
        return bar;
    }

    private TextBlock FolderTitle(FolderRow folder)
    {
        var title = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        title.Inlines.Add(new Bold(new Run(folder.Name)));
        title.Inlines.Add(new Run("  " + folder.Path) { FontSize = 12, Foreground = Res("Faint") });
        return title;
    }

    private void AddCurrentLine(Func<string> label, Func<string> current)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 12) };
        var dot = new Ellipse { Width = 8, Height = 8, Fill = Res("AccentHi"), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.BeginAnimation(OpacityProperty, Pulse());
        line.Children.Add(dot);
        var text = new TextBlock { FontSize = 13, Foreground = Res("Muted"), TextTrimming = TextTrimming.CharacterEllipsis };
        var labelRun = new Run();
        var currentRun = new Run();
        text.Inlines.Add(labelRun);
        text.Inlines.Add(new Bold(currentRun) { Foreground = Res("Text") });
        line.Children.Add(text);
        _live.Add(() =>
        {
            var now = current();
            line.Visibility = now.Length == 0 ? Visibility.Hidden : Visibility.Visible;
            labelRun.Text = label();
            currentRun.Text = now;
        });
        AnalysisPanel.Children.Add(line);
    }

    private TextBlock SectionTitle(string title, string hint)
    {
        var text = new TextBlock { Margin = new Thickness(0, 12, 0, 6), TextWrapping = TextWrapping.Wrap };
        text.Inlines.Add(new Bold(new Run(title)) { FontSize = 13 });
        text.Inlines.Add(new Run("  " + hint) { FontSize = 12, Foreground = Res("Faint") });
        return text;
    }

    private static StackPanel Card() => new();

    private Border CardBorder(StackPanel content) => new()
    {
        Child = content, Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 6, 0, 6), CornerRadius = new CornerRadius(8),
        BorderBrush = Res("Line"), BorderThickness = new Thickness(1), Background = Hex("#171D25")
    };

    private Border Reassure(params string[] lines)
    {
        var panel = new StackPanel();
        foreach (var line in lines) panel.Children.Add(new TextBlock { Text = line, FontSize = 13, Foreground = Res("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        return new Border { Child = panel, BorderBrush = Res("Line2"), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 2, 0, 0), Margin = new Thickness(0, 14, 0, 0) };
    }
}
