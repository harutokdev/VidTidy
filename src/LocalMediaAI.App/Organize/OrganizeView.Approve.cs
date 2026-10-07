using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using LocalMediaAI.Core.Organize;

namespace LocalMediaAI.App.Organize;

/// <summary>ルールを確認して承認 — per-folder counts, optional group list, then everything goes to the cleanup list.</summary>
public partial class OrganizeView
{
    private bool _approvalShowsGroups;

    private void ShowApproval()
    {
        if (Vm is null) return;
        var plan = Vm.PrepareExactApproval();
        OpenModal(L.T("ルールを確認して承認"), L.T("承認すると、片付けるファイルがまとめて整理リストに入ります。この時点ではファイルは変更されません。"), Mark(GroupCategory.Exact));

        var ruleTitle = H3(L.T("残す方のルール"));
        ruleTitle.Margin = new Thickness(0, 0, 0, 4);
        OverlayBody.Children.Add(ruleTitle);
        var rule = RuleBoxCopy();
        rule.Margin = new Thickness(0, 0, 0, 14);
        OverlayBody.Children.Add(rule);

        var line = new TextBlock { FontSize = 17, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
        line.Inlines.Add(new Bold(new Run(L.F("{0}組・{1}本・{2}", plan.Summary.GroupCount, plan.Summary.FileCount, MediaDisplayFormat.FileSize(plan.Summary.TotalBytes)))));
        line.Inlines.Add(new Run(L.T(" を片付けます。")));
        line.Inlines.Add(new Run(L.T("どの組も1本は残ります。")) { Foreground = Res("Muted") });
        OverlayBody.Children.Add(line);

        OverlayBody.Children.Add(H3(L.T("各フォルダーから片付ける本数")));
        OverlayBody.Children.Add(FolderCounts(plan));
        OverlayBody.Children.Add(new TextBlock
        {
            Text = L.T("「同じフォルダー内の重複」は、同じフォルダーに同じ動画が複数あるものです。1本は残るので、そのフォルダーから動画がなくなることはありません。"),
            FontSize = 12, Foreground = Res("Faint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0)
        });
        AddApprovalNotes(plan);

        var toggle = Link(_approvalShowsGroups ? L.T("▾ 組の一覧を閉じる") : L.F("▸ 組の一覧を見る（{0}組・確認は任意です）", plan.Groups.Count), () => { _approvalShowsGroups = !_approvalShowsGroups; ShowApproval(); });
        toggle.Margin = new Thickness(0, 12, 0, 0);
        toggle.HorizontalAlignment = HorizontalAlignment.Left;
        OverlayBody.Children.Add(toggle);
        if (_approvalShowsGroups) foreach (var group in plan.Groups) OverlayBody.Children.Add(ApprovalGroup(group));

        var buttons = SetButtons((L.T("戻って選び直す"), "BtnBase", HideOverlay), (L.T("承認して整理リストへ"), "Primary", Approve));
        buttons[1].IsEnabled = plan.Groups.Count > 0;
    }

    private void Approve()
    {
        if (Vm is null) return;
        var result = Vm.ApproveExact();
        _approvalShowsGroups = false;
        HideOverlay();
        RenderDetail();
        Toast(L.F("{0}組を整理リストに入れました", result.Added)
              + (result.LeftForNextRun > 0 ? L.F("。残り{0}組は次の回に入ります", result.LeftForNextRun) : string.Empty)
              + (result.WaitingForDrive > 0 ? L.F("。接続待ちの{0}組は除きました", result.WaitingForDrive) : string.Empty));
    }

    private Border FolderCounts(ExactApprovalPlan plan)
    {
        var list = new StackPanel();
        var max = Math.Max(1, Vm!.Folders.Max(f => plan.Summary.For(f.Id).FileCount));
        foreach (var folder in Vm.Folders)
        {
            var count = plan.Summary.For(folder.Id);
            var row = new Grid { Margin = new Thickness(12, 9, 12, 9) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            var name = new StackPanel { Orientation = Orientation.Horizontal };
            name.Children.Add(FolderGlyph());
            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new TextBlock { Text = folder.Name });
            label.Children.Add(new TextBlock { Text = L.F("優先{0}・{1}", folder.Rank, folder.Drive), FontSize = 11, Foreground = Res("Faint") });
            name.Children.Add(label);
            row.Children.Add(name);
            var bar = Bar(count.FileCount * 100.0 / max, 6, "Trash");
            bar.Margin = new Thickness(0, 0, 12, 0);
            bar.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(bar, 1);
            row.Children.Add(bar);
            var numbers = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            numbers.Children.Add(new TextBlock { Text = count.FileCount == 0 ? L.T("0本") : L.F("{0}本・{1}", count.FileCount, MediaDisplayFormat.FileSize(count.Bytes)), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Right });
            if (count.SameFolderCount > 0) numbers.Children.Add(new TextBlock { Text = L.F("うち同じフォルダー内の重複 {0}本", count.SameFolderCount), FontSize = 11, Foreground = Res("Faint"), HorizontalAlignment = HorizontalAlignment.Right });
            Grid.SetColumn(numbers, 2);
            row.Children.Add(numbers);
            list.Children.Add(new Border { Child = row, BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, folder == Vm.Folders.Last() ? 0 : 1) });
        }
        return new Border { Child = list, BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) };
    }

