using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace LocalMediaAI.App.Organize;

/// <summary>
/// The steps above the list, the sort and space-goal bar, the cleanup list section and what waits for after the run.
/// </summary>
public partial class OrganizeView
{
    private bool _syncingSort;

    /// <summary>1. 承認 → 2. 実行 → 3. よく似ているを確認, with the current step highlighted and finished ones ticked.</summary>
    private void RenderSteps()
    {
        if (Vm is null) return;
        StepsPanel.Children.Clear();
        var steps = Vm.Steps;
        var items = new[] { ("1", L.T("完全に同じ：ルールを承認"), steps.Approve), ("2", L.T("整理リストを実行"), steps.Run), ("3", L.T("よく似ている：1組ずつ確認"), steps.Review) };
        for (var i = 0; i < items.Length; i++)
        {
            var (number, text, state) = items[i];
            if (i > 0) StepsPanel.Children.Add(new TextBlock { Text = "→", Foreground = Res("Faint"), Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            var pill = new Border
            {
                CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 1, 10, 2), BorderThickness = new Thickness(1),
                BorderBrush = state switch { StepState.Current => Res("Accent"), StepState.Done => System.Windows.Media.Brushes.Transparent, _ => Res("Line2") },
                Background = state switch { StepState.Current => Hex("#20334D"), StepState.Done => Res("KeepBg"), _ => System.Windows.Media.Brushes.Transparent },
                Child = new TextBlock
                {
                    Text = $"{(state == StepState.Done ? "✓" : number)}. {text}", FontSize = 12,
                    Foreground = state switch { StepState.Current => Res("Text"), StepState.Done => Res("Keep"), _ => Res("Faint") }
                }
            };
            AutomationPropertiesHelper.SetName(pill, L.F("{0}. {1}（{2}）", number, text, state switch { StepState.Current => L.T("いまの手順"), StepState.Done => L.T("済み"), _ => L.T("このあと") }));
            StepsPanel.Children.Add(pill);
        }
    }

    /// <summary>
    /// While the run waits, only the cleanup list (and a line about what comes after) is listed; otherwise the cleanup
    /// list is a folded section above the open groups.
    /// </summary>
    private void RefreshListSections()
    {
        if (Vm is null) return;
        var runOnly = Vm.IsRunOnly;
        var others = !runOnly || Vm.PeekLater;
        QueueSection.Visibility = Vm.QueueFileCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueToggle.Visibility = runOnly ? Visibility.Collapsed : Visibility.Visible;
        QueueToggle.IsChecked = Vm.QueueExpanded;
        QueueItems.Visibility = runOnly || Vm.QueueExpanded ? Visibility.Visible : Visibility.Collapsed;
        QueueMeta.Text = L.F("{0}組・{1}件・{2}", Vm.QueueList.Count + Vm.QueuedSingles.Count, Vm.QueueFileCount, MediaDisplayFormat.FileSize(Vm.QueueBytes));
        RenderQueuedSingles();

        ExactSection.Visibility = others && Vm.ExactList.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SimilarSection.Visibility = others && Vm.SimilarList.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CheckSection.Visibility = others && Vm.CheckList.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExactPill.Visibility = Vm.ExactOpenCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        ListEmpty.Visibility = Vm.ExactList.Count + Vm.SimilarList.Count + Vm.CheckList.Count + Vm.QueueFileCount == 0 ? Visibility.Visible : Visibility.Collapsed;

        var later = new[]
        {
            Vm.LaterSimilarCount > 0 ? L.F("よく似ている動画 {0}組", Vm.LaterSimilarCount) : null,
            Vm.LaterCheckCount > 0 ? L.F("念のため確認 {0}組", Vm.LaterCheckCount) : null,
            Vm.LaterWaitingExactCount > 0 ? L.F("接続待ちの完全に同じ {0}組", Vm.LaterWaitingExactCount) : null
        }.Where(x => x is not null).ToArray();
        LaterLine.Visibility = runOnly && later.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        LaterText.Inlines.Clear();
        LaterText.Inlines.Add(new Bold(new Run(string.Join(L.T("・"), later))) { Foreground = Res("Text") });
        LaterText.Inlines.Add(new Run(L.T(" は、実行のあとで確認します。")));
        PeekLink.Content = Vm.PeekLater ? L.T("隠す") : L.T("先に見る");

        ListBar.Visibility = runOnly ? Visibility.Collapsed : Visibility.Visible;
        _syncingSort = true;
        SortBox.SelectedIndex = Vm.Sort == ListSort.Size ? 0 : 1;
        _syncingSort = false;
        RefreshGoal();
    }

    /// <summary>Files picked one by one from すべてのファイル, with 外す.</summary>
    private void RenderQueuedSingles()
    {
        QueueSingles.Children.Clear();
        foreach (var file in Vm!.QueuedSingles)
        {
            var remove = new Button { Content = L.T("外す"), Style = (Style)FindResource("Small"), VerticalAlignment = VerticalAlignment.Center };
            remove.Click += (_, _) => { Vm.DequeueSingle(file.MediaFileId); RefreshChrome(); };
            var line = FileLine(file, PillOf(L.T("個別")), remove);
            line.Margin = new Thickness(14, 4, 14, 4);
            QueueSingles.Children.Add(new Border { Child = line, BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, 1) });
        }
    }

