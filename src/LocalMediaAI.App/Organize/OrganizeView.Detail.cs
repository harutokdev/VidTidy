using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LocalMediaAI.App.Organize;

/// <summary>The right-hand pane: choose which file stays, compare, and act on one group.</summary>
public partial class OrganizeView
{
    private void RenderDetail()
    {
        StopSideBySide();
        DetailPanel.Children.Clear();
        if (Vm?.Selected is not { } group)
        {
            DetailPanel.Children.Add(new TextBlock
            {
                Text = L.T("左の一覧から組を選ぶと、ここで見比べられます。"), Foreground = Res("Muted"),
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(20, 60, 20, 0)
            });
            DetailPanel.Children.Add(Keys(L.T("キー操作：J・K 次・前の組／1・2・3 残す方を選ぶ／Enter 整理リストへ／N このまま残す"), HorizontalAlignment.Center));
            return;
        }
        var exact = group.Category == GroupCategory.Exact;
        DetailPanel.Children.Add(DetailTitle(group.Category));
        DetailPanel.Children.Add(Muted(exact
            ? L.F("中身がすべて同じです（{0}）。画質も同じなので、置き場所で選びます。", group.Keep.FactsText)
            : L.F("一致率 {0}%。別々のファイルなので、{1}本を見比べて残す方を選んでください。", group.MatchPercent, group.Files.Count), 13, new Thickness(0, 2, 0, 14)));
        foreach (var choice in group.Choices) DetailPanel.Children.Add(ChoiceCard(group, choice));
        if (!group.IsReady) DetailPanel.Children.Add(Note(L.T("接続されていないドライブのファイルが含まれています。ドライブを接続すると片付けられます。"), true));
        if (exact) RenderExactActions(group); else RenderSimilarDetail(group);
        var numbers = string.Join(L.T("・"), Enumerable.Range(1, group.Files.Count));
        DetailPanel.Children.Add(Keys(!group.IsOpen ? L.T("キー操作：J・K 次・前の組")
            : exact ? L.F("キー操作：{0} 残す方を選ぶ／N このまま残す／J・K 次・前の組", numbers)
            : L.F("キー操作：{0} 残す方を選ぶ／Enter 整理リストへ／N このまま残す／J・K 次・前の組", numbers), HorizontalAlignment.Left));
    }

    private void RenderExactActions(GroupRow group)
    {
        DetailPanel.Children.Add(group.IsManual
            ? Note(L.T("この組はあなたが選び直しました。ルールを承認しても、ルールを変更しても、この選択はそのまま残ります。"), false, ok: true)
            : Note(L.T("残す方を変えたい場合は、上で選び直してください。選び直した組は、ルールを承認してもその選択が残ります。"), false));
        var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        if (group.IsManual && group.IsOpen) actions.Children.Add(SmallButton(L.T("ルールの選択に戻す"), () => { Vm?.ResetToRule(group); RenderDetail(); }));
        if (group.Status == GroupStatus.Queued) actions.Children.Add(SmallButton(L.T("整理リストから外す"), () => { Vm?.Dequeue(group); RenderDetail(); }));
        if (group.IsOpen)
        {
            actions.Children.Add(KeepAsIsButton(group));
            actions.Children.Add(SmallButton(L.T("残す方のルールを見る・変える"), () => ShowRuleDialog(backToApproval: false)));
        }
        DetailPanel.Children.Add(actions);
    }

    private void RenderSimilarDetail(GroupRow group)
    {
        DetailPanel.Children.Add(H3(L.T("ファイル情報")));
        DetailPanel.Children.Add(CompareTable(group));
        foreach (var warning in group.Warnings) DetailPanel.Children.Add(Note(warning, true));
        AddScenesAndSideBySide(group);
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        if (group.IsOpen)
        {
            var add = new Button { Content = L.T("この組を整理リストへ"), Style = (Style)FindResource("Primary"), IsEnabled = group.IsReady, Margin = new Thickness(0, 0, 8, 0) };
            add.Click += (_, _) => EnqueueSelected(group);
            actions.Children.Add(add);
            actions.Children.Add(KeepAsIsButton(group));
        }
        else if (group.Status == GroupStatus.Queued)
        {
            actions.Children.Add(new Button { Content = L.T("整理リストから外す"), Style = (Style)FindResource("BtnBase") });
            ((Button)actions.Children[0]).Click += (_, _) => { Vm?.Dequeue(group); RenderDetail(); };
        }
        DetailPanel.Children.Add(actions);
    }

