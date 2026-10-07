using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace LocalMediaAI.App.Organize;

/// <summary>Polling and the progress strip. The screens themselves are in OrganizeView.AnalysisScreen.cs.</summary>
public partial class OrganizeView
{
    /// <summary>The clock (elapsed, countdown) ticks this often; the database is asked less often.</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ScreenPollInterval = TimeSpan.FromSeconds(1);
    private DispatcherTimer? _pollTimer;
    private DateTimeOffset _lastPoll = DateTimeOffset.MinValue;
    private bool _polling;
    private bool _showAnalysis;

    private void StartPolling()
    {
        if (_pollTimer is not null) return;
        _pollTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TickInterval };
        _pollTimer.Tick += async (_, _) => await TickAsync();
        _pollTimer.Start();
        AnalysisPulse.BeginAnimation(OpacityProperty, Pulse());
    }

    private static DoubleAnimation Pulse() => new(1, 0.25, TimeSpan.FromSeconds(0.7)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };

    private async Task TickAsync()
    {
        var screen = AnalysisScreen.Visibility == Visibility.Visible;
        if (screen) UpdateClock();
        if (DateTimeOffset.UtcNow - _lastPoll >= (screen ? ScreenPollInterval : PollInterval)) await PollOnceAsync();
    }

    private async Task PollOnceAsync()
    {
        // Skip while a dialog is open so the list never reloads under the user's approval or cleanup.
        if (Vm is null || _polling || Overlay.Visibility == Visibility.Visible) return;
        _polling = true;
        _lastPoll = DateTimeOffset.UtcNow;
        var wasAnalyzing = Vm.IsAnalyzing;
        var hadHeldResults = Vm.HasNewResults;
        try { await Vm.PollAnalysisAsync(DateTimeOffset.UtcNow, CancellationToken.None); }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException) { }
        finally { _polling = false; }
        if (wasAnalyzing && !Vm.IsAnalyzing)
        {
            _showAnalysis = false;
            Toast(Vm.NewGroupsFound > 0 ? L.F("解析が終わり、新しい重複を {0}組 見つけました", Vm.NewGroupsFound) : L.T("解析が終わりました"));
        }
        else if (hadHeldResults && !Vm.HasNewResults)
        {
            RenderDetail();
            Toast(HeldResultsShownText());
        }
        RefreshChrome();
    }

    private void RefreshAnalysisLine()
    {
        if (Vm is null) return;
        AnalysisLine.Visibility = string.IsNullOrEmpty(Vm.AnalysisText) ? Visibility.Collapsed : Visibility.Visible;
        var analyzing = Vm.IsAnalyzing;
        AnalysisPulse.Visibility = StripBar.Visibility = StripPercent.Visibility = ShowAnalysisLink.Visibility = analyzing ? Visibility.Visible : Visibility.Collapsed;
        ShowNewResultsLink.Visibility = !analyzing && Vm.HasNewResults ? Visibility.Visible : Visibility.Collapsed;
        StripPercent.Text = $"{Vm.ProgressPercent}%";
        CheckChangesLink.IsEnabled = !analyzing; // like the prototype: no second check while one runs
        CheckChangesLink.ToolTip = analyzing ? L.T("確認・解析が終わると押せます") : L.T("登録したフォルダーで増えた・変わったファイルを探して解析します。変わっていないファイルは前の解析結果を使います");
        if (Vm.Background is not null) StartPolling();
        if (AnalysisScreen.Visibility == Visibility.Visible) RenderAnalysisScreen();
        else _liveKey = null;
    }

    /// <summary>Whether the analysis screen replaces the list: asked for, or nothing to show yet while analysing.</summary>
    private bool ShowsAnalysisScreen() =>
        Vm is not null && Vm.IsAnalyzing && (_showAnalysis || (Vm.OpenGroupCount == 0 && Vm.QueueFileCount == 0));

    private void ShowAnalysis_OnClick(object sender, RoutedEventArgs e)
    {
        _showAnalysis = true;
        RefreshChrome();
    }

    private async void CheckChanges_OnClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        try
        {
            if (!await Vm.CheckChangesAsync(CancellationToken.None))
            {
                Toast(Vm.IsAnalyzing ? L.T("いま確認・解析しています。終わってから、もう一度押してください") : L.T("確認できるフォルダーがありません（接続されていません）"));
                return;
            }
            RefreshChrome();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException) { ShowMessage(L.T("変更を確認できませんでした"), ex.Message); }
    }

    /// <summary>Results held back by the cleanup list: showing them now rebuilds the list, so the user confirms first.</summary>
    private void ShowNewResults_OnClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null || !Vm.HasNewResults) return;
        OpenModal(L.T("新しい解析結果を今すぐ表示しますか？"), L.T("一覧を作り直して、解析で見つかった重複の組を表示します。"));
        OverlayBody.Children.Add(Note(L.F("整理リストに入っている {0}件 は空になり、選んだ「残す方」もルールどおりに戻ります。", Vm.QueueFileCount), true));
        OverlayBody.Children.Add(Note(L.T("ファイルが移動・削除されることはありません。整理リストを実行するか空にすると、待たずに自動で表示されます。"), false));
        SetButtons((L.T("キャンセル"), "BtnBase", HideOverlay), (L.T("今すぐ表示する"), "Primary", async () =>
        {
            try
            {
                await Vm.ShowNewResultsNowAsync(CancellationToken.None);
                HideOverlay();
                RenderDetail();
                RefreshChrome();
                Toast(HeldResultsShownText());
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException) { HideOverlay(); ShowMessage(L.T("表示できませんでした"), ex.Message); }
        }));
    }

    private string HeldResultsShownText() =>
        Vm?.NewGroupsFound > 0 ? L.F("新しい解析結果を表示しました（新しい重複 {0}組）", Vm.NewGroupsFound) : L.T("新しい解析結果を一覧に表示しました");

    private void OpenRecycleBin_OnClick(object sender, RoutedEventArgs e) => OpenRecycleBin();
}
