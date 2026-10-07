using System.Text.Json;
using LocalMediaAI.Core;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure;

/// <summary>
/// Timelines (frames along whole videos) for finding copies of different length, and what a verified comparison found.
/// The tables sit outside the versioned schema and carry no foreign keys, so an older build still opens the catalog.
/// </summary>
public sealed partial class SqliteLibraryRepository
{
    internal const string NearDuplicateTimelineSchema = """
CREATE TABLE IF NOT EXISTS near_duplicate_timeline(media_file_id TEXT NOT NULL,media_content_id TEXT NOT NULL,source_size_bytes INTEGER NOT NULL,source_last_write_time_utc TEXT NOT NULL,timeline_version TEXT NOT NULL,duration_seconds REAL NOT NULL,interval_seconds REAL NOT NULL,read_sequentially INTEGER NOT NULL CHECK(read_sequentially IN (0,1)),crop_left INTEGER NOT NULL,crop_top INTEGER NOT NULL,crop_right INTEGER NOT NULL,crop_bottom INTEGER NOT NULL,frame_count INTEGER NOT NULL,created_at TEXT NOT NULL,PRIMARY KEY(media_file_id,media_content_id,timeline_version));
CREATE TABLE IF NOT EXISTS near_duplicate_timeline_frame(media_file_id TEXT NOT NULL,media_content_id TEXT NOT NULL,timeline_version TEXT NOT NULL,frame_index INTEGER NOT NULL,seconds REAL NOT NULL,whole_hex TEXT NOT NULL,center90_hex TEXT NOT NULL,center80_hex TEXT NOT NULL,is_flat INTEGER NOT NULL CHECK(is_flat IN (0,1)),PRIMARY KEY(media_file_id,media_content_id,timeline_version,frame_index));
CREATE TABLE IF NOT EXISTS near_duplicate_timeline_bucket(media_file_id TEXT NOT NULL,media_content_id TEXT NOT NULL,timeline_version TEXT NOT NULL,frame_index INTEGER NOT NULL,variant INTEGER NOT NULL CHECK(variant BETWEEN 0 AND 2),band_index INTEGER NOT NULL CHECK(band_index BETWEEN 0 AND 3),band_value TEXT NOT NULL,PRIMARY KEY(media_file_id,media_content_id,timeline_version,frame_index,variant,band_index));
CREATE INDEX IF NOT EXISTS ix_near_timeline_bucket_lookup ON near_duplicate_timeline_bucket(timeline_version,band_index,band_value);
CREATE TABLE IF NOT EXISTS near_duplicate_timeline_evidence(near_duplicate_evidence_id TEXT PRIMARY KEY,shorter_media_file_id TEXT NOT NULL,shorter_coverage REAL NOT NULL,matched_frames INTEGER NOT NULL,extra_in_longer_json TEXT NOT NULL,extra_in_shorter_json TEXT NOT NULL,created_at TEXT NOT NULL);
""";

    /// <summary>Frame groups sharing a band with more videos than this say nothing (a black frame, a logo) and are skipped.</summary>
    private const int CrowdedTimelineBand = 64;

    private const string CurrentTimelineJoin = "JOIN media_file mf ON mf.media_file_id=t.media_file_id AND mf.media_content_id=t.media_content_id AND mf.presence_state='Present' AND mf.size_bytes=t.source_size_bytes AND mf.last_write_time_utc=t.source_last_write_time_utc";

