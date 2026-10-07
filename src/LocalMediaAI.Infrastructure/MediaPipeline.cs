using System.Text.Json;
using LocalMediaAI.Core;
using LocalMediaAI.Core.Processing;
using LocalMediaAI.WorkerProtocol;

namespace LocalMediaAI.Infrastructure;

public enum ProcessingRunState { EnabledIdle, Running, StopRequested, Draining, UserStopped, ShuttingDown }

public sealed class ProcessingRunStateChangedEventArgs(ProcessingRunState state) : EventArgs
{
    public ProcessingRunState State { get; } = state;
}

public sealed class ThumbnailReadyEventArgs(Guid mediaFileId, Guid mediaContentId, string relativePath) : EventArgs
{
    public Guid MediaFileId { get; } = mediaFileId;
    public Guid MediaContentId { get; } = mediaContentId;
    public string RelativePath { get; } = relativePath;
}

public sealed partial class MediaPipeline : IDisposable
{
    private readonly ILibraryRepository _library;
    private readonly IJobStore _jobs;
    private readonly SqliteLibraryRepository _sqliteLibrary;
    private readonly SqliteJobStore _sqliteJobs;
    private readonly FileSystemMediaScanner _scanner;
    private readonly FfmpegAdapter _ffmpeg;
    private readonly WorkerProcessManager? _worker;
    private readonly AppPaths _paths;
    private readonly Func<ProcessingSourceSession, CancellationToken, Task>? _protectedPublicationProbe;
    private readonly Func<CancellationToken, Task>? _beforeJobStoreAcquisition;
    private readonly IResourceAdmissionPolicy _resourceAdmission;
    private readonly IResourceSnapshotProvider _resourceSnapshots;
    private readonly SemanticModelProfile _semanticModel;
    private readonly SemanticExecutionProfile _semanticExecution;
    private readonly VisualSamplingPolicy _visualSamplingPolicy;
    private readonly IStructuredLogger _log;
    private readonly RetentionCleanupCoordinator _retentionCleanup;
    private readonly SemaphoreSlim _runnerAdmission = new(1, 1);
    private readonly SemaphoreSlim _acquisitionAdmission = new(1, 1);
    private readonly SemaphoreSlim _scanAdmission;
    private readonly SemaphoreSlim _metadataAdmission;
    private readonly SemaphoreSlim _thumbnailAdmission;
    private readonly SemaphoreSlim _visualSamplingAdmission;
    private readonly SemaphoreSlim _visualEmbeddingAdmission = new(1, 1);
    // Full SHA-256 is deliberately independent and hard-bounded to one sequential HDD reader.
    private readonly SemaphoreSlim _fullHashAdmission = new(1, 1);
    // Near Duplicate extracts five timestamp-ordered frames per media and must not fan out random seeks on HDD-backed libraries.
    private readonly SemaphoreSlim _nearDuplicateFingerprintAdmission = new(1, 1);
    // Content Similarity derives vectors exclusively from persisted embedding/cache rows; it is CPU/DB bounded and serial.
    private readonly SemaphoreSlim _contentSimilarityRepresentationAdmission = new(1, 1);
    // Tag suggestions are versioned metadata derived from persisted embeddings and fixed tag text vectors; serial admission protects DB/model warm-up.
    private readonly SemaphoreSlim _tagSuggestionAdmission = new(1, 1);
    private readonly SemaphoreSlim _fallbackAdmission = new(1, 1);
    private readonly object _runStateGate = new();
    private readonly object _coordinatorGate = new();
    private readonly LaneScheduler _scheduler;
    private readonly Func<TimeSpan?> _userIdle;
    private readonly Dictionary<int, SourcePacer> _pacers = [];
    private readonly object _pacerGate = new();
    private readonly object _wakeGate = new();
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ProcessingSettings _processing = ProcessingSettings.Defaults;
    private volatile bool _lanesStale;
    private readonly LibraryWideRefreshGate _refresh = new(static () => DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10));
    private ProcessingRunState _runState = ProcessingRunState.EnabledIdle;
    private bool _stopGateLinearized;
    private int _activeExecutions;
    private TaskCompletionSource<bool>? _drainCompletion;
    private TaskCompletionSource<bool>? _stopCompletion;
    private bool _coordinatorActive;
    private bool _runRequested;
    private TaskCompletionSource<bool>? _coordinatorCompletion;
    private TaskCompletionSource<bool>? _shutdownCompletion;
    private bool _terminalShutdownRequested;
    private bool _disposed;

    public MediaPipeline(
        ILibraryRepository library,
        IJobStore jobs,
        FileSystemMediaScanner scanner,
        FfmpegAdapter ffmpeg,
        IStructuredLogger log,
        PipelineConcurrencyOptions? concurrency = null,
        WorkerProcessManager? worker = null,
        AppPaths? paths = null,
        IResourceAdmissionPolicy? resourceAdmission = null,
        IResourceSnapshotProvider? resourceSnapshots = null,
        SemanticModelProfile? semanticModel = null,
        SemanticExecutionProfile? semanticExecution = null,
        VisualSamplingPolicy? visualSamplingPolicy = null,
        Func<ProcessingSourceSession, CancellationToken, Task>? protectedPublicationProbe = null,
        Func<CancellationToken, Task>? beforeJobStoreAcquisition = null,
        RetentionCleanupCoordinator? retentionCleanup = null,
        Func<TimeSpan?>? userIdle = null)
    {
        _library = library;
        _jobs = jobs;
        _sqliteLibrary = library as SqliteLibraryRepository ?? throw new ArgumentException("Protected production processing requires the SQLite library repository.", nameof(library));
        _sqliteJobs = jobs as SqliteJobStore ?? throw new ArgumentException("Protected production processing requires the SQLite Job store.", nameof(jobs));
        _scanner = scanner;
        _ffmpeg = ffmpeg;
        _worker = worker;
        _paths = paths ?? ffmpeg.ApplicationPaths;
        _protectedPublicationProbe = protectedPublicationProbe;
        _beforeJobStoreAcquisition = beforeJobStoreAcquisition;
        _resourceAdmission = resourceAdmission ?? new ConservativeResourceAdmissionPolicy();
        _resourceSnapshots = resourceSnapshots ?? new CachingResourceSnapshotProvider(
            worker is null ? new WindowsResourceSnapshotProvider() : new WorkerBackedResourceSnapshotProvider(worker), TimeSpan.FromSeconds(5));
        _semanticModel = semanticModel ?? SemanticModelRegistry.ResolveDevelopmentCandidate();
        _semanticExecution = semanticExecution ?? SemanticExecutionProfile.Resolve(_semanticModel);
        _visualSamplingPolicy = visualSamplingPolicy ?? VisualSamplingPolicy.FixedIntervalV1;
        _log = log;
        _retentionCleanup = retentionCleanup ?? new RetentionCleanupCoordinator(
            token => _library.RunRetentionCleanupAsync(DateTimeOffset.UtcNow, token), _log);
        Concurrency = concurrency ?? new PipelineConcurrencyOptions();
        _scanAdmission = new SemaphoreSlim(Concurrency.ScanConcurrency, Concurrency.ScanConcurrency);
        _metadataAdmission = new SemaphoreSlim(Concurrency.MetadataConcurrency, Concurrency.MetadataConcurrency);
        _thumbnailAdmission = new SemaphoreSlim(Concurrency.ThumbnailConcurrency, Concurrency.ThumbnailConcurrency);
        _visualSamplingAdmission = new SemaphoreSlim(Concurrency.VisualSamplingConcurrency, Concurrency.VisualSamplingConcurrency);
        // One source lane per physical drive (a drive never serves two readers) plus one compute lane.
        // Each stage semaphore remains a hard bound on top of the lanes.
        _userIdle = userIdle ?? (static () => null);
        _scheduler = new LaneScheduler(ProcessNextAsync, _sqliteJobs.HasAcquirableAsync, IsAcquisitionEnabled, () => Processing,
            () => { lock (_wakeGate) return _wake.Task; }, () => _lanesStale, TimeSpan.FromSeconds(2),
            (lane, ex) => _log.Write("Error", "ProcessingLaneFailed", new { Lane = lane }, ex));
        _log.Write("Information", "PipelineAdmissionConfigured", new
        {
            Concurrency.ScanConcurrency,
            Concurrency.MetadataConcurrency,
            Concurrency.ThumbnailConcurrency,
            Concurrency.VisualSamplingConcurrency,
            Lanes = "one per source drive + compute"
        });
    }

    public PipelineConcurrencyOptions Concurrency { get; }
    public string ThumbnailCacheRoot => _paths.CacheRoot;
    public static ProtectedPipelineDispatchMode DispatchMode => ProtectedPipelineDispatchMode.ProtectedActivated;
    public event EventHandler<ProcessingRunStateChangedEventArgs>? RunStateChanged;
    public event EventHandler<ThumbnailReadyEventArgs>? ThumbnailReady;
    public ProcessingRunState RunState { get { lock (_runStateGate) return _runState; } }

    public Task RequestRunAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool>? completion = null;
        lock (_coordinatorGate)
        {
            if (!IsAcquisitionEnabled()) return Task.CompletedTask;
            _runRequested = true;
            if (_coordinatorActive)
            {
                // New work (a change check, a new folder or drive) ends the running pass so lanes are rebuilt now,
                // instead of after the busiest drive drains.
                _lanesStale = true;
                Wake();
                return _coordinatorCompletion!.Task;
            }
            _coordinatorActive = true;
            completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _coordinatorCompletion = completion;
        }

        SetRunState(ProcessingRunState.Running);
        _ = RunCoordinatorAsync(completion, cancellationToken);
        return completion.Task;
    }

    public Task RequestStopAndDrainAsync(CancellationToken cancellationToken)
    {
        Task stopTask;
        TaskCompletionSource<bool>? start = null;
        lock (_runStateGate)
        {
            if (_terminalShutdownRequested)
                return (_shutdownCompletion?.Task ?? _stopCompletion?.Task ?? Task.CompletedTask).WaitAsync(cancellationToken);
            if (_runState == ProcessingRunState.UserStopped) return Task.CompletedTask;
            if (_stopCompletion is null || _stopCompletion.Task.IsCompleted)
            {
                _stopGateLinearized = false;
                _drainCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _stopCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _runState = ProcessingRunState.StopRequested;
                start = _stopCompletion;
            }
            stopTask = _stopCompletion.Task;
        }

        if (start is not null)
        {
            PublishRunState(ProcessingRunState.StopRequested);
            _ = CloseAcquisitionGateAndDrainAsync(start);
        }
        return stopTask.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Permanently closes production acquisition for this runtime and drains the same
    /// coordinator/execution set used by C-2. Unlike user Stop, terminal shutdown never
    /// reaches a reopenable UserStopped state and ignores caller cancellation.
    /// </summary>
    public Task RequestShutdownAndDrainAsync()
    {
        Task drainTask;
        TaskCompletionSource<bool>? start = null;
        TaskCompletionSource<bool> shutdown;
        lock (_runStateGate)
        {
            if (_shutdownCompletion is not null) return _shutdownCompletion.Task;

            _terminalShutdownRequested = true;
            shutdown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _shutdownCompletion = shutdown;

            if (_runState == ProcessingRunState.UserStopped && _activeExecutions == 0)
            {
                _runState = ProcessingRunState.ShuttingDown;
                drainTask = Task.CompletedTask;
            }
            else
            {
                if (_stopCompletion is null || _stopCompletion.Task.IsCompleted)
                {
                    _stopGateLinearized = false;
                    _drainCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _stopCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    start = _stopCompletion;
                }
                _runState = ProcessingRunState.ShuttingDown;
                drainTask = _stopCompletion.Task;
            }
        }

        PublishRunState(ProcessingRunState.ShuttingDown);
        if (start is not null) _ = CloseAcquisitionGateAndDrainAsync(start);
        var cleanupDrain = _retentionCleanup.ShutdownAsync();
        _ = CompleteTerminalShutdownAsync(drainTask, cleanupDrain, shutdown);
        return shutdown.Task;
    }

    public async Task<bool> StartProcessingAsync(CancellationToken cancellationToken)
    {
        if (RunState != ProcessingRunState.UserStopped) return false;

        var started = false;
        await _acquisitionAdmission.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_runStateGate)
            {
                if (_runState != ProcessingRunState.UserStopped) return false;
                _stopGateLinearized = false;
                _drainCompletion = null;
                _stopCompletion = null;
                _runState = ProcessingRunState.EnabledIdle;
                started = true;
            }
        }
        finally
        {
            _acquisitionAdmission.Release();
        }

        if (!started) return false;
        PublishRunState(ProcessingRunState.EnabledIdle);
        await RequestRunAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<Guid> EnqueueScanAsync(Guid rootId, CancellationToken cancellationToken)
    {
        var root = await _library.GetLibraryRootAsync(rootId, cancellationToken) ?? throw new InvalidOperationException("Library root does not exist.");
        if (root.RegistrationState != RootRegistrationState.Active) throw new InvalidOperationException("Library root is not registered.");
        return (await _jobs.EnqueueAsync(JobType.Scan, new ScanPayload(rootId, root.RegistrationRevision), 2, null, cancellationToken)).JobId;
    }
    public void RequestRetentionCleanup(string trigger)
    {
        try
        {
            if (!_retentionCleanup.Request(trigger))
                _log.Write("Information", "RetentionCleanupRequestIgnored", new { Trigger = trigger, Reason = "ShuttingDown" });
        }
        catch (Exception ex)
        {
            // Scheduling is diagnostics-only for the caller: a completed scan or
            // initialized application is never rolled back by maintenance admission.
            _log.Write("Error", "RetentionCleanupRequestFailed", new { Trigger = trigger }, ex);
        }
    }
    public async Task<Guid> EnqueueVisualSamplingAsync(Guid mediaFileId, CancellationToken cancellationToken) => (await _sqliteJobs.EnqueueForActiveMediaAsync(JobType.VisualSampling, new VisualSamplingPayload(mediaFileId), mediaFileId, 2, null, unique: false, cancellationToken) ?? throw new InvalidOperationException("Media is not managed by an active root.")).JobId;
    public async Task<Guid> EnqueueFullHashAsync(MediaFile media, CancellationToken cancellationToken) => (await _sqliteJobs.EnqueueForActiveMediaAsync(JobType.FullHash, new FullHashPayload(media.MediaFileId, media.MediaContentId, media.SizeBytes, media.LastWriteTimeUtc), media.MediaFileId, 3, null, unique: true, cancellationToken) ?? throw new InvalidOperationException("Media is not managed by an active root.")).JobId;
    public async Task<Guid> EnqueueNearDuplicateFingerprintAsync(MediaFile media, CancellationToken cancellationToken) => (await _sqliteJobs.EnqueueForActiveMediaAsync(JobType.NearDuplicateFingerprint, new NearDuplicateFingerprintPayload(media.MediaFileId, media.MediaContentId, media.SizeBytes, media.LastWriteTimeUtc), media.MediaFileId, 2, null, unique: true, cancellationToken) ?? throw new InvalidOperationException("Media is not managed by an active root.")).JobId;
    public async Task<Guid> EnqueueContentSimilarityRepresentationAsync(MediaFile media, CancellationToken cancellationToken) => (await _sqliteJobs.EnqueueForActiveMediaAsync(JobType.ContentSimilarityRepresentation, new ContentSimilarityRepresentationPayload(media.MediaFileId, media.MediaContentId, media.SizeBytes, media.LastWriteTimeUtc), media.MediaFileId, 2, null, unique: true, cancellationToken) ?? throw new InvalidOperationException("Media is not managed by an active root.")).JobId;
    public async Task<Guid> EnqueueTagSuggestionAsync(MediaFile media, CancellationToken cancellationToken) => (await _sqliteJobs.EnqueueForActiveMediaAsync(JobType.TagSuggestion, new TagSuggestionPayload(media.MediaFileId, media.MediaContentId, media.SizeBytes, media.LastWriteTimeUtc), media.MediaFileId, 2, null, unique: true, cancellationToken) ?? throw new InvalidOperationException("Media is not managed by an active root.")).JobId;

    /// <summary>Pacing for the drives holding originals. Changing it takes effect at once, ending a rest if needed.</summary>
    public ProcessingSettings Processing
    {
        get { lock (_wakeGate) return _processing; }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_wakeGate) _processing = value;
            Wake();
            _log.Write("Information", "ProcessingSettingsChanged", new { Pace = value.Pace.ToString(), value.OnlyWhenIdle });
        }
    }

    /// <summary>When a source drive is resting, the earliest time a resting drive resumes; otherwise null.</summary>
    public DateTimeOffset? SourceRestingUntil
    {
        get { lock (_pacerGate) return _pacers.Values.Select(pacer => pacer.RestingUntil).Where(until => until is not null).DefaultIfEmpty(null).Min(); }
    }

    /// <summary>True while reading originals is held back only because the user is using the PC (the "only while idle" setting).</summary>
    public bool SourceWaitingForUser
    {
        get { lock (_pacerGate) return _pacers.Values.Any(pacer => pacer.IsWaitingForUser); }
    }

    /// <summary>Acquires and runs one job from any lane, oldest first (the pre-lane order; kept for callers and tests).</summary>
    public Task<bool> ProcessNextAsync(CancellationToken cancellationToken) => ProcessNextAsync(null, static () => { }, static () => { }, cancellationToken);

    private async Task<bool> ProcessNextAsync(JobLaneFilter? lane, Action onStarted, Action onFinished, CancellationToken cancellationToken)
    {
        Job? job;
        var trackedExecution = false;
        await _acquisitionAdmission.WaitAsync(cancellationToken);
        try
        {
            if (!IsAcquisitionEnabled()) return false;
            if (_beforeJobStoreAcquisition is not null)
                await _beforeJobStoreAcquisition(cancellationToken).ConfigureAwait(false);
            // SQLite job acquisition transitions Pending/RetryWaiting to Running transactionally.
            // Serializing only this critical section preserves the C# single-writer rule while
            // allowing bounded media execution to proceed independently.
            job = await _sqliteJobs.TryAcquireNextAsync(DispatchMode, lane, cancellationToken);
            if (job is not null)
            {
                TrackAcquiredExecution();
                trackedExecution = true;
            }
        }
        finally
        {
            _acquisitionAdmission.Release();
        }
        if (job is null) return false;

        // A source lane already guarantees one job at a time per drive; a shared semaphore would serialize the drives.
        var admission = lane is { Kind: JobLaneKind.Source } ? null : GetAdmission(job.JobType);
        var admitted = false;
        try
        {
            if (admission is not null) await admission.WaitAsync(cancellationToken);
            admitted = true;
            onStarted();
            if (DispatchMode == ProtectedPipelineDispatchMode.ProtectedActivated)
                ProtectedPipelineDispatchPolicy.EnsureProtectedDispatch(job);
            switch (job.JobType)
            {
                case JobType.Scan: await ProcessScanAsync(job, cancellationToken); break;
                case JobType.ProtectedSourceOrchestration: await ProcessProtectedSourceOrchestrationAsync(job, cancellationToken); break;
                case JobType.ContentSimilarityRepresentation: await ProcessContentSimilarityRepresentationAsync(job, cancellationToken); break;
                case JobType.TagSuggestion: await ProcessTagSuggestionAsync(job, cancellationToken); break;
                case JobType.VisualSampling: await ProcessVisualSamplingAsync(job, cancellationToken); break;
                case JobType.VisualEmbedding: await ProcessVisualEmbeddingAsync(job, cancellationToken); break;
                case JobType.Metadata or JobType.Thumbnail or JobType.FullHash or JobType.NearDuplicateFingerprint or JobType.VisualSampling:
                    throw new InvalidOperationException("Protected production mode cannot execute a legacy original-source Job.");
                default: await _jobs.FailOrRetryAsync(job.JobId, "UNSUPPORTED_JOB", L.T("このジョブ種別はこの版では実装されていません。"), false, cancellationToken); break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _jobs.CompleteAsync(job.JobId, JobState.Cancelled, "Cancelled", CancellationToken.None);
            throw;
        }
        catch (MediaToolException ex)
        {
            var retryable = ex.Error.Code is "DB_BUSY" or WorkerExitedUnexpectedlyException.ErrorCode;
            await _jobs.FailOrRetryAsync(job.JobId, ex.Error.Code, ex.Error.UserMessage, retryable, cancellationToken);
            _log.Write("Warning", "MediaJobFailed", new { JobId = job.JobId, job.JobType, ErrorCode = ex.Error.Code, Retryable = retryable, Diagnostic = ex.Error.Diagnostic });
        }
        catch (UnauthorizedAccessException ex)
        {
            await _jobs.FailOrRetryAsync(job.JobId, "MEDIA_ACCESS_DENIED", L.T("メディアへアクセスできません。権限またはロック状態を確認してください。"), false, cancellationToken);
            _log.Write("Warning", "MediaAccessDenied", new { JobId = job.JobId, job.JobType }, ex);
        }
        catch (ProcessingSourceLeaseUnavailableException ex)
        {
            await _jobs.FailOrRetryAsync(job.JobId, ex.Code, L.T("対象ファイルは別のアプリケーションで変更中です。処理は保護を取得してから再試行します。"), true, cancellationToken);
            _log.Write("Warning", "ProtectedSourceLeaseUnavailable", new { JobId = job.JobId, job.JobType, ex.Code }, ex);
        }
        catch (IOException ex)
        {
            await _jobs.FailOrRetryAsync(job.JobId, "MEDIA_NOT_FOUND", L.T("メディアを読み取れません。ドライブ接続またはファイル状態を確認してください。"), true, cancellationToken);
            _log.Write("Warning", "MediaIoFailure", new { JobId = job.JobId, job.JobType }, ex);
        }
        catch (Exception ex)
        {
            var protectedAttempt = job.JobType == JobType.ProtectedSourceOrchestration;
            await _jobs.FailOrRetryAsync(job.JobId, protectedAttempt ? "PROTECTED_SOURCE_ATTEMPT_FAILED" : "UNEXPECTED_ERROR", L.T("予期しないエラーによりジョブを完了できませんでした。詳細はローカルログを確認してください。"), protectedAttempt, cancellationToken);
            _log.Write("Error", "JobUnhandledException", new { JobId = job.JobId, job.JobType }, ex);
        }
        finally
        {
            if (admitted)
            {
                onFinished();
                admission?.Release();
            }
            if (trackedExecution) CompleteTrackedExecution();
        }
        return true;
    }

    private async Task RunCoordinatorAsync(TaskCompletionSource<bool> completion, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            while (IsAcquisitionEnabled())
            {
                lock (_coordinatorGate) _runRequested = false;
                await RunUntilIdleAsync(cancellationToken).ConfigureAwait(false);
                lock (_coordinatorGate)
                {
                    if (!_runRequested || !IsAcquisitionEnabled()) break;
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            lock (_coordinatorGate)
            {
                if (RunState == ProcessingRunState.Running) SetRunState(ProcessingRunState.EnabledIdle);
                _coordinatorActive = false;
                _runRequested = false;
                if (failure is null) completion.TrySetResult(true); else completion.TrySetException(failure);
                if (ReferenceEquals(_coordinatorCompletion, completion)) _coordinatorCompletion = null;
            }
        }
    }

    private async Task CloseAcquisitionGateAndDrainAsync(TaskCompletionSource<bool> completion)
    {
        try
        {
            await _acquisitionAdmission.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                lock (_runStateGate)
                {
                    _stopGateLinearized = true;
                    _runState = _terminalShutdownRequested ? ProcessingRunState.ShuttingDown : ProcessingRunState.Draining;
                    if (_activeExecutions == 0) _drainCompletion!.TrySetResult(true);
                }
                PublishRunState(_terminalShutdownRequested ? ProcessingRunState.ShuttingDown : ProcessingRunState.Draining);
            }
            finally
            {
                _acquisitionAdmission.Release();
            }

            await _drainCompletion!.Task.ConfigureAwait(false);
            Task coordinator;
            lock (_coordinatorGate) coordinator = _coordinatorCompletion?.Task ?? Task.CompletedTask;
            await coordinator.ConfigureAwait(false);
            ProcessingRunState finalState;
            lock (_runStateGate)
            {
                finalState = _terminalShutdownRequested ? ProcessingRunState.ShuttingDown : ProcessingRunState.UserStopped;
                _runState = finalState;
            }
            PublishRunState(finalState);
            completion.TrySetResult(true);
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    private static async Task CompleteTerminalShutdownAsync(Task drainTask, Task cleanupDrain, TaskCompletionSource<bool> completion)
    {
        try
        {
            await Task.WhenAll(drainTask, cleanupDrain).ConfigureAwait(false);
            completion.TrySetResult(true);
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    private bool IsAcquisitionEnabled()
    {
        lock (_runStateGate)
            return _runState is ProcessingRunState.EnabledIdle or ProcessingRunState.Running;
    }

    private void TrackAcquiredExecution()
    {
        lock (_runStateGate) _activeExecutions++;
    }

    private void CompleteTrackedExecution()
    {
        TaskCompletionSource<bool>? drain = null;
        lock (_runStateGate)
        {
            if (_activeExecutions <= 0) throw new InvalidOperationException("The processing execution count is unbalanced.");
            _activeExecutions--;
            if (_activeExecutions == 0 && _stopGateLinearized && _runState is ProcessingRunState.StopRequested or ProcessingRunState.Draining or ProcessingRunState.ShuttingDown)
            {
                drain = _drainCompletion;
            }
        }
        drain?.TrySetResult(true);
    }

    private void SetRunState(ProcessingRunState state)
    {
        lock (_runStateGate)
        {
            if (_runState == state) return;
            if (_runState is (ProcessingRunState.StopRequested or ProcessingRunState.Draining or ProcessingRunState.UserStopped or ProcessingRunState.ShuttingDown)
                && state == ProcessingRunState.Running) return;
            _runState = state;
        }
        PublishRunState(state);
    }

    private void PublishRunState(ProcessingRunState state)
    {
        _log.Write("Information", "ProcessingRunStateChanged", new { State = state.ToString() });
        Wake();
        RunStateChanged?.Invoke(this, new ProcessingRunStateChangedEventArgs(state));
    }

    /// <summary>Releases lanes waiting out a rest or for compute work so they re-check stop state and settings.</summary>
    private void Wake()
    {
        TaskCompletionSource fired;
        lock (_wakeGate)
        {
            fired = _wake;
            _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        fired.TrySetResult();
    }

    /// <summary>Groups the active roots by physical drive: one source lane per drive. The first lane also takes orphaned work.</summary>
    private async Task<IReadOnlyList<LaneScheduler.SourceLane>> ResolveSourceLanesAsync(CancellationToken cancellationToken)
    {
        var roots = await _library.GetLibraryRootsAsync(cancellationToken).ConfigureAwait(false);
        var groups = roots
            .GroupBy(root => PhysicalDisk.TryGetDiskNumber(root.ConfiguredPath) ?? -1)
            .OrderBy(group => group.Key)
            .ToArray();
        if (groups.Length == 0)
            return [new LaneScheduler.SourceLane("source", new JobLaneFilter(JobLaneKind.Source), PacerFor(-1))];
        return groups.Select((group, index) => new LaneScheduler.SourceLane(
            group.Key < 0 ? "source-unknown-drive" : "source-disk-" + group.Key,
            new JobLaneFilter(JobLaneKind.Source, group.Select(root => root.LibraryRootId).ToArray(), IncludeUnattributed: index == 0),
            PacerFor(group.Key))).ToArray();
    }

    private SourcePacer PacerFor(int disk)
    {
        lock (_pacerGate)
        {
            if (!_pacers.TryGetValue(disk, out var pacer))
                _pacers[disk] = pacer = new SourcePacer(static () => DateTimeOffset.UtcNow, _userIdle);
            return pacer;
        }
    }

    public async Task RunUntilIdleAsync(CancellationToken cancellationToken)
    {
        await _runnerAdmission.WaitAsync(cancellationToken);
        try
        {
            // Repeat passes until a whole pass finds nothing: a scan can register work on a drive that had no lane yet,
            // and compute work can finish after every source lane went idle.
            while (IsAcquisitionEnabled())
            {
                _lanesStale = false;
                var lanes = await ResolveSourceLanesAsync(cancellationToken).ConfigureAwait(false);
                var processed = await _scheduler.RunAsync(lanes, cancellationToken).ConfigureAwait(false);
                if (IsAcquisitionEnabled()) await FlushLibraryWideRefreshesAsync(cancellationToken).ConfigureAwait(false);
                if (!processed && !_lanesStale) break;
            }
            await _library.CheckpointAsync(cancellationToken);
        }
        finally
        {
            _runnerAdmission.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _retentionCleanup.Dispose();
        _runnerAdmission.Dispose();
        _acquisitionAdmission.Dispose();
        _scanAdmission.Dispose();
        _metadataAdmission.Dispose();
        _thumbnailAdmission.Dispose();
        _visualSamplingAdmission.Dispose();
        _visualEmbeddingAdmission.Dispose();
        _fullHashAdmission.Dispose();
        _nearDuplicateFingerprintAdmission.Dispose();
        _contentSimilarityRepresentationAdmission.Dispose();
        _tagSuggestionAdmission.Dispose();
        _provenanceGate.Dispose();
        _fallbackAdmission.Dispose();
        GC.SuppressFinalize(this);
    }

    private SemaphoreSlim GetAdmission(JobType jobType) => jobType switch
    {
        JobType.Scan => _scanAdmission,
        JobType.Metadata => _metadataAdmission,
        JobType.Thumbnail => _thumbnailAdmission,
        JobType.FullHash => _fullHashAdmission,
        JobType.NearDuplicateFingerprint => _nearDuplicateFingerprintAdmission,
        JobType.ContentSimilarityRepresentation => _contentSimilarityRepresentationAdmission,
        JobType.TagSuggestion => _tagSuggestionAdmission,
        JobType.VisualSampling => _visualSamplingAdmission,
        JobType.VisualEmbedding => _visualEmbeddingAdmission,
        _ => _fallbackAdmission
    };

    /// <summary>Config hash of the thumbnails this build generates; readers use it to find current thumbnails.</summary>
    public static string CurrentThumbnailConfigHash() => ThumbnailConfigHash();

    private static string ThumbnailConfigHash() => AnalysisKey.ConfigHash(new { schema = 1, seek = "00:00:01", maxWidth = 480, format = "jpeg" });

    private bool IsUsableThumbnail(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) return false;
        try
        {
            var fullPath = Path.GetFullPath(Path.Combine(_paths.CacheRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            return PathPolicy.IsWithinRoot(_paths.CacheRoot, fullPath) && File.Exists(fullPath) && new FileInfo(fullPath).Length > 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (ArgumentException) { return false; }
    }

    public async Task<int> EnqueueMissingThumbnailJobsAsync(CancellationToken cancellationToken)
    {
        var scheduled = 0;
        var configHash = ThumbnailConfigHash();
        foreach (var root in await _library.GetLibraryRootsAsync(cancellationToken))
        {
            if (root.RegistrationState != RootRegistrationState.Active) continue;
            foreach (var candidate in await _library.GetCurrentThumbnailCandidatesAsync(root.LibraryRootId, configHash, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsUsableThumbnail(candidate.RelativePath)) continue;
                if (await _sqliteJobs.EnqueueForActiveMediaAsync(JobType.Thumbnail, new MediaPayload(candidate.MediaFileId), candidate.MediaFileId,
                    2, null, unique: true, cancellationToken) is not null) scheduled++;
            }
        }
        return scheduled;
    }

    private async Task ProcessScanAsync(Job job, CancellationToken cancellationToken)
    {
        var payload = Deserialize<ScanPayload>(job.PayloadJson);
        var root = await _library.GetLibraryRootAsync(payload.LibraryRootId, cancellationToken) ?? throw new InvalidOperationException("Library root does not exist.");
        if (root.RegistrationState != RootRegistrationState.Active || root.RegistrationRevision != payload.RegistrationRevision)
        {
            await _jobs.CompleteAsync(job.JobId, JobState.Cancelled, "RootUnregistered", cancellationToken);
            return;
        }
        if (!IsAllowedByTestLab(root.ConfiguredPath))
        {
            await _jobs.CompleteAsync(job.JobId, JobState.Cancelled, "TestLabRootRejected", cancellationToken);
            _log.Write("Warning", "TestLabRootRejected", new { JobId = job.JobId, root.LibraryRootId });
            return;
        }
        var probe = _scanner.Probe(root);
        if (probe.Observation != RootObservationKind.Online)
        {
            await _library.RecordRootObservationAsync(root.LibraryRootId, probe.Observation, DateTimeOffset.UtcNow, cancellationToken);
            await _jobs.CompleteAsync(job.JobId, JobState.Partial, probe.Observation.ToString(), cancellationToken);
            _log.Write("Warning", "RootUnavailable", new { JobId = job.JobId, LibraryRootId = root.LibraryRootId, root.ConfiguredPath, Observation = probe.Observation.ToString() });
            return;
        }
        if (!IsAllowedByTestLab(probe.EffectivePath))
        {
            await _jobs.CompleteAsync(job.JobId, JobState.Cancelled, "TestLabRootRejected", cancellationToken);
            _log.Write("Warning", "TestLabRootRejected", new { JobId = job.JobId, root.LibraryRootId });
            return;
        }
        if (!string.Equals(root.ConfiguredPath, probe.EffectivePath, StringComparison.OrdinalIgnoreCase) || root.VolumeIdentity is null || root.RootIdentity is null)
        {
            await _library.UpdateRootIdentityAsync(root.LibraryRootId, probe.EffectivePath, probe.VolumeIdentity, probe.RootDirectoryIdentity, cancellationToken);
            root = (await _library.GetLibraryRootAsync(root.LibraryRootId, cancellationToken))!;
        }
        await _library.RecordRootObservationAsync(root.LibraryRootId, RootObservationKind.Online, DateTimeOffset.UtcNow, cancellationToken);
        var scanId = await _library.StartScanAsync(root.LibraryRootId, cancellationToken);
        var observations = new ScanObservationCollector();
        var seen = 0; var added = 0; var updated = 0; var unavailable = 0; var errors = 0;
        const int reconciliationBatchSize = 25;
        var reconciliationBatch = new List<ScannedMediaFile>(reconciliationBatchSize);
        async Task ApplyReconciliationResultAsync(ScannedMediaFile file, ReconcileResult result)
        {
            if (result.IsNew) added++; else updated++;
            if (result.IsNew || result.MetadataInvalidated)
                await _sqliteJobs.EnqueueForActiveMediaAsync(FileSystemMediaScanner.IsSupportedImageExtension(file.Extension) ? JobType.Thumbnail : JobType.Metadata,
                    new MediaPayload(result.MediaFile.MediaFileId), result.MediaFile.MediaFileId, 2, null, unique: true, cancellationToken);
        }
        async Task ReconcileSingleFileAsync(ScannedMediaFile file)
        {
            try
            {
                var result = await _library.UpsertScannedFileAsync(file, scanId, cancellationToken);
                await ApplyReconciliationResultAsync(file, result);
            }
            catch (UnauthorizedAccessException) { unavailable++; }
            catch (IOException) { unavailable++; }
            catch (Exception ex) { errors++; _log.Write("Warning", "ScanFileFailed", new { JobId = job.JobId, Path = file.FullPath, ErrorCode = "SCAN_FILE_FAILED" }, ex); }
        }
        async Task FlushReconciliationBatchAsync()
        {
            if (reconciliationBatch.Count == 0) return;
            try
            {
                var results = await _library.ReconcileScannedFilesAsync(reconciliationBatch, scanId, cancellationToken);
                if (results.Count != reconciliationBatch.Count) throw new InvalidOperationException("Scan reconciliation batch returned an unexpected result count.");
                for (var index = 0; index < reconciliationBatch.Count; index++)
                {
                    try { await ApplyReconciliationResultAsync(reconciliationBatch[index], results[index]); }
                    catch (UnauthorizedAccessException) { unavailable++; }
                    catch (IOException) { unavailable++; }
                    catch (Exception ex) { errors++; _log.Write("Warning", "ScanFileFailed", new { JobId = job.JobId, Path = reconciliationBatch[index].FullPath, ErrorCode = "SCAN_FILE_FAILED" }, ex); }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _log.Write("Warning", "ScanReconciliationBatchFallback", new { JobId = job.JobId, FileCount = reconciliationBatch.Count, ErrorCode = "SCAN_RECONCILIATION_BATCH_FAILED" }, ex);
                foreach (var file in reconciliationBatch) await ReconcileSingleFileAsync(file);
            }
            finally { reconciliationBatch.Clear(); }
        }
        try
        {
            await foreach (var file in _scanner.EnumerateAsync(root, observations, cancellationToken))
            {
                seen++;
                reconciliationBatch.Add(file);
                if (reconciliationBatch.Count == reconciliationBatchSize) await FlushReconciliationBatchAsync();
                if (seen % reconciliationBatchSize == 0) await _jobs.UpdateProgressAsync(job.JobId, "Enumerating", new JobProgress(seen, 0, "files", true), cancellationToken);
            }
            await FlushReconciliationBatchAsync();
            var completion = new ScanCompletion("Completed", seen, added, updated, unavailable, errors, DateTimeOffset.UtcNow);
            await _library.CompleteScanAsync(scanId, completion, cancellationToken);
            await _jobs.UpdateProgressAsync(job.JobId, "Finalizing", new JobProgress(seen, seen, "files", false), cancellationToken);
            await _library.ReconcileCompletedScanAbsenceAsync(root.LibraryRootId, scanId, observations.Snapshot(probe.VolumeIdentity, probe.RootDirectoryIdentity, completed: errors == 0), completion.CompletedAt, cancellationToken);
            var thumbnailConfigHash = ThumbnailConfigHash();
            foreach (var candidate in await _library.GetCurrentThumbnailCandidatesAsync(root.LibraryRootId, thumbnailConfigHash, cancellationToken))
                if (!IsUsableThumbnail(candidate.RelativePath))
                    await _sqliteJobs.EnqueueForActiveMediaAsync(JobType.Thumbnail, new MediaPayload(candidate.MediaFileId), candidate.MediaFileId, 2, null, unique: true, cancellationToken);
            if (errors == 0) RequestRetentionCleanup("ScanCompleted");
            var hashCandidates = await _library.GetExactDuplicateHashCandidatesAsync(root.LibraryRootId, cancellationToken);
            foreach (var candidate in hashCandidates.Where(candidate => !FileSystemMediaScanner.IsSupportedImageExtension(candidate.Extension)))
                await _sqliteJobs.EnqueueForActiveMediaAsync(JobType.FullHash, new FullHashPayload(candidate.MediaFileId, candidate.MediaContentId, candidate.SizeBytes, candidate.LastWriteTimeUtc), candidate.MediaFileId, 3, null, unique: true, cancellationToken);
            var nearCandidates = await _library.GetNearDuplicateFingerprintCandidatesAsync(root.LibraryRootId, NearDuplicateSamplingPolicy.Version, PerceptualFrameFingerprint.Algorithm, cancellationToken);
            foreach (var candidate in nearCandidates.Where(candidate => !FileSystemMediaScanner.IsSupportedImageExtension(candidate.Extension)))
                await _sqliteJobs.EnqueueForActiveMediaAsync(JobType.NearDuplicateFingerprint, new NearDuplicateFingerprintPayload(candidate.MediaFileId, candidate.MediaContentId, candidate.SizeBytes, candidate.LastWriteTimeUtc), candidate.MediaFileId, 2, null, unique: true, cancellationToken);
            await CatchUpNearDuplicatePreparationAsync(job, cancellationToken);
            // Timelines read the video itself, so they go through the protected per-media orchestration like frames do.
            foreach (var candidate in (await _library.GetMediaWithoutNearDuplicateTimelineAsync(root.LibraryRootId, cancellationToken)).Where(candidate => !FileSystemMediaScanner.IsSupportedImageExtension(candidate.Extension)))
                await _sqliteJobs.EnqueueForActiveMediaAsync(JobType.NearDuplicateFingerprint, new NearDuplicateFingerprintPayload(candidate.MediaFileId, candidate.MediaContentId, candidate.SizeBytes, candidate.LastWriteTimeUtc), candidate.MediaFileId, 2, null, unique: true, cancellationToken);
            if (errors == 0 && _worker is not null)
                await EnqueueMissingVisualEmbeddingJobsAsync(root.LibraryRootId, null, cancellationToken);
            var contentCandidates = await _library.GetContentSimilarityRepresentationCandidatesAsync(root.LibraryRootId, ContentSimilarityVectors.AggregationVersion, _semanticModel.ModelId, _semanticModel.ModelRevision, _semanticModel.ProcessorVersion, VisualSamplingPolicy.FixedIntervalV1.Version, _semanticExecution.Dtype, _semanticModel.Dimension, cancellationToken);
            foreach (var candidate in contentCandidates.Where(candidate => !FileSystemMediaScanner.IsSupportedImageExtension(candidate.Extension)))
                await _sqliteJobs.EnqueueForActiveMediaAsync(JobType.ContentSimilarityRepresentation, new ContentSimilarityRepresentationPayload(candidate.MediaFileId, candidate.MediaContentId, candidate.SizeBytes, candidate.LastWriteTimeUtc), candidate.MediaFileId, 2, null, unique: true, cancellationToken);
            var tagCandidates = await _library.GetTagSuggestionCandidatesAsync(root.LibraryRootId, TaggingTaxonomy.TaxonomyVersion, TaggingTaxonomy.DescriptiveTextRepresentationVersion, TaggingVerificationProfile.DescriptiveHighPrecisionV1.AggregationVersion, TaggingVerificationProfile.DescriptiveHighPrecisionV1.ThresholdVersion, _semanticModel.ModelId, _semanticModel.ModelRevision, _semanticModel.ProcessorVersion, VisualSamplingPolicy.FixedIntervalV1.Version, _semanticExecution.Dtype, _semanticModel.Dimension, cancellationToken);
            foreach (var candidate in tagCandidates.Where(candidate => !FileSystemMediaScanner.IsSupportedImageExtension(candidate.Extension)))
                await _sqliteJobs.EnqueueForActiveMediaAsync(JobType.TagSuggestion, new TagSuggestionPayload(candidate.MediaFileId, candidate.MediaContentId, candidate.SizeBytes, candidate.LastWriteTimeUtc), candidate.MediaFileId, 2, null, unique: true, cancellationToken);
            await _jobs.CompleteAsync(job.JobId, errors == 0 ? JobState.Completed : JobState.Partial, $"Finalized;FullHashCandidates={hashCandidates.Count};NearFingerprintCandidates={nearCandidates.Count};ContentSimilarityCandidates={contentCandidates.Count};TagSuggestionCandidates={tagCandidates.Count}", cancellationToken);
        }
        catch
        {
            await _library.CompleteScanAsync(scanId, new ScanCompletion("Interrupted", seen, added, updated, unavailable, errors + 1, DateTimeOffset.UtcNow), CancellationToken.None);
            await _library.RecordRootObservationAsync(root.LibraryRootId, RootObservationKind.Unknown, DateTimeOffset.UtcNow, CancellationToken.None);
            throw;
        }
    }

    private async Task ProcessProtectedSourceOrchestrationAsync(Job job, CancellationToken cancellationToken)
    {
        var payload = Deserialize<ProtectedSourceOrchestrationPayload>(job.PayloadJson);
        var media = await _library.GetMediaFileAsync(payload.MediaFileId, cancellationToken);
        if (media is null || media.PresenceState != PresenceState.Present)
        {
            await _jobs.CompleteAsync(job.JobId, JobState.Partial, media?.PresenceState == PresenceState.OfflineRoot ? "RootOffline" : "MediaUnavailable", cancellationToken);
            return;
        }
        if (media.MediaContentId != payload.ExpectedMediaContentId)
        {
            await _jobs.CompleteAsync(job.JobId, JobState.Partial, "SupersededSourceVersion", cancellationToken);
            return;
        }
        if (!IsAllowedByTestLab(media.Path))
        {
            await _jobs.CompleteAsync(job.JobId, JobState.Cancelled, "TestLabMediaRejected", cancellationToken);
            _log.Write("Warning", "TestLabMediaRejected", new { JobId = job.JobId, media.MediaFileId });
            return;
        }

        if (payload.EligibleStages.HasFlag(ProtectedSourceStageEligibility.FullHash) || payload.EligibleStages.HasFlag(ProtectedSourceStageEligibility.NearDuplicateFingerprint))
        {
            var resource = await _resourceSnapshots.CaptureAsync(cancellationToken);
            var decision = _resourceAdmission.Evaluate(new ResourceCostEstimate(35, 128L * 1024 * 1024, false, 0, 95, 5, 10, 20), resource);
            if (decision.Kind == AdmissionDecisionKind.Wait)
            {
                await _jobs.DeferForResourceAsync(job.JobId, decision.Reason, decision.Code, decision.Message, DateTimeOffset.UtcNow.AddSeconds(20), cancellationToken);
                return;
            }
            if (decision.Kind != AdmissionDecisionKind.Admit)
            {
                await _jobs.FailOrRetryAsync(job.JobId, decision.Code, decision.Message, false, cancellationToken);
                return;
            }
        }

        var orchestrator = new PreActivationProtectedSourceOrchestrator(_paths, _sqliteLibrary, _sqliteJobs, _log, handleFailureInternally: false);
        ProtectedSourcePublicationBundle? publishedBundle = null;
        StoredTimeline? timeline = null;
        var result = await orchestrator.ExecuteAsync(job, media, async (session, token) =>
        {
            await using var stagePersistence = new AttemptScopedStagePersistence(job, _sqliteJobs.HeartbeatAttemptAsync);
            var analyses = new List<ProtectedAnalysisPublication>();
            var samples = new List<VisualSample>();
            var nearFrames = new List<NearDuplicateFrame>();
            MediaMetadata? metadata = null;
            FileHashEvidence? fullHash = null;
            var inputIdentity = InputIdentity(media);

            if (payload.EligibleStages.HasFlag(ProtectedSourceStageEligibility.Metadata))
            {
                var config = new { schema = 1, source = "ffprobe-json", fields = "format,streams" };
                var configHash = AnalysisKey.ConfigHash(config);
                if (await _library.FindCompletedAnalysisAsync(media.MediaContentId, "Metadata", "ffprobe", "ffprobe-json-v1", configHash, "metadata-v1", token) is null)
                {
                    await stagePersistence.PersistPhaseEntryAsync("ProtectedMetadata", token);
                    var probe = await session.ExecuteSourceStageAsync("Metadata", (source, ct) => _ffmpeg.ProbeAsync(source, media, ct), token);
                    metadata = probe.Metadata;
                    analyses.Add(new ProtectedAnalysisPublication(CreateCompletedAnalysis(media.MediaContentId, "Metadata", "ffprobe", probe.ProcessorVersion,
                        JsonSerializer.Serialize(config), configHash, inputIdentity, "metadata-v1"), []));
                }
            }

            if (payload.EligibleStages.HasFlag(ProtectedSourceStageEligibility.Thumbnail))
            {
                var config = new { schema = 1, seek = "00:00:01", maxWidth = 480, format = "jpeg" };
                var configHash = ThumbnailConfigHash();
                if (!IsUsableThumbnail(await _library.GetCompletedThumbnailArtifactPathAsync(media.MediaContentId, configHash, token)))
                {
                    var analysis = CreateCompletedAnalysis(media.MediaContentId, "Thumbnail", "ffmpeg", "thumbnail-v1", JsonSerializer.Serialize(config), configHash, inputIdentity, "thumbnail-v1");
                    var cacheKey = AnalysisKey.ArtifactCacheKey(inputIdentity, "thumbnail", "thumbnail-v1", "ffmpeg-thumbnail-v1", configHash);
                    await stagePersistence.PersistPhaseEntryAsync("ProtectedThumbnail", token);
                    var generated = await session.ExecuteSourceStageAsync("Thumbnail", (source, ct) => _ffmpeg.GenerateThumbnailAsync(source, media, cacheKey, ct), token);
                    var staged = await StageGeneratedArtifactAsync(session, generated.FullPath, "thumbnail/" + Path.GetFileName(generated.FullPath), token);
                    var artifact = new Artifact(Guid.NewGuid(), analysis.AnalysisId, "Thumbnail", generated.RelativePath, generated.Sha256, generated.ByteSize, "image/jpeg", ArtifactDurability.RebuildableCache, DateTimeOffset.UtcNow);
                    var prepared = await ProtectedArtifactPreparation.PrepareAsync(session, _paths, staged, artifact, token);
                    analyses.Add(new ProtectedAnalysisPublication(analysis, [prepared]));
                }
            }

            if (payload.EligibleStages.HasFlag(ProtectedSourceStageEligibility.VisualSampling))
            {
                var policy = _visualSamplingPolicy;
                var config = new { schema = 1, sampling = policy.Version, policy.IntervalSeconds, policy.MinimumSamples, policy.MaximumSamples, policy.EndMarginSeconds };
                var configHash = AnalysisKey.ConfigHash(config);
                if (await _library.FindCompletedAnalysisAsync(media.MediaContentId, "VisualSampling", "ffmpeg", "ffmpeg-frame-v1", configHash, "visual-sampling-v1", token) is null)
                {
                    var duration = metadata?.DurationSeconds ?? (await _library.GetMediaListItemAsync(media.MediaFileId, token))?.Metadata?.DurationSeconds;
                    var timestamps = policy.CreateTimestamps(duration);
                    var now = DateTimeOffset.UtcNow;
                    for (var index = 0; index < timestamps.Count; index++)
                    {
                        var requested = timestamps[index];
                        var frameConfigHash = AnalysisKey.ConfigHash(new { requestedTimestampSeconds = requested, sampling = policy.Version });
                        var cacheKey = AnalysisKey.ArtifactCacheKey(inputIdentity, "visual-frame", "visual-sampling-v1", "ffmpeg-frame-v1", frameConfigHash);
                        await stagePersistence.PersistPhaseEntryAsync("ProtectedVisualSampling", token);
                        var generated = await session.ExecuteSourceStageAsync("VisualSampling", (source, ct) => _ffmpeg.ExtractFrameAsync(source, media, requested, cacheKey, ct), token);
                        await StageGeneratedArtifactAsync(session, generated.FullPath, $"visual/{index:D3}-{Path.GetFileName(generated.FullPath)}", token);
                        samples.Add(new VisualSample(Guid.NewGuid(), media.MediaFileId, media.MediaContentId, inputIdentity, index, requested,
                            generated.PresentationTimestampSeconds, policy.Version, cacheKey, generated.RelativePath, generated.Sha256, generated.ByteSize,
                            VisualSampleStatus.Completed, null, null, now, DateTimeOffset.UtcNow));
                    }
                    analyses.Add(new ProtectedAnalysisPublication(CreateCompletedAnalysis(media.MediaContentId, "VisualSampling", "ffmpeg", "ffmpeg-frame-v1",
                        JsonSerializer.Serialize(config), configHash, inputIdentity, "visual-sampling-v1"), []));
                }
            }

            if (payload.EligibleStages.HasFlag(ProtectedSourceStageEligibility.NearDuplicateFingerprint))
            {
                var existing = await _library.GetCurrentNearDuplicateFramesAsync(media.MediaFileId, media.MediaContentId, NearDuplicateSamplingPolicy.Version, PerceptualFrameFingerprint.Algorithm, token);
                var duration = metadata?.DurationSeconds ?? (await _library.GetMediaListItemAsync(media.MediaFileId, token))?.Metadata?.DurationSeconds;
                var timestamps = NearDuplicateSamplingPolicy.CreateTimestamps(duration);
                if (timestamps.Count == NearDuplicateVerificationProfile.HighPrecisionV1.RequiredSampleCount)
                {
                    var complete = existing.Select(value => value.SampleIndex).ToHashSet();
                    var now = DateTimeOffset.UtcNow;
                    foreach (var timestamp in timestamps.Where(value => !complete.Contains(value.SampleIndex)))
                    {
                        var frameConfigHash = AnalysisKey.ConfigHash(new { timestamp.SampleIndex, timestamp.NormalizedPosition, timestamp.RequestedTimestampSeconds, SamplingVersion = NearDuplicateSamplingPolicy.Version, FingerprintAlgorithm = PerceptualFrameFingerprint.Algorithm });
                        var cacheKey = AnalysisKey.ArtifactCacheKey(inputIdentity, "near-duplicate-frame", NearDuplicateSamplingPolicy.Version + ":" + PerceptualFrameFingerprint.Algorithm, "ffmpeg-near-duplicate-v1", frameConfigHash);
                        await stagePersistence.PersistPhaseEntryAsync("ProtectedNearDuplicate", token);
                        var generated = await session.ExecuteSourceStageAsync("NearDuplicate", (source, ct) => _ffmpeg.ExtractNearDuplicateFrameAsync(source, media,
                            timestamp.SampleIndex, timestamp.NormalizedPosition, timestamp.RequestedTimestampSeconds, cacheKey, ct), token);
                        await StageGeneratedArtifactAsync(session, generated.FullPath, $"near/{timestamp.SampleIndex:D3}-{Path.GetFileName(generated.FullPath)}", token);
                        nearFrames.Add(new NearDuplicateFrame(Guid.NewGuid(), media.MediaFileId, media.MediaContentId, media.SizeBytes, media.LastWriteTimeUtc,
                            timestamp.SampleIndex, timestamp.NormalizedPosition, timestamp.RequestedTimestampSeconds, generated.PresentationTimestampSeconds,
                            NearDuplicateSamplingPolicy.Version, PerceptualFrameFingerprint.Algorithm, generated.FingerprintHex, generated.RelativePath,
                            generated.Sha256, generated.ByteSize, AnalysisStatus.Completed, null, null, now, DateTimeOffset.UtcNow));
                    }
                }
                if (duration is { } seconds && seconds >= TimelineSamplingPolicy.MinimumDurationSeconds &&
                    await _library.GetCurrentNearDuplicateTimelineAsync(media.MediaFileId, media.MediaContentId, token) is null)
                    timeline = await ReadTimelineAsync(session, stagePersistence, media, seconds, token);
            }

            if (payload.EligibleStages.HasFlag(ProtectedSourceStageEligibility.FullHash) &&
                await _library.GetCurrentFullHashEvidenceAsync(media.MediaFileId, media.MediaContentId, "SHA-256", "full-sha256-v1", token) is null)
            {
                await stagePersistence.PersistPhaseEntryAsync("ProtectedFullHash", token);
                var digest = await session.ExecuteSourceStageAsync("FullHash", (source, ct) => FileHash.Sha256Async(source.SourcePath, ct), token);
                fullHash = new FileHashEvidence(Guid.NewGuid(), media.MediaFileId, media.MediaContentId, "SHA-256", digest, media.SizeBytes,
                    media.LastWriteTimeUtc, "full-sha256-v1", "Verified", null, null, DateTimeOffset.UtcNow);
            }

            await (_protectedPublicationProbe?.Invoke(session, token) ?? Task.CompletedTask);
            publishedBundle = new ProtectedSourcePublicationBundle(session.SessionId, media.MediaFileId, media.MediaContentId, metadata, samples, analyses, fullHash, nearFrames);
            return publishedBundle;
        }, cancellationToken);

        if (result.Disposition != GuardedPublicationDisposition.Published || publishedBundle is null) return;
        foreach (var artifact in publishedBundle.Analyses.Where(item => item.Analysis.AnalysisType == "Thumbnail")
                     .SelectMany(item => item.Artifacts).Where(item => item.Artifact.ArtifactKind == "Thumbnail"))
            ThumbnailReady?.Invoke(this, new ThumbnailReadyEventArgs(publishedBundle.MediaFileId, publishedBundle.ExpectedMediaContentId, artifact.Artifact.RelativePath));
        if (_worker is not null && publishedBundle.VisualSamples.Any(value => value.Status == VisualSampleStatus.Completed))
            await EnqueueMissingVisualEmbeddingJobsAsync(null, publishedBundle.MediaFileId, cancellationToken);
        if (publishedBundle.FullHashEvidence is not null)
            await RefreshLibraryWideAsync(LibraryWideRefresh.ExactGroups, cancellationToken);
        if ((publishedBundle.NearDuplicateFrames?.Count ?? 0) > 0)
            await RefreshLibraryWideAsync(LibraryWideRefresh.NearDuplicate, cancellationToken);
        if (timeline is not null && await _library.SaveNearDuplicateTimelineAsync(timeline, cancellationToken))
            await VerifyTimelineCandidatesAsync(media, cancellationToken);
    }

    /// <summary>Number of library-wide refreshes actually run (diagnostics and tests).</summary>
    internal int LibraryWideRefreshRuns { get; private set; }

    /// <summary>Runs the refresh now if no more work of its kind is queued (or the interval has passed); otherwise marks it due.</summary>
    private async Task RefreshLibraryWideAsync(LibraryWideRefresh kind, CancellationToken cancellationToken)
    {
        var queued = kind == LibraryWideRefresh.ContentSimilarity
            ? await _sqliteJobs.HasPendingOfTypesAsync([JobType.ContentSimilarityRepresentation], cancellationToken)
            : await _sqliteJobs.HasPendingOfTypesAsync([JobType.ProtectedSourceOrchestration, JobType.FullHash, JobType.NearDuplicateFingerprint], cancellationToken);
        if (_refresh.ShouldRunNow(kind, queued)) await RunLibraryWideRefreshAsync(kind, cancellationToken);
    }

    private async Task RunLibraryWideRefreshAsync(LibraryWideRefresh kind, CancellationToken cancellationToken)
    {
        _refresh.Started(kind);
        try
        {
            switch (kind)
            {
                case LibraryWideRefresh.ExactGroups:
                    await _library.RebuildExactDuplicateGroupsAsync(cancellationToken);
                    break;
                case LibraryWideRefresh.NearDuplicate:
                    await VerifyNearDuplicateCandidatesAsync(null, NearDuplicateSamplingPolicy.Version, PerceptualFrameFingerprint.Algorithm, cancellationToken);
                    break;
                default:
                    var profile = ContentSimilarityVerificationProfile.HighPrecisionV1;
                    await VerifyContentSimilarityCandidatesAsync(ContentSimilarityVectors.AggregationVersion, profile.ThresholdVersion, cancellationToken);
                    break;
            }
            LibraryWideRefreshRuns++;
        }
        catch
        {
            _refresh.Failed(kind);
            throw;
        }
    }

    /// <summary>Runs every deferred library-wide refresh. Called when a pass ends; failures are logged and retried next pass.</summary>
    private async Task FlushLibraryWideRefreshesAsync(CancellationToken cancellationToken)
    {
        foreach (var kind in _refresh.Due())
        {
            try { await RunLibraryWideRefreshAsync(kind, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Write("Warning", "LibraryWideRefreshFailed", new { Kind = kind.ToString() }, ex);
            }
        }
    }

    private bool IsAllowedByTestLab(string path)
    {
        if (_paths.TestLab is null) return true;
        try
        {
            _paths.TestLab.EnsureMediaPathAllowed(path);
            return true;
        }
        catch (TestLabViolationException) { return false; }
    }

    private static Analysis CreateCompletedAnalysis(Guid contentId, string type, string processor, string processorVersion, string configJson, string configHash, string inputIdentity, string processingVersion)
    {
        var now = DateTimeOffset.UtcNow;
        return new Analysis(Guid.NewGuid(), contentId, type, AnalysisStatus.Completed, processor, processorVersion, null, null,
            configJson, configHash, inputIdentity, processingVersion, now, now, null, null);
    }

    internal async Task EnqueueMissingVisualEmbeddingJobsAsync(Guid? libraryRootId, Guid? mediaFileId, CancellationToken cancellationToken)
    {
        var candidates = await _sqliteLibrary.GetMissingVisualEmbeddingCandidatesAsync(_semanticModel, _semanticExecution, libraryRootId, mediaFileId, cancellationToken);
        foreach (var candidate in candidates)
            await _sqliteJobs.EnqueueForActiveMediaAsync(JobType.VisualEmbedding, new VisualEmbeddingPayload(candidate.Sample.VisualSampleId), candidate.Media.MediaFileId, 2, null, unique: true, cancellationToken);
    }

    private static async Task<UnpublishedProcessingArtifact> StageGeneratedArtifactAsync(ProcessingSourceSession session, string generatedPath, string stagingRelativePath, CancellationToken cancellationToken)
    {
        var staged = session.CreateUnpublishedArtifact(stagingRelativePath);
        await using var source = new FileStream(generatedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var destination = new FileStream(staged.FullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await source.CopyToAsync(destination, cancellationToken);
        await destination.FlushAsync(cancellationToken);
        destination.Flush(flushToDisk: true);
        return staged;
    }

    private async Task ProcessMetadataAsync(Job job, CancellationToken cancellationToken)
    {
        var media = await GetPresentMediaAsync(job, cancellationToken);
        var config = new { schema = 1, source = "ffprobe-json", fields = "format,streams" };
        var configHash = AnalysisKey.ConfigHash(config);
        var inputIdentity = InputIdentity(media);
        var reusable = await _library.FindCompletedAnalysisAsync(media.MediaContentId, "Metadata", "ffprobe", "ffprobe-json-v1", configHash, "metadata-v1", cancellationToken);
        if (reusable is not null) { await _jobs.CompleteAsync(job.JobId, JobState.Completed, "Reused", cancellationToken); return; }
        var analysis = await _library.StartAnalysisAsync(media.MediaContentId, "Metadata", "ffprobe", "ffprobe-json-v1", JsonSerializer.Serialize(config), configHash, inputIdentity, "metadata-v1", cancellationToken);
        try
        {
            await _jobs.HeartbeatAsync(job.JobId, "Probing", cancellationToken);
            var probe = await _ffmpeg.ProbeAsync(media, cancellationToken);
            await _library.UpsertMetadataAsync(probe.Metadata, cancellationToken);
            await _library.CompleteAnalysisAsync(analysis.AnalysisId, AnalysisStatus.Completed, cancellationToken);
            await _sqliteJobs.EnqueueForActiveMediaAsync(JobType.Thumbnail, new MediaPayload(media.MediaFileId), media.MediaFileId, 2, null, unique: false, cancellationToken);
            await _sqliteJobs.EnqueueForActiveMediaAsync(JobType.VisualSampling, new VisualSamplingPayload(media.MediaFileId), media.MediaFileId, 2, null, unique: false, cancellationToken);
            await _sqliteJobs.EnqueueForActiveMediaAsync(JobType.NearDuplicateFingerprint, new NearDuplicateFingerprintPayload(media.MediaFileId, media.MediaContentId, media.SizeBytes, media.LastWriteTimeUtc), media.MediaFileId, 2, null, unique: true, cancellationToken);
            await _jobs.CompleteAsync(job.JobId, JobState.Completed, "Persisted", cancellationToken);
        }
        catch (MediaToolException ex) { await _library.FailAnalysisAsync(analysis.AnalysisId, ex.Error.Code, ex.Error.UserMessage, cancellationToken); throw; }
        catch (Exception ex) { await _library.FailAnalysisAsync(analysis.AnalysisId, "UNEXPECTED_ERROR", L.T("メタデータ解析に失敗しました。"), cancellationToken); throw new InvalidOperationException("Metadata processing failed.", ex); }
    }

    private async Task ProcessThumbnailAsync(Job job, CancellationToken cancellationToken)
    {
        var media = await GetPresentMediaAsync(job, cancellationToken);
        var config = new { schema = 1, seek = "00:00:01", maxWidth = 480, format = "jpeg" };
        var configHash = ThumbnailConfigHash();
        var inputIdentity = InputIdentity(media);
        var reusable = await _library.FindCompletedAnalysisAsync(media.MediaContentId, "Thumbnail", "ffmpeg", "thumbnail-v1", configHash, "thumbnail-v1", cancellationToken);
        if (reusable is not null && IsUsableThumbnail(await _library.GetCompletedThumbnailArtifactPathAsync(media.MediaContentId, configHash, cancellationToken)))
        { await _jobs.CompleteAsync(job.JobId, JobState.Completed, "Reused", cancellationToken); return; }
        var analysis = await _library.StartAnalysisAsync(media.MediaContentId, "Thumbnail", "ffmpeg", "thumbnail-v1", JsonSerializer.Serialize(config), configHash, inputIdentity, "thumbnail-v1", cancellationToken);
        try
        {
            var cacheKey = AnalysisKey.ArtifactCacheKey(inputIdentity, "thumbnail", "thumbnail-v1", "ffmpeg-thumbnail-v1", configHash);
            await _jobs.HeartbeatAsync(job.JobId, "Decoding", cancellationToken);
            var generated = await _ffmpeg.GenerateThumbnailAsync(media, cacheKey, cancellationToken);
            await _library.AddArtifactAsync(new Artifact(Guid.NewGuid(), analysis.AnalysisId, "Thumbnail", generated.RelativePath, generated.Sha256, generated.ByteSize, "image/jpeg", ArtifactDurability.RebuildableCache, DateTimeOffset.UtcNow), cancellationToken);
            await _library.CompleteAnalysisAsync(analysis.AnalysisId, AnalysisStatus.Completed, cancellationToken);
            await _jobs.CompleteAsync(job.JobId, JobState.Completed, "Persisted", cancellationToken);
            ThumbnailReady?.Invoke(this, new ThumbnailReadyEventArgs(media.MediaFileId, media.MediaContentId, generated.RelativePath));
        }
        catch (MediaToolException ex) { await _library.FailAnalysisAsync(analysis.AnalysisId, ex.Error.Code, ex.Error.UserMessage, cancellationToken); throw; }
        catch (Exception ex) { await _library.FailAnalysisAsync(analysis.AnalysisId, "ARTIFACT_WRITE_FAILED", L.T("サムネイルの保存に失敗しました。"), cancellationToken); throw new InvalidOperationException("Thumbnail processing failed.", ex); }
    }

    private async Task ProcessFullHashAsync(Job job, CancellationToken cancellationToken)
    {
        var payload = Deserialize<FullHashPayload>(job.PayloadJson);
        var media = await _library.GetMediaFileAsync(payload.MediaFileId, cancellationToken);
        if (media is null || media.PresenceState == PresenceState.Missing)
        {
            await _jobs.CompleteAsync(job.JobId, JobState.Partial, "MediaMissing", cancellationToken);
            return;
        }
        if (media.PresenceState == PresenceState.OfflineRoot)
        {
            await _jobs.CompleteAsync(job.JobId, JobState.Partial, "RootOffline", cancellationToken);
            return;
        }
        if (media.PresenceState != PresenceState.Present)
        {
            await _jobs.CompleteAsync(job.JobId, JobState.Partial, "MediaUnavailable", cancellationToken);
            return;
        }
        if (media.MediaContentId != payload.MediaContentId || media.SizeBytes != payload.ExpectedSizeBytes || media.LastWriteTimeUtc != payload.ExpectedLastWriteTimeUtc)
        {
            await _jobs.CompleteAsync(job.JobId, JobState.Partial, "SourceChangedRequeueAfterScan", cancellationToken);
            _log.Write("Information", "FullHashSourceChangedBeforeRead", new { job.JobId, payload.MediaFileId, payload.MediaContentId, CurrentContent = media.MediaContentId });
            return;
        }
        var reusable = await _library.GetCurrentFullHashEvidenceAsync(media.MediaFileId, media.MediaContentId, "SHA-256", payload.ProcessingVersion, cancellationToken);
        if (reusable is not null)
        {
            await _library.RebuildExactDuplicateGroupsAsync(cancellationToken);
            await _jobs.CompleteAsync(job.JobId, JobState.Completed, "Reused", cancellationToken);
            return;
        }
        var snapshot = await _resourceSnapshots.CaptureAsync(cancellationToken);
        var admission = _resourceAdmission.Evaluate(new ResourceCostEstimate(CpuCost: 20, RamBytesEstimate: 64L * 1024 * 1024, GpuRequired: false, VramBytesEstimate: 0, SequentialReadCost: 95, RandomReadCost: 0, WriteIoCost: 2, DbWriteCost: 10), snapshot);
        _log.Write("Information", "FullHashAdmission", new { JobId = job.JobId, admission.Kind, admission.Reason, admission.Code, Snapshot = snapshot });
        if (admission.Kind == AdmissionDecisionKind.Wait)
        {
            await _jobs.DeferForResourceAsync(job.JobId, admission.Reason, admission.Code, admission.Message, DateTimeOffset.UtcNow.AddSeconds(20), cancellationToken);
            return;
        }
        if (admission.Kind != AdmissionDecisionKind.Admit)
        {
            await _jobs.FailOrRetryAsync(job.JobId, admission.Code, admission.Message, false, cancellationToken);
            return;
        }
        await _jobs.HeartbeatAsync(job.JobId, "HashingSequentially", cancellationToken);
        var startedAt = DateTimeOffset.UtcNow;
        var digest = await FileHash.Sha256Async(media.Path, cancellationToken);
        var afterRead = new FileInfo(media.Path);
        var afterWrite = new DateTimeOffset(afterRead.LastWriteTimeUtc, TimeSpan.Zero);
        if (!afterRead.Exists || afterRead.Length != payload.ExpectedSizeBytes || afterWrite != payload.ExpectedLastWriteTimeUtc)
        {
            await _jobs.CompleteAsync(job.JobId, JobState.Partial, "SourceChangedDuringHash", cancellationToken);
            _log.Write("Information", "FullHashSourceChangedDuringRead", new { job.JobId, payload.MediaFileId, ExpectedBytes = payload.ExpectedSizeBytes, ActualBytes = afterRead.Exists ? afterRead.Length : -1L });
            return;
        }
        var persisted = await _library.RecordFullHashEvidenceAsync(new FileHashEvidence(Guid.NewGuid(), media.MediaFileId, media.MediaContentId, "SHA-256", digest, payload.ExpectedSizeBytes, payload.ExpectedLastWriteTimeUtc, payload.ProcessingVersion, "Verified", null, null, DateTimeOffset.UtcNow), cancellationToken);
        if (!persisted)
        {
            await _jobs.CompleteAsync(job.JobId, JobState.Partial, "SourceChangedBeforePersist", cancellationToken);
            return;
        }
        await _library.RebuildExactDuplicateGroupsAsync(cancellationToken);
        var elapsed = DateTimeOffset.UtcNow - startedAt;
        var megabytesPerSecond = elapsed.TotalSeconds <= 0 ? 0 : payload.ExpectedSizeBytes / 1024d / 1024d / elapsed.TotalSeconds;
        _log.Write("Information", "FullHashVerified", new { JobId = job.JobId, media.MediaFileId, Bytes = payload.ExpectedSizeBytes, WallMilliseconds = elapsed.TotalMilliseconds, MegabytesPerSecond = megabytesPerSecond, Concurrency = 1, Algorithm = "SHA-256" });
        await _jobs.UpdateProgressAsync(job.JobId, "Verified", new JobProgress(payload.ExpectedSizeBytes, payload.ExpectedSizeBytes, "bytes", false), cancellationToken);
        await _jobs.CompleteAsync(job.JobId, JobState.Completed, "VerifiedAndGrouped", cancellationToken);
    }

    private async Task ProcessNearDuplicateFingerprintAsync(Job job, CancellationToken cancellationToken)
    {
        var payload = Deserialize<NearDuplicateFingerprintPayload>(job.PayloadJson);
        var media = await _library.GetMediaFileAsync(payload.MediaFileId, cancellationToken);
        if (media is null || media.PresenceState == PresenceState.Missing)
        {
            await _jobs.CompleteAsync(job.JobId, JobState.Partial, "MediaMissing", cancellationToken);
            return;
        }
        if (media.PresenceState == PresenceState.OfflineRoot)
        {
            await _jobs.CompleteAsync(job.JobId, JobState.Partial, "RootOffline", cancellationToken);
            return;
        }
        if (media.PresenceState != PresenceState.Present)
        {
            await _jobs.CompleteAsync(job.JobId, JobState.Partial, "MediaUnavailable", cancellationToken);
            return;
        }
        if (media.MediaContentId != payload.MediaContentId || media.SizeBytes != payload.ExpectedSizeBytes || media.LastWriteTimeUtc != payload.ExpectedLastWriteTimeUtc)
        {
            await _jobs.CompleteAsync(job.JobId, JobState.Partial, "SourceChangedRequeueAfterScan", cancellationToken);
            return;
        }

        var existing = await _library.GetCurrentNearDuplicateFramesAsync(media.MediaFileId, media.MediaContentId, payload.SamplingVersion, payload.FingerprintAlgorithm, cancellationToken);
        var timestamps = NearDuplicateSamplingPolicy.CreateTimestamps((await _library.GetMediaListItemAsync(media.MediaFileId, cancellationToken))?.Metadata?.DurationSeconds);
        if (timestamps.Count != NearDuplicateVerificationProfile.HighPrecisionV1.RequiredSampleCount)
        {
            await _jobs.CompleteAsync(job.JobId, JobState.Partial, "InsufficientDurationEvidence", cancellationToken);
            return;
        }
        if (existing.Count == timestamps.Count)
        {
            await PrepareNearDuplicateFramesAsync(media.MediaFileId, 1, cancellationToken);
            await VerifyNearDuplicateCandidatesAsync(null, payload.SamplingVersion, payload.FingerprintAlgorithm, cancellationToken);
            await _jobs.CompleteAsync(job.JobId, JobState.Completed, "Reused", cancellationToken);
            return;
        }

        var snapshot = await _resourceSnapshots.CaptureAsync(cancellationToken);
        var admission = _resourceAdmission.Evaluate(new ResourceCostEstimate(CpuCost: 35, RamBytesEstimate: 128L * 1024 * 1024, GpuRequired: false, VramBytesEstimate: 0, SequentialReadCost: 90, RandomReadCost: 5, WriteIoCost: 10, DbWriteCost: 20), snapshot);
        _log.Write("Information", "NearDuplicateFingerprintAdmission", new { JobId = job.JobId, admission.Kind, admission.Reason, admission.Code, Snapshot = snapshot });
        if (admission.Kind == AdmissionDecisionKind.Wait)
        {
            await _jobs.DeferForResourceAsync(job.JobId, admission.Reason, admission.Code, admission.Message, DateTimeOffset.UtcNow.AddSeconds(20), cancellationToken);
            return;
        }
        if (admission.Kind != AdmissionDecisionKind.Admit)
        {
            await _jobs.FailOrRetryAsync(job.JobId, admission.Code, admission.Message, false, cancellationToken);
            return;
        }

        var completedIndexes = existing.Select(frame => frame.SampleIndex).ToHashSet();
        var selected = payload.SampleIndexes is null
            ? timestamps.Select(item => item.SampleIndex).Where(index => !completedIndexes.Contains(index)).ToHashSet()
            : payload.SampleIndexes.ToHashSet();
        var inputIdentity = InputIdentity(media);
        var generatedFrames = new List<NearDuplicateFrame>();
        var failedIndexes = new List<int>();
        var now = DateTimeOffset.UtcNow;
        foreach (var timestamp in timestamps.Where(item => selected.Contains(item.SampleIndex)))
        {
            var frameConfigHash = AnalysisKey.ConfigHash(new { timestamp.SampleIndex, timestamp.NormalizedPosition, timestamp.RequestedTimestampSeconds, payload.SamplingVersion, payload.FingerprintAlgorithm });
            var cacheKey = AnalysisKey.ArtifactCacheKey(inputIdentity, "near-duplicate-frame", payload.SamplingVersion + ":" + payload.FingerprintAlgorithm, "ffmpeg-near-duplicate-v1", frameConfigHash);
            try
            {
                await _jobs.HeartbeatAsync(job.JobId, "ExtractingNearDuplicateFrame", cancellationToken);
                var generated = await _ffmpeg.ExtractNearDuplicateFrameAsync(media, timestamp.SampleIndex, timestamp.NormalizedPosition, timestamp.RequestedTimestampSeconds, cacheKey, cancellationToken);
                generatedFrames.Add(new NearDuplicateFrame(Guid.NewGuid(), media.MediaFileId, media.MediaContentId, payload.ExpectedSizeBytes, payload.ExpectedLastWriteTimeUtc, timestamp.SampleIndex, timestamp.NormalizedPosition, timestamp.RequestedTimestampSeconds, generated.PresentationTimestampSeconds, payload.SamplingVersion, payload.FingerprintAlgorithm, generated.FingerprintHex, generated.RelativePath, generated.Sha256, generated.ByteSize, AnalysisStatus.Completed, null, null, now, DateTimeOffset.UtcNow));
            }
            catch (MediaToolException ex)
            {
                failedIndexes.Add(timestamp.SampleIndex);
                generatedFrames.Add(new NearDuplicateFrame(Guid.NewGuid(), media.MediaFileId, media.MediaContentId, payload.ExpectedSizeBytes, payload.ExpectedLastWriteTimeUtc, timestamp.SampleIndex, timestamp.NormalizedPosition, timestamp.RequestedTimestampSeconds, timestamp.RequestedTimestampSeconds, payload.SamplingVersion, payload.FingerprintAlgorithm, null, null, null, null, AnalysisStatus.Failed, ex.Error.Code, ex.Error.UserMessage, now, DateTimeOffset.UtcNow));
                _log.Write("Warning", "NearDuplicateFrameFailed", new { JobId = job.JobId, media.MediaFileId, timestamp.SampleIndex, ex.Error.Code, ex.Error.Diagnostic });
            }
            await _jobs.UpdateProgressAsync(job.JobId, "ExtractingNearDuplicateFrames", new JobProgress(timestamp.SampleIndex + 1, timestamps.Count, "frames", false), cancellationToken);
        }

        var afterExtraction = await _library.GetMediaFileAsync(media.MediaFileId, cancellationToken);
        if (afterExtraction is null || afterExtraction.PresenceState != PresenceState.Present || afterExtraction.MediaContentId != payload.MediaContentId || afterExtraction.SizeBytes != payload.ExpectedSizeBytes || afterExtraction.LastWriteTimeUtc != payload.ExpectedLastWriteTimeUtc)
        {
            await _jobs.CompleteAsync(job.JobId, JobState.Partial, "SourceChangedDuringFrameExtraction", cancellationToken);
            return;
        }
        if (generatedFrames.Count > 0)
            await _library.ReplaceNearDuplicateFramesAsync(media.MediaFileId, media.MediaContentId, payload.ExpectedSizeBytes, payload.ExpectedLastWriteTimeUtc, payload.SamplingVersion, payload.FingerprintAlgorithm, generatedFrames, cancellationToken);

        var current = await _library.GetCurrentNearDuplicateFramesAsync(media.MediaFileId, media.MediaContentId, payload.SamplingVersion, payload.FingerprintAlgorithm, cancellationToken);
        if (current.Count == timestamps.Count)
        {
            await PrepareNearDuplicateFramesAsync(media.MediaFileId, 1, cancellationToken);
            await VerifyNearDuplicateCandidatesAsync(null, payload.SamplingVersion, payload.FingerprintAlgorithm, cancellationToken);
            await _jobs.CompleteAsync(job.JobId, failedIndexes.Count == 0 ? JobState.Completed : JobState.Partial, failedIndexes.Count == 0 ? "PersistedAndVerified" : "PartialRecovered", cancellationToken);
            return;
        }
        if (failedIndexes.Count > 0 && !payload.IsRetry)
            await _sqliteJobs.EnqueueForActiveMediaAsync(JobType.NearDuplicateFingerprint, new NearDuplicateFingerprintPayload(media.MediaFileId, media.MediaContentId, media.SizeBytes, media.LastWriteTimeUtc, payload.SamplingVersion, payload.FingerprintAlgorithm, true, failedIndexes), media.MediaFileId, 1, DateTimeOffset.UtcNow.AddSeconds(5), unique: true, cancellationToken);
        await _jobs.CompleteAsync(job.JobId, JobState.Partial, "InsufficientEvidence", cancellationToken);
    }

    private async Task VerifyNearDuplicateCandidatesAsync(Guid? rootId, string samplingVersion, string fingerprintAlgorithm, CancellationToken cancellationToken)
    {
        var profile = NearDuplicateVerificationProfile.HighPrecisionV1;
        await PrepareNearDuplicateFramesAsync(null, PreparationBatchSize, cancellationToken);
        var candidates = (await _library.GetNearDuplicateCandidatesAsync(rootId, samplingVersion, fingerprintAlgorithm, cancellationToken))
            .Concat(await _library.GetPreparedNearDuplicateCandidatesAsync(rootId, samplingVersion, fingerprintAlgorithm, NearDuplicatePreparation.Version, cancellationToken))
            .DistinctBy(candidate => (candidate.MediaFileAId, candidate.MediaFileBId))
            .ToArray();
        var publishedVerifiedEvidence = false;
        foreach (var candidate in candidates)
        {
            var reusable = await _library.GetNearDuplicateEvidenceAsync(candidate.MediaFileAId, candidate.MediaContentAId, candidate.MediaFileBId, candidate.MediaContentBId, samplingVersion, fingerprintAlgorithm, profile.ThresholdVersion, cancellationToken);
            if (reusable is not null) continue;
            var (evidence, matches) = await JudgeNearDuplicatePairAsync(candidate, samplingVersion, fingerprintAlgorithm, cancellationToken);
            var persisted = await _library.UpsertNearDuplicateEvidenceAsync(evidence, matches, cancellationToken);
            publishedVerifiedEvidence |= persisted && evidence.Status == "Verified";
        }
        if (publishedVerifiedEvidence) await _library.RebuildNearDuplicateGroupsAsync(cancellationToken);
    }

    /// <summary>
    /// Compares the five stored frames of a pair: as stored and, when that is not similar, with black bars cut off
    /// (also the middle of one with the whole of the other). Returns the evidence to keep, not yet persisted.
    /// </summary>
    private async Task<(NearDuplicateEvidence Evidence, NearDuplicateFrameMatch[] Matches)> JudgeNearDuplicatePairAsync(NearDuplicateCandidate candidate, string samplingVersion, string fingerprintAlgorithm, CancellationToken cancellationToken)
    {
        var profile = NearDuplicateVerificationProfile.HighPrecisionV1;
        var framesA = await _library.GetCurrentNearDuplicateFramesAsync(candidate.MediaFileAId, candidate.MediaContentAId, samplingVersion, fingerprintAlgorithm, cancellationToken);
        var framesB = await _library.GetCurrentNearDuplicateFramesAsync(candidate.MediaFileBId, candidate.MediaContentBId, samplingVersion, fingerprintAlgorithm, cancellationToken);
        var pairs = framesA.Join(framesB, left => left.SampleIndex, right => right.SampleIndex, (left, right) => (left, right)).OrderBy(item => item.left.SampleIndex).ToArray();
        var distances = pairs.Select(item => (item.left.SampleIndex, PerceptualFrameFingerprint.HammingDistance(item.left.FingerprintHex!, item.right.FingerprintHex!))).ToArray();
        var flat = new List<int>();
        foreach (var (left, right) in pairs)
            if (await AreBothFlatAsync(left, right, cancellationToken)) flat.Add(left.SampleIndex);
        var verification = profile.Verify(candidate.DurationASeconds, candidate.DurationBSeconds, distances, flat);
        if (!verification.IsVerified)
        {
            var preparedA = await _library.GetCurrentPreparedNearDuplicateFramesAsync(candidate.MediaFileAId, candidate.MediaContentAId, samplingVersion, fingerprintAlgorithm, NearDuplicatePreparation.Version, cancellationToken);
            var preparedB = await _library.GetCurrentPreparedNearDuplicateFramesAsync(candidate.MediaFileBId, candidate.MediaContentBId, samplingVersion, fingerprintAlgorithm, NearDuplicatePreparation.Version, cancellationToken);
            if (preparedA.Count == profile.RequiredSampleCount && preparedB.Count == profile.RequiredSampleCount)
            {
                var (preparedDistances, preparedFlat) = NearDuplicatePreparation.Compare(preparedA, preparedB);
                var prepared = profile.Verify(candidate.DurationASeconds, candidate.DurationBSeconds, preparedDistances, preparedFlat);
                if (prepared.IsVerified)
                {
                    verification = prepared;
                    distances = preparedDistances.ToArray();
                }
            }
        }
        var evidenceId = Guid.NewGuid();
        var distanceBySample = distances.ToDictionary(item => item.Item1, item => item.Item2);
        var matches = pairs.Select(item =>
        {
            var distance = distanceBySample[item.left.SampleIndex];
            return new NearDuplicateFrameMatch(evidenceId, item.left.SampleIndex, item.left.NearDuplicateFrameId, item.right.NearDuplicateFrameId, distance, distance <= profile.MaximumFrameDistance);
        }).ToArray();
        var digest = AnalysisKey.Sha256(AnalysisKey.CanonicalJson(new { relation = "NearDuplicate", candidate.MediaFileAId, candidate.MediaContentAId, candidate.MediaFileBId, candidate.MediaContentBId, fingerprintAlgorithm, samplingVersion, profile.ThresholdVersion }));
        var evidence = new NearDuplicateEvidence(evidenceId, candidate.MediaFileAId, candidate.MediaContentAId, candidate.MediaFileBId, candidate.MediaContentBId, fingerprintAlgorithm, samplingVersion, profile.ThresholdVersion, digest, candidate.DurationASeconds, candidate.DurationBSeconds, verification.DurationDifferenceRatio, verification.ComparedSampleCount, verification.MatchedSampleCount, verification.LongestConsecutiveMatchCount, verification.MeanHammingDistance, verification.MaxHammingDistance, verification.VisualMatchScore, verification.Status, verification.Status == "InsufficientEvidence" ? "NEAR_DUPLICATE_SAMPLE_COVERAGE" : null, verification.Status == "InsufficientEvidence" ? L.T("近似重複の判定に必要な複数時点の視覚証拠が不足しています。") : null, DateTimeOffset.UtcNow);
        return (evidence, matches);
    }

    /// <summary>Videos prepared per verification pass, so one pass never waits long on preparation.</summary>
    private const int PreparationBatchSize = 16;

    /// <summary>
    /// Prepares every video fingerprinted before black bars were cut off, at the end of a scan. It reads only the stored
    /// frame images, never a video, so it runs here rather than as source work (source jobs are handed to protected
    /// orchestration, which has nothing to extract for videos that already have their frames).
    /// </summary>
    private async Task CatchUpNearDuplicatePreparationAsync(Job job, CancellationToken cancellationToken)
    {
        const int Batch = 64;
        var pending = (await _library.GetPendingNearDuplicatePreparationsAsync(null, null, NearDuplicateSamplingPolicy.Version, PerceptualFrameFingerprint.Algorithm, NearDuplicatePreparation.Version, -1, cancellationToken)).Count;
        if (pending == 0)
        {
            await RecheckRejectedNearDuplicatesAsync(cancellationToken);
            return;
        }
        // A video whose frames change meanwhile can stay pending; the bound keeps this from going round forever.
        for (var round = 0; round <= pending / Batch; round++)
        {
            await _jobs.HeartbeatAsync(job.JobId, "PreparingNearDuplicateFrames", cancellationToken);
            if (await PrepareNearDuplicateFramesAsync(null, Batch, cancellationToken) < Batch) break;
        }
        _log.Write("Information", "NearDuplicatePreparationCaughtUp", new { JobId = job.JobId, Videos = pending });
        await RefreshLibraryWideAsync(LibraryWideRefresh.NearDuplicate, cancellationToken);
        await RecheckRejectedNearDuplicatesAsync(cancellationToken);
    }

    /// <summary>
    /// Cuts black bars off the stored frames of videos that have no current preparation (see <see cref="NearDuplicatePreparation"/>):
    /// <paramref name="mediaFileId"/> alone, or else the most recently fingerprinted ones. Reads only the cached frame images,
    /// never the video. Returns how many videos needed it.
    /// </summary>
    private async Task<int> PrepareNearDuplicateFramesAsync(Guid? mediaFileId, int limit, CancellationToken cancellationToken)
    {
        var pending = await _library.GetPendingNearDuplicatePreparationsAsync(null, mediaFileId, NearDuplicateSamplingPolicy.Version, PerceptualFrameFingerprint.Algorithm, NearDuplicatePreparation.Version, limit, cancellationToken);
        foreach (var item in pending)
        {
            var frames = await _library.GetCurrentNearDuplicateFramesAsync(item.MediaFileId, item.MediaContentId, NearDuplicateSamplingPolicy.Version, PerceptualFrameFingerprint.Algorithm, cancellationToken);
            if (frames.Count != NearDuplicateVerificationProfile.HighPrecisionV1.RequiredSampleCount) continue;
            var gray = await _ffmpeg.ReadStoredFramesGrayAsync(frames.Select(frame => frame.FrameRelativePath).ToArray(), NearDuplicatePreparation.Width, NearDuplicatePreparation.Height, cancellationToken);
            if (gray is null)
            {
                // Unreadable stored images: the video keeps the earlier comparison until its frames are taken again.
                await _library.SaveNearDuplicatePreparationAsync(item.MediaFileId, item.MediaContentId, NearDuplicateSamplingPolicy.Version, PerceptualFrameFingerprint.Algorithm, NearDuplicatePreparation.Version, item.FramesVersion, FrameCrop.None, [], cancellationToken);
                _log.Write("Warning", "NearDuplicatePreparationUnavailable", new { item.MediaFileId });
                continue;
            }
            var prepared = NearDuplicatePreparation.Prepare(frames.Select((frame, index) => (frame.SampleIndex, gray[index])).ToArray());
            await _library.SaveNearDuplicatePreparationAsync(item.MediaFileId, item.MediaContentId, NearDuplicateSamplingPolicy.Version, PerceptualFrameFingerprint.Algorithm, NearDuplicatePreparation.Version, item.FramesVersion,
                NearDuplicatePreparation.Bars(gray), frames.Zip(prepared, (frame, value) => (frame.NearDuplicateFrameId, value)).ToArray(), cancellationToken);
        }
        return pending.Count;
    }

    /// <summary>
    /// Both frames are black or flat (a fade, a black intro), so the pair says nothing either way. Only frames whose
    /// fingerprint has few edges are opened, and only their small cached images; a smooth gradient such as a sky is not flat.
    /// </summary>
    private async Task<bool> AreBothFlatAsync(NearDuplicateFrame left, NearDuplicateFrame right, CancellationToken cancellationToken)
    {
        if (!PerceptualFrameFingerprint.HasFewEdges(left.FingerprintHex!) || !PerceptualFrameFingerprint.HasFewEdges(right.FingerprintHex!)) return false;
        if (left.FrameRelativePath is null || right.FrameRelativePath is null) return false;
        var leftGray = await _ffmpeg.ReadStoredFrameGrayAsync(left.FrameRelativePath, cancellationToken);
        if (leftGray is null || !PerceptualFrameFingerprint.IsFlat(leftGray)) return false;
        var rightGray = await _ffmpeg.ReadStoredFrameGrayAsync(right.FrameRelativePath, cancellationToken);
        return rightGray is not null && PerceptualFrameFingerprint.IsFlat(rightGray);
    }

    private async Task ProcessVisualSamplingAsync(Job job, CancellationToken cancellationToken)
    {
        var payload = Deserialize<VisualSamplingPayload>(job.PayloadJson);
        var media = await GetPresentMediaAsync(payload.MediaFileId, cancellationToken);
        var listItem = await _library.GetMediaListItemAsync(media.MediaFileId, cancellationToken);
        var policy = _visualSamplingPolicy;
        var timestamps = policy.CreateTimestamps(listItem?.Metadata?.DurationSeconds);
        var selected = payload.SampleIndexes is null ? timestamps.Select((_, index) => index).ToHashSet() : payload.SampleIndexes.ToHashSet();
        var inputIdentity = InputIdentity(media);
        var config = new { schema = 1, sampling = policy.Version, policy.IntervalSeconds, policy.MinimumSamples, policy.MaximumSamples, policy.EndMarginSeconds };
        var configHash = AnalysisKey.ConfigHash(config);
        var reusable = await _library.FindCompletedAnalysisAsync(media.MediaContentId, "VisualSampling", "ffmpeg", "ffmpeg-frame-v1", configHash, "visual-sampling-v1", cancellationToken);
        if (reusable is not null && !payload.IsRetry)
        {
            await _jobs.CompleteAsync(job.JobId, JobState.Completed, "Reused", cancellationToken);
            return;
        }
        var analysis = await _library.StartAnalysisAsync(media.MediaContentId, "VisualSampling", "ffmpeg", "ffmpeg-frame-v1", JsonSerializer.Serialize(config), configHash, inputIdentity, "visual-sampling-v1", cancellationToken);
        var samples = new List<VisualSample>();
        var failedIndexes = new List<int>();
        var now = DateTimeOffset.UtcNow;
        try
        {
            for (var index = 0; index < timestamps.Count; index++)
            {
                if (!selected.Contains(index)) continue;
                var requested = timestamps[index];
                var frameConfigHash = AnalysisKey.ConfigHash(new { requestedTimestampSeconds = requested, sampling = policy.Version });
                var cacheKey = AnalysisKey.ArtifactCacheKey(inputIdentity, "visual-frame", "visual-sampling-v1", "ffmpeg-frame-v1", frameConfigHash);
                try
                {
                    await _jobs.HeartbeatAsync(job.JobId, "ExtractingFrame", cancellationToken);
                    var generated = await _ffmpeg.ExtractFrameAsync(media, requested, cacheKey, cancellationToken);
                    samples.Add(new VisualSample(Guid.NewGuid(), media.MediaFileId, media.MediaContentId, inputIdentity, index, requested, generated.PresentationTimestampSeconds, policy.Version, cacheKey, generated.RelativePath, generated.Sha256, generated.ByteSize, VisualSampleStatus.Completed, null, null, now, DateTimeOffset.UtcNow));
                }
                catch (MediaToolException ex)
                {
                    failedIndexes.Add(index);
                    samples.Add(new VisualSample(Guid.NewGuid(), media.MediaFileId, media.MediaContentId, inputIdentity, index, requested, requested, policy.Version, cacheKey, null, null, null, VisualSampleStatus.Failed, ex.Error.Code, ex.Error.UserMessage, now, DateTimeOffset.UtcNow));
                    _log.Write("Warning", "VisualSampleFailed", new { JobId = job.JobId, MediaFileId = media.MediaFileId, SampleIndex = index, requested, ErrorCode = ex.Error.Code, ex.Error.Diagnostic });
                }
                await _jobs.UpdateProgressAsync(job.JobId, "ExtractingFrames", new JobProgress(index + 1, timestamps.Count, "frames", false), cancellationToken);
            }
            if (samples.Count == 0) throw new MediaToolException(new DomainError("NO_VISUAL_SAMPLES", L.T("選択された検索用フレームがありません。")));
            await _library.ReplaceVisualSamplesAsync(media.MediaFileId, media.MediaContentId, inputIdentity, policy.Version, samples, cancellationToken);
            var completed = samples.Count(sample => sample.Status == VisualSampleStatus.Completed);
            if (completed == 0)
            {
                await _library.FailAnalysisAsync(analysis.AnalysisId, "ALL_VISUAL_SAMPLES_FAILED", L.T("検索用フレームを抽出できませんでした。"), cancellationToken);
                await _jobs.CompleteAsync(job.JobId, JobState.Failed, "NoFramesPersisted", cancellationToken);
                return;
            }
            var isPartial = failedIndexes.Count > 0;
            await _library.CompleteAnalysisAsync(analysis.AnalysisId, isPartial ? AnalysisStatus.Partial : AnalysisStatus.Completed, cancellationToken);
            await _jobs.CompleteAsync(job.JobId, isPartial ? JobState.Partial : JobState.Completed, isPartial ? "PartialPersisted" : "Persisted", cancellationToken);
            if (_worker is not null && _paths is not null)
            {
                var persistedSamples = await _library.GetVisualSamplesAsync(media.MediaFileId, inputIdentity, policy.Version, cancellationToken);
                foreach (var sample in persistedSamples.Where(sample => selected.Contains(sample.SampleIndex) && sample.Status == VisualSampleStatus.Completed))
                    await _sqliteJobs.EnqueueForActiveMediaAsync(JobType.VisualEmbedding, new VisualEmbeddingPayload(sample.VisualSampleId), media.MediaFileId, 2, null, unique: false, cancellationToken);
            }
            if (isPartial && !payload.IsRetry)
                await _sqliteJobs.EnqueueForActiveMediaAsync(JobType.VisualSampling, new VisualSamplingPayload(media.MediaFileId, failedIndexes, true), media.MediaFileId, 1, DateTimeOffset.UtcNow.AddSeconds(5), unique: false, cancellationToken);
        }
        catch (MediaToolException ex)
        {
            await _library.FailAnalysisAsync(analysis.AnalysisId, ex.Error.Code, ex.Error.UserMessage, cancellationToken);
            throw;
        }
        catch (Exception ex)
        {
            await _library.FailAnalysisAsync(analysis.AnalysisId, "VISUAL_SAMPLING_FAILED", L.T("検索用フレームの抽出に失敗しました。"), cancellationToken);
            throw new InvalidOperationException("Visual sampling failed.", ex);
        }
    }

    private readonly SemaphoreSlim _provenanceGate = new(1, 1);
    private string? _provenanceRecordedFor;

    /// <summary>Hashes and records the model provenance once per session and model file instead of once per frame.</summary>
    private async Task EnsureModelProvenanceRecordedAsync(string provenancePath, string modelName, string modelRevision, string processorVersion, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var identity = string.Join("|", provenancePath, modelName, modelRevision, processorVersion, File.GetLastWriteTimeUtc(provenancePath).Ticks);
        await _provenanceGate.WaitAsync(cancellationToken);
        try
        {
            if (_provenanceRecordedFor == identity) return;
            await _library.UpsertModelProvenanceAsync(new ModelProvenance(modelName, modelRevision, processorVersion, await FileHash.Sha256Async(provenancePath, cancellationToken), "Apache-2.0", false, now), cancellationToken);
            _provenanceRecordedFor = identity;
        }
        finally
        {
            _provenanceGate.Release();
        }
    }

    private async Task ProcessVisualEmbeddingAsync(Job job, CancellationToken cancellationToken)
    {
        var payload = Deserialize<VisualEmbeddingPayload>(job.PayloadJson);
        var sample = await _sqliteLibrary.GetVisualSampleRecordAsync(payload.VisualSampleId, cancellationToken);
        if (sample is null)
        {
            // A durable Job can outlive the row named by an old payload. Retrying cannot
            // recreate that identity, so retire the orphan without presenting it as a
            // current-media analysis failure.
            await _jobs.CompleteAsync(job.JobId, JobState.Partial, "OrphanedVisualSample", cancellationToken);
            _log.Write("Information", "VisualEmbeddingRetired", new { JobId = job.JobId, payload.VisualSampleId, Reason = "OrphanedVisualSample" });
            return;
        }

        var media = await _library.GetMediaFileAsync(sample.MediaFileId, cancellationToken);
        if (media is null || media.PresenceState == PresenceState.Missing)
        {
            await _jobs.CompleteAsync(job.JobId, JobState.Partial, "MediaUnavailable", cancellationToken);
            _log.Write("Information", "VisualEmbeddingRetired", new { JobId = job.JobId, sample.VisualSampleId, Reason = "MediaUnavailable" });
            return;
        }
        if (media.PresenceState == PresenceState.OfflineRoot)
        {
            await _jobs.FailOrRetryAsync(job.JobId, "ROOT_OFFLINE", L.T("登録フォルダを確認できないため、AI検索の準備を後で再試行します。"), true, cancellationToken);
            return;
        }
        if (media.MediaContentId != sample.MediaContentId)
        {
            await _jobs.CompleteAsync(job.JobId, JobState.Partial, "SupersededSourceVersion", cancellationToken);
            _log.Write("Information", "VisualEmbeddingRetired", new { JobId = job.JobId, sample.VisualSampleId, Reason = "SupersededSourceVersion" });
            return;
        }
        if (sample.Status != VisualSampleStatus.Completed || string.IsNullOrWhiteSpace(sample.FrameRelativePath))
            throw new MediaToolException(new DomainError("VISUAL_SAMPLE_UNAVAILABLE", L.T("検索用フレームは現在利用できません。")));
        if (_worker is null || _paths is null)
            throw new InvalidOperationException("Visual embedding Worker is not configured.");
        var snapshot = await _resourceSnapshots.CaptureAsync(cancellationToken);
        var admission = _resourceAdmission.Evaluate(new ResourceCostEstimate(CpuCost: _semanticExecution.IsCpuFallback ? 70 : 20, RamBytesEstimate: 4L * 1024 * 1024 * 1024, GpuRequired: !_semanticExecution.IsCpuFallback, VramBytesEstimate: _semanticExecution.IsCpuFallback ? 0 : 3L * 1024 * 1024 * 1024, SequentialReadCost: 10, RandomReadCost: 5, WriteIoCost: 5, DbWriteCost: 10), snapshot);
        _log.Write("Information", "VisualEmbeddingAdmission", new { JobId = job.JobId, admission.Kind, admission.Reason, admission.Code, Snapshot = snapshot });
        if (admission.Kind == AdmissionDecisionKind.Wait)
        {
            await _jobs.DeferForResourceAsync(job.JobId, admission.Reason, admission.Code, admission.Message, DateTimeOffset.UtcNow.AddSeconds(20), cancellationToken);
            return;
        }
        if (admission.Kind != AdmissionDecisionKind.Admit)
        {
            await _jobs.FailOrRetryAsync(job.JobId, admission.Code, admission.Message, false, cancellationToken);
            return;
        }
        var modelName = _semanticModel.ModelId;
        var modelRevision = _semanticModel.ModelRevision;
        var processorVersion = _semanticModel.ProcessorVersion;
        var dtype = _semanticExecution.Dtype;
        var cacheKey = AnalysisKey.VisualEmbeddingCacheKey(sample.InputIdentityHash, sample.RequestedTimestampSeconds, sample.PresentationTimestampSeconds, modelName, modelRevision, _semanticModel.Adapter, processorVersion, sample.SamplingVersion, dtype, _semanticModel.Dimension);
        var reusable = await _library.FindCompletedVisualEmbeddingAsync(cacheKey, cancellationToken);
        if (reusable is not null)
        {
            await _jobs.CompleteAsync(job.JobId, JobState.Completed, "Reused", cancellationToken);
            return;
        }
        var now = DateTimeOffset.UtcNow;
        var modelRoot = ResolveModelDirectory(_semanticModel.ModelDirectoryName);
        var adapterRoot = string.IsNullOrWhiteSpace(_semanticModel.AdapterDirectoryName) ? null : ResolveModelDirectory(_semanticModel.AdapterDirectoryName);
        var provenancePath = Path.Combine(modelRoot, "provenance.json");
        if (!File.Exists(provenancePath)) throw new MediaToolException(new DomainError("MODEL_PROVENANCE_MISSING", L.T("ローカルAIモデルのprovenanceが見つかりません。")));
        await EnsureModelProvenanceRecordedAsync(provenancePath, modelName, modelRevision, processorVersion, now, cancellationToken);
        try
        {
            var imagePath = _ffmpeg.ResolveVisualSamplePath(sample.FrameRelativePath);
            var descriptors = new Dictionary<string, string>
            {
                ["input_root"] = _paths.CacheRoot,
                ["image_path"] = imagePath,
                ["models_root"] = _paths.ModelsRoot,
                ["model_root"] = modelRoot
            };
            if (adapterRoot is not null) descriptors["adapter_root"] = adapterRoot;
            var request = new WorkerJobRequest(job.JobId, "visual-embedding-v1", descriptors, JsonSerializer.Serialize(new { model_id = modelName, revision = modelRevision, adapter = _semanticModel.Adapter, backend = _semanticExecution.Backend, dtype }), processorVersion, string.Empty);
            var progress = new Progress<WorkerJobProgress>(update => _log.Write("Information", "VisualEmbeddingWorkerProgress", new { JobId = job.JobId, update.Phase, update.Current, update.Total, update.Unit, update.Metrics }));
            var result = await _worker.RunAsync(request, progress, cancellationToken);
            if (!string.Equals(result.Outcome, "completed", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(result.StructuredResultJson))
                throw new WorkerEmbeddingException(result.ErrorCode ?? "EMBEDDING_FAILED", result.ErrorMessage ?? L.T("AI特徴量の生成に失敗しました。"));
            var parsed = ParseWorkerEmbedding(result.StructuredResultJson);
            if (parsed.ModelName != modelName || parsed.ModelRevision != modelRevision || parsed.ProcessorVersion != processorVersion || parsed.Dtype != "float32" || parsed.Dimension != _semanticModel.Dimension || !parsed.Normalized)
                throw new WorkerEmbeddingException("EMBEDDING_RESULT_INVALID", L.T("AI特徴量の結果契約が一致しません。"));
            var vector = Convert.FromBase64String(parsed.VectorFloat32Base64);
            if (parsed.Dimension <= 0 || vector.Length != parsed.Dimension * sizeof(float))
                throw new WorkerEmbeddingException("EMBEDDING_VECTOR_INVALID", L.T("AI特徴量ベクトルの形式が不正です。"));
            await _library.UpsertVisualEmbeddingAsync(new VisualEmbedding(Guid.NewGuid(), sample.VisualSampleId, sample.InputIdentityHash, cacheKey, modelName, modelRevision, processorVersion, sample.SamplingVersion, dtype, parsed.Dimension, true, vector, VisualEmbeddingStatus.Completed, null, null, now, DateTimeOffset.UtcNow), cancellationToken);
            // This CPU-only derived representation never invokes a second AI model. Its source is the
            // just-persisted Phase 3/4 visual embedding inventory, and EnqueueUnique preserves recovery idempotency.
            if (string.Equals(sample.SamplingVersion, VisualSamplingPolicy.FixedIntervalV1.Version, StringComparison.Ordinal))
            {
                var sourceMedia = await _library.GetMediaFileAsync(sample.MediaFileId, cancellationToken);
                if (sourceMedia is { PresenceState: PresenceState.Present } && sourceMedia.MediaContentId == sample.MediaContentId)
                {
                    await _sqliteJobs.EnqueueForActiveMediaAsync(JobType.ContentSimilarityRepresentation, new ContentSimilarityRepresentationPayload(sourceMedia.MediaFileId, sourceMedia.MediaContentId, sourceMedia.SizeBytes, sourceMedia.LastWriteTimeUtc), sourceMedia.MediaFileId, 2, null, unique: true, cancellationToken);
                    await _sqliteJobs.EnqueueForActiveMediaAsync(JobType.TagSuggestion, new TagSuggestionPayload(sourceMedia.MediaFileId, sourceMedia.MediaContentId, sourceMedia.SizeBytes, sourceMedia.LastWriteTimeUtc), sourceMedia.MediaFileId, 2, null, unique: true, cancellationToken);
                }
            }
            await _jobs.CompleteAsync(job.JobId, JobState.Completed, "Persisted", cancellationToken);
        }
        catch (WorkerEmbeddingException ex)
        {
            await _library.UpsertVisualEmbeddingAsync(new VisualEmbedding(Guid.NewGuid(), sample.VisualSampleId, sample.InputIdentityHash, cacheKey, modelName, modelRevision, processorVersion, sample.SamplingVersion, dtype, 0, false, null, VisualEmbeddingStatus.Failed, ex.Code, ex.UserMessage, now, DateTimeOffset.UtcNow), cancellationToken);
            throw new MediaToolException(new DomainError(ex.Code, ex.UserMessage));
        }
        catch (WorkerExitedUnexpectedlyException ex)
        {
            var userMessage = L.T("AI Workerが予期せず終了しました。未完了フレームを再試行します。");
            await _library.UpsertVisualEmbeddingAsync(new VisualEmbedding(Guid.NewGuid(), sample.VisualSampleId, sample.InputIdentityHash, cacheKey, modelName, modelRevision, processorVersion, sample.SamplingVersion, dtype, 0, false, null, VisualEmbeddingStatus.Failed, WorkerExitedUnexpectedlyException.ErrorCode, userMessage, now, DateTimeOffset.UtcNow), cancellationToken);
            throw new MediaToolException(new DomainError(WorkerExitedUnexpectedlyException.ErrorCode, userMessage, ex.Message));
        }
        catch (Exception ex) when (ex is not OperationCanceledException && IsWorkerUnavailableAfterEmbeddingFault())
        {
            var userMessage = L.T("AI Workerが予期せず終了しました。未完了フレームを再試行します。");
            await _library.UpsertVisualEmbeddingAsync(new VisualEmbedding(Guid.NewGuid(), sample.VisualSampleId, sample.InputIdentityHash, cacheKey, modelName, modelRevision, processorVersion, sample.SamplingVersion, dtype, 0, false, null, VisualEmbeddingStatus.Failed, WorkerExitedUnexpectedlyException.ErrorCode, userMessage, now, DateTimeOffset.UtcNow), cancellationToken);
            throw new MediaToolException(new DomainError(WorkerExitedUnexpectedlyException.ErrorCode, userMessage, ex.Message));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _library.UpsertVisualEmbeddingAsync(new VisualEmbedding(Guid.NewGuid(), sample.VisualSampleId, sample.InputIdentityHash, cacheKey, modelName, modelRevision, processorVersion, sample.SamplingVersion, dtype, 0, false, null, VisualEmbeddingStatus.Failed, "VISUAL_EMBEDDING_FAILED", L.T("AI特徴量の生成に失敗しました。"), now, DateTimeOffset.UtcNow), cancellationToken);
            throw;
        }
    }

    private async Task ProcessTagSuggestionAsync(Job job, CancellationToken cancellationToken)
    {
        var payload = Deserialize<TagSuggestionPayload>(job.PayloadJson);
        var profile = TaggingVerificationProfile.DescriptiveHighPrecisionV1;
        if (!string.Equals(payload.TaxonomyVersion, TaggingTaxonomy.TaxonomyVersion, StringComparison.Ordinal) ||
            !string.Equals(payload.TextRepresentationVersion, profile.TextRepresentationVersion, StringComparison.Ordinal) ||
            !string.Equals(payload.AggregationVersion, profile.AggregationVersion, StringComparison.Ordinal) ||
            !string.Equals(payload.ThresholdVersion, profile.ThresholdVersion, StringComparison.Ordinal))
            throw new MediaToolException(new DomainError("TAGGING_PROFILE_UNSUPPORTED", L.T("Tagging profileのversionがこのアプリ版と一致しません。")));

        var media = await _library.GetMediaFileAsync(payload.MediaFileId, cancellationToken);
        if (media is null || media.PresenceState == PresenceState.Missing)
        {
            await _jobs.CompleteAsync(job.JobId, JobState.Partial, "MediaMissing", cancellationToken);
            return;
        }
        if (media.PresenceState == PresenceState.OfflineRoot)
        {
            await _jobs.CompleteAsync(job.JobId, JobState.Partial, "RootOffline", cancellationToken);
            return;
        }
        if (media.PresenceState != PresenceState.Present)
        {
            await _jobs.CompleteAsync(job.JobId, JobState.Partial, "MediaUnavailable", cancellationToken);
            return;
        }
        if (media.MediaContentId != payload.MediaContentId || media.SizeBytes != payload.ExpectedSizeBytes || media.LastWriteTimeUtc != payload.ExpectedLastWriteTimeUtc)
        {
            await _jobs.CompleteAsync(job.JobId, JobState.Partial, "SourceChangedRequeueAfterScan", cancellationToken);
            return;
        }

        const string sourceSamplingVersion = "fixed-interval-v1";
        var sources = await _library.GetMediaTagSourceEmbeddingsAsync(media.MediaFileId, media.MediaContentId, _semanticModel.ModelId, _semanticModel.ModelRevision, _semanticModel.ProcessorVersion, sourceSamplingVersion, _semanticExecution.Dtype, _semanticModel.Dimension, cancellationToken);
        var sourceDigest = ContentSimilarityVectors.BuildMeanUnit(sources.CompletedEmbeddings, sources.ExpectedSampleCount).SourceEmbeddingDigest;
        if (sources.ExpectedSampleCount == 0 || sources.CompletedEmbeddings.Count != sources.ExpectedSampleCount)
        {
            foreach (var tag in TaggingTaxonomy.All)
            {
                var evidence = new MediaTagEvidence(Guid.NewGuid(), media.MediaFileId, media.MediaContentId, null, payload.TaxonomyVersion, tag.TagId, tag.DisplayName, tag.Group, MediaTagSource.EmbeddingZeroShot, _semanticModel.ModelId, _semanticModel.ModelRevision, _semanticModel.ProcessorVersion, _semanticExecution.Dtype, _semanticModel.Dimension, sourceSamplingVersion, payload.TextRepresentationVersion, payload.AggregationVersion, payload.ThresholdVersion, TaggingVectors.ScoreMetric, 0d, 0d, 0, sources.CompletedEmbeddings.Count, sources.ExpectedSampleCount, sources.ExpectedSampleCount == 0 ? 0d : sources.CompletedEmbeddings.Count / (double)sources.ExpectedSampleCount, sourceDigest, MediaTagEvidenceStatus.InsufficientEvidence, "TAGGING_SAMPLE_COVERAGE", L.T("Suggested Tagの生成に必要なVisual Embedding coverageが不足しています。"), DateTimeOffset.UtcNow);
                if (!await _library.UpsertMediaTagEvidenceAsync(evidence, cancellationToken))
                {
                    await _jobs.CompleteAsync(job.JobId, JobState.Partial, "SourceChangedBeforePersist", cancellationToken);
                    return;
                }
            }
            await _jobs.CompleteAsync(job.JobId, JobState.Partial, "InsufficientEvidence", cancellationToken);
            return;
        }

        var completed = 0;
        foreach (var tag in TaggingTaxonomy.All)
        {
            var textEmbedding = await GetOrCreateTagTextEmbeddingAsync(job, tag, payload, cancellationToken);
            if (textEmbedding is null) return; // The job was deferred for conservative resource admission.
            var aggregate = TaggingVectors.Aggregate(sources.CompletedEmbeddings, sources.ExpectedSampleCount, textEmbedding, profile);
            var existing = await _library.GetCurrentMediaTagEvidenceAsync(media.MediaFileId, media.MediaContentId, tag.TagId, payload.TaxonomyVersion, payload.TextRepresentationVersion, payload.AggregationVersion, payload.ThresholdVersion, _semanticModel.ModelId, _semanticModel.ModelRevision, _semanticModel.ProcessorVersion, sourceSamplingVersion, _semanticExecution.Dtype, _semanticModel.Dimension, aggregate.SourceEmbeddingDigest, cancellationToken);
            if (existing is null)
            {
                var status = profile.IsSuggested(aggregate) ? MediaTagEvidenceStatus.Suggested : MediaTagEvidenceStatus.NotSuggested;
                var evidence = new MediaTagEvidence(Guid.NewGuid(), media.MediaFileId, media.MediaContentId, aggregate.RepresentativeVisualSampleId, payload.TaxonomyVersion, tag.TagId, tag.DisplayName, tag.Group, MediaTagSource.EmbeddingZeroShot, _semanticModel.ModelId, _semanticModel.ModelRevision, _semanticModel.ProcessorVersion, _semanticExecution.Dtype, _semanticModel.Dimension, sourceSamplingVersion, payload.TextRepresentationVersion, payload.AggregationVersion, payload.ThresholdVersion, TaggingVectors.ScoreMetric, aggregate.MaximumScore, aggregate.MeanScore, aggregate.MatchedSampleCount, aggregate.CompletedSampleCount, aggregate.ExpectedSampleCount, aggregate.CoverageRatio, aggregate.SourceEmbeddingDigest, status, null, null, DateTimeOffset.UtcNow);
                if (!await _library.UpsertMediaTagEvidenceAsync(evidence, cancellationToken))
                {
                    await _jobs.CompleteAsync(job.JobId, JobState.Partial, "SourceChangedBeforePersist", cancellationToken);
                    return;
                }
            }
            completed++;
            await _jobs.UpdateProgressAsync(job.JobId, "ScoringFixedTaxonomy", new JobProgress(completed, TaggingTaxonomy.All.Count, "tags", false), cancellationToken);
        }
        await _jobs.CompleteAsync(job.JobId, JobState.Completed, "Persisted", cancellationToken);
    }

    private async Task<TagTextEmbedding?> GetOrCreateTagTextEmbeddingAsync(Job job, TagDefinition tag, TagSuggestionPayload payload, CancellationToken cancellationToken)
    {
        const string textDtype = "float32";
        var cached = await _library.GetTagTextEmbeddingAsync(payload.TaxonomyVersion, tag.TagId, payload.TextRepresentationVersion, _semanticModel.ModelId, _semanticModel.ModelRevision, _semanticModel.ProcessorVersion, textDtype, _semanticModel.Dimension, cancellationToken);
        if (cached is { Status: TagTextEmbeddingStatus.Completed, Normalized: true, VectorFloat32: not null }) return cached;
        if (_worker is null || _paths is null)
            throw new MediaToolException(new DomainError("TAG_TEXT_EMBEDDING_WORKER_UNAVAILABLE", L.T("Suggested Tagのtext embeddingに必要なローカルAI Workerが構成されていません。")));

        var snapshot = await _resourceSnapshots.CaptureAsync(cancellationToken);
        var admission = _resourceAdmission.Evaluate(new ResourceCostEstimate(CpuCost: _semanticExecution.IsCpuFallback ? 55 : 15, RamBytesEstimate: 4L * 1024 * 1024 * 1024, GpuRequired: !_semanticExecution.IsCpuFallback, VramBytesEstimate: _semanticExecution.IsCpuFallback ? 0 : 3L * 1024 * 1024 * 1024, SequentialReadCost: 0, RandomReadCost: 0, WriteIoCost: 2, DbWriteCost: 10), snapshot);
        _log.Write("Information", "TagTextEmbeddingAdmission", new { JobId = job.JobId, TagId = tag.TagId, admission.Kind, admission.Reason, admission.Code, Snapshot = snapshot });
        if (admission.Kind == AdmissionDecisionKind.Wait)
        {
            await _jobs.DeferForResourceAsync(job.JobId, admission.Reason, admission.Code, admission.Message, DateTimeOffset.UtcNow.AddSeconds(20), cancellationToken);
            return null;
        }
        if (admission.Kind != AdmissionDecisionKind.Admit)
            throw new MediaToolException(new DomainError(admission.Code, admission.Message));

        var modelRoot = ResolveModelDirectory(_semanticModel.ModelDirectoryName);
        var adapterRoot = string.IsNullOrWhiteSpace(_semanticModel.AdapterDirectoryName) ? null : ResolveModelDirectory(_semanticModel.AdapterDirectoryName);
        var descriptors = new Dictionary<string, string>
        {
            ["models_root"] = _paths.ModelsRoot,
            ["model_root"] = modelRoot,
            ["text"] = TaggingTaxonomy.TextFor(tag, payload.TextRepresentationVersion)
        };
        if (adapterRoot is not null) descriptors["adapter_root"] = adapterRoot;
        var request = new WorkerJobRequest(Guid.NewGuid(), "text-embedding-v1", descriptors, JsonSerializer.Serialize(new { model_id = _semanticModel.ModelId, revision = _semanticModel.ModelRevision, adapter = _semanticModel.Adapter, backend = _semanticExecution.Backend, dtype = _semanticExecution.Dtype }), _semanticModel.ProcessorVersion, string.Empty);
        var progress = new Progress<WorkerJobProgress>(update => _log.Write("Information", "TagTextEmbeddingWorkerProgress", new { JobId = job.JobId, TagId = tag.TagId, update.Phase, update.Current, update.Total, update.Unit, update.Metrics }));
        var now = DateTimeOffset.UtcNow;
        try
        {
            var result = await _worker.RunAsync(request, progress, cancellationToken);
            if (!string.Equals(result.Outcome, "completed", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(result.StructuredResultJson))
                throw new WorkerEmbeddingException(result.ErrorCode ?? "TAG_TEXT_EMBEDDING_FAILED", result.ErrorMessage ?? L.T("固定tagのAI特徴量生成に失敗しました。"));
            var parsed = ParseWorkerEmbedding(result.StructuredResultJson);
            if (parsed.ModelName != _semanticModel.ModelId || parsed.ModelRevision != _semanticModel.ModelRevision || parsed.ProcessorVersion != _semanticModel.ProcessorVersion || parsed.Dtype != textDtype || parsed.Dimension != _semanticModel.Dimension || !parsed.Normalized)
                throw new WorkerEmbeddingException("TAG_TEXT_EMBEDDING_RESULT_INVALID", L.T("固定tagのAI特徴量のモデル契約が一致しません。"));
            var vector = Convert.FromBase64String(parsed.VectorFloat32Base64);
            if (vector.Length != parsed.Dimension * sizeof(float))
                throw new WorkerEmbeddingException("TAG_TEXT_EMBEDDING_VECTOR_INVALID", L.T("固定tagのAI特徴量ベクトル形式が不正です。"));
            var embedding = new TagTextEmbedding(Guid.NewGuid(), payload.TaxonomyVersion, tag.TagId, tag.DisplayName, tag.Group, payload.TextRepresentationVersion, TaggingTaxonomy.TextFor(tag, payload.TextRepresentationVersion), parsed.ModelName, parsed.ModelRevision, parsed.ProcessorVersion, parsed.Dtype, parsed.Dimension, true, vector, TagTextEmbeddingStatus.Completed, null, null, now, DateTimeOffset.UtcNow);
            await _library.UpsertTagTextEmbeddingAsync(embedding, cancellationToken);
            return embedding;
        }
        catch (WorkerEmbeddingException ex)
        {
            await _library.UpsertTagTextEmbeddingAsync(new TagTextEmbedding(Guid.NewGuid(), payload.TaxonomyVersion, tag.TagId, tag.DisplayName, tag.Group, payload.TextRepresentationVersion, TaggingTaxonomy.TextFor(tag, payload.TextRepresentationVersion), _semanticModel.ModelId, _semanticModel.ModelRevision, _semanticModel.ProcessorVersion, textDtype, _semanticModel.Dimension, false, null, TagTextEmbeddingStatus.Failed, ex.Code, ex.UserMessage, now, DateTimeOffset.UtcNow), cancellationToken);
            throw new MediaToolException(new DomainError(ex.Code, ex.UserMessage));
        }
    }

    private async Task ProcessContentSimilarityRepresentationAsync(Job job, CancellationToken cancellationToken)
    {
        var payload = Deserialize<ContentSimilarityRepresentationPayload>(job.PayloadJson);
        var media = await _library.GetMediaFileAsync(payload.MediaFileId, cancellationToken);
        if (media is null || media.PresenceState == PresenceState.Missing)
        {
            await _jobs.CompleteAsync(job.JobId, JobState.Partial, "MediaMissing", cancellationToken);
            return;
        }
        if (media.PresenceState == PresenceState.OfflineRoot)
        {
            await _jobs.CompleteAsync(job.JobId, JobState.Partial, "RootOffline", cancellationToken);
            return;
        }
        if (media.PresenceState != PresenceState.Present)
        {
            await _jobs.CompleteAsync(job.JobId, JobState.Partial, "MediaUnavailable", cancellationToken);
            return;
        }
        if (media.MediaContentId != payload.MediaContentId || media.SizeBytes != payload.ExpectedSizeBytes || media.LastWriteTimeUtc != payload.ExpectedLastWriteTimeUtc)
        {
            await _jobs.CompleteAsync(job.JobId, JobState.Partial, "SourceChangedRequeueAfterScan", cancellationToken);
            return;
        }

        const string sourceSamplingVersion = "fixed-interval-v1";
        var sources = await _library.GetContentSimilaritySourceEmbeddingsAsync(media.MediaFileId, media.MediaContentId, _semanticModel.ModelId, _semanticModel.ModelRevision, _semanticModel.ProcessorVersion, sourceSamplingVersion, _semanticExecution.Dtype, _semanticModel.Dimension, cancellationToken);
        var aggregate = ContentSimilarityVectors.BuildMeanUnit(sources.CompletedEmbeddings, sources.ExpectedSampleCount);
        var existing = await _library.GetCurrentContentSimilarityRepresentationAsync(media.MediaFileId, media.MediaContentId, payload.AggregationVersion, _semanticModel.ModelId, _semanticModel.ModelRevision, _semanticModel.ProcessorVersion, sourceSamplingVersion, _semanticExecution.Dtype, _semanticModel.Dimension, cancellationToken);
        if (aggregate.HasCompleteCoverage && existing is not null && string.Equals(existing.SourceEmbeddingDigest, aggregate.SourceEmbeddingDigest, StringComparison.Ordinal))
        {
            await RefreshContentSimilarityAsync(payload, cancellationToken);
            await _jobs.CompleteAsync(job.JobId, JobState.Completed, "Reused", cancellationToken);
            return;
        }

        var snapshot = await _resourceSnapshots.CaptureAsync(cancellationToken);
        var admission = _resourceAdmission.Evaluate(new ResourceCostEstimate(CpuCost: 30, RamBytesEstimate: 256L * 1024 * 1024, GpuRequired: false, VramBytesEstimate: 0, SequentialReadCost: 0, RandomReadCost: 0, WriteIoCost: 5, DbWriteCost: 30), snapshot);
        _log.Write("Information", "ContentSimilarityRepresentationAdmission", new { JobId = job.JobId, admission.Kind, admission.Reason, admission.Code, Snapshot = snapshot });
        if (admission.Kind == AdmissionDecisionKind.Wait)
        {
            await _jobs.DeferForResourceAsync(job.JobId, admission.Reason, admission.Code, admission.Message, DateTimeOffset.UtcNow.AddSeconds(20), cancellationToken);
            return;
        }
        if (admission.Kind != AdmissionDecisionKind.Admit)
        {
            await _jobs.FailOrRetryAsync(job.JobId, admission.Code, admission.Message, false, cancellationToken);
            return;
        }

        var status = aggregate.HasCompleteCoverage ? "Completed" : "InsufficientEvidence";
        var representation = new ContentSimilarityRepresentation(Guid.NewGuid(), media.MediaFileId, media.MediaContentId, _semanticModel.ModelId, _semanticModel.ModelRevision, _semanticModel.ProcessorVersion, sourceSamplingVersion, payload.AggregationVersion, aggregate.SourceEmbeddingDigest, _semanticExecution.Dtype, _semanticModel.Dimension, aggregate.HasCompleteCoverage, aggregate.AvailableSampleCount, aggregate.ExpectedSampleCount, aggregate.CoverageRatio, aggregate.HasCompleteCoverage ? ContentSimilarityVectors.Encode(aggregate.Vector) : null, status, aggregate.HasCompleteCoverage ? null : "CONTENT_SIMILARITY_SAMPLE_COVERAGE", aggregate.HasCompleteCoverage ? null : L.T("内容類似候補の生成に必要なVisual Embedding coverageが不足しています。"), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var persisted = await _library.UpsertContentSimilarityRepresentationAsync(representation, cancellationToken);
        if (!persisted)
        {
            await _jobs.CompleteAsync(job.JobId, JobState.Partial, "SourceChangedBeforePersist", cancellationToken);
            return;
        }
        if (!aggregate.HasCompleteCoverage)
        {
            await _jobs.CompleteAsync(job.JobId, JobState.Partial, "InsufficientEvidence", cancellationToken);
            return;
        }
        await RefreshContentSimilarityAsync(payload, cancellationToken);
        await _jobs.UpdateProgressAsync(job.JobId, "DerivedFromPersistedEmbeddings", new JobProgress(aggregate.AvailableSampleCount, aggregate.ExpectedSampleCount, "embeddings", false), cancellationToken);
        await _jobs.CompleteAsync(job.JobId, JobState.Completed, "PersistedAndVerified", cancellationToken);
    }

    /// <summary>Current-version payloads use the deferred library-wide refresh; any other version verifies directly.</summary>
    private Task RefreshContentSimilarityAsync(ContentSimilarityRepresentationPayload payload, CancellationToken cancellationToken) =>
        payload.AggregationVersion == ContentSimilarityVectors.AggregationVersion && payload.ThresholdVersion == ContentSimilarityVerificationProfile.HighPrecisionV1.ThresholdVersion
            ? RefreshLibraryWideAsync(LibraryWideRefresh.ContentSimilarity, cancellationToken)
            : VerifyContentSimilarityCandidatesAsync(payload.AggregationVersion, payload.ThresholdVersion, cancellationToken);

    private async Task VerifyContentSimilarityCandidatesAsync(string aggregationVersion, string thresholdVersion, CancellationToken cancellationToken)
    {
        var profile = ContentSimilarityVerificationProfile.HighPrecisionV1;
        if (!string.Equals(thresholdVersion, profile.ThresholdVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("Unsupported content similarity threshold version.");
        const string sourceSamplingVersion = "fixed-interval-v1";
        var candidates = await _library.GetContentSimilarityCandidatesAsync(null, aggregationVersion, _semanticModel.ModelId, _semanticModel.ModelRevision, _semanticModel.ProcessorVersion, sourceSamplingVersion, _semanticExecution.Dtype, _semanticModel.Dimension, profile.TopK, cancellationToken);
        foreach (var candidate in candidates)
        {
            var reusable = await _library.GetContentSimilarityEvidenceAsync(candidate.MediaFileAId, candidate.MediaContentAId, candidate.MediaFileBId, candidate.MediaContentBId, aggregationVersion, thresholdVersion, cancellationToken);
            if (reusable is not null) continue;
            var sourceA = await _library.GetContentSimilaritySourceEmbeddingsAsync(candidate.MediaFileAId, candidate.MediaContentAId, _semanticModel.ModelId, _semanticModel.ModelRevision, _semanticModel.ProcessorVersion, sourceSamplingVersion, _semanticExecution.Dtype, _semanticModel.Dimension, cancellationToken);
            var sourceB = await _library.GetContentSimilaritySourceEmbeddingsAsync(candidate.MediaFileBId, candidate.MediaContentBId, _semanticModel.ModelId, _semanticModel.ModelRevision, _semanticModel.ProcessorVersion, sourceSamplingVersion, _semanticExecution.Dtype, _semanticModel.Dimension, cancellationToken);
            if (sourceA.ExpectedSampleCount == 0 || sourceB.ExpectedSampleCount == 0 || sourceA.CompletedEmbeddings.Count != sourceA.ExpectedSampleCount || sourceB.CompletedEmbeddings.Count != sourceB.ExpectedSampleCount)
                continue;
            var pairScore = ContentSimilarityVectors.VerifyTopSamples(sourceA.CompletedEmbeddings, sourceB.CompletedEmbeddings, profile.TopSampleCount);
            var status = profile.IsVerified(candidate.MeanCosineSimilarity, pairScore) ? "Verified" : "Rejected";
            var evidenceDigest = AnalysisKey.Sha256(AnalysisKey.CanonicalJson(new { relation = "ContentSimilar", candidate.MediaFileAId, candidate.MediaContentAId, candidate.ContentSimilarityRepresentationAId, candidate.MediaFileBId, candidate.MediaContentBId, candidate.ContentSimilarityRepresentationBId, aggregationVersion, thresholdVersion, metric = ContentSimilarityVectors.SimilarityMetric, mean = candidate.MeanCosineSimilarity, sampleAware = pairScore.TopSampleCosine, representative = pairScore.RepresentativeCosine }));
            var evidence = new ContentSimilarityEvidence(Guid.NewGuid(), candidate.MediaFileAId, candidate.MediaContentAId, candidate.ContentSimilarityRepresentationAId, candidate.MediaFileBId, candidate.MediaContentBId, candidate.ContentSimilarityRepresentationBId, pairScore.RepresentativeVisualSampleAId, pairScore.RepresentativeVisualSampleBId, _semanticModel.ModelId, _semanticModel.ModelRevision, _semanticModel.ProcessorVersion, sourceSamplingVersion, aggregationVersion, thresholdVersion, ContentSimilarityVectors.SimilarityMetric, candidate.MeanCosineSimilarity, pairScore.TopSampleCosine, pairScore.RepresentativeCosine, sourceA.CompletedEmbeddings.Count, sourceA.ExpectedSampleCount, sourceB.CompletedEmbeddings.Count, sourceB.ExpectedSampleCount, evidenceDigest, status, status == "Rejected" ? "CONTENT_SIMILARITY_THRESHOLD" : null, status == "Rejected" ? L.T("Content Similarity高Precision profileのpair verification条件を満たしません。") : null, DateTimeOffset.UtcNow);
            await _library.UpsertContentSimilarityEvidenceAsync(evidence, cancellationToken);
        }
    }

    private bool IsWorkerUnavailableAfterEmbeddingFault() =>
        _worker is not null &&
        (string.Equals(_worker.Status.ErrorCode, WorkerExitedUnexpectedlyException.ErrorCode, StringComparison.Ordinal) || _worker.ProcessId is null);

    private string ResolveModelDirectory(string directoryName)
    {
        if (_paths is null) throw new InvalidOperationException("Application paths are required for semantic model resolution.");
        var resolved = Path.GetFullPath(Path.Combine(_paths.ModelsRoot, directoryName));
        if (!PathPolicy.IsWithinRoot(_paths.ModelsRoot, resolved))
            throw new MediaToolException(new DomainError("MODEL_PATH_INVALID", L.T("ローカルAIモデルのパスがModelsRoot外を指しています。")));
        if (!Directory.Exists(resolved))
            throw new MediaToolException(new DomainError("MODEL_NOT_FOUND", L.T("ローカルAIモデルが見つかりません。Portable ModelsRootを確認してください。")));
        return resolved;
    }

    private static WorkerEmbeddingResult ParseWorkerEmbedding(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        string RequiredString(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : throw new WorkerEmbeddingException("EMBEDDING_RESULT_INVALID", L.T("AI特徴量の必須項目がありません。"));
        if (!root.TryGetProperty("dimension", out var dimensionElement) || !dimensionElement.TryGetInt32(out var dimension) ||
            !root.TryGetProperty("normalized", out var normalizedElement) || normalizedElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            throw new WorkerEmbeddingException("EMBEDDING_RESULT_INVALID", L.T("AI特徴量の形式が不正です。"));
        return new WorkerEmbeddingResult(RequiredString("model_name"), RequiredString("model_revision"), RequiredString("processor_version"), RequiredString("dtype"), dimension, normalizedElement.GetBoolean(), RequiredString("vector_float32_base64"));
    }

    private sealed record WorkerEmbeddingResult(string ModelName, string ModelRevision, string ProcessorVersion, string Dtype, int Dimension, bool Normalized, string VectorFloat32Base64);
    private sealed class WorkerEmbeddingException(string code, string userMessage) : Exception(userMessage) { public string Code { get; } = code; public string UserMessage { get; } = userMessage; }

    private async Task<MediaFile> GetPresentMediaAsync(Job job, CancellationToken cancellationToken) => await GetPresentMediaAsync(Deserialize<MediaPayload>(job.PayloadJson).MediaFileId, cancellationToken);

    private async Task<MediaFile> GetPresentMediaAsync(Guid mediaFileId, CancellationToken cancellationToken)
    {
        var media = await _library.GetMediaFileAsync(mediaFileId, cancellationToken) ?? throw new MediaToolException(new DomainError("MEDIA_NOT_FOUND", L.T("対象メディアが見つかりません。")));
        if (media.PresenceState != PresenceState.Present) throw new MediaToolException(new DomainError(media.PresenceState == PresenceState.OfflineRoot ? "ROOT_OFFLINE" : "MEDIA_NOT_FOUND", L.T("対象メディアは現在利用できません。")));
        return media;
    }

    private static T Deserialize<T>(string json) where T : class => JsonSerializer.Deserialize<T>(json) ?? throw new InvalidOperationException("Job payload is invalid.");
    private static string InputIdentity(MediaFile file) => file.FullSha256 ?? file.QuickFingerprint ?? $"size={file.SizeBytes};mtime={file.LastWriteTimeUtc:O}";
}
