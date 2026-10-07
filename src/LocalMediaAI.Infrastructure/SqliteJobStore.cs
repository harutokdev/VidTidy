using System.Globalization;
using System.Text.Json;
using LocalMediaAI.Core;
using LocalMediaAI.Core.Processing;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure;

public enum ProtectedPipelineDispatchMode { LegacyPreActivation, ProtectedActivated }

/// <summary>
/// The jobs one worker lane may take. A source lane owns the roots on one physical drive; <see cref="IncludeUnattributed"/>
/// lets exactly one source lane also take jobs whose root is unknown or no longer active (they finish quickly as unavailable).
/// </summary>
public sealed record JobLaneFilter(JobLaneKind Kind, IReadOnlyCollection<Guid>? RootIds = null, bool IncludeUnattributed = false, bool ScansOnly = false)
{
    public static JobLaneFilter Compute { get; } = new(JobLaneKind.Compute);
}

public sealed record LegacyProtectedCutoverResult(
    int SupersededLegacyJobs,
    int ProtectedJobsCreated,
    int ProtectedJobsUpdated,
    IReadOnlyList<Guid> ProtectedJobIds);

public static class ProtectedPipelineDispatchPolicy
{
    public static bool IsLegacyOriginalSourceJob(JobType type) => type is
        JobType.Metadata or JobType.Thumbnail or JobType.FullHash or JobType.VisualSampling or JobType.NearDuplicateFingerprint;

    public static bool CanUseLegacyUnprotectedExecutor(JobType type, ProtectedPipelineDispatchMode mode) =>
        mode == ProtectedPipelineDispatchMode.LegacyPreActivation && IsLegacyOriginalSourceJob(type);

    public static void EnsureProtectedDispatch(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (IsLegacyOriginalSourceJob(job.JobType))
            throw new InvalidOperationException("A protected-mode dispatcher cannot invoke a legacy unprotected source executor.");
    }
}

public sealed class SqliteJobStore : IJobStore
{
    private readonly string _connectionString;
    private readonly IStructuredLogger _log;

