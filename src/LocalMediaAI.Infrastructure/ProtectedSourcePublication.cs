using System.Text.Json;
using LocalMediaAI.Core;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure;

public enum GuardedPublicationDisposition { Published, Superseded, AlreadyFinalized }

public sealed record PreparedProtectedArtifact(Artifact Artifact, string FullPath);

public sealed record ProtectedAnalysisPublication(
    Analysis Analysis,
    IReadOnlyList<PreparedProtectedArtifact> Artifacts);

public sealed record ProtectedSourcePublicationBundle(
    Guid SessionId,
    Guid MediaFileId,
    Guid ExpectedMediaContentId,
    MediaMetadata? Metadata,
    IReadOnlyList<VisualSample> VisualSamples,
    IReadOnlyList<ProtectedAnalysisPublication> Analyses,
    FileHashEvidence? FullHashEvidence = null,
    IReadOnlyList<NearDuplicateFrame>? NearDuplicateFrames = null);

public sealed record GuardedPublicationResult(
    GuardedPublicationDisposition Disposition,
    Guid SessionId,
    Guid MediaFileId,
    Guid ExpectedMediaContentId);

/// <summary>
/// Prepares an app-owned immutable artifact before the short SQLite publication
/// transaction. The artifact is not current merely because this method succeeds;
/// only a later committed DB reference can expose it.
/// </summary>
public static class ProtectedArtifactPreparation
{
    public static async Task<PreparedProtectedArtifact> PrepareAsync(
        ProcessingSourceSession session,
        AppPaths paths,
        UnpublishedProcessingArtifact staged,
        Artifact artifact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(staged);
        ArgumentNullException.ThrowIfNull(artifact);
        if (!session.IsLeaseHeld || staged.SessionId != session.SessionId || staged.IsPublished)
            throw new InvalidOperationException("Only an active session may prepare its unpublished artifact.");
        if (!session.UnpublishedArtifacts.Contains(staged) || !File.Exists(staged.FullPath) ||
            PathPolicy.HasReparsePointAncestor(session.StagingDirectory, staged.FullPath))
            throw new InvalidOperationException("The staged artifact is not a recognized session-owned file.");
        if (Path.IsPathRooted(artifact.RelativePath))
            throw new InvalidOperationException("A published artifact path must be relative to the app cache.");

        var normalized = artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var destination = Path.GetFullPath(Path.Combine(paths.CacheRoot, normalized));
        if (!PathPolicy.IsWithinRoot(paths.CacheRoot, destination))
            throw new InvalidOperationException("The prepared artifact escaped the app-owned cache root.");
        var parent = Path.GetDirectoryName(destination) ?? throw new InvalidOperationException("The prepared artifact parent is unavailable.");
        Directory.CreateDirectory(parent);
        if (PathPolicy.HasReparsePointAncestor(paths.CacheRoot, destination))
            throw new InvalidOperationException("The prepared artifact path crosses a reparse point.");

        var temporary = destination + ".prepare-" + session.SessionId.ToString("N");
        if (!PathPolicy.IsWithinRoot(paths.CacheRoot, temporary))
            throw new InvalidOperationException("The artifact preparation path escaped the app-owned cache root.");
        try
        {
            if (!File.Exists(destination))
            {
                await using (var source = new FileStream(staged.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }
                File.Move(temporary, destination, overwrite: false);
            }

            var info = new FileInfo(destination);
            if (info.Length != artifact.ByteSize)
                throw new InvalidDataException("The prepared artifact byte length does not match its publication record.");
            var digest = await FileHash.Sha256Async(destination, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(digest, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The prepared artifact digest does not match its publication record.");
            return new PreparedProtectedArtifact(artifact, destination);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

public sealed partial class SqliteLibraryRepository
{
    /// <summary>
    /// Candidate-only B-3a publication boundary. All media work and artifact
    /// preparation must be complete before entry. The transaction contains only
    /// current-content validation and bounded SQLite writes/finalization.
    /// </summary>
    public async Task<GuardedPublicationResult> GuardedPublishProtectedSourceAsync(
        ProcessingSourceSession session,
        Guid orchestrationJobId,
        ProtectedSourcePublicationBundle bundle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(bundle);
        if (!session.IsLeaseHeld || session.Phase != ProcessingSourceSessionPhase.DerivedOrPublication)
            throw new InvalidOperationException("Guarded publication requires a live session at the derived/publication boundary.");
        if (bundle.SessionId != session.SessionId || bundle.MediaFileId != session.MediaFileId ||
            bundle.ExpectedMediaContentId != session.ExpectedMediaContentId)
            throw new InvalidOperationException("The publication bundle does not belong to the authoritative source session.");
        ValidateBundle(bundle);

        // Acquire read/delete exclusion for every prepared app-owned artifact before
        // beginning SQLite work. File I/O and hashing remain outside the transaction.
        var artifactLeases = new List<FileStream>();
        try
        {
            var preparedPaths = bundle.Analyses.SelectMany(value => value.Artifacts)
                .Select(value => (value.FullPath, (long?)value.Artifact.ByteSize))
                .Concat(bundle.VisualSamples.Where(value => value.Status == VisualSampleStatus.Completed && value.FrameRelativePath is not null)
                    .Select(value => (Path.GetFullPath(Path.Combine(_paths.CacheRoot, value.FrameRelativePath!.Replace('/', Path.DirectorySeparatorChar))), value.FrameByteSize)))
                .Concat((bundle.NearDuplicateFrames ?? []).Where(value => value.Status == AnalysisStatus.Completed && value.FrameRelativePath is not null)
                    .Select(value => (Path.GetFullPath(Path.Combine(_paths.CacheRoot, value.FrameRelativePath!.Replace('/', Path.DirectorySeparatorChar))), value.FrameByteSize)))
                .DistinctBy(value => value.Item1, StringComparer.OrdinalIgnoreCase);
            foreach (var (fullPath, expectedLength) in preparedPaths)
            {
                var expected = Path.GetFullPath(fullPath);
                if (!PathPolicy.IsWithinRoot(_paths.CacheRoot, expected) || PathPolicy.HasReparsePointAncestor(_paths.CacheRoot, expected))
                    throw new InvalidOperationException("A prepared artifact is outside the app-owned cache contract.");
                var handle = new FileStream(expected, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (expectedLength is { } length && handle.Length != length)
                {
                    handle.Dispose();
                    throw new InvalidDataException("A prepared artifact changed before publication.");
                }
                artifactLeases.Add(handle);
            }

            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var job = await QuerySingleAsync(connection, transaction, "SELECT state,phase,payload_json FROM job WHERE job_id=$id AND job_type='ProtectedSourceOrchestration';",
                reader => new CandidateJobState(reader.GetString(0), reader.GetString(1), reader.GetString(2)), cancellationToken,
                ("$id", orchestrationJobId.ToString("D"))).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The protected orchestration Job does not exist.");
            var payload = JsonSerializer.Deserialize<ProtectedSourceOrchestrationPayload>(job.PayloadJson)
                ?? throw new InvalidDataException("The protected orchestration payload is invalid.");
            if (payload.MediaFileId != session.MediaFileId || payload.ExpectedMediaContentId != session.ExpectedMediaContentId)
                throw new InvalidOperationException("The durable orchestration identity does not match the source session.");
            if (job.State == JobState.Completed.ToString() && job.Phase == "GuardedPublished")
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new(GuardedPublicationDisposition.AlreadyFinalized, session.SessionId, session.MediaFileId, session.ExpectedMediaContentId);
            }
            if (job.State != JobState.Running.ToString())
                throw new InvalidOperationException("Guarded publication requires a Running protected orchestration Job.");

            var current = await ScalarLongAsync(connection, transaction,
                "SELECT COUNT(1) FROM media_file WHERE media_file_id=$file AND media_content_id=$content AND presence_state='Present';",
                cancellationToken,
                ("$file", session.MediaFileId.ToString("D")),
                ("$content", session.ExpectedMediaContentId.ToString("D"))).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            if (current != 1)
            {
                await ExecuteNonQueryAsync(connection, transaction,
                    "UPDATE job SET state='Partial',phase='SupersededSourceVersion',reason='None',completed_at=$at,heartbeat_at=$at,updated_at=$at,last_error_code='SOURCE_VERSION_SUPERSEDED',last_error_message='The source content changed before guarded publication.' WHERE job_id=$id AND state='Running';",
                    cancellationToken, ("$at", ToDb(now)), ("$id", orchestrationJobId.ToString("D"))).ConfigureAwait(false);
                await AddProtectedJobEventAsync(connection, transaction, orchestrationJobId, "ProtectedPublicationRejected", new { session.SessionId, session.ExpectedMediaContentId, Reason = "SupersededSourceVersion" }, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new(GuardedPublicationDisposition.Superseded, session.SessionId, session.MediaFileId, session.ExpectedMediaContentId);
            }

            if (bundle.Metadata is { } metadata)
            {
                await ExecuteNonQueryAsync(connection, transaction,
                    "INSERT INTO media_metadata(media_file_id,container_format,duration_seconds,width,height,video_codec,fps_text,bitrate,audio_stream_count,subtitle_stream_count,raw_summary_json,updated_at) VALUES($id,$format,$duration,$width,$height,$codec,$fps,$bitrate,$audio,$subtitle,$raw,$updated) ON CONFLICT(media_file_id) DO UPDATE SET container_format=excluded.container_format,duration_seconds=excluded.duration_seconds,width=excluded.width,height=excluded.height,video_codec=excluded.video_codec,fps_text=excluded.fps_text,bitrate=excluded.bitrate,audio_stream_count=excluded.audio_stream_count,subtitle_stream_count=excluded.subtitle_stream_count,raw_summary_json=excluded.raw_summary_json,updated_at=excluded.updated_at;",
                    cancellationToken,
                    ("$id", metadata.MediaFileId.ToString("D")), ("$format", (object?)metadata.ContainerFormat ?? DBNull.Value),
                    ("$duration", (object?)metadata.DurationSeconds ?? DBNull.Value), ("$width", (object?)metadata.Width ?? DBNull.Value),
                    ("$height", (object?)metadata.Height ?? DBNull.Value), ("$codec", (object?)metadata.VideoCodec ?? DBNull.Value),
                    ("$fps", (object?)metadata.FpsText ?? DBNull.Value), ("$bitrate", (object?)metadata.Bitrate ?? DBNull.Value),
                    ("$audio", metadata.AudioStreamCount), ("$subtitle", metadata.SubtitleStreamCount), ("$raw", metadata.RawSummaryJson),
                    ("$updated", ToDb(metadata.UpdatedAt))).ConfigureAwait(false);
            }

            foreach (var sample in bundle.VisualSamples)
            {
                await ExecuteNonQueryAsync(connection, transaction,
                    "INSERT INTO visual_sample(visual_sample_id,media_file_id,media_content_id,input_identity_hash,sample_index,requested_timestamp_seconds,presentation_timestamp_seconds,sampling_version,cache_key,frame_relative_path,frame_sha256,frame_byte_size,status,error_code,error_message,created_at,updated_at) VALUES($id,$file,$content,$input,$index,$requested,$presentation,$version,$cache,$path,$sha,$bytes,$status,$code,$message,$created,$updated) ON CONFLICT(cache_key) DO UPDATE SET media_file_id=excluded.media_file_id,media_content_id=excluded.media_content_id,frame_relative_path=excluded.frame_relative_path,frame_sha256=excluded.frame_sha256,frame_byte_size=excluded.frame_byte_size,status=excluded.status,error_code=excluded.error_code,error_message=excluded.error_message,updated_at=excluded.updated_at;",
                    cancellationToken,
                    ("$id", sample.VisualSampleId.ToString("D")), ("$file", sample.MediaFileId.ToString("D")), ("$content", sample.MediaContentId.ToString("D")),
                    ("$input", sample.InputIdentityHash), ("$index", sample.SampleIndex), ("$requested", sample.RequestedTimestampSeconds),
                    ("$presentation", sample.PresentationTimestampSeconds), ("$version", sample.SamplingVersion), ("$cache", sample.CacheKey),
                    ("$path", (object?)sample.FrameRelativePath ?? DBNull.Value), ("$sha", (object?)sample.FrameSha256 ?? DBNull.Value),
                    ("$bytes", (object?)sample.FrameByteSize ?? DBNull.Value), ("$status", sample.Status.ToString()),
                    ("$code", (object?)sample.ErrorCode ?? DBNull.Value), ("$message", (object?)sample.ErrorMessage ?? DBNull.Value),
                    ("$created", ToDb(sample.CreatedAt)), ("$updated", ToDb(sample.UpdatedAt))).ConfigureAwait(false);
            }

            if (bundle.FullHashEvidence is { } hash)
            {
                await ExecuteNonQueryAsync(connection, transaction,
                    "INSERT INTO file_hash_evidence(file_hash_evidence_id,media_file_id,media_content_id,algorithm,digest,source_size_bytes,source_last_write_time_utc,processing_version,status,error_code,error_message,computed_at) VALUES($id,$file,$content,$algorithm,$digest,$size,$mtime,$version,'Verified',NULL,NULL,$computed) ON CONFLICT(media_file_id,media_content_id,algorithm,processing_version) DO UPDATE SET digest=excluded.digest,source_size_bytes=excluded.source_size_bytes,source_last_write_time_utc=excluded.source_last_write_time_utc,status='Verified',error_code=NULL,error_message=NULL,computed_at=excluded.computed_at;",
                    cancellationToken, ("$id", hash.FileHashEvidenceId.ToString("D")), ("$file", hash.MediaFileId.ToString("D")),
                    ("$content", hash.MediaContentId.ToString("D")), ("$algorithm", hash.Algorithm), ("$digest", hash.Digest),
                    ("$size", hash.SourceSizeBytes), ("$mtime", ToDb(hash.SourceLastWriteTimeUtc)), ("$version", hash.ProcessingVersion),
                    ("$computed", ToDb(hash.ComputedAt))).ConfigureAwait(false);
                await ExecuteNonQueryAsync(connection, transaction,
                    "UPDATE media_file SET full_sha256=$digest WHERE media_file_id=$file AND media_content_id=$content AND size_bytes=$size AND last_write_time_utc=$mtime;",
                    cancellationToken, ("$digest", hash.Digest), ("$file", hash.MediaFileId.ToString("D")), ("$content", hash.MediaContentId.ToString("D")),
                    ("$size", hash.SourceSizeBytes), ("$mtime", ToDb(hash.SourceLastWriteTimeUtc))).ConfigureAwait(false);
                await ExecuteNonQueryAsync(connection, transaction,
                    "UPDATE media_content SET canonical_sha256=$digest,identity_state='FullHashVerified' WHERE media_content_id=$content AND byte_length=$size;",
                    cancellationToken, ("$digest", hash.Digest), ("$content", hash.MediaContentId.ToString("D")), ("$size", hash.SourceSizeBytes)).ConfigureAwait(false);
            }

            foreach (var frame in bundle.NearDuplicateFrames ?? [])
            {
                await ExecuteNonQueryAsync(connection, transaction,
                    "INSERT INTO near_duplicate_frame(near_duplicate_frame_id,media_file_id,media_content_id,source_size_bytes,source_last_write_time_utc,sample_index,normalized_position,requested_timestamp_seconds,presentation_timestamp_seconds,sampling_version,fingerprint_algorithm,fingerprint_hex,frame_relative_path,frame_sha256,frame_byte_size,status,error_code,error_message,created_at,updated_at) VALUES($id,$file,$content,$size,$mtime,$index,$position,$requested,$presentation,$sampling,$algorithm,$fingerprint,$path,$sha,$bytes,$status,$errorCode,$errorMessage,$created,$updated) ON CONFLICT(media_file_id,media_content_id,sampling_version,fingerprint_algorithm,sample_index) DO UPDATE SET source_size_bytes=excluded.source_size_bytes,source_last_write_time_utc=excluded.source_last_write_time_utc,normalized_position=excluded.normalized_position,requested_timestamp_seconds=excluded.requested_timestamp_seconds,presentation_timestamp_seconds=excluded.presentation_timestamp_seconds,fingerprint_hex=excluded.fingerprint_hex,frame_relative_path=excluded.frame_relative_path,frame_sha256=excluded.frame_sha256,frame_byte_size=excluded.frame_byte_size,status=excluded.status,error_code=excluded.error_code,error_message=excluded.error_message,updated_at=excluded.updated_at;",
                    cancellationToken, ("$id", frame.NearDuplicateFrameId.ToString("D")), ("$file", frame.MediaFileId.ToString("D")),
                    ("$content", frame.MediaContentId.ToString("D")), ("$size", frame.SourceSizeBytes), ("$mtime", ToDb(frame.SourceLastWriteTimeUtc)),
                    ("$index", frame.SampleIndex), ("$position", frame.NormalizedPosition), ("$requested", frame.RequestedTimestampSeconds),
                    ("$presentation", frame.PresentationTimestampSeconds), ("$sampling", frame.SamplingVersion), ("$algorithm", frame.FingerprintAlgorithm),
                    ("$fingerprint", (object?)frame.FingerprintHex ?? DBNull.Value), ("$path", (object?)frame.FrameRelativePath ?? DBNull.Value),
                    ("$sha", (object?)frame.FrameSha256 ?? DBNull.Value), ("$bytes", (object?)frame.FrameByteSize ?? DBNull.Value),
                    ("$status", frame.Status.ToString()), ("$errorCode", (object?)frame.ErrorCode ?? DBNull.Value),
                    ("$errorMessage", (object?)frame.ErrorMessage ?? DBNull.Value), ("$created", ToDb(frame.CreatedAt)), ("$updated", ToDb(frame.UpdatedAt))).ConfigureAwait(false);
                await ExecuteNonQueryAsync(connection, transaction,
                    "DELETE FROM near_duplicate_lsh_bucket WHERE near_duplicate_frame_id=(SELECT near_duplicate_frame_id FROM near_duplicate_frame WHERE media_file_id=$file AND media_content_id=$content AND sampling_version=$sampling AND fingerprint_algorithm=$algorithm AND sample_index=$index);",
                    cancellationToken, ("$file", frame.MediaFileId.ToString("D")), ("$content", frame.MediaContentId.ToString("D")),
                    ("$sampling", frame.SamplingVersion), ("$algorithm", frame.FingerprintAlgorithm), ("$index", frame.SampleIndex)).ConfigureAwait(false);
                if (frame.Status == AnalysisStatus.Completed && frame.FingerprintHex is not null)
                {
                    foreach (var (band, index) in PerceptualFrameFingerprint.CreateLshBands(frame.FingerprintHex).Select((value, index) => (value, index)))
                        await ExecuteNonQueryAsync(connection, transaction,
                            "INSERT INTO near_duplicate_lsh_bucket(near_duplicate_frame_id,sampling_version,fingerprint_algorithm,sample_index,band_index,band_value) SELECT near_duplicate_frame_id,$sampling,$algorithm,$sample,$band,$value FROM near_duplicate_frame WHERE media_file_id=$file AND media_content_id=$content AND sampling_version=$sampling AND fingerprint_algorithm=$algorithm AND sample_index=$sample;",
                            cancellationToken, ("$file", frame.MediaFileId.ToString("D")), ("$content", frame.MediaContentId.ToString("D")),
                            ("$sampling", frame.SamplingVersion), ("$algorithm", frame.FingerprintAlgorithm), ("$sample", frame.SampleIndex),
                            ("$band", index), ("$value", band)).ConfigureAwait(false);
                }
            }

            foreach (var publication in bundle.Analyses)
            {
                var analysis = publication.Analysis;
                await ExecuteNonQueryAsync(connection, transaction,
                    "INSERT INTO analysis(analysis_id,media_content_id,analysis_type,status,processor_name,processor_version,model_name,model_version,config_json,config_hash,input_identity_hash,processing_version,started_at,completed_at,error_code,error_message) VALUES($id,$content,$type,$status,$processor,$version,$model,$modelVersion,$config,$hash,$input,$processing,$started,$completed,$code,$message) ON CONFLICT(analysis_id) DO NOTHING;",
                    cancellationToken,
                    ("$id", analysis.AnalysisId.ToString("D")), ("$content", analysis.MediaContentId.ToString("D")), ("$type", analysis.AnalysisType),
                    ("$status", analysis.Status.ToString()), ("$processor", analysis.ProcessorName), ("$version", analysis.ProcessorVersion),
                    ("$model", (object?)analysis.ModelName ?? DBNull.Value), ("$modelVersion", (object?)analysis.ModelVersion ?? DBNull.Value),
                    ("$config", analysis.ConfigJson), ("$hash", analysis.ConfigHash), ("$input", analysis.InputIdentityHash),
                    ("$processing", analysis.ProcessingVersion), ("$started", analysis.StartedAt is { } started ? ToDb(started) : DBNull.Value),
                    ("$completed", analysis.CompletedAt is { } completed ? ToDb(completed) : DBNull.Value),
                    ("$code", (object?)analysis.ErrorCode ?? DBNull.Value), ("$message", (object?)analysis.ErrorMessage ?? DBNull.Value)).ConfigureAwait(false);
                foreach (var prepared in publication.Artifacts)
                {
                    var artifact = prepared.Artifact;
                    await ExecuteNonQueryAsync(connection, transaction,
                        "INSERT INTO artifact(artifact_id,analysis_id,artifact_kind,relative_path,sha256,byte_size,mime_type,durability_class,created_at) VALUES($id,$analysis,$kind,$path,$hash,$bytes,$mime,$durability,$created) ON CONFLICT(analysis_id,artifact_kind,relative_path) DO NOTHING;",
                        cancellationToken,
                        ("$id", artifact.ArtifactId.ToString("D")), ("$analysis", artifact.AnalysisId.ToString("D")), ("$kind", artifact.ArtifactKind),
                        ("$path", artifact.RelativePath), ("$hash", artifact.Sha256), ("$bytes", artifact.ByteSize), ("$mime", artifact.MimeType),
                        ("$durability", artifact.DurabilityClass.ToString()), ("$created", ToDb(artifact.CreatedAt))).ConfigureAwait(false);
                }
            }

            await ExecuteNonQueryAsync(connection, transaction,
                "UPDATE job SET state='Completed',phase='GuardedPublished',reason='None',completed_at=$at,heartbeat_at=$at,updated_at=$at,last_error_code=NULL,last_error_message=NULL WHERE job_id=$id AND state='Running';",
                cancellationToken, ("$at", ToDb(now)), ("$id", orchestrationJobId.ToString("D"))).ConfigureAwait(false);
            await AddProtectedJobEventAsync(connection, transaction, orchestrationJobId, "ProtectedPublicationCommitted", new { session.SessionId, session.ExpectedMediaContentId }, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(GuardedPublicationDisposition.Published, session.SessionId, session.MediaFileId, session.ExpectedMediaContentId);
        }
        finally
        {
            foreach (var lease in artifactLeases) lease.Dispose();
        }
    }

    private static void ValidateBundle(ProtectedSourcePublicationBundle bundle)
    {
        if (bundle.SessionId == Guid.Empty || bundle.MediaFileId == Guid.Empty || bundle.ExpectedMediaContentId == Guid.Empty)
            throw new InvalidOperationException("A protected publication bundle requires complete identity.");
        if (bundle.Metadata is { } metadata && metadata.MediaFileId != bundle.MediaFileId)
            throw new InvalidOperationException("Metadata does not belong to the protected media file.");
        foreach (var sample in bundle.VisualSamples)
            if (sample.MediaFileId != bundle.MediaFileId || sample.MediaContentId != bundle.ExpectedMediaContentId)
                throw new InvalidOperationException("A visual sample does not belong to the protected content generation.");
        foreach (var publication in bundle.Analyses)
        {
            if (publication.Analysis.MediaContentId != bundle.ExpectedMediaContentId ||
                publication.Analysis.Status is not (AnalysisStatus.Completed or AnalysisStatus.Partial))
                throw new InvalidOperationException("An analysis is not publication-ready for the protected content generation.");
            if (publication.Artifacts.Any(value => value.Artifact.AnalysisId != publication.Analysis.AnalysisId))
                throw new InvalidOperationException("An artifact does not belong to its protected analysis.");
        }
        if (bundle.FullHashEvidence is { } hash &&
            (hash.MediaFileId != bundle.MediaFileId || hash.MediaContentId != bundle.ExpectedMediaContentId ||
             !string.Equals(hash.Algorithm, "SHA-256", StringComparison.Ordinal) || !string.Equals(hash.Status, "Verified", StringComparison.Ordinal)))
            throw new InvalidOperationException("Full SHA-256 evidence does not belong to the protected content generation.");
        foreach (var frame in bundle.NearDuplicateFrames ?? [])
            if (frame.MediaFileId != bundle.MediaFileId || frame.MediaContentId != bundle.ExpectedMediaContentId)
                throw new InvalidOperationException("A Near Duplicate frame does not belong to the protected content generation.");
    }

    private static async Task AddProtectedJobEventAsync(SqliteConnection connection, SqliteTransaction transaction, Guid jobId, string eventName, object detail, CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(connection, transaction,
            "INSERT INTO job_event(job_event_id,job_id,occurred_at,event_name,detail_json) VALUES($id,$job,$at,$event,$detail);",
            cancellationToken,
            ("$id", Guid.NewGuid().ToString("D")), ("$job", jobId.ToString("D")), ("$at", Now()),
            ("$event", eventName), ("$detail", JsonSerializer.Serialize(detail))).ConfigureAwait(false);
    }

    private sealed record CandidateJobState(string State, string Phase, string PayloadJson);
}

/// <summary>Explicit pre-activation seam; normal MediaPipeline does not reference this type.</summary>
public sealed class PreActivationProtectedSourceOrchestrator(
    AppPaths paths,
    SqliteLibraryRepository repository,
    SqliteJobStore jobs,
    IStructuredLogger log,
    bool handleFailureInternally = true)
{
    public async Task<GuardedPublicationResult> ExecuteAsync(
        Job runningJob,
        MediaFile media,
        Func<ProcessingSourceSession, CancellationToken, Task<ProtectedSourcePublicationBundle>> prepare,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runningJob);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(prepare);
        if (runningJob.JobType != JobType.ProtectedSourceOrchestration || runningJob.State != JobState.Running)
            throw new InvalidOperationException("The pre-activation candidate requires an acquired protected orchestration Job.");
        var payload = JsonSerializer.Deserialize<ProtectedSourceOrchestrationPayload>(runningJob.PayloadJson)
            ?? throw new InvalidDataException("The protected orchestration payload is invalid.");
        if (payload.MediaFileId != media.MediaFileId || payload.ExpectedMediaContentId != media.MediaContentId)
            throw new InvalidOperationException("The protected Job does not match the requested media generation.");

        try
        {
            await using var session = await ProcessingSourceSession.AcquireAsync(paths, media.MediaFileId, media.MediaContentId, media.Path, log, cancellationToken).ConfigureAwait(false);
            var bundle = await prepare(session, cancellationToken).ConfigureAwait(false);
            session.CompleteSourceStaging();
            session.EnterDerivedOrPublicationPhase();
            var result = await repository.GuardedPublishProtectedSourceAsync(session, runningJob.JobId, bundle, cancellationToken).ConfigureAwait(false);
            session.CompletePublicationBoundary(result.Disposition == GuardedPublicationDisposition.Published || result.Disposition == GuardedPublicationDisposition.AlreadyFinalized
                ? ProcessingPublicationResolution.Published
                : ProcessingPublicationResolution.Rejected);
            return result;
        }
        catch
        {
            if (handleFailureInternally)
            {
                var current = await jobs.GetJobAsync(runningJob.JobId, CancellationToken.None).ConfigureAwait(false);
                if (current?.State == JobState.Running)
                    await jobs.FailOrRetryAsync(runningJob.JobId, "PROTECTED_SOURCE_ATTEMPT_FAILED", L.T("保護されたソース処理を完了できませんでした。"), true, CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
    }
}