    private void EnqueueSelected(GroupRow group)
    {
        if (Vm is null) return;
        if (!Vm.Enqueue(group)) { Toast(L.F("よく似ている動画と個別のファイルは、1回{0}件までです。先に実行してください", Vm.OtherLimit)); return; }
        Toast(L.T("整理リストに入れました"));
        RenderDetail();
    }

    private Button KeepAsIsButton(GroupRow group)
    {
        var button = new Button { Content = L.T("このまま残す"), Style = (Style)FindResource("Ghost"), Margin = new Thickness(0, 0, 8, 0), ToolTip = L.T("どのファイルも片付けずに、この組を一覧から外します（キー：N）") };
        button.Click += async (_, _) => await KeepAsIsAsync(group);
        return button;
    }

    private async Task KeepAsIsAsync(GroupRow group)
    {
        if (Vm is null) return;
        try
        {
            await Vm.KeepAsIsAsync(group, CancellationToken.None);
            Toast(L.T("「このまま残す」と記録しました。ファイルは変更しません（片付けパネルの「判断の記録」から取り消せます）"));
            RenderDetail();
        }
        catch (Exception ex) { ShowMessage(L.T("記録できませんでした"), ex.Message); }
    }

    private static StackPanel DetailTitle(GroupCategory category)
    {
        var (mark, color, ink, text) = category switch
        {
            GroupCategory.Exact => ("=", "#3FAE7A", "#0C1B13", L.T("完全に同じファイル")),
            GroupCategory.Similar => ("≈", "#E6C547", "#15130A", L.T("よく似ている動画")),
            _ => ("!", "#E4625B", "#1D0B0A", L.T("念のため確認が必要な動画"))
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = Hex(color), Child = new TextBlock { Text = mark, FontWeight = FontWeights.Bold, FontSize = 14, Foreground = Hex(ink), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        });
        panel.Children.Add(new TextBlock { Text = text, FontSize = 18, FontWeight = FontWeights.Bold });
        return panel;
    }

    /// <summary>One selectable file: radio, thumbnail row, role pill, why it is kept; the ⋯ menu sits outside the card.</summary>
    private Grid ChoiceCard(GroupRow group, FileChoice choice)
    {
        var radio = new RadioButton
        {
            GroupName = "keep-" + group.GroupId, IsChecked = choice.IsKeep, IsEnabled = group.IsOpen,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0)
        };
        AutomationPropertiesHelper.SetName(radio, L.F("{0}：{1} を残す", choice.Letter, choice.File.FileName));
        radio.Checked += (_, _) => { if (!choice.IsKeep) { Vm?.ChooseKeep(group, choice.File.MediaFileId); RenderDetail(); } };

        var row = new ContentPresenter { Content = choice, ContentTemplate = (DataTemplate)FindResource("ChoicePlaceRow"), Margin = new Thickness(-14, -6, -14, -6) };
        var body = new StackPanel();
        if (group.Category != GroupCategory.Exact)
            body.Children.Add(new TextBlock { Text = choice.Letter, FontSize = 11, Foreground = Res("Muted"), Margin = new Thickness(0, 0, 0, 2) });
        body.Children.Add(row);
        if (!group.IsReady && !(Vm?.Folders.FirstOrDefault(f => f.Id == choice.File.RootId)?.IsOnline ?? true))
            body.Children.Add(new TextBlock { Text = L.T("未接続"), FontSize = 11, Foreground = Res("Danger") });
        if (choice.IsKeep && Vm is not null)
            body.Children.Add(new TextBlock { Text = Vm.KeepReason(group), Foreground = Res("Keep"), FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });

        var inner = new DockPanel();
        DockPanel.SetDock(radio, Dock.Left);
        inner.Children.Add(radio);
        inner.Children.Add(body);
        var card = new Border
        {
            Child = inner, Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Cursor = System.Windows.Input.Cursors.Hand,
            BorderBrush = choice.IsKeep ? Res("Keep") : Res("Line2"),
            Background = choice.IsKeep ? Res("KeepBg") : Res("Surface")
        };
        card.MouseLeftButtonUp += (_, _) => { if (group.IsOpen && !choice.IsKeep) { Vm?.ChooseKeep(group, choice.File.MediaFileId); RenderDetail(); } };

