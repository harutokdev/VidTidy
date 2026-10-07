namespace LocalMediaAI.App;

/// <summary>
/// Application-lifetime owner for one terminal graceful-shutdown operation.
/// Publishing <see cref="ShutdownTask"/> under the gate is the shutdown linearization point.
/// </summary>
public sealed class ApplicationShutdownCoordinator(
    Func<Task> teardown,
    Action authorizeFinalClose,
    Action<Exception> reportFailure,
    Action? shutdownLinearized = null)
{
    private readonly object _gate = new();
    private TaskCompletionSource<bool>? _completion;

    public Task? ShutdownTask { get { lock (_gate) return _completion?.Task; } }
    public bool IsShutdownStarted { get { lock (_gate) return _completion is not null; } }
    public bool IsFinalCloseAuthorized { get { lock (_gate) return _finalCloseAuthorized; } }
    private bool _finalCloseAuthorized;

    public Task RequestShutdownAsync()
    {
        TaskCompletionSource<bool> completion;
        lock (_gate)
        {
            if (_completion is not null) return _completion.Task;
            completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _completion = completion;
        }

        try { shutdownLinearized?.Invoke(); }
        catch { /* Lifecycle diagnostics cannot invalidate established shutdown ownership. */ }
        _ = ExecuteAsync(completion);
        return completion.Task;
    }

    private async Task ExecuteAsync(TaskCompletionSource<bool> completion)
    {
        try
        {
            await teardown();
            lock (_gate) _finalCloseAuthorized = true;
            authorizeFinalClose();
            completion.TrySetResult(true);
        }
        catch (Exception ex)
        {
            reportFailure(ex);
            completion.TrySetException(ex);
        }
    }
}
