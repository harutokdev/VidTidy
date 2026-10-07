using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using LocalMediaAI.Core.Organize;

namespace LocalMediaAI.App.Organize;

/// <summary>
/// 残す方のルール — only the keep rule (判定の順番 and フォルダーの優先順位), with how many files each folder would give
/// up under it, updated as the user reorders. Opened from the approval dialog, which it returns to.
/// </summary>
public partial class OrganizeView
{
    private void ShowRuleDialog(bool backToApproval)
    {
        if (Vm is null) return;
        var settings = Vm.Settings;
        var criteria = settings.Criteria.ToList();
        var folders = Vm.Folders.Select(f => f.Folder).ToList();
        var dateDir = new ComboBox { Width = 190, HorizontalAlignment = HorizontalAlignment.Left, SelectedIndex = settings.DateDirection == KeepDateDirection.Oldest ? 0 : 1 };
        dateDir.Items.Add(L.T("更新日時が古い方"));
        dateDir.Items.Add(L.T("更新日時が新しい方"));
        AutomationPropertiesHelper.SetName(dateDir, L.T("更新日時の向き"));

        OrganizeSettings Candidate() => settings with
        {
            Criteria = criteria.ToArray(),
            DateDirection = dateDir.SelectedIndex == 1 ? KeepDateDirection.Newest : KeepDateDirection.Oldest,
            FolderOrder = folders.Select(f => f.Id).ToArray()
        };

        OpenModal(L.T("残す方のルール"), L.T("完全に同じファイルが複数あるとき、どれを残すかの決め方です。上から順に判定します。"), Mark(GroupCategory.Exact));
        var preview = new StackPanel();
        void Update() => FillRulePreview(preview, Vm.PreviewRule(Candidate()));
        dateDir.SelectionChanged += (_, _) => Update();

        OverlayBody.Children.Add(FieldLabel(L.T("判定の順番（つかんで並べ替えます。上から順に判定します）")));
        OverlayBody.Children.Add(ReorderList(criteria, (c, i) => OrderItem(i == 0 ? "①" : "②", null,
            c == KeepCriterion.FolderPriority ? new TextBlock { Text = L.T("フォルダーの優先順位が高い方"), VerticalAlignment = VerticalAlignment.Center } : dateDir, null),
            c => c == KeepCriterion.FolderPriority ? L.T("フォルダーの優先順位") : L.T("更新日時"), Update));
        OverlayBody.Children.Add(FieldLabel(L.T("フォルダーの優先順位（つかんで並べ替えます。上ほど優先して残します）")));
        OverlayBody.Children.Add(ReorderList(folders, (f, i) => OrderItem((i + 1).ToString(CultureInfo.InvariantCulture), FolderGlyph(), new TextBlock { Text = f.Name }, f.Path), f => f.Name, Update));
        OverlayBody.Children.Add(FieldLabel(L.T("このルールで片付ける本数（承認を待っている組）")));
        OverlayBody.Children.Add(new Border { Child = preview, BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10), Background = Hex("#171D25") });
        OverlayBody.Children.Add(Hint(L.T("並べ替えると、本数がその場で変わります。キーボードでは、項目を選んで Alt + ↑↓ で動かせます。")));
        Update();

        void Back()
        {
            if (backToApproval) ShowApproval(); else HideOverlay();
        }
        SetButtons((backToApproval ? L.T("変えずに戻る") : L.T("キャンセル"), "BtnBase", Back), (L.T("このルールにする"), "Primary", async () =>
        {
            try
            {
                await Vm.SaveSettingsAsync(Candidate(), CancellationToken.None);
                Toast(L.T("ルールを変えました。手動で選んだ組以外は、残す方を選び直しました"));
                RefreshChrome();
                RenderDetail();
                Back();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { ShowMessage(L.T("ルールを保存できませんでした"), ex.Message); }
        }));
    }

