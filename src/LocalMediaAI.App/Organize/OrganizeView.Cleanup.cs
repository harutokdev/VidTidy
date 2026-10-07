using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using LocalMediaAI.Core.Organize;

namespace LocalMediaAI.App.Organize;

/// <summary>
/// 整理リストを実行: 1. 内容を確認 → 2. チェック → 3. 実行 (final confirmation, progress, result).
/// The file checks (still as analysed, kept file present, same hash) run when leaving step 1.
/// </summary>
public partial class OrganizeView
{
    private CleanupWizardViewModel? _wizard;
    private Action? _cancelWizard;
    private string? _wizardError;
    private int _wizardStep;

    private void Run_OnClick(object sender, RoutedEventArgs e) => ShowWizard();

    private void ShowWizard()
    {
        _wizard = Vm?.CreateWizard();
        if (_wizard is null) return;
        _wizardError = null;
        _wizardStep = 0;
        RenderWizard();
    }

    private void RenderWizard()
    {
        if (_wizard is not { } wizard) return;
        var step = wizard.Step switch { CleanupWizardStep.Running or CleanupWizardStep.Finished => 2, _ => _wizardStep };
        OpenModal(L.T("整理リストを実行"), headerExtra: Steps(step, wizard.Step == CleanupWizardStep.Finished), dismissable: false);
        switch (wizard.Step)
        {
            case CleanupWizardStep.Checking: RenderChecking(); break;
            case CleanupWizardStep.Running: RenderRunning(wizard); break;
            case CleanupWizardStep.Finished: RenderFinished(wizard); break;
            default:
                if (_wizardStep == 0) RenderReview(wizard);
                else if (_wizardStep == 1) RenderCheck(wizard);
                else RenderFinalConfirm(wizard);
                break;
        }
    }

