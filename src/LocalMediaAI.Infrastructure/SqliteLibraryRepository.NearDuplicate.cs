using LocalMediaAI.Core;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure;

public sealed partial class SqliteLibraryRepository
{
    public async Task<IReadOnlyList<MediaFile>> GetNearDuplicateFingerprintCandidatesAsync(Guid? rootId, string samplingVersion, string fingerprintAlgorithm, CancellationToken cancellationToken)
    {
        const string sql = "SELECT mf.* FROM media_file mf JOIN media_metadata mm ON mm.media_file_id=mf.media_file_id WHERE mf.presence_state='Present' AND mm.duration_seconds>0 AND ($root IS NULL OR mf.library_root_id=$root) AND (SELECT COUNT(1) FROM near_duplicate_frame nf WHERE nf.media_file_id=mf.media_file_id AND nf.media_content_id=mf.media_content_id AND nf.source_size_bytes=mf.size_bytes AND nf.source_last_write_time_utc=mf.last_write_time_utc AND nf.sampling_version=$sampling AND nf.fingerprint_algorithm=$algorithm AND nf.status='Completed') < 5 ORDER BY mf.normalized_path;";
        await using var connection = await OpenAsync(cancellationToken);
        return await QueryAsync(connection, null, sql, ReadMediaFile, cancellationToken, ("$root", rootId?.ToString("D") ?? (object)DBNull.Value), ("$sampling", samplingVersion), ("$algorithm", fingerprintAlgorithm));
    }

    public async Task<IReadOnlyList<NearDuplicateFrame>> GetCurrentNearDuplicateFramesAsync(Guid mediaFileId, Guid mediaContentId, string samplingVersion, string fingerprintAlgorithm, CancellationToken cancellationToken)
    {
        const string sql = "SELECT nf.* FROM near_duplicate_frame nf JOIN media_file mf ON mf.media_file_id=nf.media_file_id WHERE nf.media_file_id=$file AND nf.media_content_id=$content AND nf.sampling_version=$sampling AND nf.fingerprint_algorithm=$algorithm AND nf.status='Completed' AND mf.media_content_id=nf.media_content_id AND mf.presence_state='Present' AND mf.size_bytes=nf.source_size_bytes AND mf.last_write_time_utc=nf.source_last_write_time_utc ORDER BY nf.sample_index;";
        await using var connection = await OpenAsync(cancellationToken);
        return await QueryAsync(connection, null, sql, ReadNearDuplicateFrame, cancellationToken,
            ("$file", mediaFileId.ToString("D")), ("$content", mediaContentId.ToString("D")), ("$sampling", samplingVersion), ("$algorithm", fingerprintAlgorithm));
    }

    public async Task ReplaceNearDuplicateFramesAsync(Guid mediaFileId, Guid mediaContentId, long sourceSizeBytes, DateTimeOffset sourceLastWriteTimeUtc, string samplingVersion, string fingerprintAlgorithm, IReadOnlyList<NearDuplicateFrame> frames, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frames);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var current = await QuerySingleAsync(connection, transaction, "SELECT * FROM media_file WHERE media_file_id=$id;", ReadMediaFile, cancellationToken, ("$id", mediaFileId.ToString("D")));
        if (current is null || current.PresenceState != PresenceState.Present || current.MediaContentId != mediaContentId || current.SizeBytes != sourceSizeBytes || current.LastWriteTimeUtc != sourceLastWriteTimeUtc)
            throw new InvalidOperationException("Near Duplicate frames cannot be published for a stale source version.");

