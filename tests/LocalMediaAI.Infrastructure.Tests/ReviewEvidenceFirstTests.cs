using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using LocalMediaAI.App;
using LocalMediaAI.App.ViewModels;
using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed partial class UnifiedReviewFoundationTests
{
    private MainViewModel ReviewVm(SqliteLibraryRepository repository)
    {
        var jobs = new SqliteJobStore(_paths, _log); var worker = new WorkerProcessManager(_paths, _log);
        var pipeline = new MediaPipeline(repository, jobs, new FileSystemMediaScanner(), new FfmpegAdapter(new FfmpegPaths(null, null), _paths, _log), _log, worker: worker, paths: _paths);
        return new MainViewModel(repository, jobs, pipeline, worker, new ReviewSearchDouble(), _log, fileOperations: new SafeFileOperationService(repository, _log));
    }
    private static async Task ReviewReload(MainViewModel vm)
    {
        vm.RefreshUnifiedReviewCommand.Execute(null);
        await ReviewWait(() => !vm.IsReviewLoading);
        await vm.ReviewEvidenceLoadTask;
    }
    private static async Task ReviewWait(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!predicate()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("Review did not settle."); await Task.Delay(10); }
    }

    [Fact]
    public async Task ReviewEvidenceExactAndTagCardsUseSpecificMediaAndPreserveJudgments()
    {
        var fixture = await CreateFixtureAsync();
        await SeedExactGroupAsync(fixture.Repository, fixture.Media[0], fixture.Media[1]);
        await SeedSuggestedTagAsync(fixture.Repository, fixture.Media[2]);
        using var vm = ReviewVm(fixture.Repository); await ReviewReload(vm);
        vm.SelectedUnifiedReview = vm.UnifiedReviewItems.Single(r => r.IsExact); await vm.ReviewEvidenceLoadTask;
        Assert.Equal(2, vm.ReviewEvidence.Count); Assert.All(vm.ReviewEvidence, c => Assert.NotEmpty(c.FileName));
        Assert.Equal("完全一致", vm.SelectedUnifiedReview.GeneralType);
        Assert.Null(vm.OperationTarget);
        var card = vm.ReviewEvidence[1]; card.Handoff.Execute(null);
        await ReviewWait(() => vm.OperationTarget is not null);
        Assert.Equal(card.Media.MediaFileId, vm.OperationTarget!.MediaFileId); Assert.Null(vm.OperationPlanningType);
        var history = await fixture.Repository.GetUnifiedReviewDecisionHistoryAsync(vm.SelectedUnifiedReview.ReviewItemId, CancellationToken.None);
        Assert.Empty(history);
        vm.SelectedUnifiedReview = vm.UnifiedReviewItems.Single(r => r.IsTag); await vm.ReviewEvidenceLoadTask;
        Assert.Single(vm.ReviewEvidence); Assert.Equal(fixture.Media[2].MediaFileId, vm.ReviewEvidence[0].Media.MediaFileId);
        Assert.Equal(card.Media.MediaFileId, vm.OperationTarget.MediaFileId);
        vm.UnifiedReviewFilter = UnifiedReviewFilter.NearDuplicate;
        await ReviewWait(() => !vm.IsReviewLoading);
        Assert.Null(vm.SelectedUnifiedReview); Assert.Empty(vm.ReviewEvidence);
        Assert.Contains("絞り込み", vm.ReviewQueueMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReviewEvidenceQueueDistinguishesAnalysisAndCompletedWithoutHistoryAuthority()
    {
        var fixture = await CreateFixtureAsync(); using var vm = ReviewVm(fixture.Repository);
        await ReviewReload(vm); Assert.Contains("確認する候補はありません", vm.ReviewQueueMessage, StringComparison.Ordinal);
        var jobs = new SqliteJobStore(_paths, _log);
        await jobs.EnqueueAsync(JobType.VisualEmbedding, new { }, 1, null, CancellationToken.None);
        await ReviewReload(vm); Assert.Contains("レビュー候補を準備しています", vm.ReviewQueueMessage, StringComparison.Ordinal);
        var tag = await SeedSuggestedTagAsync(fixture.Repository, fixture.Media[2]);
        await fixture.Repository.ApplyUnifiedReviewDecisionAsync(new(UnifiedReviewPolicy.CreateReviewItemId(UnifiedReviewType.SuggestedTag, tag.MediaTagEvidenceId), UnifiedReviewAction.ConfirmTag, null), CancellationToken.None);
        vm.UnifiedReviewFilter = UnifiedReviewFilter.Unreviewed;
        await ReviewWait(() => !vm.IsReviewLoading);
        Assert.Contains("確認が完了しました", vm.ReviewQueueMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReviewEvidenceRealWindow1080By680RendersQueueEvidenceAndContextualJudgment()
    {
        var fixture = await CreateFixtureAsync(); await SeedExactGroupAsync(fixture.Repository, fixture.Media[0], fixture.Media[1]);
        using var vm = ReviewVm(fixture.Repository); await ReviewReload(vm);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow { DataContext = vm, Width = 1080, Height = 680, Left = -10000, ShowInTaskbar = false, ShowActivated = false };
                ((TabControl)window.FindName("TaskWorkspaceTabs")).SelectedIndex = 2;
                window.Show(); window.UpdateLayout();
                var review = Descendants(window).OfType<ReviewWorkspace>().Single();
                Assert.True(review.IsVisible); Assert.True(review.ActualWidth > 950);
                var evidence = Descendants(review).OfType<ItemsControl>().Single(c => System.Windows.Automation.AutomationProperties.GetAutomationId(c) == "ReviewPrimaryEvidence");
                Assert.True(evidence.ActualWidth >= 650); Assert.Equal(2, evidence.Items.Count);
                var buttons = Descendants(review).OfType<Button>().Where(b => b.IsVisible).Select(b => b.Content?.ToString()).ToArray();
                Assert.Contains("完全一致を確認", buttons); Assert.DoesNotContain("関連あり", buttons); Assert.DoesNotContain("タグを確認", buttons);
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var directory = Path.Combine(RepositoryRoot(), "artifacts", "ui-review-evidence"); Directory.CreateDirectory(directory);
                using var output = File.Create(Path.Combine(directory, "review-exact-1080x680.png")); encoder.Save(output);
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
            finally { if (window is not null) { window.DataContext = null; window.UpdateLayout(); window.AuthorizeFinalClose(); } }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); try { await done.Task; } finally { thread.Join(); }
    }

    [Fact]
    public void ReviewEvidenceXamlKeepsTechnicalCollapsedAndBulkExplicit()
    {
        var file = Path.Combine(RepositoryRoot(), "src", "LocalMediaAI.App", "ReviewWorkspace.xaml");
        var document = XDocument.Load(file);
        Assert.All(document.Descendants().Where(e => e.Name.LocalName == "Expander"), e => Assert.Equal("False", (string?)e.Attribute("IsExpanded")));
        var source = File.ReadAllText(file);
        Assert.Contains("ReviewBulkMode", source, StringComparison.Ordinal);
        Assert.Contains("内容の類似", File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "LocalMediaAI.App", "ViewModels", "MainViewModel.ReviewEvidence.cs")), StringComparison.Ordinal);
        Assert.DoesNotContain("Hamming", source, StringComparison.Ordinal);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "LocalMediaAI.App"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root missing.");
    }
    private sealed class ReviewSearchDouble : ISemanticSearchService
    {
        public SemanticModelProfile SemanticModel => SemanticModelRegistry.ResolveDevelopmentCandidate();
        public SemanticExecutionProfile SemanticExecution => new("cuda", "float32");
        public Task<IReadOnlyList<SemanticSearchCandidate>> SearchAsync(string query, int topK, CancellationToken cancellationToken, Guid? libraryRootId = null) => Task.FromResult<IReadOnlyList<SemanticSearchCandidate>>([]);
    }
}
