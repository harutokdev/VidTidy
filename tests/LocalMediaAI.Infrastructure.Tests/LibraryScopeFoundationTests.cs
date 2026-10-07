using LocalMediaAI.App.ViewModels;
using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed partial class LibraryScopeFoundationTests : IAsyncLifetime, IDisposable
{
    private readonly string _fixturePath = Path.Combine(Path.GetTempPath(), "LocalMediaAIScopeTests", Guid.NewGuid().ToString("N"));
    private JsonFileLogger _log = null!;
    private SqliteLibraryRepository _library = null!;
    private SqliteJobStore _jobs = null!;
    private MainViewModel _viewModel = null!;
    private LibraryRoot _rootA = null!;
    private LibraryRoot _rootB = null!;
    private readonly ScopeSearchDouble _search = new();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_fixturePath);
        File.WriteAllText(Path.Combine(_fixturePath, "portable.flag"), string.Empty);
        var paths = AppPaths.Create(_fixturePath);
        paths.EnsureDirectories();
        _log = new JsonFileLogger(paths);
        _library = new SqliteLibraryRepository(paths, _log);
        await _library.InitializeAsync(CancellationToken.None);
        _jobs = new SqliteJobStore(paths, _log);
        _rootA = await SeedRootAsync("A", 2);
        _rootB = await SeedRootAsync("B", 1);
        var worker = new WorkerProcessManager(paths, _log);
        var pipeline = new MediaPipeline(_library, _jobs, new FileSystemMediaScanner(), new FfmpegAdapter(new FfmpegPaths(null, null), paths, _log), _log, worker: worker, paths: paths);
        _viewModel = new MainViewModel(_library, _jobs, pipeline, worker, _search, _log);
        await ReloadAsync();
    }

    [Fact]
    public async Task SharedScopeFiltersLibraryAndPreservesOperationInputWithoutProcessingMutation()
    {
        Assert.Null(_viewModel.CurrentLibraryScope.LibraryRootId);
        Assert.Equal(3, _viewModel.MediaItems.Count);
        var selected = _viewModel.MediaItems.First(item => item.FileName.StartsWith('A'));
        _viewModel.SelectedMedia = selected;
        Assert.True(await _viewModel.HandoffMediaToOperationAsync(selected.MediaFileId, "Scope isolation test"));
        _viewModel.OperationDestinationPath = @"E:\scope-test-destination";
        _viewModel.AddSelectedToControlledBatchCommand.Execute(null);
        _viewModel.OperationPlanningAcknowledgement = true;
        _viewModel.OperationPlanningFinalConfirmation = true;
        var operationSource = _viewModel.OperationPlanningSelectedSource;
        var rootsBefore = await _library.GetLibraryRootsAsync(CancellationToken.None);
        var retentionBefore = await _library.GetRetentionRecordsAsync(CancellationToken.None);

        _viewModel.SelectedRoot = _rootB;
        Assert.Null(_viewModel.CurrentLibraryScope.LibraryRootId);
        _viewModel.CurrentLibraryScope = Scope(_rootA);
        await _viewModel.ScopeRefreshTask;
        Assert.Equal(2, _viewModel.MediaItems.Count);
        Assert.Equal(selected.MediaFileId, _viewModel.SelectedMediaFileId);
        _viewModel.CurrentLibraryScope = Scope(_rootB);
        await _viewModel.ScopeRefreshTask;
        Assert.StartsWith("B", Assert.Single(_viewModel.MediaItems).FileName, StringComparison.Ordinal);
        Assert.Null(_viewModel.SelectedMedia);
        Assert.Equal(operationSource, _viewModel.OperationPlanningSelectedSource);
        Assert.Equal(1, _viewModel.OperationPlanningSelectedCount);
        Assert.Equal(@"E:\scope-test-destination", _viewModel.OperationDestinationPath);
        Assert.Equal(selected.MediaFileId, Assert.Single(_viewModel.ControlledBatchSelection).MediaFileId);
        Assert.False(_viewModel.OperationPlanningAcknowledgement);
        Assert.False(_viewModel.OperationPlanningFinalConfirmation);
        Assert.Empty(await _jobs.GetRecentAsync(50, CancellationToken.None));
        Assert.Equal(rootsBefore, await _library.GetLibraryRootsAsync(CancellationToken.None));
        Assert.Equal(retentionBefore, await _library.GetRetentionRecordsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SearchScopeTransitionRetainsQueryClearsResultsAndRequiresExplicitSearch()
    {
        _viewModel.CurrentLibraryScope = Scope(_rootA);
        await _viewModel.ScopeRefreshTask;
        _viewModel.SemanticSearchText = "海の動画";
        await SearchAsync();
        Assert.Equal(_rootA.LibraryRootId, Assert.Single(_search.RequestedRoots));
        Assert.NotNull(_viewModel.SelectedSemanticResult);

        _viewModel.CurrentLibraryScope = Scope(_rootB);
        Assert.Empty(_viewModel.SemanticResults);
        Assert.Null(_viewModel.SelectedSemanticResult);
        Assert.Equal("海の動画", _viewModel.SemanticSearchText);
        await _viewModel.ScopeRefreshTask;
        Assert.Single(_search.RequestedRoots);
        await SearchAsync();
        Assert.Equal(_rootB.LibraryRootId, _search.RequestedRoots[1]);
        Assert.Equal(_rootB.LibraryRootId, Assert.Single(_viewModel.SemanticResults).MediaFileId);
    }

    [Fact]
    public async Task NonCooperativeOldSearchCannotPublishAfterScopeChanges()
    {
        _viewModel.CurrentLibraryScope = Scope(_rootA);
        await _viewModel.ScopeRefreshTask;
        var completion = new TaskCompletionSource<IReadOnlyList<SemanticSearchCandidate>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _search.DelayedResponse = completion;
        _viewModel.SemanticSearchText = "遅延検索";
        _viewModel.SemanticSearchCommand.Execute(null);
        await WaitForAsync(() => _search.RequestedRoots.Count == 1);
        _viewModel.CurrentLibraryScope = Scope(_rootB);
        completion.SetResult([ScopeSearchDouble.Candidate(_rootA.LibraryRootId)]);
        await _viewModel.ScopeRefreshTask;
        Assert.Empty(_viewModel.SemanticResults);
        Assert.Null(_viewModel.SelectedSemanticResult);
        Assert.Contains("対象範囲が変更", _viewModel.SemanticSearchStatus, StringComparison.Ordinal);
        Assert.False(_viewModel.IsSemanticSearchBusy);
    }

    [Fact]
    public async Task OfflineKeepsIdentityAndUnregisterFallsBackWithoutReregisterRestoration()
    {
        _viewModel.CurrentLibraryScope = Scope(_rootA);
        await _viewModel.ScopeRefreshTask;
        await _library.SetRootAvailabilityAsync(_rootA.LibraryRootId, RootAvailability.Offline, CancellationToken.None);
        await ReloadAsync();
        Assert.Equal(_rootA.LibraryRootId, _viewModel.CurrentLibraryScope.LibraryRootId);
        Assert.Contains(_viewModel.LibraryScopeOptions, scope => scope.LibraryRootId == _rootA.LibraryRootId);

        await _library.UnregisterRootAsync(_rootA.LibraryRootId, DateTimeOffset.UtcNow, CancellationToken.None);
        await ReloadAsync();
        await _viewModel.ScopeRefreshTask;
        Assert.Null(_viewModel.CurrentLibraryScope.LibraryRootId);
        Assert.DoesNotContain(_viewModel.LibraryScopeOptions, scope => scope.LibraryRootId == _rootA.LibraryRootId);
        _viewModel.CurrentLibraryScope = Scope(_rootA);
        Assert.Null(_viewModel.CurrentLibraryScope.LibraryRootId);
        await _library.ReregisterRootAsync(_rootA.ConfiguredPath, CancellationToken.None);
        await ReloadAsync();
        Assert.Null(_viewModel.CurrentLibraryScope.LibraryRootId);
        Assert.Contains(_viewModel.LibraryScopeOptions, scope => scope.LibraryRootId == _rootA.LibraryRootId);
    }

    [Fact]
    public async Task ScopeChangeInvalidatesSingleMediaReviewSelectionWithoutChangingJudgmentHistory()
    {
        var row = (await _library.QueryMediaAsync(_rootA.LibraryRootId, null, 0, 100, CancellationToken.None)).Items[0];
        var media = (await _library.GetMediaFileAsync(row.MediaFileId, CancellationToken.None))!;
        var evidence = new MediaTagEvidence(Guid.NewGuid(), media.MediaFileId, media.MediaContentId, null, TaggingTaxonomy.TaxonomyVersion,
            "ocean", "海", "Environment", MediaTagSource.EmbeddingZeroShot, "fixture-model", "fixture-revision", "fixture-processor", "float32", 4,
            "fixed-interval-v1", TaggingTaxonomy.DescriptiveTextRepresentationVersion, "media-sample-max-mean-v1", "tagging-embedding-zero-shot-v1-high-precision",
            "Cosine similarity", 0.9, 0.8, 2, 2, 2, 1, "fixture-digest", MediaTagEvidenceStatus.Suggested, null, null, DateTimeOffset.UtcNow);
        Assert.True(await _library.UpsertMediaTagEvidenceAsync(evidence, CancellationToken.None));
        _viewModel.CurrentLibraryScope = Scope(_rootA);
        await _viewModel.ScopeRefreshTask;
        Assert.NotNull(_viewModel.SelectedTagReview);
        Assert.NotNull(_viewModel.SelectedUnifiedReview);
        var id = UnifiedReviewPolicy.CreateReviewItemId(UnifiedReviewType.SuggestedTag, evidence.MediaTagEvidenceId);
        var history = await _library.GetUnifiedReviewDecisionHistoryAsync(id, CancellationToken.None);

        _viewModel.CurrentLibraryScope = Scope(_rootB);
        Assert.Null(_viewModel.SelectedTagReview);
        Assert.Null(_viewModel.SelectedUnifiedReview);
        await _viewModel.ScopeRefreshTask;
        Assert.Empty(_viewModel.TagReviewItems);
        Assert.Empty(_viewModel.UnifiedReviewItems);
        Assert.Equal(history, await _library.GetUnifiedReviewDecisionHistoryAsync(id, CancellationToken.None));
        Assert.Empty(await _library.GetMediaTagJudgmentsAsync(evidence.MediaTagEvidenceId, CancellationToken.None));
    }

    private async Task<LibraryRoot> SeedRootAsync(string name, int count)
    {
        var directory = Path.Combine(_fixturePath, name);
        Directory.CreateDirectory(directory);
        for (var index = 0; index < count; index++)
            await File.WriteAllBytesAsync(Path.Combine(directory, $"{name}{index}.mp4"), new byte[100 + index]);
        var root = await _library.AddOrGetLibraryRootAsync(directory, CancellationToken.None);
        var scan = await _library.StartScanAsync(root.LibraryRootId, CancellationToken.None);
        await foreach (var file in new FileSystemMediaScanner().EnumerateAsync(root, CancellationToken.None))
            await _library.UpsertScannedFileAsync(file, scan, CancellationToken.None);
        await _library.CompleteScanAsync(scan, new ScanCompletion("Completed", count, count, 0, 0, 0, DateTimeOffset.UtcNow), CancellationToken.None);
        return root;
    }

    private async Task ReloadAsync()
    {
        _viewModel.RefreshCommand.Execute(null);
        await WaitForAsync(() => _viewModel.RefreshCommand.CanExecute(null));
    }

    private async Task SearchAsync()
    {
        _viewModel.SemanticSearchCommand.Execute(null);
        await WaitForAsync(() => !_viewModel.IsSemanticSearchBusy);
    }

    private static LibraryScopePresentation Scope(LibraryRoot root) => new(root.LibraryRootId, root.DisplayName);
    private static async Task WaitForAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Scope transition did not settle.");
            await Task.Delay(10);
        }
    }

    public void Dispose()
    {
        _viewModel?.Dispose();
        _log?.Dispose();
        GC.SuppressFinalize(this);
    }

    public async Task DisposeAsync()
    {
        Dispose();
        SqliteConnection.ClearAllPools();
        for (var attempt = 0; Directory.Exists(_fixturePath) && attempt < 8; attempt++)
        {
            try { Directory.Delete(_fixturePath, true); }
            catch (IOException) { await Task.Delay(100); }
            catch (UnauthorizedAccessException) { await Task.Delay(100); }
        }
    }

    private sealed class ScopeSearchDouble : ISemanticSearchService
    {
        public List<Guid?> RequestedRoots { get; } = [];
        public TaskCompletionSource<IReadOnlyList<SemanticSearchCandidate>>? DelayedResponse { get; set; }
        public IReadOnlyList<SemanticSearchCandidate>? ImmediateResults { get; set; }
        public Exception? Failure { get; set; }
        public SemanticModelProfile SemanticModel => SemanticModelRegistry.ResolveDevelopmentCandidate();
        public SemanticExecutionProfile SemanticExecution => new("cuda", "float32");
        public Task<IReadOnlyList<SemanticSearchCandidate>> SearchAsync(string query, int topK, CancellationToken cancellationToken, Guid? libraryRootId = null)
        {
            RequestedRoots.Add(libraryRootId);
            if (Failure is not null) return Task.FromException<IReadOnlyList<SemanticSearchCandidate>>(Failure);
            return DelayedResponse?.Task ?? Task.FromResult(ImmediateResults ?? (IReadOnlyList<SemanticSearchCandidate>)[Candidate(libraryRootId ?? Guid.NewGuid())]);
        }
        public static SemanticSearchCandidate Candidate(Guid id) => new(id, "scope.mp4", @"E:\scope.mp4", 0, 0.9, Guid.NewGuid(), Guid.NewGuid());
    }
}