    /// <summary>Stores a video's timeline, replacing an older one. Nothing is stored when the file changed since it was read.</summary>
    public async Task<bool> SaveNearDuplicateTimelineAsync(StoredTimeline timeline, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var current = await QuerySingleAsync(connection, transaction, "SELECT * FROM media_file WHERE media_file_id=$id;", ReadMediaFile, cancellationToken, ("$id", timeline.MediaFileId.ToString("D")));
        if (current is null || current.PresenceState != PresenceState.Present || current.MediaContentId != timeline.MediaContentId || current.SizeBytes != timeline.SourceSizeBytes || current.LastWriteTimeUtc != timeline.SourceLastWriteTimeUtc)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }
        var file = timeline.MediaFileId.ToString("D");
        var content = timeline.MediaContentId.ToString("D");
        const string version = TimelineSamplingPolicy.TimelineVersion;
        await ExecuteNonQueryAsync(connection, transaction, "DELETE FROM near_duplicate_timeline_bucket WHERE media_file_id=$file AND timeline_version=$version; DELETE FROM near_duplicate_timeline_frame WHERE media_file_id=$file AND timeline_version=$version; DELETE FROM near_duplicate_timeline WHERE media_file_id=$file AND timeline_version=$version;", cancellationToken,
            ("$file", file), ("$version", version));
        await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO near_duplicate_timeline(media_file_id,media_content_id,source_size_bytes,source_last_write_time_utc,timeline_version,duration_seconds,interval_seconds,read_sequentially,crop_left,crop_top,crop_right,crop_bottom,frame_count,created_at) VALUES($file,$content,$size,$mtime,$version,$duration,$interval,$sequential,$left,$top,$right,$bottom,$count,$at);", cancellationToken,
            ("$file", file), ("$content", content), ("$size", timeline.SourceSizeBytes), ("$mtime", ToDb(timeline.SourceLastWriteTimeUtc)), ("$version", version), ("$duration", timeline.DurationSeconds), ("$interval", timeline.IntervalSeconds),
            ("$sequential", timeline.ReadSequentially ? 1 : 0), ("$left", timeline.Bars.Left), ("$top", timeline.Bars.Top), ("$right", timeline.Bars.Right), ("$bottom", timeline.Bars.Bottom), ("$count", timeline.Frames.Count), ("$at", ToDb(DateTimeOffset.UtcNow)));
        for (var index = 0; index < timeline.Frames.Count; index++)
        {
            var frame = timeline.Frames[index];
            await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO near_duplicate_timeline_frame(media_file_id,media_content_id,timeline_version,frame_index,seconds,whole_hex,center90_hex,center80_hex,is_flat) VALUES($file,$content,$version,$index,$seconds,$whole,$c90,$c80,$flat);", cancellationToken,
                ("$file", file), ("$content", content), ("$version", version), ("$index", index), ("$seconds", frame.Seconds), ("$whole", frame.Frame.WholeHex), ("$c90", frame.Frame.Center90Hex), ("$c80", frame.Frame.Center80Hex), ("$flat", frame.Frame.IsFlat ? 1 : 0));
            // A flat frame matches every other flat frame; it would only make crowded bands.
            if (frame.Frame.IsFlat) continue;
            foreach (var (hex, variant) in new[] { frame.Frame.WholeHex, frame.Frame.Center90Hex, frame.Frame.Center80Hex }.Select((hex, variant) => (hex, variant)))
                foreach (var (band, bandIndex) in PerceptualFrameFingerprint.CreateLshBands(hex).Select((band, bandIndex) => (band, bandIndex)))
                    await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO near_duplicate_timeline_bucket(media_file_id,media_content_id,timeline_version,frame_index,variant,band_index,band_value) VALUES($file,$content,$version,$index,$variant,$band,$value);", cancellationToken,
                        ("$file", file), ("$content", content), ("$version", version), ("$index", index), ("$variant", variant), ("$band", bandIndex), ("$value", band));
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>The video's timeline while the file is unchanged since it was read; null when there is none.</summary>
    public async Task<StoredTimeline?> GetCurrentNearDuplicateTimelineAsync(Guid mediaFileId, Guid mediaContentId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var parameters = new (string Name, object? Value)[] { ("$file", mediaFileId.ToString("D")), ("$content", mediaContentId.ToString("D")), ("$version", TimelineSamplingPolicy.TimelineVersion) };
        var headers = await QueryAsync(connection, null, "SELECT t.* FROM near_duplicate_timeline t " + CurrentTimelineJoin + " WHERE t.media_file_id=$file AND t.media_content_id=$content AND t.timeline_version=$version;",
            reader => (Size: reader.GetInt64(reader.GetOrdinal("source_size_bytes")), Written: ParseTime(reader.GetString(reader.GetOrdinal("source_last_write_time_utc"))),
                Duration: reader.GetDouble(reader.GetOrdinal("duration_seconds")), Interval: reader.GetDouble(reader.GetOrdinal("interval_seconds")), Sequential: reader.GetInt64(reader.GetOrdinal("read_sequentially")) != 0,
                Bars: new FrameCrop(reader.GetInt32(reader.GetOrdinal("crop_left")), reader.GetInt32(reader.GetOrdinal("crop_top")), reader.GetInt32(reader.GetOrdinal("crop_right")), reader.GetInt32(reader.GetOrdinal("crop_bottom")))),
            cancellationToken, parameters);
        if (headers.Count == 0) return null;
        var h = headers[0];
        var frames = await QueryAsync(connection, null, "SELECT * FROM near_duplicate_timeline_frame WHERE media_file_id=$file AND media_content_id=$content AND timeline_version=$version ORDER BY frame_index;",
            reader => new TimelineFrame(reader.GetDouble(reader.GetOrdinal("seconds")), new PreparedNearDuplicateFrame(reader.GetInt32(reader.GetOrdinal("frame_index")),
                reader.GetString(reader.GetOrdinal("whole_hex")), reader.GetString(reader.GetOrdinal("center90_hex")), reader.GetString(reader.GetOrdinal("center80_hex")), reader.GetInt64(reader.GetOrdinal("is_flat")) != 0)),
            cancellationToken, parameters);
        return new StoredTimeline(mediaFileId, mediaContentId, h.Size, h.Written, h.Duration, h.Interval, h.Sequential, h.Bars, frames);
    }

    /// <summary>Present videos at least as long as a timeline needs that have no current timeline.</summary>
    public async Task<IReadOnlyList<MediaFile>> GetMediaWithoutNearDuplicateTimelineAsync(Guid? rootId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT mf.* FROM media_file mf JOIN media_metadata mm ON mm.media_file_id=mf.media_file_id WHERE mf.presence_state='Present' AND mm.duration_seconds>=$minimum AND ($root IS NULL OR mf.library_root_id=$root) AND NOT EXISTS(SELECT 1 FROM near_duplicate_timeline t WHERE t.media_file_id=mf.media_file_id AND t.media_content_id=mf.media_content_id AND t.timeline_version=$version AND t.source_size_bytes=mf.size_bytes AND t.source_last_write_time_utc=mf.last_write_time_utc) ORDER BY mf.normalized_path;";
        await using var connection = await OpenAsync(cancellationToken);
        return await QueryAsync(connection, null, sql, ReadMediaFile, cancellationToken,
            ("$root", rootId?.ToString("D") ?? (object)DBNull.Value), ("$minimum", TimelineSamplingPolicy.MinimumDurationSeconds), ("$version", TimelineSamplingPolicy.TimelineVersion));
    }

    /// <summary>
    /// Videos sharing fingerprint bands with this one at two or more of its frames (whole against whole, or the middle of
    /// one against the whole of the other), with a current timeline, a length differing by more than 3% and not the
    /// same file contents.
    /// </summary>
    public async Task<IReadOnlyList<TimelineCandidate>> GetNearDuplicateTimelineCandidatesAsync(Guid mediaFileId, Guid mediaContentId, CancellationToken cancellationToken)
    {
        const string sql = """
WITH mine AS (SELECT frame_index,variant,band_index,band_value FROM near_duplicate_timeline_bucket WHERE media_file_id=$file AND media_content_id=$content AND timeline_version=$version),
usable AS (SELECT * FROM mine m WHERE (SELECT COUNT(DISTINCT c.media_file_id) FROM near_duplicate_timeline_bucket c WHERE c.timeline_version=$version AND c.band_index=m.band_index AND c.band_value=m.band_value) <= $crowded),
hits AS (SELECT o.media_file_id,o.media_content_id,u.frame_index FROM usable u JOIN near_duplicate_timeline_bucket o ON o.timeline_version=$version AND o.band_index=u.band_index AND o.band_value=u.band_value AND (u.variant=0 OR o.variant=0) AND o.media_file_id<>$file),
shared AS (SELECT media_file_id,media_content_id FROM hits GROUP BY media_file_id,media_content_id HAVING COUNT(DISTINCT frame_index) >= 2)
SELECT s.media_file_id,s.media_content_id,mo.duration_seconds FROM shared s
JOIN near_duplicate_timeline t ON t.media_file_id=s.media_file_id AND t.media_content_id=s.media_content_id AND t.timeline_version=$version AND t.frame_count>0
JOIN media_file mf ON mf.media_file_id=t.media_file_id AND mf.media_content_id=t.media_content_id AND mf.presence_state='Present' AND mf.size_bytes=t.source_size_bytes AND mf.last_write_time_utc=t.source_last_write_time_utc
JOIN media_metadata mo ON mo.media_file_id=s.media_file_id
JOIN media_file me ON me.media_file_id=$file JOIN media_metadata mm ON mm.media_file_id=$file
WHERE mo.duration_seconds>0 AND mm.duration_seconds>0
AND ABS(mo.duration_seconds-mm.duration_seconds)/CASE WHEN mo.duration_seconds>mm.duration_seconds THEN mo.duration_seconds ELSE mm.duration_seconds END > 0.03
AND (mf.full_sha256 IS NULL OR me.full_sha256 IS NULL OR mf.full_sha256<>me.full_sha256)
ORDER BY s.media_file_id;
""";
        await using var connection = await OpenAsync(cancellationToken);
        return await QueryAsync(connection, null, sql, reader => new TimelineCandidate(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetDouble(2)), cancellationToken,
            ("$file", mediaFileId.ToString("D")), ("$content", mediaContentId.ToString("D")), ("$version", TimelineSamplingPolicy.TimelineVersion), ("$crowded", CrowdedTimelineBand));
    }

    public async Task SaveTimelineEvidenceDetailAsync(TimelineEvidenceDetail detail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(detail);
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, null, "INSERT INTO near_duplicate_timeline_evidence(near_duplicate_evidence_id,shorter_media_file_id,shorter_coverage,matched_frames,extra_in_longer_json,extra_in_shorter_json,created_at) VALUES($id,$shorter,$coverage,$matched,$longer,$shorterExtra,$at) ON CONFLICT(near_duplicate_evidence_id) DO UPDATE SET shorter_media_file_id=excluded.shorter_media_file_id,shorter_coverage=excluded.shorter_coverage,matched_frames=excluded.matched_frames,extra_in_longer_json=excluded.extra_in_longer_json,extra_in_shorter_json=excluded.extra_in_shorter_json,created_at=excluded.created_at;", cancellationToken,
            ("$id", detail.NearDuplicateEvidenceId.ToString("D")), ("$shorter", detail.ShorterMediaFileId.ToString("D")), ("$coverage", detail.ShorterCoverage), ("$matched", detail.MatchedFrames),
            ("$longer", JsonSerializer.Serialize(detail.ExtraInLonger)), ("$shorterExtra", JsonSerializer.Serialize(detail.ExtraInShorter)), ("$at", ToDb(DateTimeOffset.UtcNow)));
    }

    public async Task<TimelineEvidenceDetail?> GetTimelineEvidenceDetailAsync(Guid nearDuplicateEvidenceId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await QuerySingleAsync(connection, null, "SELECT * FROM near_duplicate_timeline_evidence WHERE near_duplicate_evidence_id=$id;", reader => new TimelineEvidenceDetail(
            nearDuplicateEvidenceId, Guid.Parse(reader.GetString(reader.GetOrdinal("shorter_media_file_id"))), reader.GetDouble(reader.GetOrdinal("shorter_coverage")), reader.GetInt32(reader.GetOrdinal("matched_frames")),
            JsonSerializer.Deserialize<TimelineSpan[]>(reader.GetString(reader.GetOrdinal("extra_in_longer_json"))) ?? [],
            JsonSerializer.Deserialize<TimelineSpan[]>(reader.GetString(reader.GetOrdinal("extra_in_shorter_json"))) ?? []),
            cancellationToken, ("$id", nearDuplicateEvidenceId.ToString("D")));
    }
}
