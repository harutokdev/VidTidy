using LocalMediaAI.App.ViewModels;
using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class MainViewModelSemanticSearchAcceptanceTests
{
    [Fact]
    public async Task CancelDuringDelayedSearchTransitionsToCancelledAndSuppressesStaleResult()
    {
        var delayed = new DelayedSearchDouble(ignoreCancellation: false);
        using var fixture = new ViewModelFixture(delayed);
        var viewModel = fixture.ViewModel;

        viewModel.SemanticSearchText = "長時間検索のキャンセル確認";
        viewModel.SemanticSearchCommand.Execute(null);
        await delayed.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(viewModel.IsSemanticSearchBusy);
        Assert.True(viewModel.CancelSemanticSearchCommand.CanExecute(null));
        Assert.Equal("AI検索語を処理しています。", viewModel.SemanticSearchStatus);

        viewModel.CancelSemanticSearchCommand.Execute(null);
        await WaitForAsync(() => !viewModel.IsSemanticSearchBusy);

        Assert.Equal("AI検索をキャンセルしました。", viewModel.SemanticSearchStatus);
        Assert.Empty(viewModel.SemanticResults);
        Assert.False(viewModel.CancelSemanticSearchCommand.CanExecute(null));
    }

    [Fact]
    public async Task CancelBeforeNonCooperativeSearchCompletesSuppressesStaleResultAndReturnsIdle()
    {
        var delayed = new DelayedSearchDouble(ignoreCancellation: true);
        using var fixture = new ViewModelFixture(delayed);
        var viewModel = fixture.ViewModel;

        viewModel.SemanticSearchText = "遅延検索のstale結果抑止";
        viewModel.SemanticSearchCommand.Execute(null);
        await delayed.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(viewModel.IsSemanticSearchBusy);

        viewModel.CancelSemanticSearchCommand.Execute(null);
        delayed.CompleteWith(CreateCandidate());
        await WaitForAsync(() => !viewModel.IsSemanticSearchBusy);

        Assert.Equal("AI検索をキャンセルしました。", viewModel.SemanticSearchStatus);
        Assert.Empty(viewModel.SemanticResults);
        Assert.False(viewModel.CancelSemanticSearchCommand.CanExecute(null));
    }

    [Fact]
    public async Task EmptySearchResultProvidesUserVisibleNoResultStatus()
    {
        using var fixture = new ViewModelFixture(new ReturningSearchDouble([]));
        var viewModel = fixture.ViewModel;

        viewModel.SemanticSearchText = "一致なしのテスト";
        viewModel.SemanticSearchCommand.Execute(null);
        await WaitForAsync(() => !viewModel.IsSemanticSearchBusy);

        Assert.Equal("一致する候補が見つかりませんでした。解析済みメディア、検索語、対象範囲、またはTag filterを確認してください。", viewModel.SemanticSearchStatus);
        Assert.Empty(viewModel.SemanticResults);
        Assert.Equal(string.Empty, viewModel.ErrorText);
    }

    [Fact]
    public async Task SemanticSearchExceptionShowsOnlySafeUserMessage()
    {
        const string userMessage = "テスト用の安全な検索エラーです。ローカルログを確認してください。";
        using var fixture = new ViewModelFixture(new ThrowingSearchDouble(new SemanticSearchException("TEST_INJECTED", userMessage)));
        var viewModel = fixture.ViewModel;

        viewModel.SemanticSearchText = "error injection";
        viewModel.SemanticSearchCommand.Execute(null);
        await WaitForAsync(() => !viewModel.IsSemanticSearchBusy);

        Assert.Equal(userMessage, viewModel.SemanticSearchStatus);
        Assert.Equal(userMessage, viewModel.ErrorText);
        Assert.DoesNotContain("System.", viewModel.ErrorText, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", viewModel.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingMediaPreviewShowsSafeFileUnavailableStateAndRetainsMatchedFrameSelection()
    {
        var candidate = CreateCandidate(path: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.mp4"));
        using var fixture = new ViewModelFixture(new ReturningSearchDouble([candidate]));
        var viewModel = fixture.ViewModel;

        viewModel.SemanticSearchText = "missing media";
        viewModel.SemanticSearchCommand.Execute(null);
        await WaitForAsync(() => !viewModel.IsSemanticSearchBusy);
        viewModel.OpenPreviewCommand.Execute(null);
        await WaitForAsync(() => !viewModel.IsPreviewBusy);

        Assert.Equal(candidate.VisualSampleId, viewModel.SelectedSemanticResult?.VisualSampleId);
        Assert.False(viewModel.IsPreviewOpen);
        Assert.False(viewModel.IsPreviewAvailable);
        Assert.Contains("File unavailable", viewModel.PreviewStatus, StringComparison.Ordinal);
        Assert.Contains("matched frame", viewModel.PreviewStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreviewAvailabilityCompletionForOldSelectionIsSuppressed()
    {
        var preview = new DelayedPreviewDouble();
        var first = CreateCandidate(fileName: "first.mp4");
        var second = CreateCandidate(fileName: "second.mp4");
        using var fixture = new ViewModelFixture(new ReturningSearchDouble([first, second]), preview);
        var viewModel = fixture.ViewModel;

        viewModel.SemanticSearchText = "preview request identity";
        viewModel.SemanticSearchCommand.Execute(null);
        await WaitForAsync(() => !viewModel.IsSemanticSearchBusy);
        viewModel.OpenPreviewCommand.Execute(null);
        await preview.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.SelectedSemanticResult = second;
        preview.Complete(MediaPreviewAvailability.Available);
        await Task.Delay(100);

        Assert.Equal(second.VisualSampleId, viewModel.SelectedSemanticResult?.VisualSampleId);
        Assert.False(viewModel.IsPreviewOpen);
        Assert.False(viewModel.IsPreviewAvailable);
        Assert.Contains("matched frame", viewModel.PreviewStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AvailablePreviewOpensAndIssuesMatchedTimestampSeekRequest()
    {
        var preview = new ReturningPreviewDouble(MediaPreviewAvailability.Available);
        var candidate = CreateCandidate();
        using var fixture = new ViewModelFixture(new ReturningSearchDouble([candidate]), preview);
        var viewModel = fixture.ViewModel;

        viewModel.SemanticSearchText = "preview success";
        viewModel.SemanticSearchCommand.Execute(null);
        await WaitForAsync(() => !viewModel.IsSemanticSearchBusy);
        viewModel.OpenPreviewCommand.Execute(null);
        await WaitForAsync(() => !viewModel.IsPreviewBusy);

        Assert.True(viewModel.IsPreviewOpen);
        Assert.True(viewModel.IsPreviewAvailable);
        Assert.Equal(candidate.Path, viewModel.PreviewMediaPath);
        Assert.True(viewModel.PreviewSeekRequestId > 0);
        Assert.Contains("best matched sample", viewModel.PreviewStatus, StringComparison.OrdinalIgnoreCase);
    }

    private static SemanticSearchCandidate CreateCandidate(string? fileName = null, string? path = null) => new(
        Guid.NewGuid(),
        fileName ?? "stale-result.mp4",
        path ?? @"C:\\isolated\\stale-result.mp4",
        10d,
        0.9d,
        Guid.NewGuid(),
        Guid.NewGuid());

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var timeoutAt = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeoutAt)
                throw new TimeoutException("Semantic Search state transition did not complete within the acceptance timeout.");
            await Task.Delay(10);
        }
    }

    private sealed class ViewModelFixture : IDisposable
    {
        private readonly string _root;
        private readonly JsonFileLogger _logger;

        public ViewModelFixture(ISemanticSearchService semanticSearch, IMediaPreviewService? preview = null)
        {
            _root = Path.Combine(Path.GetTempPath(), "LocalMediaAI-ViewModelAcceptance", Guid.NewGuid().ToString("N"));
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
            ViewModel = new MainViewModel(library, jobs, pipeline, worker, semanticSearch, _logger, preview);
        }

        public MainViewModel ViewModel { get; }

        public void Dispose()
        {
            ViewModel.Dispose();
            _logger.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            for (var attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    Directory.Delete(_root, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 7)
                {
                    Thread.Sleep(50 * (attempt + 1));
                    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                }
            }
            throw new IOException("SQLite acceptance fixture directory remained locked after bounded cleanup retry.");
        }
    }

    private abstract class SearchDouble : ISemanticSearchService
    {
        public SemanticModelProfile SemanticModel => SemanticModelRegistry.ResolveDevelopmentCandidate();
        public SemanticExecutionProfile SemanticExecution => new("cuda", "float32");
        public abstract Task<IReadOnlyList<SemanticSearchCandidate>> SearchAsync(string query, int topK, CancellationToken cancellationToken, Guid? libraryRootId = null);
    }

    private sealed class DelayedSearchDouble(bool ignoreCancellation) : SearchDouble
    {
        private readonly TaskCompletionSource<IReadOnlyList<SemanticSearchCandidate>> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<IReadOnlyList<SemanticSearchCandidate>> SearchAsync(string query, int topK, CancellationToken cancellationToken, Guid? libraryRootId = null)
        {
            Started.TrySetResult(true);
            if (ignoreCancellation)
                return await _completion.Task;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        }

        public void CompleteWith(SemanticSearchCandidate candidate) => _completion.TrySetResult([candidate]);
    }

    private sealed class ReturningPreviewDouble(MediaPreviewAvailability availability) : IMediaPreviewService
    {
        public Task<MediaPreviewAvailability> CheckAvailabilityAsync(string path, CancellationToken cancellationToken) => Task.FromResult(availability);
    }

    private sealed class DelayedPreviewDouble : IMediaPreviewService
    {
        private readonly TaskCompletionSource<MediaPreviewAvailability> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<MediaPreviewAvailability> CheckAvailabilityAsync(string path, CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            return _completion.Task;
        }

        public void Complete(MediaPreviewAvailability availability) => _completion.TrySetResult(availability);
    }

    private sealed class ReturningSearchDouble(IReadOnlyList<SemanticSearchCandidate> results) : SearchDouble
    {
        public override Task<IReadOnlyList<SemanticSearchCandidate>> SearchAsync(string query, int topK, CancellationToken cancellationToken, Guid? libraryRootId = null) => Task.FromResult(results);
    }

    private sealed class ThrowingSearchDouble(Exception exception) : SearchDouble
    {
        public override Task<IReadOnlyList<SemanticSearchCandidate>> SearchAsync(string query, int topK, CancellationToken cancellationToken, Guid? libraryRootId = null) => Task.FromException<IReadOnlyList<SemanticSearchCandidate>>(exception);
    }
}
