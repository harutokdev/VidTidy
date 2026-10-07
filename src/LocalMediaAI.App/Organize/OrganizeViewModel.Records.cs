using LocalMediaAI.Core.Organize;

namespace LocalMediaAI.App.Organize;

/// <summary>Cleanup history, files the analysis could not finish, and lookups for the drawers.</summary>
public sealed partial class OrganizeViewModel
{
    private IReadOnlyList<CleanupRun> _history = [];
    private IReadOnlyList<UnanalyzedFile> _unanalyzed = [];

    public IReadOnlyList<CleanupRun> History => _history;
    public IReadOnlyList<UnanalyzedFile> Unanalyzed => _unanalyzed;
    public long CleanedTotalBytes => _history.Sum(r => r.Bytes);

    /// <summary>Reloads history and unanalyzed files; the list itself is untouched.</summary>
    public async Task LoadRecordsAsync(CancellationToken cancellationToken)
    {
        _history = await _source.LoadHistoryAsync(cancellationToken);
        _unanalyzed = await _source.LoadUnanalyzedAsync(cancellationToken);
        Raise(nameof(History));
        Raise(nameof(Unanalyzed));
        Raise(nameof(CleanedTotalBytes));
        Raise(nameof(NextAction));
    }

    public Task<IReadOnlyList<MatchedScene>> LoadMatchedScenesAsync(GroupRow group, CancellationToken cancellationToken) => _source.LoadMatchedScenesAsync(group.Data, cancellationToken);

    public Task<IReadOnlyList<string>> LoadHistoryFilesAsync(CleanupRun run, CancellationToken cancellationToken) => _source.LoadHistoryFilesAsync(run, cancellationToken);

    public Task<IReadOnlyList<OrganizeFile>> LoadFilesAsync(Guid? folderId, int limit, CancellationToken cancellationToken) => _source.LoadFilesAsync(folderId, limit, cancellationToken);

    /// <summary>Rescans one folder (もう一度解析する). Unchanged files keep their analysis.</summary>
    /// <returns>False while analysis is running; the folder is then already being checked or waits its turn.</returns>
    public async Task<bool> RecheckFolderAsync(Guid folderId, CancellationToken cancellationToken)
    {
        if (Background is null || IsAnalyzing) return false;
        await Background.RequestChangeCheckAsync([folderId], cancellationToken);
        await PollAnalysisAsync(DateTimeOffset.UtcNow, cancellationToken);
        return true;
    }
}
