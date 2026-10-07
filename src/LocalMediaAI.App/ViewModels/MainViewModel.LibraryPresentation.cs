using LocalMediaAI.Core;

namespace LocalMediaAI.App.ViewModels;
public enum LibraryEmptyState { Loading, NoFolders, Processing, FilteredEmpty, ScannedEmpty, UnverifiedEmpty, HasMedia, Error }
public sealed partial class MainViewModel
{
    private bool _libraryLoading = true;
    private bool _libraryLoadFailed;
    private string _librarySettingsFeedback = string.Empty;
    private string _libraryFolderFeedback = string.Empty;
    public string LibraryFolderFeedback { get => _libraryFolderFeedback; private set => Set(ref _libraryFolderFeedback, value); }
    public string LibrarySettingsFeedback { get => _librarySettingsFeedback; private set => Set(ref _librarySettingsFeedback, value); }
    private IReadOnlyList<LibraryRoot> _libraryRegisteredFolders = [];
    public IReadOnlyList<LibraryRoot> LibraryRegisteredFolders => _libraryRegisteredFolders;
    public bool LibraryHasSelection => SelectedMedia is not null;
    public string LibrarySelectionHint => LibraryHasSelection ? string.Empty : L.T("メディアを選択すると詳細を確認できます");
    public string LibraryMediaFacts => SelectedMedia is { } m ? $"{MediaDisplayFormat.FileSize(m.SizeBytes)} · {(m.Metadata?.DurationSeconds is { } d ? L.F("長さ {0}", MediaDisplayFormat.Duration(d)) : L.T("再生時間は未取得"))} · {m.ResolutionDisplay}" : string.Empty;
    public bool LibraryIsLoading => _libraryLoading;
    private string _libraryPreviewFeedback = string.Empty;
    public string LibraryPreviewFeedback { get => _libraryPreviewFeedback; private set => Set(ref _libraryPreviewFeedback, value); }
    public LibraryEmptyState LibraryContentState => ClassifyLibraryEmpty(_libraryLoading, _libraryLoadFailed, LibraryRegisteredFolders, CurrentLibraryScope.LibraryRootId, MediaItems.Count,
        !string.IsNullOrWhiteSpace(SearchText) || !string.IsNullOrWhiteSpace(TagFilterText), ActiveJobs.ToArray(), _rootScanCurrentness);
    public static LibraryEmptyState ClassifyLibraryEmpty(bool loading, bool failed, IReadOnlyList<LibraryRoot> roots, Guid? scope, int count, bool filtered, IReadOnlyList<Job> jobs, IReadOnlyList<RootScanCurrentness> scans)
    {
        if (loading) return LibraryEmptyState.Loading;
        if (failed) return LibraryEmptyState.Error;
        if (!roots.Any(r => r.RegistrationState == RootRegistrationState.Active)) return LibraryEmptyState.NoFolders;
        if (count > 0) return LibraryEmptyState.HasMedia;
        if (filtered) return LibraryEmptyState.FilteredEmpty;
        var scoped = roots.Where(r => r.RegistrationState == RootRegistrationState.Active && (scope is null || r.LibraryRootId == scope)).ToArray();
        if (HasLibraryScanProcessing(scoped, jobs)) return LibraryEmptyState.Processing;
        return scoped.Length > 0 && scoped.All(r => r.Availability == RootAvailability.Online && scans.Any(s => s.LibraryRootId == r.LibraryRootId && s.RegistrationRevision == r.RegistrationRevision && s.State == "Completed" && s.ErrorCount == 0)) ? LibraryEmptyState.ScannedEmpty : LibraryEmptyState.UnverifiedEmpty;
    }
    private static bool HasLibraryScanProcessing(IReadOnlyList<LibraryRoot> scopedRoots, IReadOnlyList<Job> jobs)
    {
        // Only an executing Scan can change the media listing. Analysis/waiting jobs
        // and failures are not evidence that this scope's listing is still being built.
        foreach (var job in jobs.Where(j => j.JobType == JobType.Scan && j.State == JobState.Running))
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(job.PayloadJson);
                // Do not infer a registration lifecycle from legacy/defaulted payload fields.
                if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty(nameof(ScanPayload.RegistrationRevision), out var revision) ||
                    revision.ValueKind != System.Text.Json.JsonValueKind.Number ||
                    !revision.TryGetInt64(out var registrationRevision)) continue;
                var payload = System.Text.Json.JsonSerializer.Deserialize<ScanPayload>(job.PayloadJson);
                if (payload is not null && scopedRoots.Any(r => r.LibraryRootId == payload.LibraryRootId && r.RegistrationRevision == registrationRevision)) return true;
            }
            catch (System.Text.Json.JsonException) { }
        }
        return false;
    }
    public string LibraryContentMessage => LibraryContentState switch
    {
        LibraryEmptyState.Loading => L.T("メディア一覧を取得しています。"),
        LibraryEmptyState.NoFolders => L.T("登録フォルダーがありません。フォルダーを追加してください。"),
        LibraryEmptyState.Processing => L.T("メディアを確認しています。進み具合は画面下の「現在の処理」で確認できます。"),
        LibraryEmptyState.FilteredEmpty => L.T("条件に一致するメディアがありません。検索・タグ条件を解除できます。"),
        LibraryEmptyState.ScannedEmpty => L.T("スキャンが完了した現在の範囲に、表示できるメディアはありません。"),
        LibraryEmptyState.UnverifiedEmpty => L.T("現在の範囲に表示できるメディアはありません。フォルダーの状態と更新状況を確認してください。"),
        LibraryEmptyState.Error => L.T("メディア一覧を確認できませんでした。フォルダーへのアクセスを確認して更新できます。"),
        _ => L.F("{0:N0} 件 · {1}", TotalMediaCount, PageStatus)
    };
    public bool LibraryNeedsFolder => LibraryContentState == LibraryEmptyState.NoFolders;
    public bool LibraryHasFilters => !string.IsNullOrWhiteSpace(SearchText) || !string.IsNullOrWhiteSpace(TagFilterText);
    private AsyncCommand? _libraryPreviewCommand, _libraryClearFiltersCommand, _viewManagedFolderCommand;
    public AsyncCommand LibraryPreviewCommand => _libraryPreviewCommand ??= new(async () => { LibraryPreviewFeedback = string.Empty; await SelectMediaForPreviewAsync(); await OpenPreviewAsync(); if (!IsPreviewOpen) LibraryPreviewFeedback = L.T("プレビューを開けませんでした。対象ファイルの利用状態を確認してください。"); }, () => SelectedMedia is { PresenceState: PresenceState.Present } && !IsPreviewBusy);
    public AsyncCommand LibraryClearFiltersCommand => _libraryClearFiltersCommand ??= new(async () => { TagFilterText = string.Empty; SearchText = string.Empty; CurrentPage = 0; await SearchAsync(); }, () => LibraryHasFilters);
    public AsyncCommand ViewManagedFolderCommand => _viewManagedFolderCommand ??= new(() => { if (SelectedRoot is { RegistrationState: RootRegistrationState.Active } root) CurrentLibraryScope = LibraryScopeOptions.First(s => s.LibraryRootId == root.LibraryRootId); return ScopeRefreshTask; }, () => SelectedRoot is { RegistrationState: RootRegistrationState.Active });
    private void NotifyLibraryPresentation()
    {
        foreach (var p in new[] { nameof(LibraryRegisteredFolders), nameof(LibraryHasSelection), nameof(LibrarySelectionHint), nameof(LibraryMediaFacts), nameof(LibraryIsLoading), nameof(LibraryContentState), nameof(LibraryContentMessage), nameof(LibraryNeedsFolder), nameof(LibraryHasFilters) }) Raise(p);
        _libraryPreviewCommand?.RaiseCanExecuteChanged(); _libraryClearFiltersCommand?.RaiseCanExecuteChanged(); _viewManagedFolderCommand?.RaiseCanExecuteChanged();
    }
}
