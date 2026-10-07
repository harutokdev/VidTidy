using LocalMediaAI.App.ViewModels;
using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class MainViewModelUnifiedReviewAcceptanceTests
{
    [Fact]
    public void SelectingExactUnifiedRowEnablesOnlyCompatibleCommandsAndExplainsPriority()
    {
        using var fixture = new Fixture();
        fixture.ViewModel.SelectedUnifiedReview = CreateRow(UnifiedReviewType.ExactDuplicate, ReviewEvidenceState.Current);

        Assert.True(fixture.ViewModel.ConfirmUnifiedReviewCommand.CanExecute(null));
        Assert.True(fixture.ViewModel.KeepBothUnifiedReviewCommand.CanExecute(null));
        Assert.False(fixture.ViewModel.ConfirmNearUnifiedReviewCommand.CanExecute(null));
        Assert.False(fixture.ViewModel.RelatedUnifiedReviewCommand.CanExecute(null));
        Assert.False(fixture.ViewModel.ConfirmTagUnifiedReviewCommand.CanExecute(null));
        Assert.Contains("priority", fixture.ViewModel.UnifiedReviewStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StaleOrUnavailableUnifiedRowsCannotReceiveNewDecision()
    {
        using var fixture = new Fixture();
        fixture.ViewModel.SelectedUnifiedReview = CreateRow(UnifiedReviewType.SuggestedTag, ReviewEvidenceState.SourceUnavailable);

        Assert.False(fixture.ViewModel.ConfirmTagUnifiedReviewCommand.CanExecute(null));
        Assert.False(fixture.ViewModel.IgnoreUnifiedReviewCommand.CanExecute(null));
        Assert.Contains("Source unavailable", fixture.ViewModel.UnifiedReviewStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BulkUnifiedReviewRequiresExplicitRowsAndAcknowledgement()
    {
        using var fixture = new Fixture();
        var row = CreateRow(UnifiedReviewType.NearDuplicate, ReviewEvidenceState.Current);
        fixture.ViewModel.UnifiedReviewItems.Add(row);
        Assert.False(fixture.ViewModel.BulkUnifiedReviewCommand.CanExecute(null));

        row.IsBulkSelected = true;
        await Task.Delay(30);
        Assert.False(fixture.ViewModel.BulkUnifiedReviewCommand.CanExecute(null));
        Assert.Contains("一括action preview", fixture.ViewModel.UnifiedReviewBulkPreview, StringComparison.Ordinal);

        fixture.ViewModel.BulkUnifiedReviewConfirmation = true;
        Assert.True(fixture.ViewModel.BulkUnifiedReviewCommand.CanExecute(null));
    }

    [Fact]
    public void UnifiedReviewFilterSortAndBulkActionRemainExplicit()
    {
        using var fixture = new Fixture();
        fixture.ViewModel.UnifiedReviewFilter = UnifiedReviewFilter.ContentSimilar;
        fixture.ViewModel.UnifiedReviewSort = UnifiedReviewSort.MediaSizeDescending;
        fixture.ViewModel.UnifiedReviewBulkAction = UnifiedReviewAction.NotRelated;

        Assert.Equal(UnifiedReviewFilter.ContentSimilar, fixture.ViewModel.UnifiedReviewFilter);
        Assert.Equal(UnifiedReviewSort.MediaSizeDescending, fixture.ViewModel.UnifiedReviewSort);
        Assert.Equal(UnifiedReviewAction.NotRelated, fixture.ViewModel.UnifiedReviewBulkAction);
        Assert.Contains(UnifiedReviewFilter.SuggestedTag, fixture.ViewModel.UnifiedReviewFilters);
        Assert.Contains(UnifiedReviewAction.ConfirmTag, fixture.ViewModel.UnifiedReviewBulkActions);
    }

    private static UnifiedReviewRow CreateRow(UnifiedReviewType type, ReviewEvidenceState state)
    {
        var id = Guid.NewGuid();
        var item = new UnifiedReviewItem(UnifiedReviewPolicy.CreateReviewItemId(type, id), type, id.ToString("D"), id, Guid.NewGuid(), Guid.NewGuid(), null, null, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), state, state == ReviewEvidenceState.Current ? "Available" : "Source unavailable", "Unreviewed", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UnifiedReviewPolicy.ComputePriority(type, "Unreviewed", state), UnifiedReviewPolicy.PriorityReason(type, "Unreviewed", state), "fixture evidence", "fixture evidence summary", "fixture type-specific score", "fixture provenance", null, null, 1L, 1, "fixture");
        return new UnifiedReviewRow(item);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        private readonly JsonFileLogger _logger;
        public Fixture()
        {
            _root = Path.Combine(Path.GetTempPath(), "LocalMediaAI-UnifiedReviewViewModel", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "portable.flag"), string.Empty);
            var paths = AppPaths.Create(_root);
            paths.EnsureDirectories();
            _logger = new JsonFileLogger(paths);
            var library = new SqliteLibraryRepository(paths, _logger);
            var jobs = new SqliteJobStore(paths, _logger);
            var worker = new WorkerProcessManager(paths, _logger);
            var ffmpeg = new FfmpegAdapter(new FfmpegPaths(null, null), paths, _logger);
            var pipeline = new MediaPipeline(library, jobs, new FileSystemMediaScanner(), ffmpeg, _logger, worker: worker, paths: paths);
            ViewModel = new MainViewModel(library, jobs, pipeline, worker, new EmptySearchDouble(), _logger);
        }
        public MainViewModel ViewModel { get; }
        public void Dispose()
        {
            ViewModel.Dispose();
            _logger.Dispose();
            SqliteConnection.ClearAllPools();
            for (var attempt = 0; Directory.Exists(_root) && attempt < 8; attempt++)
            {
                try { Directory.Delete(_root, recursive: true); }
                catch (IOException) { Thread.Sleep(100); SqliteConnection.ClearAllPools(); }
                catch (UnauthorizedAccessException) { Thread.Sleep(100); SqliteConnection.ClearAllPools(); }
            }
        }
    }

    private sealed class EmptySearchDouble : ISemanticSearchService
    {
        public SemanticModelProfile SemanticModel => SemanticModelRegistry.ResolveDevelopmentCandidate();
        public SemanticExecutionProfile SemanticExecution => new("cuda", "float32");
        public Task<IReadOnlyList<SemanticSearchCandidate>> SearchAsync(string query, int topK, CancellationToken cancellationToken, Guid? libraryRootId = null) => Task.FromResult<IReadOnlyList<SemanticSearchCandidate>>([]);
    }
}