    /// <summary>One row per folder: name and rank, a bar, the count and how it differs from the current rule.</summary>
    private void FillRulePreview(StackPanel panel, IReadOnlyList<RulePreviewRow> rows)
    {
        panel.Children.Clear();
        if (rows.All(r => r.Now == 0 && r.Next == 0))
        {
            panel.Children.Add(Muted(L.T("いま承認を待っている組はありません。次に見つかった組から、このルールを使います。"), 13));
            return;
        }
        var max = Math.Max(1, rows.Max(r => r.Next));
        foreach (var row in rows)
        {
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            var name = new StackPanel { Orientation = Orientation.Horizontal };
            name.Children.Add(FolderGlyph());
            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new TextBlock { Text = row.Folder.Name });
            label.Children.Add(new TextBlock { Text = L.F("優先{0}", row.Rank), FontSize = 11, Foreground = Res("Faint") });
            name.Children.Add(label);
            grid.Children.Add(name);
            var bar = Bar(row.Next * 100.0 / max, 6, "Trash");
            bar.VerticalAlignment = VerticalAlignment.Center;
            bar.Margin = new Thickness(0, 0, 12, 0);
            Grid.SetColumn(bar, 1);
            grid.Children.Add(bar);
            var numbers = new TextBlock { TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
            numbers.Inlines.Add(new Run(row.Next == 0 ? L.T("0本 片付ける") : L.F("{0}本 片付ける（{1}）", row.Next, MediaDisplayFormat.FileSize(row.NextBytes))));
            numbers.Inlines.Add(new LineBreak());
            var diff = row.Next - row.Now;
            numbers.Inlines.Add(new Run(diff == 0 ? L.T("今のルールと同じ") : L.F("今のルールより {0}{1}本", (diff > 0 ? "+" : string.Empty), diff))
            {
                FontSize = 11, Foreground = diff == 0 ? Res("Faint") : Res("AccentHi")
            });
            Grid.SetColumn(numbers, 2);
            grid.Children.Add(numbers);
            panel.Children.Add(grid);
        }
        panel.Children.Add(Muted(L.T("どの組も1本は残ります。あなたが選び直した組は、ルールを変えてもそのままです。"), 12, new Thickness(0, 6, 0, 0)));
    }

    /// <summary>はっきりした組をまとめて承認 — the conditions, the groups with what stays and what goes, then into the cleanup list.</summary>
    private void ShowClearApproval()
    {
        if (Vm is null) return;
        var plan = Vm.PrepareClearApproval();
        OpenModal(L.T("はっきりした組をまとめて承認"), L.T("承認すると、片付けるファイルが整理リストに入ります。この時点ではファイルは変更されません。"), Mark(GroupCategory.Similar));
        var title = H3(L.T("次の条件をすべて満たす組です"));
        title.Margin = new Thickness(0, 0, 0, 4);
        OverlayBody.Children.Add(title);
        foreach (var condition in new[]
                 {
                     L.F("一致率が{0:0}%以上", ClearSimilarRule.MinimumMatch * 100),
                     L.F("長さの差が{0:0}秒以内（片方にしかない場面がない）", ClearSimilarRule.MaximumLengthDifferenceSeconds),
                     L.T("残す方の解像度とビットレートが、ほかと同じか上")
                 })
            OverlayBody.Children.Add(new TextBlock { Text = L.T("・") + condition, FontSize = 13, Foreground = Res("Muted"), Margin = new Thickness(6, 1, 0, 1) });

        var line = new TextBlock { FontSize = 17, Margin = new Thickness(0, 12, 0, 6), TextWrapping = TextWrapping.Wrap };
        line.Inlines.Add(new Bold(new Run(L.F("{0}組・{1}本・{2}", plan.Groups.Count, plan.FileCount, MediaDisplayFormat.FileSize(plan.Bytes)))));
        line.Inlines.Add(new Run(L.T(" を片付けます。")));
        OverlayBody.Children.Add(line);
        if (plan.LeftForNextRun > 0) OverlayBody.Children.Add(Note(L.F("よく似ている動画は1回{0}件までのため、残り {1}組 は次の回になります。", Vm.OtherLimit, plan.LeftForNextRun), true));
        foreach (var group in plan.Groups) OverlayBody.Children.Add(ClearGroupCard(group));
        OverlayBody.Children.Add(Note(L.T("気になる組は「戻って選び直す」で一覧に戻り、1組ずつ見比べられます。"), false));

        var buttons = SetButtons((L.T("戻って選び直す"), "BtnBase", HideOverlay), (L.T("承認して整理リストへ"), "Primary", () =>
        {
            var added = Vm.ApproveClear();
            HideOverlay();
            RenderDetail();
            Toast(L.F("{0}組を整理リストに入れました（実行のときの1件ずつのチェックは要りません）", added));
        }));
        buttons[1].IsEnabled = plan.Groups.Count > 0;
    }

    /// <summary>The kept file with its facts and match, then each file to clean up with its facts.</summary>
    private Border ClearGroupCard(GroupRow group)
    {
        var panel = new StackPanel();
        var keep = new DockPanel { Background = Hex("#15261F") };
        var match = PillOf(L.F("一致率 {0}%", group.MatchPercent), "info");
        match.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(match, Dock.Right);
        keep.Children.Add(match);
        var keepLine = FileLine(group.Keep, PillOf(L.T("残す"), "keep"));
        keepLine.Margin = new Thickness(10, 4, 10, 4);
        keep.Children.Add(keepLine);
        panel.Children.Add(keep);
        var trash = new StackPanel { Margin = new Thickness(10, 4, 10, 6) };
        foreach (var file in group.CleanupFiles) trash.Children.Add(FileLine(file, PillOf(L.T("片付ける"), "trash")));
        panel.Children.Add(new Border { Child = trash, BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 1, 0, 0) });
        return new Border { Child = panel, Margin = new Thickness(0, 8, 0, 0), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), ClipToBounds = true };
    }
}