        foreach (var frame in frames)
        {
            if (frame.MediaFileId != mediaFileId || frame.MediaContentId != mediaContentId || frame.SourceSizeBytes != sourceSizeBytes || frame.SourceLastWriteTimeUtc != sourceLastWriteTimeUtc || frame.SamplingVersion != samplingVersion || frame.FingerprintAlgorithm != fingerprintAlgorithm)
                throw new ArgumentException("Near Duplicate frame source identity does not match the persistence request.", nameof(frames));
            await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO near_duplicate_frame(near_duplicate_frame_id,media_file_id,media_content_id,source_size_bytes,source_last_write_time_utc,sample_index,normalized_position,requested_timestamp_seconds,presentation_timestamp_seconds,sampling_version,fingerprint_algorithm,fingerprint_hex,frame_relative_path,frame_sha256,frame_byte_size,status,error_code,error_message,created_at,updated_at) VALUES($id,$file,$content,$size,$mtime,$index,$position,$requested,$presentation,$sampling,$algorithm,$fingerprint,$path,$sha,$bytes,$status,$errorCode,$errorMessage,$created,$updated) ON CONFLICT(media_file_id,media_content_id,sampling_version,fingerprint_algorithm,sample_index) DO UPDATE SET source_size_bytes=excluded.source_size_bytes,source_last_write_time_utc=excluded.source_last_write_time_utc,normalized_position=excluded.normalized_position,requested_timestamp_seconds=excluded.requested_timestamp_seconds,presentation_timestamp_seconds=excluded.presentation_timestamp_seconds,fingerprint_hex=excluded.fingerprint_hex,frame_relative_path=excluded.frame_relative_path,frame_sha256=excluded.frame_sha256,frame_byte_size=excluded.frame_byte_size,status=excluded.status,error_code=excluded.error_code,error_message=excluded.error_message,updated_at=excluded.updated_at;", cancellationToken,
                ("$id", frame.NearDuplicateFrameId.ToString("D")), ("$file", mediaFileId.ToString("D")), ("$content", mediaContentId.ToString("D")), ("$size", sourceSizeBytes), ("$mtime", ToDb(sourceLastWriteTimeUtc)), ("$index", frame.SampleIndex), ("$position", frame.NormalizedPosition), ("$requested", frame.RequestedTimestampSeconds), ("$presentation", frame.PresentationTimestampSeconds), ("$sampling", samplingVersion), ("$algorithm", fingerprintAlgorithm), ("$fingerprint", (object?)frame.FingerprintHex ?? DBNull.Value), ("$path", (object?)frame.FrameRelativePath ?? DBNull.Value), ("$sha", (object?)frame.FrameSha256 ?? DBNull.Value), ("$bytes", (object?)frame.FrameByteSize ?? DBNull.Value), ("$status", frame.Status.ToString()), ("$errorCode", (object?)frame.ErrorCode ?? DBNull.Value), ("$errorMessage", (object?)frame.ErrorMessage ?? DBNull.Value), ("$created", ToDb(frame.CreatedAt)), ("$updated", ToDb(frame.UpdatedAt)));

