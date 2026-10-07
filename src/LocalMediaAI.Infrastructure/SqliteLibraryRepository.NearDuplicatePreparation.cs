using LocalMediaAI.Core;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure;

/// <summary>
/// Fingerprints of the near-duplicate frames with black bars cut off (<see cref="NearDuplicatePreparation"/>). The
/// tables sit outside the versioned schema and carry no foreign keys, so an older build can still open and clean up a
/// catalog that has them. A preparation is current while its frames are: it records the newest frame update it was
/// made from, and a re-extracted frame makes it stale.
/// </summary>
public sealed partial class SqliteLibraryRepository
{
    internal const string NearDuplicatePreparationSchema = """
CREATE TABLE IF NOT EXISTS near_duplicate_preparation(media_file_id TEXT NOT NULL,media_content_id TEXT NOT NULL,sampling_version TEXT NOT NULL,fingerprint_algorithm TEXT NOT NULL,preparation_version TEXT NOT NULL,frames_version TEXT NOT NULL,status TEXT NOT NULL CHECK(status IN ('Completed','Unavailable')),crop_left INTEGER NOT NULL,crop_top INTEGER NOT NULL,crop_right INTEGER NOT NULL,crop_bottom INTEGER NOT NULL,prepared_at TEXT NOT NULL,PRIMARY KEY(media_file_id,media_content_id,sampling_version,fingerprint_algorithm,preparation_version));
CREATE TABLE IF NOT EXISTS near_duplicate_prepared_frame(near_duplicate_frame_id TEXT NOT NULL,preparation_version TEXT NOT NULL,media_file_id TEXT NOT NULL,sample_index INTEGER NOT NULL,whole_hex TEXT NOT NULL,center90_hex TEXT NOT NULL,center80_hex TEXT NOT NULL,is_flat INTEGER NOT NULL CHECK(is_flat IN (0,1)),PRIMARY KEY(near_duplicate_frame_id,preparation_version));
CREATE INDEX IF NOT EXISTS ix_near_prepared_frame_file ON near_duplicate_prepared_frame(media_file_id,preparation_version);
CREATE TABLE IF NOT EXISTS near_duplicate_prepared_lsh_bucket(near_duplicate_frame_id TEXT NOT NULL,preparation_version TEXT NOT NULL,media_file_id TEXT NOT NULL,sample_index INTEGER NOT NULL,variant INTEGER NOT NULL CHECK(variant BETWEEN 0 AND 2),band_index INTEGER NOT NULL CHECK(band_index BETWEEN 0 AND 3),band_value TEXT NOT NULL,PRIMARY KEY(near_duplicate_frame_id,preparation_version,variant,band_index));
CREATE INDEX IF NOT EXISTS ix_near_prepared_bucket_lookup ON near_duplicate_prepared_lsh_bucket(preparation_version,sample_index,band_index,band_value);
CREATE INDEX IF NOT EXISTS ix_near_prepared_bucket_file ON near_duplicate_prepared_lsh_bucket(media_file_id,preparation_version);
CREATE TABLE IF NOT EXISTS near_duplicate_recheck(near_duplicate_evidence_id TEXT PRIMARY KEY,preparation_version TEXT NOT NULL,checked_at TEXT NOT NULL);
""";

    /// <summary>Current completed frames per video: how many, and the newest update among them (the frames' version).</summary>
    private const string CurrentNearFramesCte = "current_frames AS (SELECT nf.media_file_id,nf.media_content_id,MAX(nf.updated_at) frames_version,COUNT(1) frame_count FROM near_duplicate_frame nf JOIN media_file mf ON mf.media_file_id=nf.media_file_id AND mf.media_content_id=nf.media_content_id AND mf.presence_state='Present' AND mf.size_bytes=nf.source_size_bytes AND mf.last_write_time_utc=nf.source_last_write_time_utc WHERE nf.sampling_version=$sampling AND nf.fingerprint_algorithm=$algorithm AND nf.status='Completed' AND ($root IS NULL OR mf.library_root_id=$root) GROUP BY nf.media_file_id,nf.media_content_id)";

