using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class AttemptScopedStagePersistenceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LocalMediaAIAttemptPersistenceTests", Guid.NewGuid().ToString("N"));
    private AppPaths _paths = null!;
    private SqliteLibraryRepository _repository = null!;
    private SqliteJobStore _jobs = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "portable.flag"), string.Empty);
        _paths = AppPaths.Create(_root);
        _paths.EnsureDirectories();
        var logger = new NullLogger();
        _repository = new SqliteLibraryRepository(_paths, logger);
        _jobs = new SqliteJobStore(_paths, logger);
        await _repository.InitializeAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return Task.CompletedTask;
    }

    [Fact]
    public void OwnershipCannotBeCreatedBeforeAuthoritativeAcquisition()
    {
        var pending = CreateJob(JobState.Pending, attempt: 0, startedAt: null);
        Assert.Throws<InvalidOperationException>(() => new AttemptScopedStagePersistence(pending, (_, _, _, _) => Task.CompletedTask));
    }

    [Fact]
    public async Task FirstPhasePersistsConsecutiveDuplicateSkipsAndReentryPersists()
    {
        var phases = new List<string>();
        await using var scope = new AttemptScopedStagePersistence(CreateJob(), (_, _, phase, _) =>
        {
            phases.Add(phase);
            return Task.CompletedTask;
        });

        Assert.True(await scope.PersistPhaseEntryAsync("A", CancellationToken.None));
        Assert.False(await scope.PersistPhaseEntryAsync("A", CancellationToken.None));
        Assert.True(await scope.PersistPhaseEntryAsync("B", CancellationToken.None));
        Assert.True(await scope.PersistPhaseEntryAsync("A", CancellationToken.None));

        Assert.Equal(["A", "B", "A"], phases);
    }

    [Fact]
    public async Task ConcurrentSamePhaseDecisionIsLinearizableForOneAttempt()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        await using var scope = new AttemptScopedStagePersistence(CreateJob(), async (_, _, _, _) =>
        {
            Interlocked.Increment(ref writes);
            entered.TrySetResult(true);
            await release.Task;
        });

        var first = scope.PersistPhaseEntryAsync("Visual", CancellationToken.None);
        await entered.Task;
        var second = scope.PersistPhaseEntryAsync("Visual", CancellationToken.None);
        Assert.False(second.IsCompleted);
        release.TrySetResult(true);

        Assert.True(await first);
        Assert.False(await second);
        Assert.Equal(1, writes);
    }

    [Fact]
    public async Task UnrelatedJobScopesDoNotShareAGlobalGate()
    {
        var firstEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var first = new AttemptScopedStagePersistence(CreateJob(), async (_, _, _, _) =>
        {
            firstEntered.TrySetResult(true);
            await releaseFirst.Task;
        });
        await using var second = new AttemptScopedStagePersistence(CreateJob(), (_, _, _, _) => Task.CompletedTask);

        var blockedFirst = first.PersistPhaseEntryAsync("Visual", CancellationToken.None);
        await firstEntered.Task;
        var independentSecond = second.PersistPhaseEntryAsync("Visual", CancellationToken.None);

        Assert.True(await independentSecond.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.False(blockedFirst.IsCompleted);
        releaseFirst.TrySetResult(true);
        Assert.True(await blockedFirst);
    }

    [Fact]
    public async Task FailedPersistenceDoesNotAdvanceSuppressionState()
    {
        var writes = 0;
        await using var scope = new AttemptScopedStagePersistence(CreateJob(), (_, _, _, _) =>
        {
            if (Interlocked.Increment(ref writes) == 1) throw new IOException("synthetic persistence failure");
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<IOException>(() => scope.PersistPhaseEntryAsync("Visual", CancellationToken.None));
        Assert.True(await scope.PersistPhaseEntryAsync("Visual", CancellationToken.None));
        Assert.Equal(2, writes);
    }

    [Fact]
    public async Task MeaningfulProgressInSamePhaseBypassesHeartbeatSuppression()
    {
        var acquired = await EnqueueAndAcquireAsync();
        await using (var scope = new AttemptScopedStagePersistence(acquired, _jobs.HeartbeatAttemptAsync))
        {
            Assert.True(await scope.PersistPhaseEntryAsync("ProtectedVisualSampling", CancellationToken.None));
            await _jobs.UpdateProgressAsync(acquired.JobId, "ProtectedVisualSampling", new JobProgress(1, 7, "frames", false), CancellationToken.None);
            Assert.False(await scope.PersistPhaseEntryAsync("ProtectedVisualSampling", CancellationToken.None));
        }
        var current = await _jobs.GetJobAsync(acquired.JobId, CancellationToken.None);
        Assert.Equal(new JobProgress(1, 7, "frames", false), current!.Progress);
        await _jobs.CompleteAsync(acquired.JobId, JobState.Completed, "SyntheticTerminal", CancellationToken.None);
    }

    [Fact]
    public async Task AttemptPredicateRejectsStaleOwnershipAfterRecoveryAndNewAttemptStartsFresh()
    {
        var attemptOne = await EnqueueAndAcquireAsync();
        var scopeOne = new AttemptScopedStagePersistence(attemptOne, _jobs.HeartbeatAttemptAsync);
        Assert.True(await scopeOne.PersistPhaseEntryAsync("ProtectedVisualSampling", CancellationToken.None));
        await scopeOne.DisposeAsync();
        Assert.True(scopeOne.IsRetired);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scopeOne.PersistPhaseEntryAsync("ProtectedNearDuplicate", CancellationToken.None));

        await _jobs.RecoverInterruptedJobsAsync(CancellationToken.None);
        var recovered = await _jobs.GetJobAsync(attemptOne.JobId, CancellationToken.None);
        Assert.Equal(JobState.RetryWaiting, recovered!.State);
        Assert.Equal(JobReason.AppRestartRecovery, recovered.Reason);

        var attemptTwo = await _jobs.TryAcquireNextAsync(ProtectedPipelineDispatchMode.ProtectedActivated, CancellationToken.None);
        Assert.NotNull(attemptTwo);
        Assert.Equal(attemptOne.JobId, attemptTwo.JobId);
        Assert.Equal(attemptOne.Attempt + 1, attemptTwo.Attempt);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _jobs.HeartbeatAttemptAsync(
            attemptOne.JobId, attemptOne.Attempt, "StaleAttemptMustNotWrite", CancellationToken.None));
        await using (var scopeTwo = new AttemptScopedStagePersistence(attemptTwo, _jobs.HeartbeatAttemptAsync))
            Assert.True(await scopeTwo.PersistPhaseEntryAsync("ProtectedVisualSampling", CancellationToken.None));
        await _jobs.CompleteAsync(attemptTwo.JobId, JobState.Completed, "SyntheticTerminal", CancellationToken.None);
    }

    [Fact]
    public async Task RetiredScopeCannotWriteAfterTerminalAndDoesNotAccumulateState()
    {
        var acquired = await EnqueueAndAcquireAsync();
        var writes = 0;
        var scope = new AttemptScopedStagePersistence(acquired, (_, _, _, _) =>
        {
            writes++;
            return Task.CompletedTask;
        });
        Assert.True(await scope.PersistPhaseEntryAsync("Metadata", CancellationToken.None));
        await scope.DisposeAsync();
        await _jobs.CompleteAsync(acquired.JobId, JobState.Completed, "SyntheticTerminal", CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.PersistPhaseEntryAsync("Thumbnail", CancellationToken.None));
        Assert.True(scope.IsRetired);
        Assert.Equal(1, writes);
    }

    private async Task<Job> EnqueueAndAcquireAsync()
    {
        var mediaFileId = Guid.NewGuid();
        var mediaContentId = Guid.NewGuid();
        await _jobs.EnqueueAsync(JobType.ProtectedSourceOrchestration,
            new ProtectedSourceOrchestrationPayload(mediaFileId, mediaContentId, ProtectedSourceStageEligibility.Metadata),
            3, null, CancellationToken.None);
        var acquired = await _jobs.TryAcquireNextAsync(ProtectedPipelineDispatchMode.ProtectedActivated, CancellationToken.None);
        Assert.NotNull(acquired);
        Assert.Equal(JobState.Running, acquired.State);
        Assert.Equal(1, acquired.Attempt);
        return acquired;
    }

    private static Job CreateJob(JobState state = JobState.Running, int attempt = 1, DateTimeOffset? startedAt = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new Job(Guid.NewGuid(), JobType.ProtectedSourceOrchestration, state, "Starting", JobReason.None, "{}", attempt, 3,
            null, startedAt ?? (state == JobState.Running ? now : null), null, state == JobState.Running ? now : null,
            new JobProgress(0, 0, "stages", true), null, null, now, now);
    }

    private sealed class NullLogger : IStructuredLogger
    {
        public void Write(string level, string eventName, object fields, Exception? exception = null) { }
    }
}