    private void AddApprovalNotes(ExactApprovalPlan plan)
    {
        var top = Vm!.Folders.FirstOrDefault(f => f.Rank == 1);
        if (top is not null && Vm.Settings.Criteria[0] == KeepCriterion.Date)
        {
            var count = plan.Summary.For(top.Id);
            var cross = count.FileCount - count.SameFolderCount;
            if (cross > 0)
            {
                var text = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = Res("WarnText") };
                text.Inlines.Add(new Run(L.F("判定の順番で「更新日時」を先にしているため、優先1の「{0}」にあるファイルも、ほかのフォルダーのファイルより後回しにされ {1}本 片付けます。", top.Name, cross)));
                var fix = new Hyperlink(new Run(L.T("判定の順番を見直す"))) { Foreground = Res("AccentHi"), TextDecorations = null };
                fix.Click += (_, _) => ShowRuleDialog(backToApproval: true);
                text.Inlines.Add(fix);
                OverlayBody.Children.Add(Note(text, true));
            }
        }
        if (plan.ManualCount > 0) OverlayBody.Children.Add(Note(L.F("あなたが選び直した {0}組 は、その選択のまま承認します。", plan.ManualCount), false, ok: true));
        if (plan.LeftForNextRun > 0) OverlayBody.Children.Add(Note(L.F("1回に承認できるのは{0}件までのため、残り {1}組 は次の回になります。", Vm.ExactLimit, plan.LeftForNextRun), true));
        if (plan.WaitingForDrive > 0) OverlayBody.Children.Add(Note(L.F("接続されていないドライブを含む {0}組 は、接続するまで対象外です。", plan.WaitingForDrive), false));
    }

    /// <summary>The green rule box with a link to the rule-only dialog, which comes back here.</summary>
    private Border RuleBoxCopy()
    {
        var text = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        AddRule(text.Inlines, withLabel: false);
        var edit = new Hyperlink(new Run(L.T("  ルールを変更"))) { Foreground = Res("AccentHi"), TextDecorations = null };
        edit.Click += (_, _) => ShowRuleDialog(backToApproval: true);
        text.Inlines.Add(edit);
        var panel = new DockPanel();
        var mark = Mark(GroupCategory.Exact);
        DockPanel.SetDock(mark, Dock.Left);
        panel.Children.Add(mark);
        panel.Children.Add(text);
        return new Border { Child = panel, Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(6), Background = Hex("#1A2B26"), BorderBrush = Hex("#5A3FAE7A"), BorderThickness = new Thickness(1) };
    }

    /// <summary>One group in the optional list: the kept file on a green strip, the files to clean up below.</summary>
    private Border ApprovalGroup(GroupRow group)
    {
        var panel = new StackPanel();
        var keep = new DockPanel { Background = Hex("#15261F") };
        if (group.IsManual) { var manual = PillOf(L.T("手動で選択"), "manual"); manual.Margin = new Thickness(0, 0, 10, 0); DockPanel.SetDock(manual, Dock.Right); keep.Children.Add(manual); }
        var keepLine = FileLine(group.Keep, PillOf(L.T("残す"), "keep"));
        keepLine.Margin = new Thickness(10, 4, 10, 4);
        keep.Children.Add(keepLine);
        panel.Children.Add(keep);
        var trash = new StackPanel { Margin = new Thickness(76, 6, 10, 8) };
        foreach (var file in group.CleanupFiles)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var pill = PillOf(L.T("片付ける"), "trash");
            pill.Margin = new Thickness(0, 0, 6, 0);
            DockPanel.SetDock(pill, Dock.Left);
            row.Children.Add(pill);
            var chip = new Border { Background = Res("Raised"), BorderBrush = Res("Line2"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = FolderName(file), FontSize = 11 } };
            DockPanel.SetDock(chip, Dock.Left);
            row.Children.Add(chip);
            if (file.RootId == group.Keep.RootId) { var same = PillOf(L.T("同じフォルダー内"), size: 10); DockPanel.SetDock(same, Dock.Right); row.Children.Add(same); }
            var size = new TextBlock { Text = file.SizeText, FontSize = 12, Foreground = Res("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            DockPanel.SetDock(size, Dock.Right);
            row.Children.Add(size);
            row.Children.Add(new TextBlock { Text = file.Path, FontSize = 12, Foreground = Res("Faint"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, ToolTip = file.Path });
            trash.Children.Add(row);
        }
        panel.Children.Add(new Border { Child = trash, BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 1, 0, 0) });
        return new Border { Child = panel, Margin = new Thickness(0, 8, 0, 0), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), ClipToBounds = true };
    }

    private string FolderName(OrganizeFile file) => Vm?.Folders.FirstOrDefault(f => f.Id == file.RootId)?.Name ?? string.Empty;
}
