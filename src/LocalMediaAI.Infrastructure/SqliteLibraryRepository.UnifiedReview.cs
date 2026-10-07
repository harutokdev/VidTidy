using System.Text.Json;
using LocalMediaAI.Core;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure;

public sealed partial class SqliteLibraryRepository
{
    public async Task<PagedResult<UnifiedReviewItem>> QueryUnifiedReviewAsync(UnifiedReviewQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var offset = Math.Max(0, query.Offset);
        var limit = Math.Clamp(query.Limit, 1, 500);
        const string projection = """
WITH unified AS (
SELECT
 'ExactDuplicate' review_type,
 'exact:' || dg.duplicate_group_id review_item_id,
 dg.duplicate_group_id source_stable_id,
 dg.duplicate_group_id evidence_id,
 MIN(dm.media_file_id) primary_media_file_id,
 MIN(dm.media_content_id) primary_media_content_id,
 NULL secondary_media_file_id,
 NULL secondary_media_content_id,
 GROUP_CONCAT(dm.media_file_id, ',') involved_media_file_ids,
 GROUP_CONCAT(dm.media_content_id, ',') involved_media_content_ids,
 CASE WHEN SUM(CASE WHEN mf.presence_state='Present' AND mf.media_content_id=dm.media_content_id AND fhe.status='Verified' AND fhe.media_content_id=mf.media_content_id AND fhe.source_size_bytes=mf.size_bytes AND fhe.source_last_write_time_utc=mf.last_write_time_utc THEN 1 ELSE 0 END)=COUNT(*) AND COUNT(*)>=2 THEN 'Current' WHEN SUM(CASE WHEN mf.presence_state IN ('Missing','OfflineRoot','AccessDenied') THEN 1 ELSE 0 END)>0 THEN 'SourceUnavailable' ELSE 'Stale' END evidence_state,
 CASE WHEN SUM(CASE WHEN mf.presence_state='Present' THEN 1 ELSE 0 END)=COUNT(*) THEN 'Available' ELSE 'One or more sources unavailable' END source_availability,
 COALESCE((SELECT decision FROM duplicate_judgment j WHERE j.duplicate_group_id=dg.duplicate_group_id AND j.target_scope='Group' ORDER BY j.updated_at DESC LIMIT 1),'Unreviewed') human_decision,
 (SELECT note FROM duplicate_judgment j WHERE j.duplicate_group_id=dg.duplicate_group_id AND j.target_scope='Group' ORDER BY j.updated_at DESC LIMIT 1) human_note,
 dg.verified_at created_at,dg.verified_at updated_at,
 'Full SHA-256 verified evidence' evidence_kind,
 'Exact group with ' || COUNT(*) || ' members; verified digest ' || dg.evidence_digest evidence_summary,
 'Full SHA-256 — exact evidence; no cross-type score' score_text,
 dg.evidence_algorithm || ' | ' || dg.processing_version provenance_summary,
 NULL representative_frame_relative_path,NULL representative_timestamp_seconds,
 MAX(mf.size_bytes) media_size_bytes,COUNT(*) involved_media_count,dg.evidence_digest display_name
FROM duplicate_group dg JOIN duplicate_member dm ON dm.duplicate_group_id=dg.duplicate_group_id JOIN media_file mf ON mf.media_file_id=dm.media_file_id LEFT JOIN file_hash_evidence fhe ON fhe.file_hash_evidence_id=dm.file_hash_evidence_id
WHERE dg.relation_type='Exact' AND NOT EXISTS(SELECT 1 FROM duplicate_member rdm LEFT JOIN media_file rmf ON rmf.media_file_id=rdm.media_file_id LEFT JOIN library_root sr ON sr.library_root_id=rmf.library_root_id WHERE rdm.duplicate_group_id=dg.duplicate_group_id AND (sr.registration_state IS NULL OR sr.registration_state<>'Active' OR ($root IS NOT NULL AND rmf.library_root_id<>$root)))
GROUP BY dg.duplicate_group_id
UNION ALL
SELECT
 'NearDuplicate' review_type,
 'near:' || dg.duplicate_group_id review_item_id,
 dg.duplicate_group_id source_stable_id,
 dg.duplicate_group_id evidence_id,
 MIN(ndm.media_file_id) primary_media_file_id,
 MIN(ndm.media_content_id) primary_media_content_id,
 NULL secondary_media_file_id,
 NULL secondary_media_content_id,
 GROUP_CONCAT(ndm.media_file_id, ',') involved_media_file_ids,
 GROUP_CONCAT(ndm.media_content_id, ',') involved_media_content_ids,
 CASE WHEN SUM(CASE WHEN mf.presence_state='Present' AND mf.media_content_id=ndm.media_content_id AND ne.status='Verified' THEN 1 ELSE 0 END)=COUNT(*) AND COUNT(*)>=2 THEN 'Current' WHEN SUM(CASE WHEN mf.presence_state IN ('Missing','OfflineRoot','AccessDenied') THEN 1 ELSE 0 END)>0 THEN 'SourceUnavailable' ELSE 'Stale' END evidence_state,
 CASE WHEN SUM(CASE WHEN mf.presence_state='Present' THEN 1 ELSE 0 END)=COUNT(*) THEN 'Available' ELSE 'One or more sources unavailable' END source_availability,
 COALESCE((SELECT decision FROM duplicate_judgment j WHERE j.duplicate_group_id=dg.duplicate_group_id AND j.target_scope='Group' ORDER BY j.updated_at DESC LIMIT 1),'Unreviewed') human_decision,
 (SELECT note FROM duplicate_judgment j WHERE j.duplicate_group_id=dg.duplicate_group_id AND j.target_scope='Group' ORDER BY j.updated_at DESC LIMIT 1) human_note,
 MAX(ne.verified_at) created_at,MAX(ne.verified_at) updated_at,
 'Temporal perceptual-frame evidence' evidence_kind,
 'Near group with ' || COUNT(*) || ' members; temporal verification retained' evidence_summary,
 'Near Duplicate temporal evidence — not Full SHA-256 exact evidence' score_text,
 MAX(ne.evidence_algorithm || ' | ' || ne.sampling_version || ' | ' || ne.threshold_version) provenance_summary,
 NULL representative_frame_relative_path,NULL representative_timestamp_seconds,
 MAX(mf.size_bytes) media_size_bytes,COUNT(*) involved_media_count,MAX(ne.evidence_digest) display_name
FROM duplicate_group dg JOIN near_duplicate_member ndm ON ndm.duplicate_group_id=dg.duplicate_group_id JOIN media_file mf ON mf.media_file_id=ndm.media_file_id LEFT JOIN near_duplicate_evidence ne ON ne.near_duplicate_evidence_id=ndm.near_duplicate_evidence_id
WHERE dg.relation_type='NearDuplicate' AND NOT EXISTS(SELECT 1 FROM near_duplicate_member rndm LEFT JOIN media_file rmf ON rmf.media_file_id=rndm.media_file_id LEFT JOIN library_root sr ON sr.library_root_id=rmf.library_root_id WHERE rndm.duplicate_group_id=dg.duplicate_group_id AND (sr.registration_state IS NULL OR sr.registration_state<>'Active' OR ($root IS NOT NULL AND rmf.library_root_id<>$root)))
GROUP BY dg.duplicate_group_id
UNION ALL
SELECT
 'ContentSimilar' review_type,
 'content:' || cse.content_similarity_evidence_id review_item_id,
 cse.content_similarity_evidence_id source_stable_id,
 cse.content_similarity_evidence_id evidence_id,
 cse.media_file_a_id primary_media_file_id,cse.media_content_a_id primary_media_content_id,cse.media_file_b_id secondary_media_file_id,cse.media_content_b_id secondary_media_content_id,
 cse.media_file_a_id || ',' || cse.media_file_b_id involved_media_file_ids,
 cse.media_content_a_id || ',' || cse.media_content_b_id involved_media_content_ids,
 CASE WHEN a.presence_state='Present' AND b.presence_state='Present' AND a.media_content_id=cse.media_content_a_id AND b.media_content_id=cse.media_content_b_id AND cse.status='Verified' THEN 'Current' WHEN a.presence_state IN ('Missing','OfflineRoot','AccessDenied') OR b.presence_state IN ('Missing','OfflineRoot','AccessDenied') THEN 'SourceUnavailable' ELSE 'Stale' END evidence_state,
 CASE WHEN a.presence_state='Present' AND b.presence_state='Present' THEN 'Available' ELSE 'One or more sources unavailable' END source_availability,
 COALESCE(csj.decision,'Unreviewed') human_decision,csj.note human_note,cse.verified_at created_at,COALESCE(csj.updated_at,cse.verified_at) updated_at,
 'Non-transitive Content Similarity pair evidence' evidence_kind,
 'Pair edge; representative cosine ' || printf('%.2f',cse.representative_cosine_similarity) || '; no automatic group merge' evidence_summary,
 cse.similarity_metric || ': ' || printf('%.2f',cse.sample_aware_cosine_similarity) || ' — type-specific evidence only' score_text,
 cse.model_name || ' @ ' || cse.model_revision || ' | ' || cse.aggregation_version provenance_summary,
 va.frame_relative_path representative_frame_relative_path,va.presentation_timestamp_seconds representative_timestamp_seconds,
 CASE WHEN a.size_bytes>b.size_bytes THEN a.size_bytes ELSE b.size_bytes END media_size_bytes,2 involved_media_count,cse.threshold_version display_name
FROM content_similarity_evidence cse JOIN media_file a ON a.media_file_id=cse.media_file_a_id JOIN media_file b ON b.media_file_id=cse.media_file_b_id LEFT JOIN content_similarity_judgment csj ON csj.content_similarity_evidence_id=cse.content_similarity_evidence_id LEFT JOIN visual_sample va ON va.visual_sample_id=cse.representative_visual_sample_a_id
WHERE ($root IS NULL OR (a.library_root_id=$root AND b.library_root_id=$root)) AND EXISTS(SELECT 1 FROM library_root sr WHERE sr.library_root_id=a.library_root_id AND sr.registration_state='Active') AND EXISTS(SELECT 1 FROM library_root sr WHERE sr.library_root_id=b.library_root_id AND sr.registration_state='Active')
UNION ALL
SELECT
 'SuggestedTag' review_type,
 'tag:' || mte.media_tag_evidence_id review_item_id,
 mte.media_tag_evidence_id source_stable_id,
 mte.media_tag_evidence_id evidence_id,
 mte.media_file_id primary_media_file_id,mte.media_content_id primary_media_content_id,NULL secondary_media_file_id,NULL secondary_media_content_id,
 mte.media_file_id involved_media_file_ids,mte.media_content_id involved_media_content_ids,
 CASE WHEN mf.presence_state='Present' AND mf.media_content_id=mte.media_content_id AND mte.status='Suggested' THEN 'Current' WHEN mf.presence_state IN ('Missing','OfflineRoot','AccessDenied') THEN 'SourceUnavailable' ELSE 'Stale' END evidence_state,
 CASE WHEN mf.presence_state='Present' THEN 'Available' ELSE 'Source unavailable' END source_availability,
 COALESCE(mtj.decision,'Unreviewed') human_decision,mtj.note human_note,mte.created_at created_at,COALESCE(mtj.updated_at,mte.created_at) updated_at,
 'Suggested Tag AI evidence' evidence_kind,
 mte.display_name || ' | coverage ' || mte.completed_sample_count || '/' || mte.expected_sample_count || '; human confirmation required' evidence_summary,
 'Tag Match Score: ' || printf('%.2f',mte.maximum_score) || ' — ' || mte.score_metric || '; type-specific tag evidence only' score_text,
 mte.model_name || ' @ ' || mte.model_revision || ' | ' || mte.threshold_version provenance_summary,
 vs.frame_relative_path representative_frame_relative_path,vs.presentation_timestamp_seconds representative_timestamp_seconds,
 mf.size_bytes media_size_bytes,1 involved_media_count,mte.display_name display_name
FROM media_tag_evidence mte JOIN media_file mf ON mf.media_file_id=mte.media_file_id LEFT JOIN media_tag_judgment mtj ON mtj.media_tag_evidence_id=mte.media_tag_evidence_id LEFT JOIN visual_sample vs ON vs.visual_sample_id=mte.representative_visual_sample_id
WHERE mte.status='Suggested' AND EXISTS(SELECT 1 FROM library_root sr WHERE sr.library_root_id=mf.library_root_id AND sr.registration_state='Active') AND ($root IS NULL OR mf.library_root_id=$root)
)
""";
        var filter = query.Filter switch
        {
            UnifiedReviewFilter.All => "1=1",
            UnifiedReviewFilter.Unreviewed => "human_decision='Unreviewed'",
            UnifiedReviewFilter.Reviewed => "human_decision<>'Unreviewed'",
            UnifiedReviewFilter.ExactDuplicate => "review_type='ExactDuplicate'",
            UnifiedReviewFilter.NearDuplicate => "review_type='NearDuplicate'",
            UnifiedReviewFilter.ContentSimilar => "review_type='ContentSimilar'",
            UnifiedReviewFilter.SuggestedTag => "review_type='SuggestedTag'",
            UnifiedReviewFilter.AvailableSourceOnly => "evidence_state='Current'",
            UnifiedReviewFilter.MissingOrOfflineSource => "evidence_state='SourceUnavailable'",
            _ => throw new ArgumentOutOfRangeException(nameof(query), query.Filter, "Unified Review filter is unsupported.")
        };
        var orderBy = query.Sort switch
        {
            UnifiedReviewSort.Priority => "review_priority ASC, updated_at DESC, media_size_bytes DESC, review_item_id ASC",
            UnifiedReviewSort.CreatedNewest => "created_at DESC, review_priority ASC, review_item_id ASC",
            UnifiedReviewSort.MediaSizeDescending => "media_size_bytes DESC, review_priority ASC, review_item_id ASC",
            _ => throw new ArgumentOutOfRangeException(nameof(query), query.Sort, "Unified Review sort is unsupported.")
        };
        const string finalSelect = """
SELECT *,
 CASE review_type WHEN 'ExactDuplicate' THEN 0 WHEN 'NearDuplicate' THEN 5 WHEN 'ContentSimilar' THEN 10 ELSE 15 END
 + CASE WHEN human_decision='Unreviewed' THEN 0 ELSE 30 END
 + CASE evidence_state WHEN 'Current' THEN 0 WHEN 'Stale' THEN 100 ELSE 200 END review_priority
FROM unified
""";
        await using var connection = await OpenAsync(cancellationToken);
        var root = query.LibraryRootId?.ToString("D") ?? (object)DBNull.Value;
        var pageSql = projection + "SELECT *, COUNT(1) OVER() AS __total_count FROM (" + finalSelect + " WHERE " + filter + ") ORDER BY " + orderBy + " LIMIT $limit OFFSET $offset;";
        await using var command = CreateCommand(connection, null, pageSql, ("$root", root), ("$limit", limit), ("$offset", offset));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<UnifiedReviewItem>();
        var total = 0;
        var hasRows = false;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!hasRows)
            {
                total = reader.GetInt32(reader.GetOrdinal("__total_count"));
                hasRows = true;
            }
            rows.Add(ReadUnifiedReviewItem(reader));
        }
        // A window count is absent when OFFSET produces an empty page. Preserve the existing
        // PagedResult contract for out-of-range offsets (and an actually empty review set).
        if (!hasRows)
            total = (int)await ScalarLongAsync(connection, null, projection + "SELECT COUNT(1) FROM (" + finalSelect + " WHERE " + filter + ");", cancellationToken, ("$root", root));
        return new PagedResult<UnifiedReviewItem>(rows, total, offset, limit);
    }

    public async Task<UnifiedReviewDecisionResult> ApplyUnifiedReviewDecisionAsync(UnifiedReviewDecisionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!UnifiedReviewPolicy.TryParseReviewItemId(request.ReviewItemId, out var reviewType, out var sourceId)) throw new ArgumentException("Review item ID is invalid.", nameof(request));
        if (!UnifiedReviewPolicy.IsAllowed(reviewType, request.Action)) throw new InvalidOperationException($"{request.Action} is not compatible with {reviewType}.");
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        var previous = "Unreviewed";
        string current;
        switch (reviewType)
        {
            case UnifiedReviewType.ExactDuplicate:
            case UnifiedReviewType.NearDuplicate:
                (current, previous) = await ApplyDuplicateUnifiedDecisionAsync(connection, transaction, reviewType, sourceId, request, note, now, cancellationToken);
                break;
            case UnifiedReviewType.ContentSimilar:
                (current, previous) = await ApplyContentUnifiedDecisionAsync(connection, transaction, sourceId, request, note, now, cancellationToken);
                break;
            case UnifiedReviewType.SuggestedTag:
                (current, previous) = await ApplyTagUnifiedDecisionAsync(connection, transaction, sourceId, request, note, now, cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request), reviewType, "Unified Review type is unsupported.");
        }
        await transaction.CommitAsync(cancellationToken);
        return new UnifiedReviewDecisionResult(request.ReviewItemId, reviewType, previous, current, now);
    }

    public async Task<IReadOnlyList<UnifiedReviewDecisionHistoryEntry>> GetUnifiedReviewDecisionHistoryAsync(string reviewItemId, CancellationToken cancellationToken)
    {
        if (!UnifiedReviewPolicy.TryParseReviewItemId(reviewItemId, out _, out _)) throw new ArgumentException("Review item ID is invalid.", nameof(reviewItemId));
        await using var connection = await OpenAsync(cancellationToken);
        return await QueryAsync(connection, null, "SELECT * FROM review_decision_history WHERE review_item_id=$id ORDER BY occurred_at DESC,review_decision_history_id DESC;", ReadUnifiedReviewHistory, cancellationToken, ("$id", reviewItemId));
    }

    public async Task<UnifiedReviewResumeState> GetUnifiedReviewResumeStateAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var value = await ScalarStringAsync(connection, null, "SELECT value FROM app_setting WHERE key=$key;", cancellationToken, ("$key", UnifiedReviewPolicy.ResumeSettingKey));
        if (string.IsNullOrWhiteSpace(value)) return UnifiedReviewResumeState.Default;
        try { return JsonSerializer.Deserialize<UnifiedReviewResumeState>(value) ?? UnifiedReviewResumeState.Default; }
        catch (JsonException) { return UnifiedReviewResumeState.Default; }
    }

    public async Task SaveUnifiedReviewResumeStateAsync(UnifiedReviewResumeState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var normalized = state with { Offset = Math.Max(0, state.Offset), UpdatedAt = DateTimeOffset.UtcNow };
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, null, "INSERT INTO app_setting(key,value,updated_at) VALUES($key,$value,$at) ON CONFLICT(key) DO UPDATE SET value=excluded.value,updated_at=excluded.updated_at;", cancellationToken, ("$key", UnifiedReviewPolicy.ResumeSettingKey), ("$value", JsonSerializer.Serialize(normalized)), ("$at", ToDb(normalized.UpdatedAt)));
    }

    private static UnifiedReviewItem ReadUnifiedReviewItem(SqliteDataReader reader)
    {
        var type = Enum.Parse<UnifiedReviewType>(reader.GetString(reader.GetOrdinal("review_type")));
        var state = Enum.Parse<ReviewEvidenceState>(reader.GetString(reader.GetOrdinal("evidence_state")));
        return new UnifiedReviewItem(reader.GetString(reader.GetOrdinal("review_item_id")), type, reader.GetString(reader.GetOrdinal("source_stable_id")), Guid.Parse(reader.GetString(reader.GetOrdinal("evidence_id"))), ParseGuid(reader, "primary_media_file_id"), ParseGuid(reader, "primary_media_content_id"), ParseGuid(reader, "secondary_media_file_id"), ParseGuid(reader, "secondary_media_content_id"), reader.GetString(reader.GetOrdinal("involved_media_file_ids")), reader.GetString(reader.GetOrdinal("involved_media_content_ids")), state, reader.GetString(reader.GetOrdinal("source_availability")), reader.GetString(reader.GetOrdinal("human_decision")), GetNullableString(reader, "human_note"), ParseTime(reader.GetString(reader.GetOrdinal("created_at"))), ParseTime(reader.GetString(reader.GetOrdinal("updated_at"))), reader.GetInt32(reader.GetOrdinal("review_priority")), UnifiedReviewPolicy.PriorityReason(type, reader.GetString(reader.GetOrdinal("human_decision")), state), reader.GetString(reader.GetOrdinal("evidence_kind")), reader.GetString(reader.GetOrdinal("evidence_summary")), reader.GetString(reader.GetOrdinal("score_text")), reader.GetString(reader.GetOrdinal("provenance_summary")), GetNullableString(reader, "representative_frame_relative_path"), GetNullableDouble(reader, "representative_timestamp_seconds"), reader.GetInt64(reader.GetOrdinal("media_size_bytes")), reader.GetInt32(reader.GetOrdinal("involved_media_count")), GetNullableString(reader, "display_name"));
    }

    private static UnifiedReviewDecisionHistoryEntry ReadUnifiedReviewHistory(SqliteDataReader reader) => new(Guid.Parse(reader.GetString(reader.GetOrdinal("review_decision_history_id"))), reader.GetString(reader.GetOrdinal("review_item_id")), Enum.Parse<UnifiedReviewType>(reader.GetString(reader.GetOrdinal("review_type"))), reader.GetString(reader.GetOrdinal("source_stable_id")), Guid.Parse(reader.GetString(reader.GetOrdinal("evidence_id"))), reader.GetString(reader.GetOrdinal("event_kind")), GetNullableString(reader, "previous_decision"), reader.GetString(reader.GetOrdinal("current_decision")), GetNullableString(reader, "note"), ParseTime(reader.GetString(reader.GetOrdinal("occurred_at"))));

    private static Guid? ParseGuid(SqliteDataReader reader, string name) => GetNullableString(reader, name) is { } value ? Guid.Parse(value) : null;

    private static async Task<(string Current, string Previous)> ApplyDuplicateUnifiedDecisionAsync(SqliteConnection connection, SqliteTransaction transaction, UnifiedReviewType type, Guid groupId, UnifiedReviewDecisionRequest request, string? note, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var relation = await ScalarStringAsync(connection, transaction, "SELECT relation_type FROM duplicate_group WHERE duplicate_group_id=$id;", cancellationToken, ("$id", groupId.ToString("D")));
        var expected = type == UnifiedReviewType.ExactDuplicate ? "Exact" : "NearDuplicate";
        if (!string.Equals(relation, expected, StringComparison.Ordinal)) throw new InvalidOperationException("Review source type no longer matches the requested action.");
        var memberTable = type == UnifiedReviewType.ExactDuplicate ? "duplicate_member" : "near_duplicate_member";
        var total = await ScalarLongAsync(connection, transaction, $"SELECT COUNT(1) FROM {memberTable} WHERE duplicate_group_id=$id;", cancellationToken, ("$id", groupId.ToString("D")));
        var current = await ScalarLongAsync(connection, transaction, $"SELECT COUNT(1) FROM {memberTable} dm JOIN media_file mf ON mf.media_file_id=dm.media_file_id WHERE dm.duplicate_group_id=$id AND mf.presence_state='Present' AND mf.media_content_id=dm.media_content_id;", cancellationToken, ("$id", groupId.ToString("D")));
        if (total < 2 || current != total) throw new InvalidOperationException("Current source evidence is unavailable or stale; it is retained for audit and cannot receive a new unified decision.");
        var previous = await ScalarStringAsync(connection, transaction, "SELECT decision FROM duplicate_judgment WHERE duplicate_group_id=$id AND target_scope='Group';", cancellationToken, ("$id", groupId.ToString("D"))) ?? "Unreviewed";
        var decision = request.Action switch
        {
            UnifiedReviewAction.ConfirmDuplicate or UnifiedReviewAction.ConfirmNearDuplicate => DuplicateJudgmentDecision.ConfirmedDuplicate,
            UnifiedReviewAction.KeepBoth => DuplicateJudgmentDecision.KeepBoth,
            UnifiedReviewAction.Ignore => DuplicateJudgmentDecision.Ignored,
            UnifiedReviewAction.NotDuplicate => DuplicateJudgmentDecision.NotDuplicate,
            UnifiedReviewAction.ResetToUnreviewed => DuplicateJudgmentDecision.Unreviewed,
            UnifiedReviewAction.Preferred => DuplicateJudgmentDecision.Preferred,
            _ => throw new InvalidOperationException("Duplicate action is incompatible.")
        };
        if (decision == DuplicateJudgmentDecision.Preferred)
        {
            if (type != UnifiedReviewType.ExactDuplicate || request.PreferredMediaFileId is null) throw new InvalidOperationException("Preferred is only available for an explicitly selected Exact Duplicate member.");
            var member = await ScalarLongAsync(connection, transaction, "SELECT COUNT(1) FROM duplicate_member WHERE duplicate_group_id=$group AND media_file_id=$member;", cancellationToken, ("$group", groupId.ToString("D")), ("$member", request.PreferredMediaFileId.Value.ToString("D")));
            if (member != 1) throw new InvalidOperationException("Preferred member is not part of this Exact Duplicate group.");
            await ExecuteNonQueryAsync(connection, transaction, "UPDATE duplicate_judgment SET decision='Unreviewed',updated_at=$at WHERE duplicate_group_id=$group AND target_scope='Member' AND decision='Preferred' AND media_file_id<>$member;", cancellationToken, ("$at", ToDb(now)), ("$group", groupId.ToString("D")), ("$member", request.PreferredMediaFileId.Value.ToString("D")));
            await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO duplicate_judgment(duplicate_judgment_id,duplicate_group_id,media_file_id,target_scope,target_key,decision,origin,note,created_at,updated_at) VALUES($id,$group,$member,'Member',$key,$decision,'Human',$note,$now,$now) ON CONFLICT(duplicate_group_id,target_scope,target_key) DO UPDATE SET decision=excluded.decision,note=excluded.note,updated_at=excluded.updated_at;", cancellationToken, ("$id", Guid.NewGuid().ToString("D")), ("$group", groupId.ToString("D")), ("$member", request.PreferredMediaFileId.Value.ToString("D")), ("$key", request.PreferredMediaFileId.Value.ToString("D")), ("$decision", decision.ToString()), ("$note", (object?)note ?? DBNull.Value), ("$now", ToDb(now)));
            return (decision.ToString(), previous);
        }
        await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO duplicate_judgment(duplicate_judgment_id,duplicate_group_id,media_file_id,target_scope,target_key,decision,origin,note,created_at,updated_at) VALUES($id,$group,NULL,'Group','group',$decision,'Human',$note,$now,$now) ON CONFLICT(duplicate_group_id,target_scope,target_key) DO UPDATE SET decision=excluded.decision,note=excluded.note,updated_at=excluded.updated_at;", cancellationToken, ("$id", Guid.NewGuid().ToString("D")), ("$group", groupId.ToString("D")), ("$decision", decision.ToString()), ("$note", (object?)note ?? DBNull.Value), ("$now", ToDb(now)));
        return (decision.ToString(), previous);
    }

    private static async Task<(string Current, string Previous)> ApplyContentUnifiedDecisionAsync(SqliteConnection connection, SqliteTransaction transaction, Guid evidenceId, UnifiedReviewDecisionRequest request, string? note, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var currentSource = await ScalarLongAsync(connection, transaction, "SELECT COUNT(1) FROM content_similarity_evidence e JOIN media_file a ON a.media_file_id=e.media_file_a_id JOIN media_file b ON b.media_file_id=e.media_file_b_id WHERE e.content_similarity_evidence_id=$id AND e.status='Verified' AND a.presence_state='Present' AND b.presence_state='Present' AND a.media_content_id=e.media_content_a_id AND b.media_content_id=e.media_content_b_id;", cancellationToken, ("$id", evidenceId.ToString("D")));
        if (currentSource != 1) throw new InvalidOperationException("Current Content Similarity source evidence is unavailable or stale; it is retained for audit and cannot receive a new unified decision.");
        var previous = await ScalarStringAsync(connection, transaction, "SELECT decision FROM content_similarity_judgment WHERE content_similarity_evidence_id=$id;", cancellationToken, ("$id", evidenceId.ToString("D"))) ?? "Unreviewed";
        var decision = request.Action switch { UnifiedReviewAction.Related => ContentSimilarityJudgmentDecision.Related, UnifiedReviewAction.NotRelated => ContentSimilarityJudgmentDecision.NotRelated, UnifiedReviewAction.KeepBoth => ContentSimilarityJudgmentDecision.KeepBoth, UnifiedReviewAction.Ignore => ContentSimilarityJudgmentDecision.Ignored, UnifiedReviewAction.ResetToUnreviewed => ContentSimilarityJudgmentDecision.Unreviewed, _ => throw new InvalidOperationException("Content Similarity action is incompatible.") };
        await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO content_similarity_judgment(content_similarity_judgment_id,content_similarity_evidence_id,decision,origin,note,created_at,updated_at) VALUES($row,$id,$decision,'Human',$note,$now,$now) ON CONFLICT(content_similarity_evidence_id) DO UPDATE SET decision=excluded.decision,note=excluded.note,updated_at=excluded.updated_at;", cancellationToken, ("$row", Guid.NewGuid().ToString("D")), ("$id", evidenceId.ToString("D")), ("$decision", decision.ToString()), ("$note", (object?)note ?? DBNull.Value), ("$now", ToDb(now)));
        return (decision.ToString(), previous);
    }

    private static async Task<(string Current, string Previous)> ApplyTagUnifiedDecisionAsync(SqliteConnection connection, SqliteTransaction transaction, Guid evidenceId, UnifiedReviewDecisionRequest request, string? note, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var currentSource = await ScalarLongAsync(connection, transaction, "SELECT COUNT(1) FROM media_tag_evidence e JOIN media_file mf ON mf.media_file_id=e.media_file_id WHERE e.media_tag_evidence_id=$id AND e.status='Suggested' AND mf.presence_state='Present' AND mf.media_content_id=e.media_content_id;", cancellationToken, ("$id", evidenceId.ToString("D")));
        if (currentSource != 1) throw new InvalidOperationException("Current Suggested Tag source evidence is unavailable or stale; it is retained for audit and cannot receive a new unified decision.");
        var previous = await ScalarStringAsync(connection, transaction, "SELECT decision FROM media_tag_judgment WHERE media_tag_evidence_id=$id;", cancellationToken, ("$id", evidenceId.ToString("D"))) ?? "Unreviewed";
        var decision = request.Action switch { UnifiedReviewAction.ConfirmTag => MediaTagJudgmentDecision.Confirmed, UnifiedReviewAction.RejectTag => MediaTagJudgmentDecision.Rejected, UnifiedReviewAction.Ignore => MediaTagJudgmentDecision.Ignored, UnifiedReviewAction.ResetToUnreviewed => MediaTagJudgmentDecision.Unreviewed, _ => throw new InvalidOperationException("Suggested Tag action is incompatible.") };
        await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO media_tag_judgment(media_tag_judgment_id,media_tag_evidence_id,decision,origin,note,created_at,updated_at) VALUES($row,$id,$decision,'Human',$note,$now,$now) ON CONFLICT(media_tag_evidence_id) DO UPDATE SET decision=excluded.decision,note=excluded.note,updated_at=excluded.updated_at;", cancellationToken, ("$row", Guid.NewGuid().ToString("D")), ("$id", evidenceId.ToString("D")), ("$decision", decision.ToString()), ("$note", (object?)note ?? DBNull.Value), ("$now", ToDb(now)));
        return (decision.ToString(), previous);
    }
}