    public async Task<IReadOnlyList<NearDuplicatePreparationPending>> GetPendingNearDuplicatePreparationsAsync(Guid? rootId, Guid? mediaFileId, string samplingVersion, string fingerprintAlgorithm, string preparationVersion, int limit, CancellationToken cancellationToken)
    {
        const string sql = "WITH " + CurrentNearFramesCte + " SELECT cf.media_file_id,cf.media_content_id,mf.size_bytes,mf.last_write_time_utc,cf.frames_version FROM current_frames cf JOIN media_file mf ON mf.media_file_id=cf.media_file_id WHERE cf.frame_count>=$required AND ($media IS NULL OR cf.media_file_id=$media) AND NOT EXISTS(SELECT 1 FROM near_duplicate_preparation p WHERE p.media_file_id=cf.media_file_id AND p.media_content_id=cf.media_content_id AND p.sampling_version=$sampling AND p.fingerprint_algorithm=$algorithm AND p.preparation_version=$version AND p.frames_version=cf.frames_version) ORDER BY cf.frames_version DESC LIMIT $limit;";
        await using var connection = await OpenAsync(cancellationToken);
        return await QueryAsync(connection, null, sql, reader => new NearDuplicatePreparationPending(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetInt64(2), ParseTime(reader.GetString(3)), reader.GetString(4)), cancellationToken,
            ("$root", rootId?.ToString("D") ?? (object)DBNull.Value), ("$media", mediaFileId?.ToString("D") ?? (object)DBNull.Value), ("$sampling", samplingVersion), ("$algorithm", fingerprintAlgorithm), ("$version", preparationVersion),
            ("$required", NearDuplicateVerificationProfile.HighPrecisionV1.RequiredSampleCount), ("$limit", limit < 0 ? -1 : limit));
    }

