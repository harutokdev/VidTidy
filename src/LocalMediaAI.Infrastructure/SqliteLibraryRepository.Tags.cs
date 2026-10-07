using LocalMediaAI.Core;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure;

public sealed partial class SqliteLibraryRepository
{
    public async Task<MediaTagSourceEmbeddings> GetMediaTagSourceEmbeddingsAsync(
        Guid mediaFileId,
        Guid mediaContentId,
        string modelName,
        string modelRevision,
        string processorVersion,
        string sourceSamplingVersion,
        string dtype,
        int dimension,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        const string expectedSql = "SELECT COUNT(1) FROM visual_sample vs JOIN media_file mf ON mf.media_file_id=vs.media_file_id AND mf.media_content_id=vs.media_content_id WHERE vs.media_file_id=$file AND vs.media_content_id=$content AND vs.sampling_version=$sampling AND mf.presence_state='Present';";
        var expected = (int)await ScalarLongAsync(connection, null, expectedSql, cancellationToken, ("$file", mediaFileId.ToString("D")), ("$content", mediaContentId.ToString("D")), ("$sampling", sourceSamplingVersion));
        const string completedSql = "SELECT ve.visual_embedding_id ve_visual_embedding_id,ve.visual_sample_id ve_visual_sample_id,ve.input_identity_hash ve_input_identity_hash,ve.cache_key ve_cache_key,ve.model_name ve_model_name,ve.model_revision ve_model_revision,ve.processor_version ve_processor_version,ve.sampling_version ve_sampling_version,ve.dtype ve_dtype,ve.dimension ve_dimension,ve.normalized ve_normalized,ve.vector_float32 ve_vector_float32,ve.status ve_status,ve.error_code ve_error_code,ve.error_message ve_error_message,ve.created_at ve_created_at,ve.updated_at ve_updated_at,vs.* FROM visual_embedding ve JOIN visual_sample vs ON vs.visual_sample_id=ve.visual_sample_id JOIN media_file mf ON mf.media_file_id=vs.media_file_id AND mf.media_content_id=vs.media_content_id WHERE vs.media_file_id=$file AND vs.media_content_id=$content AND vs.sampling_version=$sampling AND vs.status='Completed' AND ve.status='Completed' AND ve.model_name=$model AND ve.model_revision=$revision AND ve.processor_version=$processor AND ve.dtype=$dtype AND ve.dimension=$dimension AND ve.normalized=1 AND mf.presence_state='Present' ORDER BY vs.sample_index,vs.visual_sample_id;";
        var completed = await QueryAsync(connection, null, completedSql, reader => (ReadVisualEmbedding(reader, "ve_"), ReadVisualSample(reader)), cancellationToken,
            ("$file", mediaFileId.ToString("D")), ("$content", mediaContentId.ToString("D")), ("$sampling", sourceSamplingVersion), ("$model", modelName), ("$revision", modelRevision), ("$processor", processorVersion), ("$dtype", dtype), ("$dimension", dimension));
        return new MediaTagSourceEmbeddings(expected, completed);
    }

    public async Task<IReadOnlyList<MediaFile>> GetTagSuggestionCandidatesAsync(
        Guid? rootId,
        string taxonomyVersion,
        string textRepresentationVersion,
        string aggregationVersion,
        string thresholdVersion,
        string modelName,
        string modelRevision,
        string processorVersion,
        string sourceSamplingVersion,
        string dtype,
        int dimension,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT mf.* FROM media_file mf WHERE mf.presence_state='Present' AND ($root IS NULL OR mf.library_root_id=$root) AND EXISTS(SELECT 1 FROM visual_sample vs JOIN visual_embedding ve ON ve.visual_sample_id=vs.visual_sample_id WHERE vs.media_file_id=mf.media_file_id AND vs.media_content_id=mf.media_content_id AND vs.sampling_version=$sampling AND vs.status='Completed' AND ve.status='Completed' AND ve.normalized=1 AND ve.model_name=$model AND ve.model_revision=$revision AND ve.processor_version=$processor AND ve.dtype=$dtype AND ve.dimension=$dimension) AND NOT EXISTS(SELECT 1 FROM media_tag_evidence mte WHERE mte.media_file_id=mf.media_file_id AND mte.media_content_id=mf.media_content_id AND mte.taxonomy_version=$taxonomy AND mte.source='EmbeddingZeroShot' AND mte.model_name=$model AND mte.model_revision=$revision AND mte.processor_version=$processor AND mte.dtype=$dtype AND mte.dimension=$dimension AND mte.source_sampling_version=$sampling AND mte.text_representation_version=$textRepresentation AND mte.aggregation_version=$aggregation AND mte.threshold_version=$threshold) ORDER BY mf.file_name COLLATE NOCASE,mf.media_file_id;";
        await using var connection = await OpenAsync(cancellationToken);
        return await QueryAsync(connection, null, sql, ReadMediaFile, cancellationToken,
            ("$root", rootId?.ToString("D") ?? (object)DBNull.Value), ("$taxonomy", taxonomyVersion), ("$textRepresentation", textRepresentationVersion), ("$aggregation", aggregationVersion), ("$threshold", thresholdVersion), ("$model", modelName), ("$revision", modelRevision), ("$processor", processorVersion), ("$sampling", sourceSamplingVersion), ("$dtype", dtype), ("$dimension", dimension));
    }

