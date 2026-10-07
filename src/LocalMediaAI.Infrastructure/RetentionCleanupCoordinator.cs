using LocalMediaAI.Core;

namespace LocalMediaAI.Infrastructure;

/// <summary>
/// One app-lifetime serialization domain for retention cleanup. Requests are dirty-bit
/// coalesced: each accepted request is covered by the active run or by a later rerun.
/// </summary>
public sealed class RetentionCleanupCoordinator : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<CancellationToken, Task> _cleanup;
    private readonly IStructuredLogger _log;
    private readonly CancellationTokenSource _shutdown = new();
    private bool _accepting = true;
    private bool _running;
    private bool _dirty;
    private Task _worker = Task.CompletedTask;
    private Task? _shutdownTask;
    private bool _disposed;
    private int _runNumber;

    internal Func<int, CancellationToken, Task>? BeforeIdleDecisionForTests { get; set; }

    public RetentionCleanupCoordinator(Func<CancellationToken, Task> cleanup, IStructuredLogger log)
    {
        _cleanup = cleanup ?? throw new ArgumentNullException(nameof(cleanup));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public bool Request(string trigger)
    {
        lock (_gate)
        {
            if (!_accepting) return false;
            _dirty = true;
            if (!_running)
            {
                _running = true;
                _worker = Task.Run(RunAsync);
            }
        }
        Log("Information", "RetentionCleanupRequested", new { Trigger = trigger });
        return true;
    }

    public Task ShutdownAsync()
    {
        Task worker;
        TaskCompletionSource<bool> completion;
        lock (_gate)
        {
            if (_shutdownTask is not null) return _shutdownTask;
            _accepting = false;
            _dirty = false; // durable retention state makes deferred work recoverable next startup
            worker = _worker;
            completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _shutdownTask = completion.Task;
        }
        // Cancellation callbacks are deliberately invoked outside _gate.
        _shutdown.Cancel();
        _ = ObserveShutdownAsync(worker, completion);
        return completion.Task;
    }

    private async Task RunAsync()
    {
        while (true)
        {
            int runNumber;
            lock (_gate)
            {
                if (!_accepting || !_dirty)
                {
                    _running = false;
                    return;
                }
                _dirty = false;
                runNumber = ++_runNumber;
            }

            try
            {
                await _cleanup(_shutdown.Token).ConfigureAwait(false);
                Log("Information", "RetentionCleanupCompleted", new { RunNumber = runNumber });
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                Log("Information", "RetentionCleanupCancelledForShutdown", new { RunNumber = runNumber });
            }
            catch (Exception ex)
            {
                // Failure does not create an immediate retry loop. A request accepted
                // during this run remains in _dirty and is handled by the next iteration.
                Log("Error", "RetentionCleanupFailed", new { RunNumber = runNumber }, ex);
            }

            try
            {
                if (BeforeIdleDecisionForTests is not null)
                    await BeforeIdleDecisionForTests(runNumber, _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Log("Error", "RetentionCleanupCoordinatorHookFailed", new { RunNumber = runNumber }, ex);
            }
            // The next loop's single lock linearizes dirty consumption and Idle.
            // A concurrent Request either leaves dirty=true for this worker or sees
            // running=false and creates its successor; there is no unowned gap.
        }
    }

    private static async Task ObserveShutdownAsync(Task worker, TaskCompletionSource<bool> completion)
    {
        try { await worker.ConfigureAwait(false); }
        catch { /* RunAsync observes production failures; retain shutdown safety if an invariant bug escapes. */ }
        finally { completion.TrySetResult(true); }
    }

    private void Log(string level, string eventName, object fields, Exception? exception = null)
    {
        try { _log.Write(level, eventName, fields, exception); }
        catch { /* Diagnostics must not fault the owned worker or its caller. */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        ShutdownAsync().GetAwaiter().GetResult();
        _shutdown.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
