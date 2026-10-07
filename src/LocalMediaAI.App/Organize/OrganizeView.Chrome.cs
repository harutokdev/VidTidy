using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Threading;

namespace LocalMediaAI.App.Organize;

/// <summary>Screen switching, the next-action banner, the side panel counters and the toast.</summary>
public partial class OrganizeView
{
    private DispatcherTimer? _toastTimer;

    private void RefreshChrome()
    {
        if (Vm is null) return;
        var next = Vm.NextAction;
        TitleMark.Visibility = next.Kind == NextActionKind.ApproveExact ? Visibility.Visible : Visibility.Collapsed;
        NextActionButton.Visibility = string.IsNullOrEmpty(next.ButtonLabel) ? Visibility.Collapsed : Visibility.Visible;
        QueueSize.Text = MediaDisplayFormat.FileSize(Vm.QueueBytes);
        RunButton.IsEnabled = Vm.Executor is not null && Vm.QueueFileCount > 0;
        // One big button: while the card above offers the run, the bar does not repeat it.
        RunButton.Visibility = next.Kind == NextActionKind.RunQueue ? Visibility.Collapsed : Visibility.Visible;
        var nearLimit = Vm.QueueMeter >= 80;
        LimitText.Text = nearLimit
            ? L.F("・1回に 完全に同じ{0}件／ほか{1}件まで（あと {2}件／{3}件）", Vm.ExactLimit, Vm.OtherLimit, Math.Max(0, Vm.ExactLimit - Vm.QueueExactFileCount), Math.Max(0, Vm.OtherLimit - Vm.QueueOtherFileCount))
            : string.Empty;
        QueueMeterBox.Visibility = nearLimit ? Visibility.Visible : Visibility.Collapsed;
        FolderHint.Visibility = Vm.Settings.HideFolderHint ? Visibility.Collapsed : Visibility.Visible;
        FolderHintOpen.Visibility = Vm.Settings.HideFolderHint ? Visibility.Visible : Visibility.Collapsed;
        RenderSteps();
        SideQueueCount.Text = Vm.QueueFileCount > 0 ? L.F("{0}件", Vm.QueueFileCount) : string.Empty;
        OpenBinLink.Visibility = Vm.Drives.Any(d => d.RecycleBinBytes > 0) ? Visibility.Visible : Visibility.Collapsed;
        StatsLine.Inlines.Clear();
        AddStats(StatsLine.Inlines);
        RefreshListSections();
        FailedLink.Visibility = Vm.Unanalyzed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FailedCount.Text = L.F("{0}本", Vm.Unanalyzed.Count);
        RefreshScreen();
        RefreshAnalysisLine();
        MaybeAskPriority();
    }

    /// <summary>First run, all clean, or the organize flow — the same choice the prototype makes.</summary>
    private void RefreshScreen()
    {
        if (Vm is null) return;
        var first = Vm.Folders.Count == 0;
        var clean = !first && Vm.OpenGroupCount == 0 && Vm.QueueFileCount == 0 && !Vm.IsAnalyzing;
        var analysis = !first && ShowsAnalysisScreen();
        OnboardScreen.Visibility = first ? Visibility.Visible : Visibility.Collapsed;
        CleanScreen.Visibility = clean ? Visibility.Visible : Visibility.Collapsed;
        AnalysisScreen.Visibility = analysis ? Visibility.Visible : Visibility.Collapsed;
        FlowScreen.Visibility = first || clean || analysis ? Visibility.Collapsed : Visibility.Visible;
        if (analysis) RenderAnalysisScreen();
        if (clean)
        {
            var bin = Vm.Drives.Sum(d => d.RecycleBinBytes ?? 0);
            CleanText.Text = (Vm.Settings.CheckChangesOnStartup
                    ? L.T("次に起動したとき、フォルダーの変更を自動で確認して、新しい重複だけをお知らせします。")
                    : L.T("起動時の自動確認はオフです。新しい動画を追加したら「変更を確認」を押してください。"))
                + (bin > 0 ? L.F("{0}{1}ごみ箱に {2} あります。ごみ箱を空にすると、その分ドライブが空きます。", Environment.NewLine, Environment.NewLine, MediaDisplayFormat.FileSize(bin)) : string.Empty);
        }
    }