    private void RefreshGoal()
    {
        var goal = Vm!.Goal;
        GoalLink.Content = goal is null ? L.T("空けたい量を決める") : L.T("目標を変える");
        GoalText.Inlines.Clear();
        GoalLeftText.Visibility = goal is null ? Visibility.Collapsed : Visibility.Visible;
        if (goal is null) return;
        var queued = Vm.GoalQueuedBytes;
        GoalText.Inlines.Add(new Run(L.F("目標：{0}・整理リストで ", goal.Label)));
        GoalText.Inlines.Add(new Bold(new Run(MediaDisplayFormat.FileSize(queued))) { Foreground = Res("Keep") });
        GoalText.Inlines.Add(new Run(L.F("（{0}%）", Math.Min(100, queued * 100 / Math.Max(1, goal.Bytes)))));
        if (Vm.GoalReachableBytes < goal.Bytes)
            GoalText.Inlines.Add(new Run(L.F("・一覧をすべて片付けても {0} までです", MediaDisplayFormat.FileSize(Vm.GoalReachableBytes))) { Foreground = Res("Warn") });
        GoalLeftText.Text = L.F("目標（{0}）まで あと {1}", goal.Label, MediaDisplayFormat.FileSize(Math.Max(0, goal.Bytes - queued)));
    }

    private async void SortBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Vm is null || _syncingSort || SortBox.SelectedIndex < 0) return;
        try { await Vm.SetSortAsync(SortBox.SelectedIndex == 0 ? ListSort.Size : ListSort.Found, CancellationToken.None); RefreshChrome(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException) { ShowMessage(L.T("並べ方を保存できませんでした"), ex.Message); }
    }

    private void GoalLink_OnClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        if (GoalForm.Visibility == Visibility.Visible) { GoalForm.Visibility = Visibility.Collapsed; return; }
        GoalDrive.Items.Clear();
        GoalDrive.Items.Add(L.T("すべてのドライブ"));
        foreach (var drive in Vm.Folders.Select(f => f.Drive).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)) GoalDrive.Items.Add(drive);
        GoalDrive.SelectedItem = Vm.Goal?.Drive is { } chosen && GoalDrive.Items.Contains(chosen) ? chosen : GoalDrive.Items[0];
        GoalSize.Text = Vm.Goal is { } goal ? (goal.Bytes / (double)CleanupGoal.BytesPerGb).ToString("0.##", CultureInfo.InvariantCulture) : string.Empty;
        GoalClear.Visibility = Vm.Goal is null ? Visibility.Collapsed : Visibility.Visible;
        GoalForm.Visibility = Visibility.Visible;
        GoalSize.Focus();
    }

    private void GoalSet_OnClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        if (!double.TryParse(GoalSize.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var gigabytes) || gigabytes <= 0 || gigabytes > 1_000_000)
        {
            Toast(L.T("空けたい量を GB の数字で入力してください（例：50）"));
            return;
        }
        var drive = GoalDrive.SelectedIndex <= 0 ? null : GoalDrive.SelectedItem as string;
        Vm.Goal = new CleanupGoal(drive, (long)(gigabytes * CleanupGoal.BytesPerGb));
        GoalForm.Visibility = Visibility.Collapsed;
        RefreshChrome();
        Toast(Vm.Sort == ListSort.Size ? L.F("目標「{0}」に届く順に並べました", Vm.Goal.Label) : L.F("目標を「{0}」にしました。「片付けられる量が大きい順」にすると、届く順に並びます", Vm.Goal.Label));
    }

    private void GoalClear_OnClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        Vm.Goal = null;
        GoalForm.Visibility = Visibility.Collapsed;
        RefreshChrome();
    }

    private void GoalCancel_OnClick(object sender, RoutedEventArgs e) => GoalForm.Visibility = Visibility.Collapsed;

    private void Peek_OnClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        Vm.PeekLater = !Vm.PeekLater;
        RefreshChrome();
    }

    private void QueueToggle_OnChanged(object sender, RoutedEventArgs e)
    {
        if (Vm is null || QueueToggle.IsChecked == Vm.QueueExpanded) return;
        Vm.QueueExpanded = QueueToggle.IsChecked == true;
        RefreshChrome();
    }

    private async void FolderHintClose_OnClick(object sender, RoutedEventArgs e) => await SetFolderHintHiddenAsync(true);

    private async void FolderHintOpen_OnClick(object sender, RoutedEventArgs e) => await SetFolderHintHiddenAsync(false);

    private async Task SetFolderHintHiddenAsync(bool hidden)
    {
        if (Vm is null) return;
        try { await Vm.SetFolderHintHiddenAsync(hidden, CancellationToken.None); RefreshChrome(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException) { ShowMessage(L.T("設定を保存できませんでした"), ex.Message); }
    }
}
