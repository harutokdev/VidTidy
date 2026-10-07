using System.Globalization;
using LocalMediaAI.Core;

namespace LocalMediaAI.App.ViewModels;

public enum AiSearchViewState { BeforeSearch, Searching, Results, NoResults, ScopeChanged, Failed, Cancelled }

public sealed partial class MainViewModel
{
    private AiSearchViewState _aiSearchState;
    public AiSearchViewState AiSearchState
    {
        get => _aiSearchState;
        private set { _aiSearchState = value; NotifyAiSearchPresentation(); }
    }
    private string _aiSearchLocalFeedback = string.Empty;
    public string AiSearchLocalFeedback { get => _aiSearchLocalFeedback; private set { if (Set(ref _aiSearchLocalFeedback, value)) Raise(nameof(AiSearchHasFeedback)); } }
    public bool AiSearchHasFeedback => !string.IsNullOrEmpty(AiSearchLocalFeedback);
    public string AiSearchStateMessage => AiSearchState switch
    {
        AiSearchViewState.BeforeSearch => L.T("何を探しますか？ 探したい内容を入力して検索してください。"),
        AiSearchViewState.Searching => L.T("検索中…"),
        AiSearchViewState.Results => L.F("検索結果 {0:N0} 件 · 順位順に表示しています。プレビューで内容を確認してください。", SemanticResults.Count),
        AiSearchViewState.NoResults => L.T("条件に一致するメディアが見つかりませんでした。検索語・対象範囲・タグ条件を確認して再検索できます。"),
        AiSearchViewState.ScopeChanged => L.T("対象範囲が変更されました。もう一度検索してください。"),
        AiSearchViewState.Failed => L.T("検索を実行できませんでした。検索語とAI機能の利用状態を確認して再試行できます。"),
        _ => L.T("検索をキャンセルしました。もう一度検索できます。")
    };
    public bool AiSearchHasResults => AiSearchState == AiSearchViewState.Results && SemanticResults.Count > 0;
    public bool AiSearchHasEvidence => AiSearchHasResults && SelectedSemanticResult is { } selected && SemanticResults.Contains(selected);
    public bool AiSearchCanSubmit => SemanticSearchCommand.CanExecute(null) && !IsWorkerUnavailable;
    public string AiSearchCapabilityMessage => IsWorkerUnavailable ? L.T("AI検索は現在利用できません。現在の状態・要対応からAI機能の利用状態を確認してください。") : string.Empty;
    public string AiSearchEvidenceTime => SelectedSemanticResult is { } selected ? FormatAiSearchTimestamp(selected.TimestampSeconds) : string.Empty;
    public static string FormatAiSearchTimestamp(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds >= TimeSpan.MaxValue.TotalSeconds) return L.T("一致時刻を確認できません");
        var time = TimeSpan.FromSeconds(seconds);
        return string.Format(CultureInfo.InvariantCulture, L.T("一致時刻 {0:00}:{1:00}:{2:00}.{3:000}"), (long)time.TotalHours, time.Minutes, time.Seconds, time.Milliseconds);
    }

    private AsyncCommand? _aiSearchPreviewCommand, _showAiSearchLibraryCommand;
    public AsyncCommand AiSearchPreviewCommand => _aiSearchPreviewCommand ??= new(async () =>
    {
        var selected = SelectedSemanticResult;
        if (!AiSearchHasEvidence || selected is null) return;
        AiSearchLocalFeedback = string.Empty;
        SetPreviewTarget(new PreviewTarget(selected.Path, selected.TimestampSeconds, "Matched timestamp", L.T("検索に使用された一致時刻からプレビューします。")));
        await OpenPreviewAsync();
        if (!IsPreviewOpen && AiSearchHasEvidence && SelectedSemanticResult == selected) AiSearchLocalFeedback = L.T("プレビューを開けませんでした。対象ファイルの利用状態を確認してください。");
    }, () => AiSearchHasEvidence && !IsPreviewBusy);

    private long _libraryRevealRequestId;
    private Guid? _libraryRevealRecoveryMediaId;
    private long _libraryRevealRecoverySearchId;
    private AsyncCommand? _clearLibraryRevealFiltersCommand;
    public bool AiSearchCanClearLibraryFilters => AiSearchHasEvidence && _libraryRevealRecoveryMediaId == SelectedSemanticResult?.MediaFileId && _libraryRevealRecoverySearchId == Volatile.Read(ref _semanticSearchRequestId);
    public AsyncCommand ClearLibraryFiltersAndRevealCommand => _clearLibraryRevealFiltersCommand ??= new(() => RevealAiSearchMediaAsync(clearFilters: true), () => AiSearchCanClearLibraryFilters && !IsBusy);
    public AsyncCommand ShowAiSearchResultInLibraryCommand => _showAiSearchLibraryCommand ??= new(() => RevealAiSearchMediaAsync(clearFilters: false), () => AiSearchHasEvidence && !IsBusy);

    private void SetLibraryRevealRecovery(Guid? mediaFileId, long searchId)
    {
        _libraryRevealRecoveryMediaId = mediaFileId;
        _libraryRevealRecoverySearchId = searchId;
        Raise(nameof(AiSearchCanClearLibraryFilters));
        _clearLibraryRevealFiltersCommand?.RaiseCanExecuteChanged();
    }

    private async Task RevealAiSearchMediaAsync(bool clearFilters)
    {
        if (clearFilters && !AiSearchCanClearLibraryFilters) return;
        var selected = SelectedSemanticResult;
        var searchId = Volatile.Read(ref _semanticSearchRequestId);
        var revealId = Interlocked.Increment(ref _libraryRevealRequestId);
        var scopeVersion = Volatile.Read(ref _scopeVersion);
        var rootId = CurrentLibraryScope.LibraryRootId;
        var conditions = (SearchText, TagFilterText, TagFilterIncludeSuggested, CurrentPage);
        var libraryRequestId = Volatile.Read(ref _searchRequestId);
        bool StillCurrent() => revealId == Volatile.Read(ref _libraryRevealRequestId) && searchId == Volatile.Read(ref _semanticSearchRequestId) && scopeVersion == Volatile.Read(ref _scopeVersion) && AiSearchHasEvidence && SelectedSemanticResult == selected;
        bool LibraryUnchanged() => conditions == (SearchText, TagFilterText, TagFilterIncludeSuggested, CurrentPage) && libraryRequestId == Volatile.Read(ref _searchRequestId);
        if (!StillCurrent() || selected is null) return;
        SetLibraryRevealRecovery(null, searchId);
        try
        {
            var media = await _library.GetMediaFileAsync(selected.MediaFileId, _lifetime.Token);
            if (!StillCurrent()) return;
            if (media?.PresenceState != PresenceState.Present || (rootId is { } root && media.LibraryRootId != root))
            {
                AiSearchLocalFeedback = L.T("現在のライブラリではこのメディアを表示できません。");
                return;
            }
            var offset = await _library.FindLibraryMediaOffsetAsync(selected.MediaFileId, rootId, clearFilters ? null : SearchText, clearFilters ? null : TagFilterText, TagFilterIncludeSuggested, _lifetime.Token);
            if (!StillCurrent()) return;
            if (!LibraryUnchanged()) { AiSearchLocalFeedback = L.T("ライブラリの表示条件が変更されました。もう一度表示してください。"); return; }
            if (offset is null)
            {
                var unfiltered = await _library.FindLibraryMediaOffsetAsync(selected.MediaFileId, rootId, null, null, false, _lifetime.Token);
                if (!StillCurrent()) return;
                if (!LibraryUnchanged()) { AiSearchLocalFeedback = L.T("ライブラリの表示条件が変更されました。もう一度表示してください。"); return; }
                if (!clearFilters && unfiltered is not null && (!string.IsNullOrWhiteSpace(SearchText) || !string.IsNullOrWhiteSpace(TagFilterText)))
                {
                    SetLibraryRevealRecovery(selected.MediaFileId, searchId);
                    AiSearchLocalFeedback = L.T("現在の絞り込みではこのメディアを表示できません。絞り込みを解除して表示できます。");
                }
                else AiSearchLocalFeedback = L.T("現在のライブラリではこのメディアを表示できません。");
                return;
            }
            if (clearFilters)
            {
                // Only this explicit action clears local filters; never change the shared scope.
                TagFilterText = string.Empty;
                SearchText = string.Empty;
            }
            CurrentPage = offset.Value / PageSize;
            conditions = (SearchText, TagFilterText, TagFilterIncludeSuggested, CurrentPage);
            var load = SearchAsync();
            libraryRequestId = Volatile.Read(ref _searchRequestId);
            await load;
            if (!StillCurrent()) return;
            if (!LibraryUnchanged() || _libraryLoading || _libraryLoadFailed)
            {
                AiSearchLocalFeedback = L.T("ライブラリの現在の表示条件を確認できませんでした。もう一度表示してください。");
                return;
            }
            var visible = MediaItems.FirstOrDefault(item => item.MediaFileId == selected.MediaFileId && item.PresenceState == PresenceState.Present);
            if (visible is null) { AiSearchLocalFeedback = L.T("ライブラリの対象メディアの位置が変更された可能性があります。もう一度表示してください。"); return; }
            SelectedMedia = visible;
            AiSearchLocalFeedback = string.Empty;
            AiSearchLibraryRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (StillCurrent()) AiSearchLocalFeedback = L.T("ライブラリの対象メディアを確認できませんでした。もう一度試してください。");
            _log.Write("Warning", "AiSearchLibraryNavigationFailed", new { selected.MediaFileId }, ex);
        }
    }
    public event EventHandler? AiSearchLibraryRequested;

    private void NotifyAiSearchPresentation()
    {
        foreach (var property in new[] { nameof(AiSearchState), nameof(AiSearchStateMessage), nameof(AiSearchHasResults), nameof(AiSearchHasEvidence), nameof(AiSearchCanSubmit), nameof(AiSearchCapabilityMessage), nameof(AiSearchEvidenceTime) }) Raise(property);
        _aiSearchPreviewCommand?.RaiseCanExecuteChanged();
        _showAiSearchLibraryCommand?.RaiseCanExecuteChanged();
        Raise(nameof(AiSearchCanClearLibraryFilters));
        _clearLibraryRevealFiltersCommand?.RaiseCanExecuteChanged();
    }
}