    public SqliteJobStore(AppPaths paths, IStructuredLogger log)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared }.ToString();
        _log = log;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    public async Task<Job> EnqueueAsync(JobType type, object payload, int maxAttempts, DateTimeOffset? notBefore, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var job = new Job(Guid.NewGuid(), type, JobState.Pending, "Queued", JobReason.None, JsonSerializer.Serialize(payload), 0, Math.Max(1, maxAttempts), notBefore, null, null, null, new JobProgress(0, 0, "items", true), null, null, now, now);
        await using var connection = await OpenAsync(cancellationToken);
        await InsertJobAsync(connection, null, job, cancellationToken);
        await AddEventAsync(connection, null, job.JobId, "Enqueued", new { job.JobType, job.NotBefore }, cancellationToken);
        return job;
    }

    public async Task<Job> EnqueueUniqueAsync(JobType type, object payload, int maxAttempts, DateTimeOffset? notBefore, CancellationToken cancellationToken)
    {
        var payloadJson = JsonSerializer.Serialize(payload);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var existing = await ReadSingleAsync(connection, transaction, "SELECT * FROM job WHERE job_type=$type AND payload_json=$payload AND state IN ('Pending','Running','Waiting','Paused','RetryWaiting') ORDER BY created_at LIMIT 1;", cancellationToken, ("$type", type.ToString()), ("$payload", payloadJson));
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existing;
        }
        var now = DateTimeOffset.UtcNow;
        var job = new Job(Guid.NewGuid(), type, JobState.Pending, "Queued", JobReason.None, payloadJson, 0, Math.Max(1, maxAttempts), notBefore, null, null, null, new JobProgress(0, 0, "items", true), null, null, now, now);
        await InsertJobAsync(connection, transaction, job, cancellationToken);
        await AddEventAsync(connection, transaction, job.JobId, "Enqueued", new { job.JobType, job.NotBefore, UniquePayload = true }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return job;
    }

    /// <summary>
    /// Atomically gates a follow-up Job on the media still being present under an
    /// active registration. Unregister either commits first (and no Job is created),
    /// or this insert commits first (and unregister cancels the queued Job).
    /// </summary>
    public async Task<Job?> EnqueueForActiveMediaAsync(JobType type, object payload, Guid mediaFileId, int maxAttempts, DateTimeOffset? notBefore, bool unique, CancellationToken cancellationToken)
    {
        var payloadJson = JsonSerializer.Serialize(payload);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var active = await ScalarLongAsync(connection, transaction,
            "SELECT COUNT(1) FROM media_file mf JOIN library_root lr ON lr.library_root_id=mf.library_root_id WHERE mf.media_file_id=$media AND mf.presence_state='Present' AND mf.cleanup_committed_at IS NULL AND lr.registration_state='Active';",
            cancellationToken, ("$media", mediaFileId.ToString("D")));
        if (active != 1)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        if (unique)
        {
            var existing = await ReadSingleAsync(connection, transaction, "SELECT * FROM job WHERE job_type=$type AND payload_json=$payload AND state IN ('Pending','Running','Waiting','Paused','RetryWaiting') ORDER BY created_at LIMIT 1;", cancellationToken, ("$type", type.ToString()), ("$payload", payloadJson));
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return existing;
            }
        }
        var now = DateTimeOffset.UtcNow;
        var job = new Job(Guid.NewGuid(), type, JobState.Pending, "Queued", JobReason.None, payloadJson, 0, Math.Max(1, maxAttempts), notBefore, null, null, null, new JobProgress(0, 0, "items", true), null, null, now, now);
        await InsertJobAsync(connection, transaction, job, cancellationToken);
        await AddEventAsync(connection, transaction, job.JobId, "Enqueued", new { job.JobType, job.NotBefore, ActiveMediaGate = true, UniquePayload = unique }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return job;
    }

    public Task<Job?> TryAcquireNextAsync(CancellationToken cancellationToken) =>
        TryAcquireNextCoreAsync(protectedMode: false, null, cancellationToken);

    /// <summary>
    /// Explicit future-activation seam. Normal MediaPipeline continues to call the
    /// legacy overload. Protected mode translates legacy rows before structurally
    /// excluding every legacy original-source kind from acquisition.
    /// </summary>
    public Task<Job?> TryAcquireNextAsync(ProtectedPipelineDispatchMode mode, CancellationToken cancellationToken) =>
        TryAcquireNextAsync(mode, null, cancellationToken);

    /// <summary>
    /// Lane-aware acquisition. With a lane, only that lane's job kinds (and, for a source lane, its drive's roots) are
    /// eligible; a source lane takes scans first, then every quick pass, then deep work. Without a lane the legacy
    /// single-queue order (oldest first) is kept.
    /// </summary>
    public async Task<Job?> TryAcquireNextAsync(ProtectedPipelineDispatchMode mode, JobLaneFilter? lane, CancellationToken cancellationToken)
    {
        if (mode == ProtectedPipelineDispatchMode.LegacyPreActivation)
            return await TryAcquireNextCoreAsync(protectedMode: false, lane, cancellationToken).ConfigureAwait(false);
        await TransitionLegacySourceJobsAsync(cancellationToken).ConfigureAwait(false);
        var job = await TryAcquireNextCoreAsync(protectedMode: true, lane, cancellationToken).ConfigureAwait(false);
        if (job is not null) ProtectedPipelineDispatchPolicy.EnsureProtectedDispatch(job);
        return job;
    }

    private const string JobRootExpression = "COALESCE(json_extract(j.payload_json,'$.LibraryRootId'),mf.library_root_id)";
    private const string PassPriorityExpression =
        "CASE WHEN j.job_type='Scan' THEN 0 WHEN j.job_type='ProtectedSourceOrchestration' AND (CAST(json_extract(j.payload_json,'$.EligibleStages') AS INTEGER) & ~3)=0 THEN 1 ELSE 2 END";

    /// <summary>True when a job of one of the types is still queued (pending, waiting or retrying); running jobs do not count.</summary>
    public async Task<bool> HasPendingOfTypesAsync(IReadOnlyCollection<JobType> types, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(types);
        if (types.Count == 0) return false;
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Create(connection, null, "SELECT 1 FROM job WHERE job_type IN (" + string.Join(",", types.Select(type => "'" + type + "'")) + ") AND state IN ('Pending','Waiting','RetryWaiting') LIMIT 1;");
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    /// <summary>True when the lane has a job it could take now (legacy source rows count; they are converted on acquisition).</summary>
    public async Task<bool> HasAcquirableAsync(JobLaneFilter lane, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lane);
        var (from, where, _, parameters) = BuildAcquisitionQuery(protectedMode: false, lane);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Create(connection, null, "SELECT 1 FROM " + from + " WHERE " + where + " LIMIT 1;", [.. parameters]);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static (string From, string Where, string Order, List<(string Name, object? Value)> Parameters) BuildAcquisitionQuery(bool protectedMode, JobLaneFilter? lane)
    {
        // The media_file join is only needed to attribute file jobs to a root; the compute and single-queue paths skip it.
        var from = lane?.RootIds is null ? "job j" : "job j LEFT JOIN media_file mf ON mf.media_file_id=json_extract(j.payload_json,'$.MediaFileId')";
        var parameters = new List<(string Name, object? Value)> { ("$now", Db(DateTimeOffset.UtcNow)) };
        var where = "(j.state='Pending' OR (j.state='RetryWaiting' AND (j.not_before IS NULL OR j.not_before <= $now))) AND (j.not_before IS NULL OR j.not_before <= $now)";
        if (protectedMode) where += " AND j.job_type NOT IN ('Metadata','Thumbnail','FullHash','VisualSampling','NearDuplicateFingerprint')";
        var order = "j.created_at";
        if (lane is not null)
        {
            var types = lane.Kind == JobLaneKind.Compute ? JobLanes.ComputeTypes : JobLanes.SourceTypes;
            where += lane.ScansOnly ? " AND j.job_type='Scan'" : " AND j.job_type IN (" + string.Join(",", types.Select(type => "'" + type + "'")) + ")";
            if (lane.RootIds is not null)
            {
                var names = lane.RootIds.Select((root, index) =>
                {
                    parameters.Add(("$root" + index, root.ToString("D")));
                    return "$root" + index;
                }).ToArray();
                var owned = names.Length == 0 ? "0" : JobRootExpression + " IN (" + string.Join(",", names) + ")";
                where += lane.IncludeUnattributed
                    ? $" AND ({owned} OR {JobRootExpression} IS NULL OR {JobRootExpression} NOT IN (SELECT library_root_id FROM library_root WHERE registration_state='Active'))"
                    : $" AND {owned}";
            }
            if (lane.Kind == JobLaneKind.Source) order = PassPriorityExpression + ",j.created_at";
        }
        return (from, where, order, parameters);
    }

    private async Task<Job?> TryAcquireNextCoreAsync(bool protectedMode, JobLaneFilter? lane, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var (from, where, order, parameters) = BuildAcquisitionQuery(protectedMode, lane);
        var pickSql = "SELECT j.* FROM " + from + " WHERE " + where + " ORDER BY " + order + " LIMIT 1;";
        var candidate = await ReadSingleAsync(connection, transaction, pickSql, cancellationToken, [.. parameters]);
        if (candidate is null) { await transaction.CommitAsync(cancellationToken); return null; }
        JobStateMachine.EnsureTransition(candidate.State, JobState.Running);
        var now = DateTimeOffset.UtcNow;
        await ExecuteAsync(connection, transaction, "UPDATE job SET state='Running',phase='Starting',reason='None',attempt=attempt+1,started_at=$started,heartbeat_at=$heartbeat,updated_at=$updated WHERE job_id=$id;", cancellationToken, ("$started", Db(now)), ("$heartbeat", Db(now)), ("$updated", Db(now)), ("$id", candidate.JobId.ToString("D")));
        await AddEventAsync(connection, transaction, candidate.JobId, "Acquired", new { Attempt = candidate.Attempt + 1 }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return candidate with { State = JobState.Running, Phase = "Starting", Reason = JobReason.None, Attempt = candidate.Attempt + 1, StartedAt = now, HeartbeatAt = now, UpdatedAt = now };
    }

    public async Task<LegacyProtectedCutoverResult> TransitionLegacySourceJobsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var legacy = await ReadManyAsync(connection, transaction,
            "SELECT * FROM job WHERE job_type IN ('Metadata','Thumbnail','FullHash','VisualSampling','NearDuplicateFingerprint') AND state IN ('Pending','Running','Waiting','Paused','RetryWaiting') ORDER BY created_at;",
            cancellationToken).ConfigureAwait(false);
        if (legacy.Any(value => value.State == JobState.Running))
            throw new InvalidOperationException("Protected cutover requires active legacy Jobs to finish or be recovered before transition.");

        var requests = new Dictionary<(Guid MediaFileId, Guid MediaContentId), ProtectedRequest>();
        foreach (var job in legacy)
        {
            var mediaFileId = ReadLegacyMediaFileId(job);
            var current = await ReadCurrentMediaIdentityAsync(connection, transaction, mediaFileId, cancellationToken).ConfigureAwait(false);
            if (current is not null)
            {
                var key = (mediaFileId, current.Value.MediaContentId);
                var stage = MapLegacyStage(job.JobType);
                requests[key] = requests.TryGetValue(key, out var existing)
                    ? existing with { Stages = existing.Stages | stage, MaxAttempts = Math.Max(existing.MaxAttempts, job.MaxAttempts) }
                    : new ProtectedRequest(stage, job.MaxAttempts);
            }

            var now = DateTimeOffset.UtcNow;
            await ExecuteAsync(connection, transaction,
                "UPDATE job SET state='Cancelled',phase='SupersededByProtectedOrchestration',reason='None',completed_at=$at,heartbeat_at=$at,updated_at=$at,last_error_code='LEGACY_SOURCE_JOB_SUPERSEDED',last_error_message='Legacy source work was transferred to protected per-media orchestration.' WHERE job_id=$id AND state IN ('Pending','Waiting','Paused','RetryWaiting');",
                cancellationToken, ("$at", Db(now)), ("$id", job.JobId.ToString("D"))).ConfigureAwait(false);
            await AddEventAsync(connection, transaction, job.JobId, "SupersededByProtectedOrchestration", new { job.JobType, MediaFileId = mediaFileId }, cancellationToken).ConfigureAwait(false);
        }

        var activeProtected = await ReadManyAsync(connection, transaction,
            "SELECT * FROM job WHERE job_type='ProtectedSourceOrchestration' AND state IN ('Pending','Running','Waiting','Paused','RetryWaiting') ORDER BY created_at;",
            cancellationToken).ConfigureAwait(false);
        var protectedIds = new List<Guid>();
        var created = 0;
        var updated = 0;
        foreach (var request in requests)
        {
            var matching = activeProtected.Where(value =>
            {
                var payload = DeserializeProtectedPayload(value.PayloadJson);
                return payload.MediaFileId == request.Key.MediaFileId && payload.ExpectedMediaContentId == request.Key.MediaContentId;
            }).ToArray();
            var runningStages = matching.Where(value => value.State == JobState.Running)
                .Aggregate(ProtectedSourceStageEligibility.None, (stages, value) => stages | DeserializeProtectedPayload(value.PayloadJson).EligibleStages);
            foreach (var running in matching.Where(value => value.State == JobState.Running)) protectedIds.Add(running.JobId);
            var remainingStages = request.Value.Stages & ~runningStages;
            if (remainingStages == ProtectedSourceStageEligibility.None) continue;
            // Each file becomes a quick pass (metadata + thumbnail, run for every file first) and a deep pass.
            var (quickPass, deepPass) = JobLanes.SplitPasses(remainingStages);
            foreach (var passStages in new[] { quickPass, deepPass }.Where(value => value != ProtectedSourceStageEligibility.None))
            {
                var isQuick = JobLanes.IsQuickPass(passStages);
                var existing = matching.FirstOrDefault(value => value.State != JobState.Running && JobLanes.IsQuickPass(DeserializeProtectedPayload(value.PayloadJson).EligibleStages) == isQuick);
                if (existing is null)
                {
                    var payload = new ProtectedSourceOrchestrationPayload(request.Key.MediaFileId, request.Key.MediaContentId, passStages);
                    var now = DateTimeOffset.UtcNow;
                    var job = new Job(Guid.NewGuid(), JobType.ProtectedSourceOrchestration, JobState.Pending, "Queued", JobReason.None,
                        JsonSerializer.Serialize(payload), 0, Math.Max(1, request.Value.MaxAttempts), null, null, null, null,
                        new JobProgress(0, 0, "stages", true), null, null, now, now);
                    await InsertJobAsync(connection, transaction, job, cancellationToken).ConfigureAwait(false);
                    await AddEventAsync(connection, transaction, job.JobId, "ProtectedOrchestrationCreatedFromLegacy", new { payload.MediaFileId, payload.ExpectedMediaContentId, payload.EligibleStages }, cancellationToken).ConfigureAwait(false);
                    activeProtected.Add(job);
                    protectedIds.Add(job.JobId);
                    created++;
                }
                else
                {
                    var payload = DeserializeProtectedPayload(existing.PayloadJson);
                    var merged = payload with { EligibleStages = payload.EligibleStages | passStages };
                    if (merged != payload)
                    {
                        await ExecuteAsync(connection, transaction,
                            "UPDATE job SET payload_json=$payload,max_attempts=MAX(max_attempts,$attempts),updated_at=$at WHERE job_id=$id;",
                            cancellationToken, ("$payload", JsonSerializer.Serialize(merged)), ("$attempts", request.Value.MaxAttempts),
                            ("$at", Db(DateTimeOffset.UtcNow)), ("$id", existing.JobId.ToString("D"))).ConfigureAwait(false);
                        updated++;
                    }
                    protectedIds.Add(existing.JobId);
                }
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(legacy.Count, created, updated, protectedIds.Distinct().ToArray());
    }

    public async Task UpdateProgressAsync(Guid jobId, string phase, JobProgress progress, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        await ExecuteAsync(connection, null, "UPDATE job SET phase=$phase,progress_current=$current,progress_total=$total,progress_unit=$unit,progress_indeterminate=$indeterminate,heartbeat_at=$heartbeat,updated_at=$updated WHERE job_id=$id AND state='Running';", cancellationToken,
            ("$phase", phase), ("$current", progress.Current), ("$total", progress.Total), ("$unit", progress.Unit), ("$indeterminate", progress.IsIndeterminate ? 1 : 0), ("$heartbeat", Db(now)), ("$updated", Db(now)), ("$id", jobId.ToString("D")));
    }

    public async Task HeartbeatAsync(Guid jobId, string phase, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        await ExecuteAsync(connection, null, "UPDATE job SET phase=$phase,heartbeat_at=$at,updated_at=$at WHERE job_id=$id AND state='Running';", cancellationToken, ("$phase", phase), ("$at", Db(now)), ("$id", jobId.ToString("D")));
    }

    internal async Task HeartbeatAttemptAsync(Guid jobId, int attempt, string phase, CancellationToken cancellationToken)
    {
        if (jobId == Guid.Empty) throw new ArgumentException("A Job id is required.", nameof(jobId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempt);
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        await using var command = Create(connection, null,
            "UPDATE job SET phase=$phase,heartbeat_at=$at,updated_at=$at WHERE job_id=$id AND state='Running' AND attempt=$attempt;",
            ("$phase", phase), ("$at", Db(now)), ("$id", jobId.ToString("D")), ("$attempt", attempt));
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != 1)
            throw new InvalidOperationException("The authoritative Running Job attempt no longer owns stage persistence.");
    }

    public async Task CompleteAsync(Guid jobId, JobState state, string phase, CancellationToken cancellationToken)
    {
        if (state is not (JobState.Completed or JobState.Partial or JobState.Cancelled)) throw new ArgumentOutOfRangeException(nameof(state));
        await using var connection = await OpenAsync(cancellationToken);
        var current = await ReadSingleAsync(connection, null, "SELECT * FROM job WHERE job_id=$id;", cancellationToken, ("$id", jobId.ToString("D"))) ?? throw new InvalidOperationException("Job not found.");
        JobStateMachine.EnsureTransition(current.State, state);
        var now = DateTimeOffset.UtcNow;
        await ExecuteAsync(connection, null, "UPDATE job SET state=$state,phase=$phase,reason='None',completed_at=$at,heartbeat_at=$at,updated_at=$at WHERE job_id=$id;", cancellationToken, ("$state", state.ToString()), ("$phase", phase), ("$at", Db(now)), ("$id", jobId.ToString("D")));
        await AddEventAsync(connection, null, jobId, "Completed", new { State = state.ToString(), Phase = phase }, cancellationToken);
    }

    public async Task FailOrRetryAsync(Guid jobId, string errorCode, string userMessage, bool retryable, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var current = await ReadSingleAsync(connection, null, "SELECT * FROM job WHERE job_id=$id;", cancellationToken, ("$id", jobId.ToString("D"))) ?? throw new InvalidOperationException("Job not found.");
        var shouldRetry = retryable && current.Attempt < current.MaxAttempts;
        var state = shouldRetry ? JobState.RetryWaiting : JobState.Failed;
        JobStateMachine.EnsureTransition(current.State, state);
        var now = DateTimeOffset.UtcNow;
        var notBefore = shouldRetry ? now.AddSeconds(Math.Min(300, Math.Pow(2, current.Attempt) * 5)) : (DateTimeOffset?)null;
        await ExecuteAsync(connection, null, "UPDATE job SET state=$state,phase='Failed',reason=$reason,not_before=$notBefore,completed_at=$completed,updated_at=$updated,last_error_code=$code,last_error_message=$message WHERE job_id=$id;", cancellationToken,
            ("$state", state.ToString()), ("$reason", shouldRetry ? JobReason.Backoff.ToString() : JobReason.None.ToString()), ("$notBefore", notBefore is null ? DBNull.Value : Db(notBefore.Value)), ("$completed", shouldRetry ? DBNull.Value : Db(now)), ("$updated", Db(now)), ("$code", errorCode), ("$message", userMessage), ("$id", jobId.ToString("D")));
        await AddEventAsync(connection, null, jobId, shouldRetry ? "RetryScheduled" : "Failed", new { errorCode, userMessage, NotBefore = notBefore }, cancellationToken);
        _log.Write("Warning", "JobFailed", new { JobId = jobId, ErrorCode = errorCode, Retry = shouldRetry, UserMessage = userMessage });
    }

    public async Task DeferForResourceAsync(Guid jobId, JobReason reason, string code, string userMessage, DateTimeOffset notBefore, CancellationToken cancellationToken)
    {
        if (reason is not (JobReason.ResourceGpu or JobReason.ResourceVram or JobReason.ResourceRam or JobReason.ResourceIo or JobReason.ResourceCpu or JobReason.DatabaseBusy))
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "Resource deferral requires a resource-related reason.");
        await using var connection = await OpenAsync(cancellationToken);
        var current = await ReadSingleAsync(connection, null, "SELECT * FROM job WHERE job_id=$id;", cancellationToken, ("$id", jobId.ToString("D"))) ?? throw new InvalidOperationException("Job not found.");
        JobStateMachine.EnsureTransition(current.State, JobState.RetryWaiting);
        var now = DateTimeOffset.UtcNow;
        await ExecuteAsync(connection, null, "UPDATE job SET state='RetryWaiting',phase='ResourceWaiting',reason=$reason,not_before=$notBefore,updated_at=$updated,last_error_code=$code,last_error_message=$message WHERE job_id=$id;", cancellationToken,
            ("$reason", reason.ToString()), ("$notBefore", Db(notBefore)), ("$updated", Db(now)), ("$code", code), ("$message", userMessage), ("$id", jobId.ToString("D")));
        await AddEventAsync(connection, null, jobId, "ResourceDeferred", new { Reason = reason.ToString(), code, userMessage, NotBefore = notBefore }, cancellationToken);
        _log.Write("Information", "JobResourceDeferred", new { JobId = jobId, Reason = reason.ToString(), code, NotBefore = notBefore });
    }

    public async Task RecoverInterruptedJobsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var jobs = await ReadManyAsync(connection, transaction, "SELECT * FROM job WHERE state='Running';", cancellationToken);
        var now = DateTimeOffset.UtcNow;
        foreach (var job in jobs)
        {
            await ExecuteAsync(connection, transaction, "UPDATE job SET state='RetryWaiting',phase='RecoveryPending',reason='AppRestartRecovery',not_before=$now,updated_at=$now,last_error_code='APP_RESTART_RECOVERY',last_error_message='The application restarted while the job was running.' WHERE job_id=$id;", cancellationToken, ("$now", Db(now)), ("$id", job.JobId.ToString("D")));
            await AddEventAsync(connection, transaction, job.JobId, "RecoveredAfterRestart", new { PreviousState = "Running" }, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        if (jobs.Count > 0) _log.Write("Information", "JobsRecovered", new { Count = jobs.Count, Reason = JobReason.AppRestartRecovery.ToString() });
    }

    public async Task<IReadOnlyList<Job>> GetRecentAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await ReadManyAsync(connection, null, "SELECT * FROM job ORDER BY updated_at DESC LIMIT $limit;", cancellationToken, ("$limit", Math.Clamp(limit, 1, 500)));
    }

    public async Task<IReadOnlyList<Job>> GetActiveAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await ReadManyAsync(connection, null,
            "SELECT * FROM job WHERE state IN ('Pending','Running','Waiting','Paused','RetryWaiting') ORDER BY updated_at DESC;",
            cancellationToken);
    }

    public async Task<Job?> GetJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadSingleAsync(connection, null, "SELECT * FROM job WHERE job_id=$id;", cancellationToken,
            ("$id", jobId.ToString("D"))).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<JobEvent>> GetEventsAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Create(connection, null, "SELECT * FROM job_event WHERE job_id=$id ORDER BY occurred_at DESC;", ("$id", jobId.ToString("D")));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var events = new List<JobEvent>();
        while (await reader.ReadAsync(cancellationToken)) events.Add(new JobEvent(Guid.Parse(reader.GetString(reader.GetOrdinal("job_event_id"))), Guid.Parse(reader.GetString(reader.GetOrdinal("job_id"))), Parse(reader.GetString(reader.GetOrdinal("occurred_at"))), reader.GetString(reader.GetOrdinal("event_name")), reader.IsDBNull(reader.GetOrdinal("detail_json")) ? null : reader.GetString(reader.GetOrdinal("detail_json"))));
        return events;
    }

    private static async Task InsertJobAsync(SqliteConnection c, SqliteTransaction? tx, Job job, CancellationToken cancellationToken) => await ExecuteAsync(c, tx, "INSERT INTO job(job_id,job_type,state,phase,reason,payload_json,attempt,max_attempts,not_before,started_at,completed_at,heartbeat_at,progress_current,progress_total,progress_unit,progress_indeterminate,last_error_code,last_error_message,created_at,updated_at) VALUES($id,$type,$state,$phase,$reason,$payload,$attempt,$max,$notBefore,NULL,NULL,NULL,$current,$total,$unit,$indeterminate,NULL,NULL,$created,$updated);", cancellationToken, ("$id", job.JobId.ToString("D")), ("$type", job.JobType.ToString()), ("$state", job.State.ToString()), ("$phase", job.Phase), ("$reason", job.Reason.ToString()), ("$payload", job.PayloadJson), ("$attempt", job.Attempt), ("$max", job.MaxAttempts), ("$notBefore", job.NotBefore is null ? DBNull.Value : Db(job.NotBefore.Value)), ("$current", job.Progress.Current), ("$total", job.Progress.Total), ("$unit", job.Progress.Unit), ("$indeterminate", job.Progress.IsIndeterminate ? 1 : 0), ("$created", Db(job.CreatedAt)), ("$updated", Db(job.UpdatedAt)));
    private static async Task AddEventAsync(SqliteConnection c, SqliteTransaction? tx, Guid jobId, string name, object? detail, CancellationToken cancellationToken) => await ExecuteAsync(c, tx, "INSERT INTO job_event(job_event_id,job_id,occurred_at,event_name,detail_json) VALUES($id,$job,$at,$name,$detail);", cancellationToken, ("$id", Guid.NewGuid().ToString("D")), ("$job", jobId.ToString("D")), ("$at", Db(DateTimeOffset.UtcNow)), ("$name", name), ("$detail", detail is null ? DBNull.Value : JsonSerializer.Serialize(detail)));
    private static async Task ExecuteAsync(SqliteConnection c, SqliteTransaction? tx, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] p) { await using var cmd = Create(c, tx, sql, p); await cmd.ExecuteNonQueryAsync(cancellationToken); }
    private static async Task<Job?> ReadSingleAsync(SqliteConnection c, SqliteTransaction? tx, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] p) { await using var cmd = Create(c, tx, sql, p); await using var r = await cmd.ExecuteReaderAsync(cancellationToken); return await r.ReadAsync(cancellationToken) ? Read(r) : null; }
    private static async Task<List<Job>> ReadManyAsync(SqliteConnection c, SqliteTransaction? tx, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] p) { await using var cmd = Create(c, tx, sql, p); await using var r = await cmd.ExecuteReaderAsync(cancellationToken); var jobs = new List<Job>(); while (await r.ReadAsync(cancellationToken)) jobs.Add(Read(r)); return jobs; }
    private static SqliteCommand Create(SqliteConnection c, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] p) { var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; foreach (var item in p) cmd.Parameters.AddWithValue(item.Name, item.Value ?? DBNull.Value); return cmd; }
    private static Job Read(SqliteDataReader r) => new(Guid.Parse(r.GetString(r.GetOrdinal("job_id"))), Enum.Parse<JobType>(r.GetString(r.GetOrdinal("job_type"))), Enum.Parse<JobState>(r.GetString(r.GetOrdinal("state"))), r.GetString(r.GetOrdinal("phase")), Enum.Parse<JobReason>(r.GetString(r.GetOrdinal("reason"))), r.GetString(r.GetOrdinal("payload_json")), r.GetInt32(r.GetOrdinal("attempt")), r.GetInt32(r.GetOrdinal("max_attempts")), NullableTime(r, "not_before"), NullableTime(r, "started_at"), NullableTime(r, "completed_at"), NullableTime(r, "heartbeat_at"), new JobProgress(r.GetInt64(r.GetOrdinal("progress_current")), r.GetInt64(r.GetOrdinal("progress_total")), r.GetString(r.GetOrdinal("progress_unit")), r.GetInt64(r.GetOrdinal("progress_indeterminate")) != 0), NullableString(r, "last_error_code"), NullableString(r, "last_error_message"), Parse(r.GetString(r.GetOrdinal("created_at"))), Parse(r.GetString(r.GetOrdinal("updated_at"))));
    private static string? NullableString(SqliteDataReader r, string column) => r.IsDBNull(r.GetOrdinal(column)) ? null : r.GetString(r.GetOrdinal(column));
    private static DateTimeOffset? NullableTime(SqliteDataReader r, string column) => r.IsDBNull(r.GetOrdinal(column)) ? null : Parse(r.GetString(r.GetOrdinal(column)));
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    private static string Db(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static Guid ReadLegacyMediaFileId(Job job)
    {
        try
        {
            return job.JobType switch
            {
                JobType.Metadata or JobType.Thumbnail => JsonSerializer.Deserialize<MediaPayload>(job.PayloadJson)!.MediaFileId,
                JobType.FullHash => JsonSerializer.Deserialize<FullHashPayload>(job.PayloadJson)!.MediaFileId,
                JobType.VisualSampling => JsonSerializer.Deserialize<VisualSamplingPayload>(job.PayloadJson)!.MediaFileId,
                JobType.NearDuplicateFingerprint => JsonSerializer.Deserialize<NearDuplicateFingerprintPayload>(job.PayloadJson)!.MediaFileId,
                _ => throw new InvalidOperationException("The Job is not a legacy original-source kind.")
            };
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("A legacy source Job payload cannot be transitioned safely.", ex);
        }
    }

    private static ProtectedSourceStageEligibility MapLegacyStage(JobType type) => type switch
    {
        // Metadata historically schedules Thumbnail, Visual Sampling and Near Duplicate
        // after success, so preserve that existing eligibility relationship.
        JobType.Metadata => ProtectedSourceStageEligibility.Metadata |
                            ProtectedSourceStageEligibility.Thumbnail |
                            ProtectedSourceStageEligibility.VisualSampling |
                            ProtectedSourceStageEligibility.NearDuplicateFingerprint,
        JobType.Thumbnail => ProtectedSourceStageEligibility.Thumbnail,
        JobType.FullHash => ProtectedSourceStageEligibility.FullHash,
        JobType.VisualSampling => ProtectedSourceStageEligibility.VisualSampling,
        JobType.NearDuplicateFingerprint => ProtectedSourceStageEligibility.NearDuplicateFingerprint,
        _ => ProtectedSourceStageEligibility.None
    };

    private static ProtectedSourceOrchestrationPayload DeserializeProtectedPayload(string json) =>
        JsonSerializer.Deserialize<ProtectedSourceOrchestrationPayload>(json)
        ?? throw new InvalidDataException("A protected orchestration payload is invalid.");

    private static async Task<(Guid MediaContentId, PresenceState PresenceState)?> ReadCurrentMediaIdentityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid mediaFileId,
        CancellationToken cancellationToken)
    {
        await using var command = Create(connection, transaction,
            "SELECT media_content_id,presence_state FROM media_file WHERE media_file_id=$id;",
            ("$id", mediaFileId.ToString("D")));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var state = Enum.Parse<PresenceState>(reader.GetString(1));
        return state == PresenceState.Present ? (Guid.Parse(reader.GetString(0)), state) : null;
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = Create(connection, transaction, sql, parameters);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record ProtectedRequest(ProtectedSourceStageEligibility Stages, int MaxAttempts);
}
