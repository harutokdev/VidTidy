using LocalMediaAI.Core.Processing;

namespace LocalMediaAI.Infrastructure;

/// <summary>
/// Runs one worker per source drive plus one compute worker. Each source worker processes at most one job at a time
/// (so a drive never serves two readers) and honours its drive's pacing; the compute worker keeps the GPU and catalog
/// busy with whatever the source workers produce. A worker whose queue runs dry keeps watching for new work until
/// every source worker is idle at the same moment; a restart request ends the pass so the caller can rebuild lanes.
/// </summary>
internal sealed class LaneScheduler(
    LaneScheduler.ProcessNext processNext,
    Func<JobLaneFilter, CancellationToken, Task<bool>> hasWork,
    Func<bool> acquisitionEnabled,
    Func<ProcessingSettings> settings,
    Func<Task> wakeup,
    Func<bool> restartRequested,
    TimeSpan idlePoll,
    Action<string, Exception>? laneFailed = null)
{
    /// <summary>Acquires and runs one job of the lane; returns false when the lane had nothing to take.</summary>
    public delegate Task<bool> ProcessNext(JobLaneFilter lane, Action onStarted, Action onFinished, CancellationToken cancellationToken);

    public sealed record SourceLane(string Name, JobLaneFilter Filter, SourcePacer Pacer);

    /// <summary>Runs every lane until no lane can take more work. Returns true when anything was processed.</summary>
    public async Task<bool> RunAsync(IReadOnlyList<SourceLane> sourceLanes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceLanes);
        var pass = new Pass(sourceLanes.Count);
        if (sourceLanes.Count == 0) pass.EnterIdle(); // nothing reads originals: the pass is drained from the start
        var outcomes = await Task.WhenAll(sourceLanes.Select(lane => GuardAsync(lane.Name, pass, isSource: true, () => RunSourceAsync(lane, pass, cancellationToken)))
            .Append(GuardAsync("compute", pass, isSource: false, () => RunComputeAsync(pass, cancellationToken)))).ConfigureAwait(false);
        return outcomes.Any(processed => processed);
    }

    private bool ShouldStop(Pass pass) => !acquisitionEnabled() || restartRequested() || pass.Stopping;

    private async Task<bool> RunSourceAsync(SourceLane lane, Pass pass, CancellationToken cancellationToken)
    {
        var processedAny = false;
        while (true)
        {
            // Snapshot the wake signal before checking state, so a stop or settings change that lands while this
            // lane is between the check and the wait still wakes it.
            var wake = wakeup();
            if (ShouldStop(pass)) break;
            var decision = lane.Pacer.Decide(settings());
            if (decision.Wait is { } wait)
            {
                // A change check the user just asked for is not held back by the "only while idle" rule.
                if (decision.WaitingForUser && await processNext(lane.Filter with { ScansOnly = true }, lane.Pacer.JobStarted, lane.Pacer.JobFinished, cancellationToken).ConfigureAwait(false))
                {
                    processedAny = true;
                    continue;
                }
                if (await hasWork(lane.Filter, cancellationToken).ConfigureAwait(false))
                {
                    await WaitAnyAsync(wait, cancellationToken, wake).ConfigureAwait(false);
                    continue;
                }
                if (await IdleAsync(pass, wake, cancellationToken).ConfigureAwait(false)) break;
                continue;
            }
            if (await processNext(lane.Filter, lane.Pacer.JobStarted, lane.Pacer.JobFinished, cancellationToken).ConfigureAwait(false))
            {
                processedAny = true;
                pass.WorkArrived();
                continue;
            }
            if (await IdleAsync(pass, wake, cancellationToken).ConfigureAwait(false)) break;
        }
        return processedAny;
    }

    private async Task<bool> RunComputeAsync(Pass pass, CancellationToken cancellationToken)
    {
        var processedAny = false;
        while (true)
        {
            var wake = wakeup();
            if (ShouldStop(pass)) break;
            if (await processNext(JobLaneFilter.Compute, static () => { }, static () => { }, cancellationToken).ConfigureAwait(false))
            {
                processedAny = true;
                continue;
            }
            if (pass.SourcesDrained) break;
            // Source work in flight will produce compute work; wait for it instead of ending the pass.
            await WaitAnyAsync(idlePoll, cancellationToken, wake, pass.WorkArrivedSignal, pass.DrainedSignal).ConfigureAwait(false);
        }
        return processedAny;
    }

    /// <summary>Marks the lane idle; returns true when every source lane is idle (the pass is drained), else waits and lets the lane retry.</summary>
    private async Task<bool> IdleAsync(Pass pass, Task wake, CancellationToken cancellationToken)
    {
        if (pass.EnterIdle()) return true;
        try { await WaitAnyAsync(idlePoll, cancellationToken, wake, pass.DrainedSignal).ConfigureAwait(false); }
        finally { pass.LeaveIdle(); }
        return pass.SourcesDrained;
    }

    private async Task<bool> GuardAsync(string lane, Pass pass, bool isSource, Func<Task<bool>> body)
    {
        try { return await body().ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            laneFailed?.Invoke(lane, ex);
            pass.Stop();
            throw;
        }
        finally
        {
            if (isSource) pass.SourceLaneExited();
        }
    }

    private static async Task WaitAnyAsync(TimeSpan delay, CancellationToken cancellationToken, params Task[] signals)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sleep = Task.Delay(delay, timer.Token);
        await Task.WhenAny(signals.Append(sleep)).ConfigureAwait(false);
        await timer.CancelAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Shared state of one pass: how many source lanes are running or idle, and the signals between lanes.</summary>
    private sealed class Pass(int sourceLanes)
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _workArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _running = sourceLanes;
        private int _idle;
        private volatile bool _stopping;

        public bool SourcesDrained => _drained.Task.IsCompleted;
        public bool Stopping => _stopping;
        public Task DrainedSignal => _drained.Task;
        public Task WorkArrivedSignal { get { lock (_gate) return _workArrived.Task; } }

        public void WorkArrived()
        {
            TaskCompletionSource fired;
            lock (_gate)
            {
                fired = _workArrived;
                _workArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            fired.TrySetResult();
        }

        public bool EnterIdle()
        {
            lock (_gate)
            {
                _idle++;
                if (_idle >= _running) _drained.TrySetResult();
                return SourcesDrained;
            }
        }

        public void LeaveIdle() { lock (_gate) _idle--; }

        public void SourceLaneExited()
        {
            lock (_gate)
            {
                _running--;
                if (_idle >= _running) _drained.TrySetResult();
            }
        }

        public void Stop()
        {
            _stopping = true;
            _drained.TrySetResult();
        }
    }
}
