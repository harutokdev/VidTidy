using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class RetentionCleanupCoordinatorTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LocalMediaAICleanupCoordinatorTests", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "portable.flag"), string.Empty);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        for (var attempt = 0; Directory.Exists(_root) && attempt < 5; attempt++)
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { await Task.Delay(50); }
            catch (UnauthorizedAccessException) { await Task.Delay(50); }
        }
    }

    [Fact]
    public async Task RunningRerunAndIdleBoundaryRequestsAreCoalescedWithoutLossOrParallelRuns()
    {
        var logger = new CapturingLogger();
        var entered = Enumerable.Range(0, 4).Select(_ => NewSignal()).ToArray();
        var release = Enumerable.Range(0, 4).Select(_ => NewSignal()).ToArray();
        var idleBoundaryEntered = NewSignal();
        var releaseIdleBoundary = NewSignal();
        var runs = 0;
        var active = 0;
        var maximumActive = 0;
        using var coordinator = new RetentionCleanupCoordinator(async token =>
        {
            var index = Interlocked.Increment(ref runs) - 1;
            var current = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximumActive, current);
            entered[index].TrySetResult(true);
            try { await release[index].Task.WaitAsync(token); }
            finally { Interlocked.Decrement(ref active); }
        }, logger);
        coordinator.BeforeIdleDecisionForTests = async (run, token) =>
        {
            if (run != 3) return;
            idleBoundaryEntered.TrySetResult(true);
            await releaseIdleBoundary.Task.WaitAsync(token);
        };

        Assert.True(coordinator.Request("Startup"));
        await entered[0].Task;
        Assert.True(coordinator.Request("Scan-1"));
        Assert.True(coordinator.Request("Scan-2"));
        release[0].TrySetResult(true);

        await entered[1].Task;
        Assert.True(coordinator.Request("Scan-during-rerun"));
        release[1].TrySetResult(true);

        await entered[2].Task;
        release[2].TrySetResult(true);
        await idleBoundaryEntered.Task;
        Assert.True(coordinator.Request("Scan-at-idle-boundary"));
        releaseIdleBoundary.TrySetResult(true);

        await entered[3].Task;
        release[3].TrySetResult(true);
        await coordinator.ShutdownAsync();

        Assert.Equal(4, runs);
        Assert.Equal(1, maximumActive);
    }

    [Fact]
    public async Task FailureIsObservedAndAnAcceptedPendingRequestStillRuns()
    {
        var logger = new CapturingLogger();
        var firstEntered = NewSignal();
        var releaseFailure = NewSignal();
        var secondEntered = NewSignal();
        var runs = 0;
        using var coordinator = new RetentionCleanupCoordinator(async token =>
        {
            if (Interlocked.Increment(ref runs) == 1)
            {
                firstEntered.TrySetResult(true);
                await releaseFailure.Task.WaitAsync(token);
                throw new IOException("synthetic cleanup failure");
            }
            secondEntered.TrySetResult(true);
        }, logger);

        Assert.True(coordinator.Request("Startup"));
        await firstEntered.Task;
        Assert.True(coordinator.Request("ScanCompleted"));
        releaseFailure.TrySetResult(true);
        await secondEntered.Task;
        await coordinator.ShutdownAsync();

        Assert.Equal(2, runs);
        Assert.Contains(logger.Events, item => item.EventName == "RetentionCleanupFailed" && item.Exception is IOException);
    }

    [Fact]
    public async Task ShutdownRejectsRacingRequestsCancelsAndJoinsTheOwnedWorker()
    {
        var entered = NewSignal();
        var exited = NewSignal();
        using var coordinator = new RetentionCleanupCoordinator(async token =>
        {
            entered.TrySetResult(true);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { exited.TrySetResult(true); }
        }, new CapturingLogger());

        Assert.True(coordinator.Request("Startup"));
        await entered.Task;
        var shutdown = coordinator.ShutdownAsync();
        Assert.False(coordinator.Request("RacingScan"));
        await shutdown;

        Assert.True(exited.Task.IsCompletedSuccessfully);
        Assert.False(coordinator.Request("AfterShutdown"));
    }

    [Fact]
    public async Task ScanPublishesDurableStateThenRequestsCleanupWithoutWaitingForIt()
    {
        var paths = AppPaths.Create(_root);
        paths.EnsureDirectories();
        var logger = new CapturingLogger();
        var repository = new SqliteLibraryRepository(paths, logger);
        await repository.InitializeAsync(CancellationToken.None);
        var jobs = new SqliteJobStore(paths, logger);
        var cleanupEntered = NewSignal();
        var releaseCleanup = NewSignal();
        string? durableScanState = null;
        var cleanup = new RetentionCleanupCoordinator(async token =>
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath }.ToString());
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT state FROM library_scan ORDER BY started_at DESC LIMIT 1;";
            durableScanState = Convert.ToString(await command.ExecuteScalarAsync(token), System.Globalization.CultureInfo.InvariantCulture);
            cleanupEntered.TrySetResult(true);
            await releaseCleanup.Task.WaitAsync(token);
            throw new IOException("synthetic background cleanup failure");
        }, logger);
        using var pipeline = new MediaPipeline(repository, jobs, new FileSystemMediaScanner(),
            new FfmpegAdapter(new FfmpegPaths(null, null), paths, logger), logger, retentionCleanup: cleanup);
        var mediaRoot = Directory.CreateDirectory(Path.Combine(_root, "empty-media")).FullName;
        var root = await repository.AddOrGetLibraryRootAsync(mediaRoot, CancellationToken.None);
        var scanJobId = await pipeline.EnqueueScanAsync(root.LibraryRootId, CancellationToken.None);

        var processing = pipeline.ProcessNextAsync(CancellationToken.None);
        await cleanupEntered.Task;
        Assert.Equal("Completed", durableScanState);
        Assert.True(await processing);
        var scanJob = Assert.Single(await jobs.GetRecentAsync(20, CancellationToken.None), job => job.JobId == scanJobId);
        Assert.Equal(JobState.Completed, scanJob.State);
        Assert.False(releaseCleanup.Task.IsCompleted);

        releaseCleanup.TrySetResult(true);
        await pipeline.RequestShutdownAndDrainAsync();
        scanJob = Assert.Single(await jobs.GetRecentAsync(20, CancellationToken.None), job => job.JobId == scanJobId);
        Assert.Equal(JobState.Completed, scanJob.State);
        Assert.Contains(logger.Events, item => item.EventName == "RetentionCleanupFailed" && item.Exception is IOException);
    }

    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void UpdateMaximum(ref int target, int value)
    {
        int prior;
        do
        {
            prior = Volatile.Read(ref target);
            if (prior >= value) return;
        } while (Interlocked.CompareExchange(ref target, value, prior) != prior);
    }

    private sealed class CapturingLogger : IStructuredLogger
    {
        private readonly object _gate = new();
        private readonly List<(string EventName, Exception? Exception)> _events = [];
        public IReadOnlyList<(string EventName, Exception? Exception)> Events { get { lock (_gate) return _events.ToArray(); } }
        public void Write(string level, string eventName, object fields, Exception? exception = null)
        {
            lock (_gate) _events.Add((eventName, exception));
        }
    }
}
