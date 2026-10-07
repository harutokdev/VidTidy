using LocalMediaAI.App.ViewModels;
using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class MainViewModelTaggingAcceptanceTests
{
    [Fact]
    public void SelectingSuggestedTagSetsRepresentativePreviewAndHumanWarning()
    {
        using var fixture = new Fixture();
        var row = CreateRow(timestamp: 12.5d);
        fixture.ViewModel.SelectedTagReview = row;

        Assert.Equal(12.5d, fixture.ViewModel.PreviewTimestampSeconds);
        Assert.Contains("Human-confirmed Tagではありません", fixture.ViewModel.TagReviewStatus, StringComparison.Ordinal);
        Assert.True(fixture.ViewModel.ConfirmTagCommand.CanExecute(null));
        Assert.True(fixture.ViewModel.RejectTagCommand.CanExecute(null));
    }

    [Fact]
    public async Task BulkConfirmRequiresExplicitRowsAndAcknowledgement()
    {
        using var fixture = new Fixture();
        var row = CreateRow();
        fixture.ViewModel.TagReviewItems.Add(row);
        fixture.ViewModel.BulkConfirmTagsCommand.Execute(null);
        await Task.Delay(30);
        Assert.Contains("明示的にチェック", fixture.ViewModel.TagReviewStatus, StringComparison.Ordinal);

        row.IsBulkSelected = true;
        fixture.ViewModel.BulkConfirmTagsCommand.Execute(null);
        await Task.Delay(30);
        Assert.Contains("確認チェック", fixture.ViewModel.TagReviewStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualTagCommandsRequireBothSelectedEvidenceAndNonblankInput()
    {
        using var fixture = new Fixture();
        Assert.False(fixture.ViewModel.AddManualTagCommand.CanExecute(null));
        fixture.ViewModel.SelectedTagReview = CreateRow();
        Assert.False(fixture.ViewModel.AddManualTagCommand.CanExecute(null));
        fixture.ViewModel.ManualTagText = "家族旅行";
        Assert.True(fixture.ViewModel.AddManualTagCommand.CanExecute(null));
        Assert.True(fixture.ViewModel.RemoveManualTagCommand.CanExecute(null));
    }

    [Fact]
    public void TagFilterDefaultsToSuggestedInclusiveButCanRepresentHumanOnlyScope()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.ViewModel.TagFilterIncludeSuggested);
        fixture.ViewModel.TagFilterText = "ocean";
        fixture.ViewModel.TagFilterIncludeSuggested = false;
        Assert.Equal("ocean", fixture.ViewModel.TagFilterText);
        Assert.False(fixture.ViewModel.TagFilterIncludeSuggested);
    }

    private static TagReviewRow CreateRow(double? timestamp = 10d)
    {
        var now = DateTimeOffset.UtcNow;
        var evidence = new MediaTagEvidence(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), TaggingTaxonomy.TaxonomyVersion, "ocean", "海", "Environment", MediaTagSource.EmbeddingZeroShot, "fixture/model", "revision", "processor", "float32", 4, "fixed-interval-v1", TaggingTaxonomy.DescriptiveTextRepresentationVersion, TaggingVerificationProfile.DescriptiveHighPrecisionV1.AggregationVersion, TaggingVerificationProfile.DescriptiveHighPrecisionV1.ThresholdVersion, TaggingVectors.ScoreMetric, 0.91d, 0.87d, 3, 3, 3, 1d, "source", MediaTagEvidenceStatus.Suggested, null, null, now);
        return new TagReviewRow(new MediaTagReviewItem(evidence, MediaTagJudgmentDecision.Unreviewed, null, "ocean.mp4", @"C:\isolated\ocean.mp4", PresenceState.Present, timestamp, "frames/ocean.jpg"));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        private readonly JsonFileLogger _logger;
        public Fixture()
        {
            _root = Path.Combine(Path.GetTempPath(), "LocalMediaAI-TagViewModel", Guid.NewGuid().ToString("N"));
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
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class EmptySearchDouble : ISemanticSearchService
    {
        public SemanticModelProfile SemanticModel => SemanticModelRegistry.ResolveDevelopmentCandidate();
        public SemanticExecutionProfile SemanticExecution => new("cuda", "float32");
        public Task<IReadOnlyList<SemanticSearchCandidate>> SearchAsync(string query, int topK, CancellationToken cancellationToken, Guid? libraryRootId = null) => Task.FromResult<IReadOnlyList<SemanticSearchCandidate>>([]);
    }
}