        var menu = new Button { Style = (Style)FindResource("Kebab"), Margin = new Thickness(4, 12, 0, 0), DataContext = choice.File };
        AutomationPropertiesHelper.SetName(menu, L.F("{0}の操作（再生・エクスプローラーで表示）", choice.File.FileName));
        menu.Click += (_, _) => OpenFileMenu(menu, choice.File);

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(card);
        Grid.SetColumn(menu, 1);
        grid.Children.Add(menu);
        return grid;
    }

    /// <summary>File ⋯ menu: 再生 / エクスプローラーで表示.</summary>
    private void OpenFileMenu(FrameworkElement anchor, OrganizeFile file, bool allowQueue = false)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var play = new MenuItem { Header = MenuHeader(L.T("再生"), L.T("このアプリの中で再生します")) };
        play.Click += (_, _) => ShowPlayer(file, TimeSpan.Zero);
        var reveal = new MenuItem { Header = MenuHeader(L.T("エクスプローラーで表示"), L.T("このファイルを選んだ状態でフォルダーを開きます")) };
        reveal.Click += (_, _) => RevealInExplorer(file.Path);
        menu.Items.Add(play);
        menu.Items.Add(reveal);
        if (allowQueue)
        {
            menu.Items.Add(new Separator());
            var queue = new MenuItem { Header = MenuHeader(L.T("整理リストに入れる"), L.T("不要なファイルとして片付けの対象にします（実行前に1件ずつ確認します）")) };
            queue.Click += (_, _) => { QueueSingle(file); _ = ShowFilesDrawerAsync(null); };
            menu.Items.Add(queue);
        }
        menu.IsOpen = true;
    }

    private StackPanel MenuHeader(string title, string hint)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title });
        panel.Children.Add(new TextBlock { Text = hint, FontSize = 11, Foreground = Res("Faint") });
        return panel;
    }

    private Grid CompareTable(GroupRow group)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.35, GridUnitType.Star) });
        foreach (var _ in group.Files) grid.ColumnDefinitions.Add(new ColumnDefinition());
        var rows = group.CompareRows;
        for (var r = 0; r <= rows.Count; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var c = 0; c < group.Files.Count; c++) Cell(grid, new TextBlock { Text = ((char)('A' + c)).ToString(), Foreground = Res("Muted") }, 0, c + 1);
        Cell(grid, new TextBlock(), 0, 0);
        for (var r = 0; r < rows.Count; r++)
        {
            Cell(grid, new TextBlock { Text = rows[r].Label, Foreground = Res("Muted") }, r + 1, 0);
            for (var c = 0; c < rows[r].Cells.Count; c++)
            {
                var cell = rows[r].Cells[c];
                Cell(grid, new TextBlock
                {
                    Text = cell.IsBest ? cell.Text + " ▲" : cell.Text,
                    Foreground = cell.IsBest ? Res("Keep") : Res("Text"),
                    FontWeight = cell.IsBest ? FontWeights.Bold : FontWeights.Normal
                }, r + 1, c + 1);
            }
        }
        return grid;
    }

    private void Cell(Grid grid, TextBlock text, int row, int column)
    {
        text.FontSize = 13;
        var border = new Border { Child = text, Padding = new Thickness(8, 6, 8, 6), BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, 1) };
        Grid.SetRow(border, row);
        Grid.SetColumn(border, column);
        grid.Children.Add(border);
    }

    private static TextBlock H3(string text) => new() { Text = text, FontSize = 13, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 14, 0, 4) };

    private TextBlock Keys(string text, HorizontalAlignment alignment) => new()
    {
        Text = text, FontSize = 11, Foreground = Res("Faint"), Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap, HorizontalAlignment = alignment
    };

    private Button SmallButton(string label, Action onClick)
    {
        var button = new Button { Content = label, Style = (Style)FindResource("Small"), Margin = new Thickness(0, 0, 8, 0) };
        button.Click += (_, _) => onClick();
        return button;
    }

    private Brush Res(string key) => (Brush)FindResource(key);

    private static Brush Hex(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;
}