    /// <summary>
    /// Stores one video's prepared frames, or marks it unavailable (its stored frame images could not be read) when
    /// <paramref name="frames"/> is empty. Nothing is stored when the frames changed since <paramref name="framesVersion"/>.
    /// </summary>
    public async Task<bool> SaveNearDuplicatePreparationAsync(Guid mediaFileId, Guid mediaContentId, string samplingVersion, string fingerprintAlgorithm, string preparationVersion, string framesVersion, FrameCrop bars, IReadOnlyList<(Guid NearDuplicateFrameId, PreparedNearDuplicateFrame Prepared)> frames, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frames);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var current = await QuerySingleAsync(connection, transaction, "WITH " + CurrentNearFramesCte + " SELECT frames_version FROM current_frames WHERE media_file_id=$file AND media_content_id=$content;", reader => reader.GetString(0), cancellationToken,
            ("$root", DBNull.Value), ("$sampling", samplingVersion), ("$algorithm", fingerprintAlgorithm), ("$file", mediaFileId.ToString("D")), ("$content", mediaContentId.ToString("D")));
        if (!string.Equals(current, framesVersion, StringComparison.Ordinal))
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        var file = mediaFileId.ToString("D");
        await ExecuteNonQueryAsync(connection, transaction, "DELETE FROM near_duplicate_prepared_lsh_bucket WHERE media_file_id=$file AND preparation_version=$version; DELETE FROM near_duplicate_prepared_frame WHERE media_file_id=$file AND preparation_version=$version; DELETE FROM near_duplicate_preparation WHERE media_file_id=$file AND preparation_version=$version;", cancellationToken,
            ("$file", file), ("$version", preparationVersion));
        await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO near_duplicate_preparation(media_file_id,media_content_id,sampling_version,fingerprint_algorithm,preparation_version,frames_version,status,crop_left,crop_top,crop_right,crop_bottom,prepared_at) VALUES($file,$content,$sampling,$algorithm,$version,$frames,$status,$left,$top,$right,$bottom,$at);", cancellationToken,
            ("$file", file), ("$content", mediaContentId.ToString("D")), ("$sampling", samplingVersion), ("$algorithm", fingerprintAlgorithm), ("$version", preparationVersion), ("$frames", framesVersion),
            ("$status", frames.Count == 0 ? "Unavailable" : "Completed"), ("$left", bars.Left), ("$top", bars.Top), ("$right", bars.Right), ("$bottom", bars.Bottom), ("$at", ToDb(DateTimeOffset.UtcNow)));
        foreach (var (frameId, prepared) in frames)
        {
            await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO near_duplicate_prepared_frame(near_duplicate_frame_id,preparation_version,media_file_id,sample_index,whole_hex,center90_hex,center80_hex,is_flat) VALUES($frame,$version,$file,$sample,$whole,$c90,$c80,$flat);", cancellationToken,
                ("$frame", frameId.ToString("D")), ("$version", preparationVersion), ("$file", file), ("$sample", prepared.SampleIndex), ("$whole", prepared.WholeHex), ("$c90", prepared.Center90Hex), ("$c80", prepared.Center80Hex), ("$flat", prepared.IsFlat ? 1 : 0));
            foreach (var (hex, variant) in new[] { prepared.WholeHex, prepared.Center90Hex, prepared.Center80Hex }.Select((hex, variant) => (hex, variant)))
                foreach (var (band, index) in PerceptualFrameFingerprint.CreateLshBands(hex).Select((band, index) => (band, index)))
                    await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO near_duplicate_prepared_lsh_bucket(near_duplicate_frame_id,preparation_version,media_file_id,sample_index,variant,band_index,band_value) VALUES($frame,$version,$file,$sample,$variant,$band,$value);", cancellationToken,
                        ("$frame", frameId.ToString("D")), ("$version", preparationVersion), ("$file", file), ("$sample", prepared.SampleIndex), ("$variant", variant), ("$band", index), ("$value", band));
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>The video's prepared frames while they are current; empty when it is not prepared, unavailable or stale.</summary>
    public async Task<IReadOnlyList<PreparedNearDuplicateFrame>> GetCurrentPreparedNearDuplicateFramesAsync(Guid mediaFileId, Guid mediaContentId, string samplingVersion, string fingerprintAlgorithm, string preparationVersion, CancellationToken cancellationToken)
    {
        const string sql = "WITH " + CurrentNearFramesCte + " SELECT pf.sample_index,pf.whole_hex,pf.center90_hex,pf.center80_hex,pf.is_flat FROM current_frames cf JOIN near_duplicate_preparation p ON p.media_file_id=cf.media_file_id AND p.media_content_id=cf.media_content_id AND p.sampling_version=$sampling AND p.fingerprint_algorithm=$algorithm AND p.preparation_version=$version AND p.frames_version=cf.frames_version AND p.status='Completed' JOIN near_duplicate_frame nf ON nf.media_file_id=cf.media_file_id AND nf.media_content_id=cf.media_content_id AND nf.sampling_version=$sampling AND nf.fingerprint_algorithm=$algorithm AND nf.status='Completed' JOIN near_duplicate_prepared_frame pf ON pf.near_duplicate_frame_id=nf.near_duplicate_frame_id AND pf.preparation_version=$version WHERE cf.media_file_id=$file AND cf.media_content_id=$content ORDER BY pf.sample_index;";
        await using var connection = await OpenAsync(cancellationToken);
        return await QueryAsync(connection, null, sql, reader => new PreparedNearDuplicateFrame(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4) != 0), cancellationToken,
            ("$root", DBNull.Value), ("$sampling", samplingVersion), ("$algorithm", fingerprintAlgorithm), ("$version", preparationVersion), ("$file", mediaFileId.ToString("D")), ("$content", mediaContentId.ToString("D")));
    }

    /// <summary>
    /// Pairs whose prepared frames share a fingerprint band at the same sample: whole against whole, or the middle of one
    /// against the whole of the other. Same length rule and crowded-band limit as <see cref="GetNearDuplicateCandidatesAsync"/>.
    /// </summary>
    public async Task<IReadOnlyList<NearDuplicateCandidate>> GetPreparedNearDuplicateCandidatesAsync(Guid? rootId, string samplingVersion, string fingerprintAlgorithm, string preparationVersion, CancellationToken cancellationToken)
    {
        const string sql = "WITH " + CurrentNearFramesCte + ", prepared AS (SELECT cf.media_file_id,cf.media_content_id FROM current_frames cf JOIN near_duplicate_preparation p ON p.media_file_id=cf.media_file_id AND p.media_content_id=cf.media_content_id AND p.sampling_version=$sampling AND p.fingerprint_algorithm=$algorithm AND p.preparation_version=$version AND p.frames_version=cf.frames_version AND p.status='Completed'), usable AS (SELECT b.media_file_id,b.sample_index,b.variant,b.band_index,b.band_value FROM near_duplicate_prepared_lsh_bucket b JOIN prepared pr ON pr.media_file_id=b.media_file_id WHERE b.preparation_version=$version), crowded AS (SELECT sample_index,band_index,band_value FROM usable GROUP BY sample_index,band_index,band_value HAVING COUNT(DISTINCT media_file_id) > 128), matched AS (SELECT DISTINCT CASE WHEN la.media_file_id<lb.media_file_id THEN la.media_file_id ELSE lb.media_file_id END file_a,CASE WHEN la.media_file_id<lb.media_file_id THEN lb.media_file_id ELSE la.media_file_id END file_b FROM usable la JOIN usable lb ON lb.sample_index=la.sample_index AND lb.band_index=la.band_index AND lb.band_value=la.band_value AND lb.variant=0 AND lb.media_file_id<>la.media_file_id WHERE NOT EXISTS(SELECT 1 FROM crowded k WHERE k.sample_index=la.sample_index AND k.band_index=la.band_index AND k.band_value=la.band_value)) SELECT mfa.media_file_id,mfa.media_content_id,mfb.media_file_id,mfb.media_content_id,ma.duration_seconds,mb.duration_seconds FROM matched m JOIN media_file mfa ON mfa.media_file_id=m.file_a JOIN media_file mfb ON mfb.media_file_id=m.file_b JOIN media_metadata ma ON ma.media_file_id=mfa.media_file_id JOIN media_metadata mb ON mb.media_file_id=mfb.media_file_id WHERE ma.duration_seconds>0 AND mb.duration_seconds>0 AND ABS(ma.duration_seconds-mb.duration_seconds)/CASE WHEN ma.duration_seconds>mb.duration_seconds THEN ma.duration_seconds ELSE mb.duration_seconds END <= 0.03 AND (mfa.full_sha256 IS NULL OR mfb.full_sha256 IS NULL OR mfa.full_sha256<>mfb.full_sha256) AND ($scope IS NULL OR mfa.library_root_id=$scope OR mfb.library_root_id=$scope) ORDER BY mfa.media_file_id,mfb.media_file_id;";
        await using var connection = await OpenAsync(cancellationToken);
        return await QueryAsync(connection, null, sql, reader => new NearDuplicateCandidate(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)), Guid.Parse(reader.GetString(3)), reader.GetDouble(4), reader.GetDouble(5)), cancellationToken,
            ("$root", DBNull.Value), ("$scope", rootId?.ToString("D") ?? (object)DBNull.Value), ("$sampling", samplingVersion), ("$algorithm", fingerprintAlgorithm), ("$version", preparationVersion));
    }
    /// <summary>
    /// Pairs of <paramref name="thresholdVersion"/> judged not similar and not yet judged again with
    /// <paramref name="preparationVersion"/>, whose two videos are present with the judged content and both have a
    /// preparation of that version (made, or found unreadable). Every pair listed can be settled, so the list empties.
    /// </summary>
    public async Task<IReadOnlyList<NearDuplicateEvidence>> GetNearDuplicateRechecksDueAsync(string thresholdVersion, string preparationVersion, CancellationToken cancellationToken)
    {
        const string sql = "SELECT nde.* FROM near_duplicate_evidence nde " +
            "JOIN media_file mfa ON mfa.media_file_id=nde.media_file_a_id AND mfa.media_content_id=nde.media_content_a_id AND mfa.presence_state='Present' " +
            "JOIN media_file mfb ON mfb.media_file_id=nde.media_file_b_id AND mfb.media_content_id=nde.media_content_b_id AND mfb.presence_state='Present' " +
            "WHERE nde.status<>'Verified' AND nde.threshold_version=$threshold " +
            "AND NOT EXISTS(SELECT 1 FROM near_duplicate_recheck r WHERE r.near_duplicate_evidence_id=nde.near_duplicate_evidence_id AND r.preparation_version=$version) " +
            "AND EXISTS(SELECT 1 FROM near_duplicate_preparation pa WHERE pa.media_file_id=nde.media_file_a_id AND pa.media_content_id=nde.media_content_a_id AND pa.sampling_version=nde.sampling_version AND pa.fingerprint_algorithm=nde.evidence_algorithm AND pa.preparation_version=$version) " +
            "AND EXISTS(SELECT 1 FROM near_duplicate_preparation pb WHERE pb.media_file_id=nde.media_file_b_id AND pb.media_content_id=nde.media_content_b_id AND pb.sampling_version=nde.sampling_version AND pb.fingerprint_algorithm=nde.evidence_algorithm AND pb.preparation_version=$version) " +
            "ORDER BY nde.near_duplicate_evidence_id;";
        await using var connection = await OpenAsync(cancellationToken);
        return await QueryAsync(connection, null, sql, ReadNearDuplicateEvidence, cancellationToken, ("$threshold", thresholdVersion), ("$version", preparationVersion));
    }

    public async Task MarkNearDuplicateRecheckedAsync(Guid nearDuplicateEvidenceId, string preparationVersion, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, null, "INSERT INTO near_duplicate_recheck(near_duplicate_evidence_id,preparation_version,checked_at) VALUES($evidence,$version,$checked) ON CONFLICT(near_duplicate_evidence_id) DO UPDATE SET preparation_version=excluded.preparation_version,checked_at=excluded.checked_at;", cancellationToken,
            ("$evidence", nearDuplicateEvidenceId.ToString("D")), ("$version", preparationVersion), ("$checked", ToDb(DateTimeOffset.UtcNow)));
    }
}
