using LocalMediaAI.Core;

namespace LocalMediaAI.Infrastructure;

/// <summary>
/// Suppresses only consecutive, observability-only phase heartbeats for one
/// authoritatively acquired Job attempt. There is no timer, buffer, or deferred work.
/// </summary>
internal sealed class AttemptScopedStagePersistence : IAsyncDisposable
{
    private readonly Guid _jobId;
    private readonly int _attempt;
    private readonly Func<Guid, int, string, CancellationToken, Task> _persist;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _currentPersistedPhase;
    private bool _retired;

    internal AttemptScopedStagePersistence(
        Job acquiredJob,
        Func<Guid, int, string, CancellationToken, Task> persist)
    {
        ArgumentNullException.ThrowIfNull(acquiredJob);
        ArgumentNullException.ThrowIfNull(persist);
        if (acquiredJob.State != JobState.Running || acquiredJob.Attempt <= 0 || acquiredJob.StartedAt is null)
            throw new InvalidOperationException("Stage persistence ownership requires an authoritatively acquired Running Job attempt.");
        _jobId = acquiredJob.JobId;
        _attempt = acquiredJob.Attempt;
        _persist = persist;
    }

    internal Guid JobId => _jobId;
    internal int Attempt => _attempt;
    internal bool IsRetired => _retired;

    internal async Task<bool> PersistPhaseEntryAsync(string phase, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_retired) throw new InvalidOperationException("The Job attempt stage-persistence scope is retired.");
            if (string.Equals(_currentPersistedPhase, phase, StringComparison.Ordinal)) return false;
            await _persist(_jobId, _attempt, phase, cancellationToken).ConfigureAwait(false);
            _currentPersistedPhase = phase;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _retired = true;
            _currentPersistedPhase = null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