            var persisted = await QuerySingleAsync(connection, transaction, "SELECT * FROM near_duplicate_frame WHERE media_file_id=$file AND media_content_id=$content AND sampling_version=$sampling AND fingerprint_algorithm=$algorithm AND sample_index=$index;", ReadNearDuplicateFrame, cancellationToken,
                ("$file", mediaFileId.ToString("D")), ("$content", mediaContentId.ToString("D")), ("$sampling", samplingVersion), ("$algorithm", fingerprintAlgorithm), ("$index", frame.SampleIndex)) ?? throw new InvalidOperationException("Near Duplicate frame was not persisted.");
            await ExecuteNonQueryAsync(connection, transaction, "DELETE FROM near_duplicate_lsh_bucket WHERE near_duplicate_frame_id=$frame;", cancellationToken, ("$frame", persisted.NearDuplicateFrameId.ToString("D")));
            if (persisted.Status == AnalysisStatus.Completed && persisted.FingerprintHex is not null)
            {
                foreach (var (band, index) in PerceptualFrameFingerprint.CreateLshBands(persisted.FingerprintHex).Select((value, index) => (value, index)))
                    await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO near_duplicate_lsh_bucket(near_duplicate_frame_id,sampling_version,fingerprint_algorithm,sample_index,band_index,band_value) VALUES($frame,$sampling,$algorithm,$sample,$band,$value);", cancellationToken,
                        ("$frame", persisted.NearDuplicateFrameId.ToString("D")), ("$sampling", samplingVersion), ("$algorithm", fingerprintAlgorithm), ("$sample", persisted.SampleIndex), ("$band", index), ("$value", band));
            }
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<NearDuplicateCandidate>> GetNearDuplicateCandidatesAsync(Guid? rootId, string samplingVersion, string fingerprintAlgorithm, CancellationToken cancellationToken)
    {
        const string sql = "WITH usable_bucket AS (SELECT lb.sampling_version,lb.fingerprint_algorithm,lb.sample_index,lb.band_index,lb.band_value FROM near_duplicate_lsh_bucket lb JOIN near_duplicate_frame nf ON nf.near_duplicate_frame_id=lb.near_duplicate_frame_id JOIN media_file mf ON mf.media_file_id=nf.media_file_id AND mf.media_content_id=nf.media_content_id AND mf.presence_state='Present' AND mf.size_bytes=nf.source_size_bytes AND mf.last_write_time_utc=nf.source_last_write_time_utc WHERE lb.sampling_version=$sampling AND lb.fingerprint_algorithm=$algorithm AND nf.status='Completed' GROUP BY lb.sampling_version,lb.fingerprint_algorithm,lb.sample_index,lb.band_index,lb.band_value HAVING COUNT(DISTINCT nf.media_file_id) <= 128), pairs AS (SELECT DISTINCT CASE WHEN fa.media_file_id<fb.media_file_id THEN fa.media_file_id ELSE fb.media_file_id END file_a,CASE WHEN fa.media_file_id<fb.media_file_id THEN fa.media_content_id ELSE fb.media_content_id END content_a,CASE WHEN fa.media_file_id<fb.media_file_id THEN fb.media_file_id ELSE fa.media_file_id END file_b,CASE WHEN fa.media_file_id<fb.media_file_id THEN fb.media_content_id ELSE fa.media_content_id END content_b,CASE WHEN fa.media_file_id<fb.media_file_id THEN ma.duration_seconds ELSE mb.duration_seconds END duration_a,CASE WHEN fa.media_file_id<fb.media_file_id THEN mb.duration_seconds ELSE ma.duration_seconds END duration_b FROM usable_bucket ub JOIN near_duplicate_lsh_bucket la ON la.sampling_version=ub.sampling_version AND la.fingerprint_algorithm=ub.fingerprint_algorithm AND la.sample_index=ub.sample_index AND la.band_index=ub.band_index AND la.band_value=ub.band_value JOIN near_duplicate_lsh_bucket lb ON lb.sampling_version=la.sampling_version AND lb.fingerprint_algorithm=la.fingerprint_algorithm AND lb.sample_index=la.sample_index AND lb.band_index=la.band_index AND lb.band_value=la.band_value AND la.near_duplicate_frame_id<>lb.near_duplicate_frame_id JOIN near_duplicate_frame fa ON fa.near_duplicate_frame_id=la.near_duplicate_frame_id AND fa.status='Completed' JOIN near_duplicate_frame fb ON fb.near_duplicate_frame_id=lb.near_duplicate_frame_id AND fb.status='Completed' JOIN media_file mfa ON mfa.media_file_id=fa.media_file_id AND mfa.media_content_id=fa.media_content_id AND mfa.presence_state='Present' AND mfa.size_bytes=fa.source_size_bytes AND mfa.last_write_time_utc=fa.source_last_write_time_utc JOIN media_file mfb ON mfb.media_file_id=fb.media_file_id AND mfb.media_content_id=fb.media_content_id AND mfb.presence_state='Present' AND mfb.size_bytes=fb.source_size_bytes AND mfb.last_write_time_utc=fb.source_last_write_time_utc JOIN media_metadata ma ON ma.media_file_id=mfa.media_file_id JOIN media_metadata mb ON mb.media_file_id=mfb.media_file_id WHERE ma.duration_seconds>0 AND mb.duration_seconds>0 AND ABS(ma.duration_seconds-mb.duration_seconds)/CASE WHEN ma.duration_seconds>mb.duration_seconds THEN ma.duration_seconds ELSE mb.duration_seconds END <= 0.03 AND (mfa.full_sha256 IS NULL OR mfb.full_sha256 IS NULL OR mfa.full_sha256<>mfb.full_sha256) AND ($root IS NULL OR mfa.library_root_id=$root OR mfb.library_root_id=$root)) SELECT file_a,content_a,file_b,content_b,duration_a,duration_b FROM pairs ORDER BY file_a,file_b;";
        await using var connection = await OpenAsync(cancellationToken);
        return await QueryAsync(connection, null, sql, reader => new NearDuplicateCandidate(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)), Guid.Parse(reader.GetString(3)), reader.GetDouble(4), reader.GetDouble(5)), cancellationToken,
            ("$sampling", samplingVersion), ("$algorithm", fingerprintAlgorithm), ("$root", rootId?.ToString("D") ?? (object)DBNull.Value));
    }

    public async Task<NearDuplicateEvidence?> GetNearDuplicateEvidenceAsync(Guid mediaFileAId, Guid mediaContentAId, Guid mediaFileBId, Guid mediaContentBId, string samplingVersion, string fingerprintAlgorithm, string thresholdVersion, CancellationToken cancellationToken)
    {
        Canonicalize(ref mediaFileAId, ref mediaContentAId, ref mediaFileBId, ref mediaContentBId);
        const string sql = "SELECT * FROM near_duplicate_evidence WHERE media_file_a_id=$fileA AND media_content_a_id=$contentA AND media_file_b_id=$fileB AND media_content_b_id=$contentB AND evidence_algorithm=$algorithm AND sampling_version=$sampling AND threshold_version=$threshold LIMIT 1;";
        await using var connection = await OpenAsync(cancellationToken);
        return await QuerySingleAsync(connection, null, sql, ReadNearDuplicateEvidence, cancellationToken,
            ("$fileA", mediaFileAId.ToString("D")), ("$contentA", mediaContentAId.ToString("D")), ("$fileB", mediaFileBId.ToString("D")), ("$contentB", mediaContentBId.ToString("D")), ("$algorithm", fingerprintAlgorithm), ("$sampling", samplingVersion), ("$threshold", thresholdVersion));
    }

    public async Task<bool> UpsertNearDuplicateEvidenceAsync(NearDuplicateEvidence evidence, IReadOnlyList<NearDuplicateFrameMatch> frameMatches, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(frameMatches);
        if (string.CompareOrdinal(evidence.MediaFileAId.ToString("D"), evidence.MediaFileBId.ToString("D")) >= 0)
            throw new ArgumentException("Near Duplicate evidence pair must use canonical media file order.", nameof(evidence));
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var currentSources = await ScalarLongAsync(connection, transaction, "SELECT COUNT(1) FROM media_file WHERE presence_state='Present' AND ((media_file_id=$fileA AND media_content_id=$contentA) OR (media_file_id=$fileB AND media_content_id=$contentB));", cancellationToken,
            ("$fileA", evidence.MediaFileAId.ToString("D")), ("$contentA", evidence.MediaContentAId.ToString("D")), ("$fileB", evidence.MediaFileBId.ToString("D")), ("$contentB", evidence.MediaContentBId.ToString("D")));
        var currentFrames = await ScalarLongAsync(connection, transaction, "SELECT COUNT(1) FROM near_duplicate_frame nf JOIN media_file mf ON mf.media_file_id=nf.media_file_id WHERE nf.sampling_version=$sampling AND nf.fingerprint_algorithm=$algorithm AND nf.status='Completed' AND mf.presence_state='Present' AND mf.media_content_id=nf.media_content_id AND mf.size_bytes=nf.source_size_bytes AND mf.last_write_time_utc=nf.source_last_write_time_utc AND ((nf.media_file_id=$fileA AND nf.media_content_id=$contentA) OR (nf.media_file_id=$fileB AND nf.media_content_id=$contentB));", cancellationToken,
            ("$sampling", evidence.SamplingVersion), ("$algorithm", evidence.EvidenceAlgorithm), ("$fileA", evidence.MediaFileAId.ToString("D")), ("$contentA", evidence.MediaContentAId.ToString("D")), ("$fileB", evidence.MediaFileBId.ToString("D")), ("$contentB", evidence.MediaContentBId.ToString("D")));
        var verifiedRequiresCompleteCoverage = string.Equals(evidence.Status, "Verified", StringComparison.Ordinal);
        if (currentSources != 2 || (verifiedRequiresCompleteCoverage && currentFrames < NearDuplicateVerificationProfile.HighPrecisionV1.RequiredSampleCount * 2))
        {
            await transaction.CommitAsync(cancellationToken);
            _log.Write("Information", "NearDuplicateEvidenceRejectedAsStale", new { evidence.MediaFileAId, evidence.MediaFileBId, evidence.SamplingVersion, evidence.Status });
            return false;
        }

        await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO near_duplicate_evidence(near_duplicate_evidence_id,media_file_a_id,media_content_a_id,media_file_b_id,media_content_b_id,evidence_algorithm,sampling_version,threshold_version,evidence_digest,duration_a_seconds,duration_b_seconds,duration_difference_ratio,compared_sample_count,matched_sample_count,longest_consecutive_match_count,mean_hamming_distance,max_hamming_distance,visual_match_score,status,error_code,error_message,verified_at) VALUES($id,$fileA,$contentA,$fileB,$contentB,$algorithm,$sampling,$threshold,$digest,$durationA,$durationB,$ratio,$compared,$matched,$longest,$mean,$max,$score,$status,$errorCode,$errorMessage,$verified) ON CONFLICT(media_file_a_id,media_content_a_id,media_file_b_id,media_content_b_id,evidence_algorithm,sampling_version,threshold_version) DO UPDATE SET evidence_digest=excluded.evidence_digest,duration_a_seconds=excluded.duration_a_seconds,duration_b_seconds=excluded.duration_b_seconds,duration_difference_ratio=excluded.duration_difference_ratio,compared_sample_count=excluded.compared_sample_count,matched_sample_count=excluded.matched_sample_count,longest_consecutive_match_count=excluded.longest_consecutive_match_count,mean_hamming_distance=excluded.mean_hamming_distance,max_hamming_distance=excluded.max_hamming_distance,visual_match_score=excluded.visual_match_score,status=excluded.status,error_code=excluded.error_code,error_message=excluded.error_message,verified_at=excluded.verified_at;", cancellationToken,
            ("$id", evidence.NearDuplicateEvidenceId.ToString("D")), ("$fileA", evidence.MediaFileAId.ToString("D")), ("$contentA", evidence.MediaContentAId.ToString("D")), ("$fileB", evidence.MediaFileBId.ToString("D")), ("$contentB", evidence.MediaContentBId.ToString("D")), ("$algorithm", evidence.EvidenceAlgorithm), ("$sampling", evidence.SamplingVersion), ("$threshold", evidence.ThresholdVersion), ("$digest", evidence.EvidenceDigest), ("$durationA", evidence.DurationASeconds), ("$durationB", evidence.DurationBSeconds), ("$ratio", evidence.DurationDifferenceRatio), ("$compared", evidence.ComparedSampleCount), ("$matched", evidence.MatchedSampleCount), ("$longest", evidence.LongestConsecutiveMatchCount), ("$mean", evidence.MeanHammingDistance), ("$max", evidence.MaxHammingDistance), ("$score", evidence.VisualMatchScore), ("$status", evidence.Status), ("$errorCode", (object?)evidence.ErrorCode ?? DBNull.Value), ("$errorMessage", (object?)evidence.ErrorMessage ?? DBNull.Value), ("$verified", ToDb(evidence.VerifiedAt)));
        var persisted = await QuerySingleAsync(connection, transaction, "SELECT * FROM near_duplicate_evidence WHERE media_file_a_id=$fileA AND media_content_a_id=$contentA AND media_file_b_id=$fileB AND media_content_b_id=$contentB AND evidence_algorithm=$algorithm AND sampling_version=$sampling AND threshold_version=$threshold;", ReadNearDuplicateEvidence, cancellationToken,
            ("$fileA", evidence.MediaFileAId.ToString("D")), ("$contentA", evidence.MediaContentAId.ToString("D")), ("$fileB", evidence.MediaFileBId.ToString("D")), ("$contentB", evidence.MediaContentBId.ToString("D")), ("$algorithm", evidence.EvidenceAlgorithm), ("$sampling", evidence.SamplingVersion), ("$threshold", evidence.ThresholdVersion)) ?? throw new InvalidOperationException("Near Duplicate evidence was not persisted.");
        await ExecuteNonQueryAsync(connection, transaction, "DELETE FROM near_duplicate_frame_match WHERE near_duplicate_evidence_id=$evidence;", cancellationToken, ("$evidence", persisted.NearDuplicateEvidenceId.ToString("D")));
        foreach (var match in frameMatches)
            await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO near_duplicate_frame_match(near_duplicate_evidence_id,sample_index,near_duplicate_frame_a_id,near_duplicate_frame_b_id,hamming_distance,is_match) VALUES($evidence,$index,$frameA,$frameB,$distance,$isMatch);", cancellationToken,
                ("$evidence", persisted.NearDuplicateEvidenceId.ToString("D")), ("$index", match.SampleIndex), ("$frameA", match.NearDuplicateFrameAId.ToString("D")), ("$frameB", match.NearDuplicateFrameBId.ToString("D")), ("$distance", match.HammingDistance), ("$isMatch", match.IsMatch ? 1 : 0));
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task RebuildNearDuplicateGroupsAsync(CancellationToken cancellationToken)
    {
        const string evidenceSql = "SELECT nde.* FROM near_duplicate_evidence nde JOIN media_file mfa ON mfa.media_file_id=nde.media_file_a_id AND mfa.media_content_id=nde.media_content_a_id JOIN media_file mfb ON mfb.media_file_id=nde.media_file_b_id AND mfb.media_content_id=nde.media_content_b_id WHERE nde.status='Verified' AND mfa.presence_state<>'Missing' AND mfb.presence_state<>'Missing';";
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var verified = await QueryAsync(connection, transaction, evidenceSql, ReadNearDuplicateEvidence, cancellationToken);
        foreach (var evidence in verified)
        {
            var now = DateTimeOffset.UtcNow;
            var maxLength = await ScalarLongAsync(connection, transaction, "SELECT MAX(size_bytes) FROM media_file WHERE media_file_id IN ($a,$b);", cancellationToken, ("$a", evidence.MediaFileAId.ToString("D")), ("$b", evidence.MediaFileBId.ToString("D")));
            await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO duplicate_group(duplicate_group_id,relation_type,evidence_algorithm,evidence_digest,byte_length,processing_version,verified_at) VALUES($id,'NearDuplicate',$algorithm,$digest,$bytes,$version,$verified) ON CONFLICT(relation_type,evidence_algorithm,evidence_digest) DO UPDATE SET byte_length=excluded.byte_length,processing_version=excluded.processing_version,verified_at=excluded.verified_at;", cancellationToken,
                ("$id", Guid.NewGuid().ToString("D")), ("$algorithm", evidence.EvidenceAlgorithm), ("$digest", evidence.EvidenceDigest), ("$bytes", maxLength), ("$version", evidence.SamplingVersion + ":" + evidence.ThresholdVersion), ("$verified", ToDb(evidence.VerifiedAt)));
            var group = await QuerySingleAsync(connection, transaction, "SELECT duplicate_group_id,relation_type,evidence_algorithm,evidence_digest,byte_length,processing_version,verified_at,0 current_member_count,NULL review_decision,NULL review_note FROM duplicate_group WHERE relation_type='NearDuplicate' AND evidence_algorithm=$algorithm AND evidence_digest=$digest;", ReadDuplicateGroup, cancellationToken, ("$algorithm", evidence.EvidenceAlgorithm), ("$digest", evidence.EvidenceDigest)) ?? throw new InvalidOperationException("Near Duplicate group was not persisted.");
            foreach (var member in new[] { (evidence.MediaFileAId, evidence.MediaContentAId), (evidence.MediaFileBId, evidence.MediaContentBId) })
                await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO near_duplicate_member(duplicate_group_id,media_file_id,media_content_id,near_duplicate_evidence_id,added_at) VALUES($group,$file,$content,$evidence,$added) ON CONFLICT(duplicate_group_id,media_file_id,media_content_id) DO UPDATE SET near_duplicate_evidence_id=excluded.near_duplicate_evidence_id;", cancellationToken,
                    ("$group", group.DuplicateGroupId.ToString("D")), ("$file", member.Item1.ToString("D")), ("$content", member.Item2.ToString("D")), ("$evidence", evidence.NearDuplicateEvidenceId.ToString("D")), ("$added", ToDb(now)));
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<NearDuplicateComparisonMember>> GetNearDuplicateComparisonMembersAsync(Guid duplicateGroupId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT ndm.duplicate_group_id ndm_group_id,ndm.media_file_id ndm_file_id,ndm.media_content_id ndm_content_id,ndm.near_duplicate_evidence_id ndm_evidence_id,mf.*,mm.media_file_id md_media_file_id,mm.container_format md_container_format,mm.duration_seconds md_duration_seconds,mm.width md_width,mm.height md_height,mm.video_codec md_video_codec,mm.fps_text md_fps_text,mm.bitrate md_bitrate,mm.audio_stream_count md_audio_stream_count,mm.subtitle_stream_count md_subtitle_stream_count,mm.raw_summary_json md_raw_summary_json,mm.updated_at md_updated_at,nde.*, (SELECT dj.decision FROM duplicate_judgment dj WHERE dj.duplicate_group_id=ndm.duplicate_group_id AND dj.target_scope='Member' AND dj.media_file_id=ndm.media_file_id ORDER BY dj.updated_at DESC LIMIT 1) judgment_decision,(SELECT dj.note FROM duplicate_judgment dj WHERE dj.duplicate_group_id=ndm.duplicate_group_id AND dj.target_scope='Member' AND dj.media_file_id=ndm.media_file_id ORDER BY dj.updated_at DESC LIMIT 1) judgment_note FROM near_duplicate_member ndm JOIN media_file mf ON mf.media_file_id=ndm.media_file_id AND mf.media_content_id=ndm.media_content_id JOIN near_duplicate_evidence nde ON nde.near_duplicate_evidence_id=ndm.near_duplicate_evidence_id LEFT JOIN media_metadata mm ON mm.media_file_id=mf.media_file_id WHERE ndm.duplicate_group_id=$group ORDER BY mf.file_name COLLATE NOCASE,mf.media_file_id;";
        await using var connection = await OpenAsync(cancellationToken);
        return await QueryAsync(connection, null, sql, reader =>
        {
            var media = ReadMediaFile(reader);
            var decision = GetNullableString(reader, "judgment_decision") is { } text ? Enum.Parse<DuplicateJudgmentDecision>(text) : DuplicateJudgmentDecision.Unreviewed;
            return new NearDuplicateComparisonMember(Guid.Parse(reader.GetString(reader.GetOrdinal("ndm_group_id"))), Guid.Parse(reader.GetString(reader.GetOrdinal("ndm_file_id"))), Guid.Parse(reader.GetString(reader.GetOrdinal("ndm_content_id"))), Guid.Parse(reader.GetString(reader.GetOrdinal("ndm_evidence_id"))), media, ReadJoinedMetadata(reader, media.MediaFileId), ReadNearDuplicateEvidence(reader), decision, GetNullableString(reader, "judgment_note"));
        }, cancellationToken, ("$group", duplicateGroupId.ToString("D")));
    }

    public async Task<IReadOnlyList<NearDuplicateEvidence>> GetNearDuplicateEvidenceForGroupAsync(Guid duplicateGroupId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT nde.* FROM near_duplicate_member ndm JOIN near_duplicate_evidence nde ON nde.near_duplicate_evidence_id=ndm.near_duplicate_evidence_id WHERE ndm.duplicate_group_id=$group GROUP BY nde.near_duplicate_evidence_id ORDER BY nde.verified_at DESC;";
        await using var connection = await OpenAsync(cancellationToken);
        return await QueryAsync(connection, null, sql, ReadNearDuplicateEvidence, cancellationToken, ("$group", duplicateGroupId.ToString("D")));
    }

    public async Task<IReadOnlyList<NearDuplicateFrameMatch>> GetNearDuplicateFrameMatchesAsync(Guid nearDuplicateEvidenceId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT * FROM near_duplicate_frame_match WHERE near_duplicate_evidence_id=$evidence ORDER BY sample_index;";
        await using var connection = await OpenAsync(cancellationToken);
        return await QueryAsync(connection, null, sql, reader => new NearDuplicateFrameMatch(Guid.Parse(reader.GetString(reader.GetOrdinal("near_duplicate_evidence_id"))), reader.GetInt32(reader.GetOrdinal("sample_index")), Guid.Parse(reader.GetString(reader.GetOrdinal("near_duplicate_frame_a_id"))), Guid.Parse(reader.GetString(reader.GetOrdinal("near_duplicate_frame_b_id"))), reader.GetInt32(reader.GetOrdinal("hamming_distance")), reader.GetInt64(reader.GetOrdinal("is_match")) != 0), cancellationToken, ("$evidence", nearDuplicateEvidenceId.ToString("D")));
    }

    private static NearDuplicateFrame ReadNearDuplicateFrame(SqliteDataReader reader) => new(Guid.Parse(reader.GetString(reader.GetOrdinal("near_duplicate_frame_id"))), Guid.Parse(reader.GetString(reader.GetOrdinal("media_file_id"))), Guid.Parse(reader.GetString(reader.GetOrdinal("media_content_id"))), reader.GetInt64(reader.GetOrdinal("source_size_bytes")), ParseTime(reader.GetString(reader.GetOrdinal("source_last_write_time_utc"))), reader.GetInt32(reader.GetOrdinal("sample_index")), reader.GetDouble(reader.GetOrdinal("normalized_position")), reader.GetDouble(reader.GetOrdinal("requested_timestamp_seconds")), reader.GetDouble(reader.GetOrdinal("presentation_timestamp_seconds")), reader.GetString(reader.GetOrdinal("sampling_version")), reader.GetString(reader.GetOrdinal("fingerprint_algorithm")), GetNullableString(reader, "fingerprint_hex"), GetNullableString(reader, "frame_relative_path"), GetNullableString(reader, "frame_sha256"), GetNullableLong(reader, "frame_byte_size"), Enum.Parse<AnalysisStatus>(reader.GetString(reader.GetOrdinal("status"))), GetNullableString(reader, "error_code"), GetNullableString(reader, "error_message"), ParseTime(reader.GetString(reader.GetOrdinal("created_at"))), ParseTime(reader.GetString(reader.GetOrdinal("updated_at"))));

    private static NearDuplicateEvidence ReadNearDuplicateEvidence(SqliteDataReader reader) => new(Guid.Parse(reader.GetString(reader.GetOrdinal("near_duplicate_evidence_id"))), Guid.Parse(reader.GetString(reader.GetOrdinal("media_file_a_id"))), Guid.Parse(reader.GetString(reader.GetOrdinal("media_content_a_id"))), Guid.Parse(reader.GetString(reader.GetOrdinal("media_file_b_id"))), Guid.Parse(reader.GetString(reader.GetOrdinal("media_content_b_id"))), reader.GetString(reader.GetOrdinal("evidence_algorithm")), reader.GetString(reader.GetOrdinal("sampling_version")), reader.GetString(reader.GetOrdinal("threshold_version")), reader.GetString(reader.GetOrdinal("evidence_digest")), reader.GetDouble(reader.GetOrdinal("duration_a_seconds")), reader.GetDouble(reader.GetOrdinal("duration_b_seconds")), reader.GetDouble(reader.GetOrdinal("duration_difference_ratio")), reader.GetInt32(reader.GetOrdinal("compared_sample_count")), reader.GetInt32(reader.GetOrdinal("matched_sample_count")), reader.GetInt32(reader.GetOrdinal("longest_consecutive_match_count")), reader.GetDouble(reader.GetOrdinal("mean_hamming_distance")), reader.GetInt32(reader.GetOrdinal("max_hamming_distance")), reader.GetDouble(reader.GetOrdinal("visual_match_score")), reader.GetString(reader.GetOrdinal("status")), GetNullableString(reader, "error_code"), GetNullableString(reader, "error_message"), ParseTime(reader.GetString(reader.GetOrdinal("verified_at"))));

    private static void Canonicalize(ref Guid mediaFileAId, ref Guid mediaContentAId, ref Guid mediaFileBId, ref Guid mediaContentBId)
    {
        if (string.CompareOrdinal(mediaFileAId.ToString("D"), mediaFileBId.ToString("D")) <= 0) return;
        (mediaFileAId, mediaFileBId) = (mediaFileBId, mediaFileAId);
        (mediaContentAId, mediaContentBId) = (mediaContentBId, mediaContentAId);
    }
}