    /// <summary>重複 N組・片付けられる X・接続待ち N組 (bold numbers like the prototype). Files that could not be analysed are shown in the side panel only.</summary>
    private void AddStats(InlineCollection inlines)
    {
        if (Vm is null) return;
        inlines.Add(new Run(L.T("重複 ")));
        inlines.Add(new Bold(new Run(L.F("{0}組", Vm.OpenGroupCount))) { Foreground = Res("Text") });
        inlines.Add(new Run(L.T("・片付けられる ")));
        inlines.Add(new Bold(new Run(MediaDisplayFormat.FileSize(Vm.CleanableBytes))) { Foreground = Res("Text") });
        inlines.Add(new Run(L.T("・これまでに片付けた ")));
        inlines.Add(new Bold(new Run(MediaDisplayFormat.FileSize(Vm.CleanedTotalBytes))) { Foreground = Res("Text") });
        var bin = Vm.Drives.Sum(d => d.RecycleBinBytes ?? 0);
        if (bin > 0) inlines.Add(new Run(L.F("（うちごみ箱の中 {0}）", MediaDisplayFormat.FileSize(bin))));
        if (Vm.WaitingForDriveCount > 0) inlines.Add(new Run(L.F("・接続待ち {0}組", Vm.WaitingForDriveCount)));
    }

    /// <summary>残す方のルール： ① … → ② … with the folder order in brackets.</summary>
    private void AddRule(InlineCollection inlines, bool withLabel = true)
    {
        if (Vm is null) return;
        if (withLabel) inlines.Add(new Run(L.T("残す方のルール： ")));
        var folders = string.Join(" → ", Vm.Folders.Select(f => $"{f.Rank}. {f.Name}"));
        for (var i = 0; i < Vm.Settings.Criteria.Count; i++)
        {
            if (i > 0) inlines.Add(new Run("  →  ") { Foreground = Res("Faint") });
            var folderFirst = Vm.Settings.Criteria[i] == Core.Organize.KeepCriterion.FolderPriority;
            var name = folderFirst ? L.T("フォルダーの優先順位が高い方")
                : Vm.Settings.DateDirection == Core.Organize.KeepDateDirection.Oldest ? L.T("更新日時が古い方") : L.T("更新日時が新しい方");
            inlines.Add(new Bold(new Run($"{(i == 0 ? "①" : "②")} {name}")));
            if (folderFirst) inlines.Add(new Run(L.F("（{0}）", folders)));
        }
        var manual = Vm.ExactGroups.Count(g => g.IsOpen && g.IsManual);
        if (manual > 0) inlines.Add(new Run(L.F("   あなたが選び直した {0}組 はその選択を残します", manual.ToString(CultureInfo.InvariantCulture))) { Foreground = Res("AccentHi") });
    }

    private void Toast(string message)
    {
        ToastText.Text = message;
        ToastBox.Visibility = Visibility.Visible;
        _toastTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _toastTimer.Stop();
        _toastTimer.Tick -= HideToast;
        _toastTimer.Tick += HideToast;
        _toastTimer.Start();
    }

    private void HideToast(object? sender, EventArgs e)
    {
        _toastTimer?.Stop();
        ToastBox.Visibility = Visibility.Collapsed;
    }

    private void FolderMenu_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } button) { menu.PlacementTarget = button; menu.IsOpen = true; }
    }

}

/// <summary>True when both bound values are the same object; used to highlight the selected group.</summary>
public sealed class SameReferenceConverter : System.Windows.Data.IMultiValueConverter
{
    public static readonly SameReferenceConverter Instance = new();
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => values.Length == 2 && ReferenceEquals(values[0], values[1]);
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