    public async Task<TagTextEmbedding?> GetTagTextEmbeddingAsync(string taxonomyVersion, string tagId, string textRepresentationVersion, string modelName, string modelRevision, string processorVersion, string dtype, int dimension, CancellationToken cancellationToken)
    {
        const string sql = "SELECT * FROM tag_text_embedding WHERE taxonomy_version=$taxonomy AND tag_id=$tag AND text_representation_version=$textRepresentation AND model_name=$model AND model_revision=$revision AND processor_version=$processor AND dtype=$dtype AND dimension=$dimension ORDER BY updated_at DESC LIMIT 1;";
        await using var connection = await OpenAsync(cancellationToken);
        return await QuerySingleAsync(connection, null, sql, ReadTagTextEmbedding, cancellationToken,
            ("$taxonomy", taxonomyVersion), ("$tag", tagId), ("$textRepresentation", textRepresentationVersion), ("$model", modelName), ("$revision", modelRevision), ("$processor", processorVersion), ("$dtype", dtype), ("$dimension", dimension));
    }

    public async Task UpsertTagTextEmbeddingAsync(TagTextEmbedding embedding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(embedding);
        if (embedding.SourceTextIsInvalid()) throw new ArgumentException("Tag text embedding provenance is invalid.", nameof(embedding));
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, null, "INSERT INTO tag_text_embedding(tag_text_embedding_id,taxonomy_version,tag_id,display_name,group_name,text_representation_version,text_value,model_name,model_revision,processor_version,dtype,dimension,normalized,vector_float32,status,error_code,error_message,created_at,updated_at) VALUES($id,$taxonomy,$tag,$display,$group,$textRepresentation,$text,$model,$revision,$processor,$dtype,$dimension,$normalized,$vector,$status,$code,$message,$created,$updated) ON CONFLICT(taxonomy_version,tag_id,text_representation_version,model_name,model_revision,processor_version,dtype,dimension) DO UPDATE SET display_name=excluded.display_name,group_name=excluded.group_name,text_value=excluded.text_value,normalized=excluded.normalized,vector_float32=excluded.vector_float32,status=excluded.status,error_code=excluded.error_code,error_message=excluded.error_message,updated_at=excluded.updated_at;", cancellationToken,
            ("$id", embedding.TagTextEmbeddingId.ToString("D")), ("$taxonomy", embedding.TaxonomyVersion), ("$tag", embedding.TagId), ("$display", embedding.DisplayName), ("$group", embedding.Group), ("$textRepresentation", embedding.TextRepresentationVersion), ("$text", embedding.Text), ("$model", embedding.ModelName), ("$revision", embedding.ModelRevision), ("$processor", embedding.ProcessorVersion), ("$dtype", embedding.Dtype), ("$dimension", embedding.Dimension), ("$normalized", embedding.Normalized ? 1 : 0), ("$vector", (object?)embedding.VectorFloat32 ?? DBNull.Value), ("$status", embedding.Status.ToString()), ("$code", (object?)embedding.ErrorCode ?? DBNull.Value), ("$message", (object?)embedding.ErrorMessage ?? DBNull.Value), ("$created", ToDb(embedding.CreatedAt)), ("$updated", ToDb(embedding.UpdatedAt)));
    }

    public async Task<MediaTagEvidence?> GetCurrentMediaTagEvidenceAsync(Guid mediaFileId, Guid mediaContentId, string tagId, string taxonomyVersion, string textRepresentationVersion, string aggregationVersion, string thresholdVersion, string modelName, string modelRevision, string processorVersion, string sourceSamplingVersion, string dtype, int dimension, string sourceEmbeddingDigest, CancellationToken cancellationToken)
    {
        const string sql = "SELECT mte.* FROM media_tag_evidence mte JOIN media_file mf ON mf.media_file_id=mte.media_file_id AND mf.media_content_id=mte.media_content_id WHERE mte.media_file_id=$file AND mte.media_content_id=$content AND mte.tag_id=$tag AND mte.taxonomy_version=$taxonomy AND mte.source='EmbeddingZeroShot' AND mte.text_representation_version=$textRepresentation AND mte.aggregation_version=$aggregation AND mte.threshold_version=$threshold AND mte.model_name=$model AND mte.model_revision=$revision AND mte.processor_version=$processor AND mte.dtype=$dtype AND mte.dimension=$dimension AND mte.source_sampling_version=$sampling AND mte.source_embedding_digest=$digest AND mte.status IN ('Suggested','NotSuggested','InsufficientEvidence') AND mf.presence_state='Present' ORDER BY mte.created_at DESC LIMIT 1;";
        await using var connection = await OpenAsync(cancellationToken);
        return await QuerySingleAsync(connection, null, sql, ReadMediaTagEvidence, cancellationToken,
            ("$file", mediaFileId.ToString("D")), ("$content", mediaContentId.ToString("D")), ("$tag", tagId), ("$taxonomy", taxonomyVersion), ("$textRepresentation", textRepresentationVersion), ("$aggregation", aggregationVersion), ("$threshold", thresholdVersion), ("$model", modelName), ("$revision", modelRevision), ("$processor", processorVersion), ("$dtype", dtype), ("$dimension", dimension), ("$sampling", sourceSamplingVersion), ("$digest", sourceEmbeddingDigest));
    }

    public async Task<bool> UpsertMediaTagEvidenceAsync(MediaTagEvidence evidence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Source != MediaTagSource.EmbeddingZeroShot) throw new ArgumentException("Phase 5.4 AI evidence must use EmbeddingZeroShot source.", nameof(evidence));
        if (evidence.Dimension <= 0 || evidence.ExpectedSampleCount < 0 || evidence.CompletedSampleCount < 0 || evidence.MatchedSampleCount < 0 || evidence.CompletedSampleCount > evidence.ExpectedSampleCount || evidence.MatchedSampleCount > evidence.CompletedSampleCount)
            throw new ArgumentOutOfRangeException(nameof(evidence), "Tag evidence coverage counts are invalid.");
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var current = await ScalarLongAsync(connection, transaction, "SELECT COUNT(1) FROM media_file WHERE media_file_id=$file AND media_content_id=$content AND presence_state='Present';", cancellationToken, ("$file", evidence.MediaFileId.ToString("D")), ("$content", evidence.MediaContentId.ToString("D")));
        if (current != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO media_tag_evidence(media_tag_evidence_id,media_file_id,media_content_id,representative_visual_sample_id,taxonomy_version,tag_id,display_name,group_name,source,model_name,model_revision,processor_version,dtype,dimension,source_sampling_version,text_representation_version,aggregation_version,threshold_version,score_metric,maximum_score,mean_score,matched_sample_count,completed_sample_count,expected_sample_count,coverage_ratio,source_embedding_digest,status,error_code,error_message,created_at) VALUES($id,$file,$content,$sample,$taxonomy,$tag,$display,$group,$source,$model,$revision,$processor,$dtype,$dimension,$sampling,$textRepresentation,$aggregation,$threshold,$metric,$max,$mean,$matched,$completed,$expected,$coverage,$digest,$status,$code,$message,$created) ON CONFLICT(media_file_id,media_content_id,taxonomy_version,tag_id,source,model_name,model_revision,processor_version,dtype,dimension,source_sampling_version,text_representation_version,aggregation_version,threshold_version,source_embedding_digest) DO UPDATE SET representative_visual_sample_id=excluded.representative_visual_sample_id,display_name=excluded.display_name,group_name=excluded.group_name,score_metric=excluded.score_metric,maximum_score=excluded.maximum_score,mean_score=excluded.mean_score,matched_sample_count=excluded.matched_sample_count,completed_sample_count=excluded.completed_sample_count,expected_sample_count=excluded.expected_sample_count,coverage_ratio=excluded.coverage_ratio,status=excluded.status,error_code=excluded.error_code,error_message=excluded.error_message,created_at=excluded.created_at;", cancellationToken,
            ("$id", evidence.MediaTagEvidenceId.ToString("D")), ("$file", evidence.MediaFileId.ToString("D")), ("$content", evidence.MediaContentId.ToString("D")), ("$sample", evidence.RepresentativeVisualSampleId?.ToString("D") ?? (object)DBNull.Value), ("$taxonomy", evidence.TaxonomyVersion), ("$tag", evidence.TagId), ("$display", evidence.DisplayName), ("$group", evidence.Group), ("$source", evidence.Source.ToString()), ("$model", evidence.ModelName), ("$revision", evidence.ModelRevision), ("$processor", evidence.ProcessorVersion), ("$dtype", evidence.Dtype), ("$dimension", evidence.Dimension), ("$sampling", evidence.SourceSamplingVersion), ("$textRepresentation", evidence.TextRepresentationVersion), ("$aggregation", evidence.AggregationVersion), ("$threshold", evidence.ThresholdVersion), ("$metric", evidence.ScoreMetric), ("$max", evidence.MaximumScore), ("$mean", evidence.MeanScore), ("$matched", evidence.MatchedSampleCount), ("$completed", evidence.CompletedSampleCount), ("$expected", evidence.ExpectedSampleCount), ("$coverage", evidence.CoverageRatio), ("$digest", evidence.SourceEmbeddingDigest), ("$status", evidence.Status.ToString()), ("$code", (object?)evidence.ErrorCode ?? DBNull.Value), ("$message", (object?)evidence.ErrorMessage ?? DBNull.Value), ("$created", ToDb(evidence.CreatedAt)));
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<PagedResult<MediaTagReviewItem>> QueryMediaTagReviewAsync(Guid? rootId, bool includeHidden, int offset, int limit, CancellationToken cancellationToken)
    {
        offset = Math.Max(0, offset); limit = Math.Clamp(limit, 1, 500);
        const string from = " FROM media_tag_evidence mte JOIN media_file mf ON mf.media_file_id=mte.media_file_id AND mf.media_content_id=mte.media_content_id LEFT JOIN media_tag_judgment mtj ON mtj.media_tag_evidence_id=mte.media_tag_evidence_id LEFT JOIN visual_sample vs ON vs.visual_sample_id=mte.representative_visual_sample_id ";
        const string predicate = " WHERE mte.status='Suggested' AND mf.presence_state='Present' AND EXISTS(SELECT 1 FROM library_root sr WHERE sr.library_root_id=mf.library_root_id AND sr.registration_state='Active') AND ($root IS NULL OR mf.library_root_id=$root) AND ($includeHidden=1 OR COALESCE(mtj.decision,'Unreviewed') NOT IN ('Rejected','Ignored'))";
        await using var connection = await OpenAsync(cancellationToken);
        var count = (int)await ScalarLongAsync(connection, null, "SELECT COUNT(1)" + from + predicate + ";", cancellationToken, ("$root", rootId?.ToString("D") ?? (object)DBNull.Value), ("$includeHidden", includeHidden ? 1 : 0));
        const string select = "SELECT mte.*,COALESCE(mtj.decision,'Unreviewed') judgment_decision,mtj.note judgment_note,mf.file_name,mf.path,mf.presence_state,vs.presentation_timestamp_seconds,vs.frame_relative_path";
        var rows = await QueryAsync(connection, null, select + from + predicate + " ORDER BY CASE COALESCE(mtj.decision,'Unreviewed') WHEN 'Unreviewed' THEN 0 WHEN 'Confirmed' THEN 1 ELSE 2 END,mte.maximum_score DESC,mf.file_name COLLATE NOCASE,mte.media_tag_evidence_id LIMIT $limit OFFSET $offset;", reader => new MediaTagReviewItem(ReadMediaTagEvidence(reader), Enum.Parse<MediaTagJudgmentDecision>(reader.GetString(reader.GetOrdinal("judgment_decision"))), GetNullableString(reader, "judgment_note"), reader.GetString(reader.GetOrdinal("file_name")), reader.GetString(reader.GetOrdinal("path")), Enum.Parse<PresenceState>(reader.GetString(reader.GetOrdinal("presence_state"))), GetNullableDouble(reader, "presentation_timestamp_seconds"), GetNullableString(reader, "frame_relative_path")), cancellationToken, ("$root", rootId?.ToString("D") ?? (object)DBNull.Value), ("$includeHidden", includeHidden ? 1 : 0), ("$limit", limit), ("$offset", offset));
        return new PagedResult<MediaTagReviewItem>(rows, count, offset, limit);
    }

    public async Task<MediaTagJudgment> UpsertMediaTagJudgmentAsync(Guid mediaTagEvidenceId, MediaTagJudgmentDecision decision, string? note, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, null, "INSERT INTO media_tag_judgment(media_tag_judgment_id,media_tag_evidence_id,decision,origin,note,created_at,updated_at) VALUES($id,$evidence,$decision,'Human',$note,$now,$now) ON CONFLICT(media_tag_evidence_id) DO UPDATE SET decision=excluded.decision,note=excluded.note,updated_at=excluded.updated_at;", cancellationToken, ("$id", Guid.NewGuid().ToString("D")), ("$evidence", mediaTagEvidenceId.ToString("D")), ("$decision", decision.ToString()), ("$note", string.IsNullOrWhiteSpace(note) ? DBNull.Value : note.Trim()), ("$now", ToDb(now)));
        var stored = await QuerySingleAsync(connection, null, "SELECT * FROM media_tag_judgment WHERE media_tag_evidence_id=$evidence;", ReadMediaTagJudgment, cancellationToken, ("$evidence", mediaTagEvidenceId.ToString("D")));
        return stored ?? throw new InvalidOperationException("Tag judgment persistence did not return a stored row.");
    }

    public async Task<IReadOnlyList<MediaTagJudgment>> GetMediaTagJudgmentsAsync(Guid mediaTagEvidenceId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await QueryAsync(connection, null, "SELECT * FROM media_tag_judgment WHERE media_tag_evidence_id=$evidence ORDER BY updated_at DESC;", ReadMediaTagJudgment, cancellationToken, ("$evidence", mediaTagEvidenceId.ToString("D")));
    }

    public async Task<ManualMediaTag> AddManualMediaTagAsync(Guid mediaFileId, string displayTag, string? note, CancellationToken cancellationToken)
    {
        var display = TagNormalization.Display(displayTag);
        var canonical = TagNormalization.CanonicalizeManualTag(display);
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, null, "INSERT INTO manual_media_tag(manual_media_tag_id,media_file_id,canonical_tag,display_tag,source,note,created_at,updated_at) VALUES($id,$file,$canonical,$display,'Human',$note,$now,$now) ON CONFLICT(media_file_id,canonical_tag) DO UPDATE SET display_tag=excluded.display_tag,note=excluded.note,updated_at=excluded.updated_at;", cancellationToken, ("$id", Guid.NewGuid().ToString("D")), ("$file", mediaFileId.ToString("D")), ("$canonical", canonical), ("$display", display), ("$note", string.IsNullOrWhiteSpace(note) ? DBNull.Value : note.Trim()), ("$now", ToDb(now)));
        var stored = await QuerySingleAsync(connection, null, "SELECT * FROM manual_media_tag WHERE media_file_id=$file AND canonical_tag=$canonical;", ReadManualMediaTag, cancellationToken, ("$file", mediaFileId.ToString("D")), ("$canonical", canonical));
        return stored ?? throw new InvalidOperationException("Manual tag persistence did not return a stored row.");
    }

    public async Task<IReadOnlyList<ManualMediaTag>> GetManualMediaTagsAsync(Guid mediaFileId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await QueryAsync(connection, null, "SELECT * FROM manual_media_tag WHERE media_file_id=$file ORDER BY display_tag COLLATE NOCASE,manual_media_tag_id;", ReadManualMediaTag, cancellationToken, ("$file", mediaFileId.ToString("D")));
    }

    public async Task<bool> RemoveManualMediaTagAsync(Guid mediaFileId, string canonicalTag, CancellationToken cancellationToken)
    {
        var canonical = TagNormalization.CanonicalizeManualTag(canonicalTag);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, "DELETE FROM manual_media_tag WHERE media_file_id=$file AND canonical_tag=$canonical;", ("$file", mediaFileId.ToString("D")), ("$canonical", canonical));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<IReadOnlyList<MediaTagSummary>> GetMediaTagSummariesAsync(IReadOnlyList<Guid> mediaFileIds, CancellationToken cancellationToken)
    {
        if (mediaFileIds.Count == 0) return Array.Empty<MediaTagSummary>();
        var parameters = new List<(string Name, object? Value)>();
        var placeholders = new List<string>();
        for (var index = 0; index < mediaFileIds.Count; index++) { var name = "$id" + index; placeholders.Add(name); parameters.Add((name, mediaFileIds[index].ToString("D"))); }
        var inClause = string.Join(',', placeholders);
        await using var connection = await OpenAsync(cancellationToken);
        var manual = await QueryAsync(connection, null, $"SELECT * FROM manual_media_tag WHERE media_file_id IN ({inClause});", ReadManualMediaTag, cancellationToken, parameters.ToArray());
        var evidence = await QueryAsync(connection, null, $"SELECT mte.media_file_id,mte.display_name,COALESCE(mtj.decision,'Unreviewed') decision FROM media_tag_evidence mte JOIN media_file mf ON mf.media_file_id=mte.media_file_id AND mf.media_content_id=mte.media_content_id LEFT JOIN media_tag_judgment mtj ON mtj.media_tag_evidence_id=mte.media_tag_evidence_id WHERE mte.media_file_id IN ({inClause}) AND mf.presence_state='Present' AND mte.status='Suggested';", reader => (MediaFileId: Guid.Parse(reader.GetString(0)), DisplayName: reader.GetString(1), Decision: Enum.Parse<MediaTagJudgmentDecision>(reader.GetString(2))), cancellationToken, parameters.ToArray());
        return mediaFileIds.Select(id => new MediaTagSummary(id,
            manual.Where(tag => tag.MediaFileId == id).Select(tag => tag.DisplayTag).Concat(evidence.Where(tag => tag.MediaFileId == id && tag.Decision == MediaTagJudgmentDecision.Confirmed).Select(tag => tag.DisplayName)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            evidence.Where(tag => tag.MediaFileId == id && tag.Decision == MediaTagJudgmentDecision.Unreviewed).Select(tag => tag.DisplayName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            evidence.Where(tag => tag.MediaFileId == id && tag.Decision == MediaTagJudgmentDecision.Rejected).Select(tag => tag.DisplayName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())).ToArray();
    }

    public async Task<PagedResult<MediaListItem>> QueryMediaByTagAsync(Guid? rootId, string canonicalOrTaxonomyTag, bool includeSuggested, int offset, int limit, CancellationToken cancellationToken)
    {
        offset = Math.Max(0, offset); limit = Math.Clamp(limit, 1, 500);
        const string prefix = " FROM media_file mf LEFT JOIN media_metadata mm ON mm.media_file_id=mf.media_file_id ";
        var (predicate, args) = BuildLibraryTagFilter(rootId, canonicalOrTaxonomyTag, includeSuggested);
        await using var connection = await OpenAsync(cancellationToken);
        var count = (int)await ScalarLongAsync(connection, null, "SELECT COUNT(1)" + prefix + " WHERE " + predicate + ";", cancellationToken, args);
        var select = "SELECT mf.media_file_id,mf.file_name,mf.path,mf.size_bytes,mf.last_write_time_utc,mf.presence_state,mm.container_format,mm.duration_seconds,mm.width,mm.height,mm.video_codec,mm.fps_text,mm.bitrate,mm.audio_stream_count,mm.subtitle_stream_count,mm.raw_summary_json,mm.updated_at,(SELECT a.relative_path FROM artifact a JOIN analysis an ON an.analysis_id=a.analysis_id WHERE an.media_content_id=mf.media_content_id AND a.artifact_kind='Thumbnail' ORDER BY a.created_at DESC LIMIT 1) thumbnail_path,(SELECT j.state FROM job j WHERE j.payload_json LIKE '%' || mf.media_file_id || '%' ORDER BY j.updated_at DESC LIMIT 1) job_state,(SELECT an.error_code FROM analysis an WHERE an.media_content_id=mf.media_content_id AND an.status='Failed' ORDER BY an.completed_at DESC LIMIT 1) error_code";
        var rows = await QueryAsync(connection, null, select + prefix + " WHERE " + predicate + " ORDER BY mf.file_name COLLATE NOCASE,mf.media_file_id LIMIT $limit OFFSET $offset;", ReadMediaListItem, cancellationToken, args.Append(("$limit", (object?)limit)).Append(("$offset", (object?)offset)).ToArray());
        return new PagedResult<MediaListItem>(rows, count, offset, limit);
    }

    public async Task<IReadOnlyList<Guid>> FilterMediaIdsByTagAsync(IReadOnlyList<Guid> orderedMediaFileIds, string canonicalOrTaxonomyTag, bool includeSuggested, CancellationToken cancellationToken)
    {
        if (orderedMediaFileIds.Count == 0) return Array.Empty<Guid>();
        var tag = canonicalOrTaxonomyTag.Trim();
        var display = TaggingTaxonomy.All.FirstOrDefault(item => string.Equals(item.TagId, tag, StringComparison.Ordinal))?.DisplayName ?? TagNormalization.Display(tag);
        var canonical = TagNormalization.CanonicalizeManualTag(display);
        var parameters = new List<(string Name, object? Value)> { ("$tag", tag), ("$display", display), ("$canonical", canonical) };
        var placeholders = new List<string>();
        for (var index = 0; index < orderedMediaFileIds.Count; index++) { var name = "$id" + index; placeholders.Add(name); parameters.Add((name, orderedMediaFileIds[index].ToString("D"))); }
        await using var connection = await OpenAsync(cancellationToken);
        var matched = await QueryAsync(connection, null, "SELECT mf.media_file_id FROM media_file mf WHERE mf.media_file_id IN (" + string.Join(',', placeholders) + ") AND " + BuildTagFilterPredicate(includeSuggested) + ";", reader => Guid.Parse(reader.GetString(0)), cancellationToken, parameters.ToArray());
        var set = matched.ToHashSet();
        return orderedMediaFileIds.Where(set.Contains).ToArray();
    }

    private static string BuildTagFilterPredicate(bool includeSuggested)
    {
        var evidenceDecision = includeSuggested
            ? "COALESCE(mtj.decision,'Unreviewed') IN ('Unreviewed','Confirmed')"
            : "mtj.decision='Confirmed'";
        return "(EXISTS(SELECT 1 FROM manual_media_tag mmt WHERE mmt.media_file_id=mf.media_file_id AND mmt.canonical_tag=$canonical) OR EXISTS(SELECT 1 FROM media_tag_evidence mte LEFT JOIN media_tag_judgment mtj ON mtj.media_tag_evidence_id=mte.media_tag_evidence_id WHERE mte.media_file_id=mf.media_file_id AND mte.media_content_id=mf.media_content_id AND mte.status='Suggested' AND (mte.tag_id=$tag OR mte.display_name=$display) AND " + evidenceDecision + "))";
    }

    private static TagTextEmbedding ReadTagTextEmbedding(SqliteDataReader reader) => new(Guid.Parse(reader.GetString(reader.GetOrdinal("tag_text_embedding_id"))), reader.GetString(reader.GetOrdinal("taxonomy_version")), reader.GetString(reader.GetOrdinal("tag_id")), reader.GetString(reader.GetOrdinal("display_name")), reader.GetString(reader.GetOrdinal("group_name")), reader.GetString(reader.GetOrdinal("text_representation_version")), reader.GetString(reader.GetOrdinal("text_value")), reader.GetString(reader.GetOrdinal("model_name")), reader.GetString(reader.GetOrdinal("model_revision")), reader.GetString(reader.GetOrdinal("processor_version")), reader.GetString(reader.GetOrdinal("dtype")), reader.GetInt32(reader.GetOrdinal("dimension")), reader.GetInt64(reader.GetOrdinal("normalized")) != 0, reader.IsDBNull(reader.GetOrdinal("vector_float32")) ? null : reader.GetFieldValue<byte[]>(reader.GetOrdinal("vector_float32")), Enum.Parse<TagTextEmbeddingStatus>(reader.GetString(reader.GetOrdinal("status"))), GetNullableString(reader, "error_code"), GetNullableString(reader, "error_message"), ParseTime(reader.GetString(reader.GetOrdinal("created_at"))), ParseTime(reader.GetString(reader.GetOrdinal("updated_at"))));
    private static MediaTagEvidence ReadMediaTagEvidence(SqliteDataReader reader) => new(Guid.Parse(reader.GetString(reader.GetOrdinal("media_tag_evidence_id"))), Guid.Parse(reader.GetString(reader.GetOrdinal("media_file_id"))), Guid.Parse(reader.GetString(reader.GetOrdinal("media_content_id"))), GetNullableString(reader, "representative_visual_sample_id") is { } sample ? Guid.Parse(sample) : null, reader.GetString(reader.GetOrdinal("taxonomy_version")), reader.GetString(reader.GetOrdinal("tag_id")), reader.GetString(reader.GetOrdinal("display_name")), reader.GetString(reader.GetOrdinal("group_name")), Enum.Parse<MediaTagSource>(reader.GetString(reader.GetOrdinal("source"))), reader.GetString(reader.GetOrdinal("model_name")), reader.GetString(reader.GetOrdinal("model_revision")), reader.GetString(reader.GetOrdinal("processor_version")), reader.GetString(reader.GetOrdinal("dtype")), reader.GetInt32(reader.GetOrdinal("dimension")), reader.GetString(reader.GetOrdinal("source_sampling_version")), reader.GetString(reader.GetOrdinal("text_representation_version")), reader.GetString(reader.GetOrdinal("aggregation_version")), reader.GetString(reader.GetOrdinal("threshold_version")), reader.GetString(reader.GetOrdinal("score_metric")), reader.GetDouble(reader.GetOrdinal("maximum_score")), reader.GetDouble(reader.GetOrdinal("mean_score")), reader.GetInt32(reader.GetOrdinal("matched_sample_count")), reader.GetInt32(reader.GetOrdinal("completed_sample_count")), reader.GetInt32(reader.GetOrdinal("expected_sample_count")), reader.GetDouble(reader.GetOrdinal("coverage_ratio")), reader.GetString(reader.GetOrdinal("source_embedding_digest")), Enum.Parse<MediaTagEvidenceStatus>(reader.GetString(reader.GetOrdinal("status"))), GetNullableString(reader, "error_code"), GetNullableString(reader, "error_message"), ParseTime(reader.GetString(reader.GetOrdinal("created_at"))));
    private static MediaTagJudgment ReadMediaTagJudgment(SqliteDataReader reader) => new(Guid.Parse(reader.GetString(reader.GetOrdinal("media_tag_judgment_id"))), Guid.Parse(reader.GetString(reader.GetOrdinal("media_tag_evidence_id"))), Enum.Parse<MediaTagJudgmentDecision>(reader.GetString(reader.GetOrdinal("decision"))), Enum.Parse<MediaTagSource>(reader.GetString(reader.GetOrdinal("origin"))), GetNullableString(reader, "note"), ParseTime(reader.GetString(reader.GetOrdinal("created_at"))), ParseTime(reader.GetString(reader.GetOrdinal("updated_at"))));
    private static ManualMediaTag ReadManualMediaTag(SqliteDataReader reader) => new(Guid.Parse(reader.GetString(reader.GetOrdinal("manual_media_tag_id"))), Guid.Parse(reader.GetString(reader.GetOrdinal("media_file_id"))), reader.GetString(reader.GetOrdinal("canonical_tag")), reader.GetString(reader.GetOrdinal("display_tag")), Enum.Parse<MediaTagSource>(reader.GetString(reader.GetOrdinal("source"))), GetNullableString(reader, "note"), ParseTime(reader.GetString(reader.GetOrdinal("created_at"))), ParseTime(reader.GetString(reader.GetOrdinal("updated_at"))));
}

internal static class TagTextEmbeddingValidation
{
    public static bool SourceTextIsInvalid(this TagTextEmbedding embedding) => string.IsNullOrWhiteSpace(embedding.TaxonomyVersion) || string.IsNullOrWhiteSpace(embedding.TagId) || string.IsNullOrWhiteSpace(embedding.TextRepresentationVersion) || string.IsNullOrWhiteSpace(embedding.Text) || string.IsNullOrWhiteSpace(embedding.ModelName) || string.IsNullOrWhiteSpace(embedding.ModelRevision) || string.IsNullOrWhiteSpace(embedding.ProcessorVersion) || embedding.Dimension <= 0;
}
