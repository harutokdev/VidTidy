using LocalMediaAI.App.ViewModels;
using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;

namespace LocalMediaAI.Infrastructure.Tests;

internal static class ReviewEvidenceFixture
{
    public static MainViewModel Create(SqliteLibraryRepository repository, AppPaths paths, JsonFileLogger log)
    {
        var jobs = new SqliteJobStore(paths, log); var worker = new WorkerProcessManager(paths, log);
        var pipeline = new MediaPipeline(repository, jobs, new FileSystemMediaScanner(), new FfmpegAdapter(new FfmpegPaths(null, null), paths, log), log, worker: worker, paths: paths);
        return new(repository, jobs, pipeline, worker, new Search(), log, fileOperations: new SafeFileOperationService(repository, log));
    }
    public static async Task Load(MainViewModel vm)
    {
        vm.RefreshUnifiedReviewCommand.Execute(null);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (vm.IsReviewLoading) { if (DateTime.UtcNow > deadline) throw new TimeoutException(); await Task.Delay(10); }
        await vm.ReviewEvidenceLoadTask;
    }
    private sealed class Search : ISemanticSearchService
    {
        public SemanticModelProfile SemanticModel => SemanticModelRegistry.ResolveDevelopmentCandidate();
        public SemanticExecutionProfile SemanticExecution => new("cuda", "float32");
        public Task<IReadOnlyList<SemanticSearchCandidate>> SearchAsync(string query, int topK, CancellationToken cancellationToken, Guid? libraryRootId = null) => Task.FromResult<IReadOnlyList<SemanticSearchCandidate>>([]);
    }
}

public sealed partial class NearDuplicateFoundationTests
{
    [Fact]
    public async Task ReviewEvidenceNearUsesCurrentFramesAndSpecificMemberIdentity()
    {
        var fixture = await CreateVerifiedNearFixtureAsync(19);
        using var vm = ReviewEvidenceFixture.Create(fixture.Repository, _paths, _log); await ReviewEvidenceFixture.Load(vm);
        Assert.True(vm.SelectedUnifiedReview!.IsNear); Assert.Equal(2, vm.ReviewEvidence.Count);
        foreach (var card in vm.ReviewEvidence)
        {
            Assert.NotNull(card.Frame); Assert.True(card.Timestamp >= 0); Assert.NotEmpty(card.Duration);
        }
        Assert.False(vm.ConfirmUnifiedReviewCommand.CanExecute(null)); Assert.True(vm.ConfirmNearUnifiedReviewCommand.CanExecute(null));
        vm.SelectedUnifiedReview = null; Assert.Empty(vm.ReviewEvidence);
    }
}

public sealed partial class ContentSimilarityFoundationTests
{
    [Fact]
    public async Task ReviewEvidenceContentUsesBothRepresentativeTimestampsWithoutDuplicateLabel()
    {
        var fixture = await CreateStrongPairAsync(19);
        var candidate = Assert.Single(await fixture.Repository.GetContentSimilarityCandidatesAsync(fixture.Root.LibraryRootId, ContentSimilarityVectors.AggregationVersion, Model, Revision, Processor, Sampling, Dtype, Dimension, 20, CancellationToken.None));
        await PersistEvidenceAsync(fixture.Repository, candidate, "Verified");
        using var vm = ReviewEvidenceFixture.Create(fixture.Repository, _paths, _log); await ReviewEvidenceFixture.Load(vm);
        Assert.True(vm.SelectedUnifiedReview!.IsContent); Assert.Equal(2, vm.ReviewEvidence.Count);
        Assert.Contains("重複を意味しません", vm.ReviewMeaning, StringComparison.Ordinal);
        foreach (var card in vm.ReviewEvidence) Assert.NotNull(card.Frame);
        Assert.True(vm.RelatedUnifiedReviewCommand.CanExecute(null)); Assert.False(vm.ConfirmUnifiedReviewCommand.CanExecute(null));
    }
}