    private WrapPanel Steps(int current, bool finished)
    {
        var panel = new WrapPanel();
        string[] names = [L.T("内容を確認"), L.T("チェック"), L.T("実行")];
        for (var i = 0; i < names.Length; i++)
        {
            var done = i < current || finished;
            var on = i == current && !finished;
            panel.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0), BorderThickness = new Thickness(1),
                BorderBrush = on ? Res("Accent") : done ? System.Windows.Media.Brushes.Transparent : Res("Line2"),
                Background = on ? Hex("#26405F") : done ? Res("KeepBg") : System.Windows.Media.Brushes.Transparent,
                Child = new TextBlock { Text = $"{i + 1}. {names[i]}", FontSize = 12, Foreground = on ? Res("Text") : done ? Res("Keep") : Res("Muted") }
            });
        }
        return panel;
    }

    // ---------- 1. 内容を確認 ----------
    private void RenderReview(CleanupWizardViewModel wizard)
    {
        var intro = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = Res("Muted") };
        intro.Inlines.Add(new Run(L.T("実行前の確認です。まだファイルは変更されていません。送り先: ")));
        intro.Inlines.Add(new Bold(new Run(L.T("Windowsのごみ箱へ移動"))) { Foreground = Res("Text") });
        OverlayBody.Children.Add(Note(intro, false));
        OverlayBody.Children.Add(Note(L.T("このアプリには「元に戻す」機能はありません。Windowsのごみ箱から戻せます。"), true));
        AddWizardGroups(wizard, withChecks: false);
        OverlayFootNote.Text = L.F("{0}件・{1}", wizard.Rows.Count, MediaDisplayFormat.FileSize(wizard.TotalBytes));
        SetButtons((L.T("やめる"), "BtnBase", CloseWizard), (L.T("内容を確認しました"), "Primary", () => _ = CheckWizardAsync()));
    }

    // ---------- 2. チェック ----------
    private void RenderChecking()
    {
        OverlayBody.Children.Add(Muted(L.T("ファイルが解析したときのままか、残す方のファイルがあるかを確かめています。ファイルは変更しません。"), 13, new Thickness(0, 0, 0, 8)));
        var bar = Bar(0, 6);
        ((ProgressBar)bar.Child).IsIndeterminate = true;
        OverlayBody.Children.Add(bar);
        SetButtons();
    }

    private void RenderCheck(CleanupWizardViewModel wizard)
    {
        var need = wizard.Rows.Where(r => r.NeedsManualCheck && r.State == CleanupRowState.Ready).ToArray();
        var progress = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = Res("Muted") };
        OverlayBody.Children.Add(Note(progress, false));
        if (wizard.BlockedCount > 0)
            OverlayBody.Children.Add(Note(L.F("{0}件 は解析したあとに変わったなどの理由で、今回は移せません。整理リストから外れて一覧に戻ります。", wizard.BlockedCount), true));
        AddWizardGroups(wizard, withChecks: true);
        OverlayFootNote.Text = L.F("{0}件・{1}", wizard.Rows.Count, MediaDisplayFormat.FileSize(wizard.TotalBytes));
        var buttons = SetButtons((L.T("戻る"), "BtnBase", () => { _wizardStep = 0; RenderWizard(); }), (L.T("チェックを終えました"), "Primary", () => { _wizardStep = 2; RenderWizard(); }));
        void Update()
        {
            var done = need.Count(r => r.IsChecked);
            progress.Text = L.F("よく似ている動画・念のため確認の動画・個別に選んだファイルは、1件ずつチェックしてください（{0}/{1}）。完全に同じファイルと、まとめて承認した組は承認済みです。", done, need.Length);
            buttons[1].IsEnabled = wizard.CanRun;
        }
        foreach (var row in wizard.Rows) row.PropertyChanged += (_, _) => Update();
        Update();
    }

    /// <summary>Rows grouped like .wgroup: header with type and approval, the kept file, then the files to clean up.</summary>
    private void AddWizardGroups(CleanupWizardViewModel wizard, bool withChecks)
    {
        foreach (var group in wizard.Rows.GroupBy(r => r.Request.GroupId))
        {
            var first = group.First().Request;
            var row0 = Vm?.GroupOf(first.Target.MediaFileId);
            var category = first.Kind == CleanupKind.Exact ? GroupCategory.Exact : row0?.Category ?? GroupCategory.Similar;
            var box = new StackPanel();
            var head = new DockPanel { Margin = new Thickness(10, 6, 10, 6) };
            if (first.Kind == CleanupKind.Exact)
            {
                var pill = row0?.IsManual == true ? PillOf(L.T("手動で選択・承認済み"), "manual") : PillOf(L.T("ルールで承認済み"), "keep");
                DockPanel.SetDock(pill, Dock.Right);
                head.Children.Add(pill);
            }
            else if (first.Approved)
            {
                var pill = PillOf(L.T("まとめて承認済み"), "keep");
                DockPanel.SetDock(pill, Dock.Right);
                head.Children.Add(pill);
            }
            var single = first.Kind == CleanupKind.Single;
            if (single) { var tag = PillOf(L.T("個別")); tag.Margin = new Thickness(0, 0, 8, 0); head.Children.Add(tag); }
            else head.Children.Add(Mark(category));
            head.Children.Add(new TextBlock { Text = single ? L.T("個別に選んだファイル") : category switch { GroupCategory.Exact => L.T("完全に同じ"), GroupCategory.Similar => L.T("よく似ている"), _ => L.T("念のため確認") }, FontSize = 12, Foreground = Res("Muted"), VerticalAlignment = VerticalAlignment.Center });
            box.Children.Add(new Border { Child = head, BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, 1) });
            if (first.Keep is { } keep) box.Children.Add(WizardLine(null, PillOf(L.T("残す"), "keep"), keep, null));
            foreach (var row in group) box.Children.Add(WizardLine(withChecks && row.NeedsManualCheck && row.State == CleanupRowState.Ready ? row : null, PillOf(L.T("片付ける"), "trash"), row.Request.Target, row));
            OverlayBody.Children.Add(new Border { Child = box, BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 10) });
        }
    }

    private Grid WizardLine(CleanupWizardRow? checkRow, Border role, OrganizeFile file, CleanupWizardRow? row)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 10, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        if (checkRow is not null)
        {
            var check = new CheckBox { IsChecked = checkRow.IsChecked, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            AutomationPropertiesHelper.SetName(check, L.F("{0}を片付けることを確認", file.FileName));
            check.Checked += (_, _) => checkRow.IsChecked = true;
            check.Unchecked += (_, _) => checkRow.IsChecked = false;
            grid.Children.Add(check);
        }
        role.Margin = new Thickness(0);
        role.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(role, 1);
        grid.Children.Add(role);
        var line = new StackPanel();
        line.Children.Add(FileLine(file));
        if (row is { State: CleanupRowState.Blocked or CleanupRowState.Failed, Reason: { } reason })
            line.Children.Add(new TextBlock { Text = L.T("移せません: ") + reason, FontSize = 12, Foreground = Res("WarnText"), Margin = new Thickness(66, 0, 0, 4), TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(line, 2);
        grid.Children.Add(line);
        if (row is { State: CleanupRowState.Blocked }) grid.Opacity = 0.6;
        return grid;
    }

    // ---------- 3. 実行 ----------
    private void RenderFinalConfirm(CleanupWizardViewModel wizard)
    {
        var line = new TextBlock { FontSize = 18, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
        line.Inlines.Add(new Bold(new Run(L.F("{0}件（{1}）", wizard.RunCount, MediaDisplayFormat.FileSize(wizard.RunBytes)))));
        line.Inlines.Add(new Run(L.T("をWindowsのごみ箱へ移動します。")));
        OverlayBody.Children.Add(line);
        OverlayBody.Children.Add(Note(L.T("1件ずつ順番に処理します。完全に同じファイルは、処理の直前に中身が同じかをもう一度確かめます。途中でやめても、処理済みのファイルは自動では戻りません。"), false));
        OverlayBody.Children.Add(Note(L.T("ドライブの容量は、ごみ箱を空にしたときに空きます。"), false));
        var buttons = SetButtons((L.T("戻る"), "BtnBase", () => { _wizardStep = 1; RenderWizard(); }), (L.T("ごみ箱へ移動する"), "Primary", () => _ = RunWizardAsync()));
        buttons[1].IsEnabled = wizard.CanRun;
    }

    private void RenderRunning(CleanupWizardViewModel wizard)
    {
        var done = wizard.Progress?.Done ?? 0;
        var total = Math.Max(1, wizard.Progress?.Total ?? wizard.RunCount);
        OverlayBody.Children.Add(new TextBlock { Text = L.F("処理しています… {0}/{1}件", done, total), Margin = new Thickness(0, 0, 0, 8) });
        OverlayBody.Children.Add(Bar(done * 100.0 / total, 6));
        var list = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var row in wizard.Rows.Where(r => r.WillRun).Take(done).TakeLast(6)) list.Children.Add(FileLine(row.Request.Target));
        OverlayBody.Children.Add(list);
        SetButtons((L.T("残りをやめる"), "BtnBase", () => { _cancelWizard?.Invoke(); Toast(L.T("いま処理中のまとまりが終わったら止めます")); }));
    }

    private void RenderFinished(CleanupWizardViewModel wizard)
    {
        var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(10, 16, 10, 12) };
        center.Children.Add(new TextBlock { Text = MediaDisplayFormat.FileSize(wizard.DoneBytes), FontSize = 40, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center });
        center.Children.Add(new TextBlock { Text = L.F("をごみ箱へ移動しました（{0}件）", wizard.DoneCount), FontSize = 16, Margin = new Thickness(0, 6, 0, 6), HorizontalAlignment = HorizontalAlignment.Center });
        var notDone = wizard.Rows.Where(r => r.State != CleanupRowState.Done).Select(r => r.Request.GroupId).Distinct().Count();
        center.Children.Add(new TextBlock { Text = L.F("ごみ箱を空にすると、この分のドライブ容量が空きます。残り {0}組。", (Vm?.OpenGroupCount ?? 0) + notDone), Foreground = Res("Muted"), HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var open = Action(L.T("ごみ箱を開く"), OpenRecycleBin);
        open.HorizontalAlignment = HorizontalAlignment.Center;
        center.Children.Add(open);
        OverlayBody.Children.Add(center);
        if (_wizardError is not null) OverlayBody.Children.Add(Note(_wizardError, true));
        var failed = wizard.Rows.Where(r => r.State is CleanupRowState.Failed or CleanupRowState.Blocked).ToArray();
        if (failed.Length > 0)
        {
            var text = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = Res("WarnText") };
            text.Inlines.Add(new Bold(new Run(L.F("{0}件", failed.Length))));
            text.Inlines.Add(new Run(L.T("は移動できませんでした。整理リストから外して一覧に戻したので、原因を直してからもう一度実行できます。")));
            OverlayBody.Children.Add(Note(text, true));
            foreach (var row in failed)
            {
                var line = new DockPanel();
                var reason = new TextBlock { Text = row.Reason ?? L.T("実行されませんでした"), FontSize = 12, Foreground = Res("WarnText"), MaxWidth = 220, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                DockPanel.SetDock(reason, Dock.Right);
                line.Children.Add(reason);
                line.Children.Add(FileLine(row.Request.Target));
                OverlayBody.Children.Add(line);
            }
        }
        SetButtons((L.T("続けて片付ける"), "Primary", FinishWizard));
    }

    private async Task CheckWizardAsync()
    {
        if (_wizard is null) return;
        var check = _wizard.CheckAsync(CancellationToken.None);
        RenderWizard();
        try { await check; }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException)
        {
            _wizard = null;
            ShowMessage(L.T("チェックできませんでした"), ex.Message);
            return;
        }
        _wizardStep = 1;
        RenderWizard();
    }

    private async Task RunWizardAsync()
    {
        if (_wizard is not { CanRun: true } wizard) return;
        using var cancel = new CancellationTokenSource();
        _cancelWizard = cancel.Cancel;
        wizard.PropertyChanged += OnWizardProgress;
        try
        {
            var run = wizard.RunAsync(cancel.Token);
            RenderWizard();
            await run;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException)
        {
            _wizardError = L.F("途中で止まりました: {0}", ex.Message);
        }
        finally
        {
            wizard.PropertyChanged -= OnWizardProgress;
            _cancelWizard = null;
        }
        RenderWizard();
    }

    private void OnWizardProgress(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CleanupWizardViewModel.Progress)) return;
        Dispatcher.InvokeAsync(() => { if (_wizard?.Step == CleanupWizardStep.Running) RenderWizard(); });
    }

    private void FinishWizard()
    {
        if (_wizard is not null && Vm is not null)
        {
            Vm.ApplyCleanupResults(_wizard.CompletedFileIds);
            _ = RefreshRecordsAsync();
        }
        CloseWizard();
        RenderDetail();
        RefreshChrome();
    }

    private async Task RefreshRecordsAsync()
    {
        if (Vm is null) return;
        try { await Vm.LoadRecordsAsync(CancellationToken.None); RefreshChrome(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException) { }
    }

    private void CloseWizard()
    {
        _wizard = null;
        HideOverlay();
    }

    private static void OpenRecycleBin()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
