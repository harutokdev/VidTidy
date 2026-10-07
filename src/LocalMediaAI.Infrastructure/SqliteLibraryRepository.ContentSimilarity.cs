using LocalMediaAI.Core;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure;

public sealed partial class SqliteLibraryRepository
{
    private const int ContentSimilarityBucketCardinalityCap = 256;

    public async Task<ContentSimilaritySourceEmbeddings> GetContentSimilaritySourceEmbeddingsAsync(
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
        return new ContentSimilaritySourceEmbeddings(expected, completed);
    }

    public async Task<IReadOnlyList<MediaFile>> GetContentSimilarityRepresentationCandidatesAsync(
        Guid? rootId,
        string aggregationVersion,
        string modelName,
        string modelRevision,
        string processorVersion,
        string sourceSamplingVersion,
        string dtype,
        int dimension,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT mf.* FROM media_file mf WHERE mf.presence_state='Present' AND ($root IS NULL OR mf.library_root_id=$root) AND EXISTS(SELECT 1 FROM visual_sample vs JOIN visual_embedding ve ON ve.visual_sample_id=vs.visual_sample_id WHERE vs.media_file_id=mf.media_file_id AND vs.media_content_id=mf.media_content_id AND vs.sampling_version=$sampling AND vs.status='Completed' AND ve.status='Completed' AND ve.normalized=1 AND ve.model_name=$model AND ve.model_revision=$revision AND ve.processor_version=$processor AND ve.dtype=$dtype AND ve.dimension=$dimension) AND NOT EXISTS(SELECT 1 FROM content_similarity_representation csr WHERE csr.media_file_id=mf.media_file_id AND csr.media_content_id=mf.media_content_id AND csr.aggregation_version=$aggregation AND csr.model_name=$model AND csr.model_revision=$revision AND csr.processor_version=$processor AND csr.source_sampling_version=$sampling AND csr.dtype=$dtype AND csr.dimension=$dimension AND csr.status='Completed' AND csr.coverage_ratio>=1.0) ORDER BY mf.file_name COLLATE NOCASE,mf.media_file_id;";
        await using var connection = await OpenAsync(cancellationToken);
        return await QueryAsync(connection, null, sql, ReadMediaFile, cancellationToken, ("$root", rootId?.ToString("D") ?? (object)DBNull.Value), ("$aggregation", aggregationVersion), ("$model", modelName), ("$revision", modelRevision), ("$processor", processorVersion), ("$sampling", sourceSamplingVersion), ("$dtype", dtype), ("$dimension", dimension));
    }

    public async Task<ContentSimilarityRepresentation?> GetCurrentContentSimilarityRepresentationAsync(
        Guid mediaFileId,
        Guid mediaContentId,
        string aggregationVersion,
        string modelName,
        string modelRevision,
        string processorVersion,
        string sourceSamplingVersion,
        string dtype,
        int dimension,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT csr.* FROM content_similarity_representation csr JOIN media_file mf ON mf.media_file_id=csr.media_file_id AND mf.media_content_id=csr.media_content_id WHERE csr.media_file_id=$file AND csr.media_content_id=$content AND csr.aggregation_version=$aggregation AND csr.model_name=$model AND csr.model_revision=$revision AND csr.processor_version=$processor AND csr.source_sampling_version=$sampling AND csr.dtype=$dtype AND csr.dimension=$dimension AND csr.status='Completed' AND csr.normalized=1 AND mf.presence_state='Present' ORDER BY csr.updated_at DESC LIMIT 1;";
        await using var connection = await OpenAsync(cancellationToken);
        return await QuerySingleAsync(connection, null, sql, ReadContentSimilarityRepresentation, cancellationToken,
            ("$file", mediaFileId.ToString("D")), ("$content", mediaContentId.ToString("D")), ("$aggregation", aggregationVersion), ("$model", modelName), ("$revision", modelRevision), ("$processor", processorVersion), ("$sampling", sourceSamplingVersion), ("$dtype", dtype), ("$dimension", dimension));
    }

    public async Task<bool> UpsertContentSimilarityRepresentationAsync(ContentSimilarityRepresentation representation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(representation);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var current = await ScalarLongAsync(connection, transaction, "SELECT COUNT(1) FROM media_file WHERE media_file_id=$file AND media_content_id=$content AND presence_state='Present';", cancellationToken, ("$file", representation.MediaFileId.ToString("D")), ("$content", representation.MediaContentId.ToString("D")));
        if (current != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO content_similarity_representation(content_similarity_representation_id,media_file_id,media_content_id,model_name,model_revision,processor_version,source_sampling_version,aggregation_version,source_embedding_digest,dtype,dimension,normalized,available_sample_count,expected_sample_count,coverage_ratio,vector_float32,status,error_code,error_message,created_at,updated_at) VALUES($id,$file,$content,$model,$revision,$processor,$sampling,$aggregation,$digest,$dtype,$dimension,$normalized,$available,$expected,$coverage,$vector,$status,$code,$message,$created,$updated) ON CONFLICT(media_file_id,media_content_id,model_name,model_revision,processor_version,source_sampling_version,aggregation_version,source_embedding_digest,dtype,dimension) DO UPDATE SET vector_float32=excluded.vector_float32,normalized=excluded.normalized,available_sample_count=excluded.available_sample_count,expected_sample_count=excluded.expected_sample_count,coverage_ratio=excluded.coverage_ratio,status=excluded.status,error_code=excluded.error_code,error_message=excluded.error_message,updated_at=excluded.updated_at;", cancellationToken,
            ("$id", representation.ContentSimilarityRepresentationId.ToString("D")), ("$file", representation.MediaFileId.ToString("D")), ("$content", representation.MediaContentId.ToString("D")), ("$model", representation.ModelName), ("$revision", representation.ModelRevision), ("$processor", representation.ProcessorVersion), ("$sampling", representation.SourceSamplingVersion), ("$aggregation", representation.AggregationVersion), ("$digest", representation.SourceEmbeddingDigest), ("$dtype", representation.Dtype), ("$dimension", representation.Dimension), ("$normalized", representation.Normalized ? 1 : 0), ("$available", representation.AvailableSampleCount), ("$expected", representation.ExpectedSampleCount), ("$coverage", representation.CoverageRatio), ("$vector", (object?)representation.VectorFloat32 ?? DBNull.Value), ("$status", representation.Status), ("$code", (object?)representation.ErrorCode ?? DBNull.Value), ("$message", (object?)representation.ErrorMessage ?? DBNull.Value), ("$created", ToDb(representation.CreatedAt)), ("$updated", ToDb(representation.UpdatedAt)));

        var stored = await QuerySingleAsync(connection, transaction, "SELECT * FROM content_similarity_representation WHERE media_file_id=$file AND media_content_id=$content AND model_name=$model AND model_revision=$revision AND processor_version=$processor AND source_sampling_version=$sampling AND aggregation_version=$aggregation AND source_embedding_digest=$digest AND dtype=$dtype AND dimension=$dimension;", ReadContentSimilarityRepresentation, cancellationToken,
            ("$file", representation.MediaFileId.ToString("D")), ("$content", representation.MediaContentId.ToString("D")), ("$model", representation.ModelName), ("$revision", representation.ModelRevision), ("$processor", representation.ProcessorVersion), ("$sampling", representation.SourceSamplingVersion), ("$aggregation", representation.AggregationVersion), ("$digest", representation.SourceEmbeddingDigest), ("$dtype", representation.Dtype), ("$dimension", representation.Dimension));
        if (stored is null) throw new InvalidOperationException("Content similarity representation persistence did not return the stored row.");

        await ExecuteNonQueryAsync(connection, transaction, "DELETE FROM content_similarity_lsh_bucket WHERE content_similarity_representation_id=$id AND candidate_index_version=$version;", cancellationToken, ("$id", stored.ContentSimilarityRepresentationId.ToString("D")), ("$version", ContentSimilarityVectors.CandidateIndexVersion));
        if (stored.Status == "Completed" && stored.Normalized && stored.VectorFloat32 is not null && stored.CoverageRatio >= 1d)
        {
            var signature = ContentSimilaritySignLsh.Signature(ContentSimilarityVectors.Decode(stored.VectorFloat32, stored.Dimension));
            var bands = ContentSimilaritySignLsh.Bands(signature);
            for (var index = 0; index < bands.Count; index++)
                await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO content_similarity_lsh_bucket(content_similarity_representation_id,candidate_index_version,band_index,band_value) VALUES($id,$version,$index,$value);", cancellationToken, ("$id", stored.ContentSimilarityRepresentationId.ToString("D")), ("$version", ContentSimilarityVectors.CandidateIndexVersion), ("$index", index), ("$value", bands[index]));
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<ContentSimilarityCandidate>> GetContentSimilarityCandidatesAsync(
        Guid? rootId,
        string aggregationVersion,
        string modelName,
        string modelRevision,
        string processorVersion,
        string sourceSamplingVersion,
        string dtype,
        int dimension,
        int topK,
        CancellationToken cancellationToken)
    {
        topK = Math.Clamp(topK, 1, ContentSimilarityVerificationProfile.HighPrecisionV1.TopK);
        const string sql = "WITH current_rep AS (SELECT csr.* FROM content_similarity_representation csr JOIN media_file mf ON mf.media_file_id=csr.media_file_id AND mf.media_content_id=csr.media_content_id WHERE csr.status='Completed' AND csr.normalized=1 AND csr.coverage_ratio>=1.0 AND mf.presence_state='Present' AND csr.aggregation_version=$aggregation AND csr.model_name=$model AND csr.model_revision=$revision AND csr.processor_version=$processor AND csr.source_sampling_version=$sampling AND csr.dtype=$dtype AND csr.dimension=$dimension AND ($root IS NULL OR mf.library_root_id=$root)), allowed_bucket AS (SELECT lsh.content_similarity_representation_id,lsh.band_index,lsh.band_value FROM content_similarity_lsh_bucket lsh JOIN current_rep cr ON cr.content_similarity_representation_id=lsh.content_similarity_representation_id WHERE lsh.candidate_index_version=$indexVersion AND (SELECT COUNT(1) FROM content_similarity_lsh_bucket sized WHERE sized.candidate_index_version=lsh.candidate_index_version AND sized.band_index=lsh.band_index AND sized.band_value=lsh.band_value) <= $bucketCap), raw_pair AS (SELECT a.content_similarity_representation_id a_rep_id,a.media_file_id a_file_id,a.media_content_id a_content_id,a.vector_float32 a_vector,a.dimension a_dimension,b.content_similarity_representation_id b_rep_id,b.media_file_id b_file_id,b.media_content_id b_content_id,b.vector_float32 b_vector,b.dimension b_dimension FROM allowed_bucket la JOIN allowed_bucket lb ON lb.band_index=la.band_index AND lb.band_value=la.band_value AND lb.content_similarity_representation_id>la.content_similarity_representation_id JOIN current_rep a ON a.content_similarity_representation_id=la.content_similarity_representation_id JOIN current_rep b ON b.content_similarity_representation_id=lb.content_similarity_representation_id WHERE a.media_file_id<>b.media_file_id AND NOT EXISTS(SELECT 1 FROM duplicate_member da JOIN duplicate_member db ON db.duplicate_group_id=da.duplicate_group_id JOIN duplicate_group dg ON dg.duplicate_group_id=da.duplicate_group_id AND dg.relation_type='Exact' WHERE (da.media_file_id=a.media_file_id AND da.media_content_id=a.media_content_id AND db.media_file_id=b.media_file_id AND db.media_content_id=b.media_content_id) OR (da.media_file_id=b.media_file_id AND da.media_content_id=b.media_content_id AND db.media_file_id=a.media_file_id AND db.media_content_id=a.media_content_id)) AND NOT EXISTS(SELECT 1 FROM near_duplicate_evidence ne WHERE ne.status='Verified' AND ((ne.media_file_a_id=a.media_file_id AND ne.media_content_a_id=a.media_content_id AND ne.media_file_b_id=b.media_file_id AND ne.media_content_b_id=b.media_content_id) OR (ne.media_file_a_id=b.media_file_id AND ne.media_content_a_id=b.media_content_id AND ne.media_file_b_id=a.media_file_id AND ne.media_content_b_id=a.media_content_id)))) SELECT DISTINCT * FROM raw_pair LIMIT 100000;";
        await using var connection = await OpenAsync(cancellationToken);
        var pairs = await QueryAsync(connection, null, sql, reader =>
        {
            var vectorA = reader.GetFieldValue<byte[]>(reader.GetOrdinal("a_vector"));
            var vectorB = reader.GetFieldValue<byte[]>(reader.GetOrdinal("b_vector"));
            var dimensionA = reader.GetInt32(reader.GetOrdinal("a_dimension"));
            var dimensionB = reader.GetInt32(reader.GetOrdinal("b_dimension"));
            var fileA = Guid.Parse(reader.GetString(reader.GetOrdinal("a_file_id")));
            var contentA = Guid.Parse(reader.GetString(reader.GetOrdinal("a_content_id")));
            var representationA = Guid.Parse(reader.GetString(reader.GetOrdinal("a_rep_id")));
            var fileB = Guid.Parse(reader.GetString(reader.GetOrdinal("b_file_id")));
            var contentB = Guid.Parse(reader.GetString(reader.GetOrdinal("b_content_id")));
            var representationB = Guid.Parse(reader.GetString(reader.GetOrdinal("b_rep_id")));
            var score = ContentSimilarityVectors.Cosine(ContentSimilarityVectors.Decode(vectorA, dimensionA), ContentSimilarityVectors.Decode(vectorB, dimensionB));
            return string.CompareOrdinal(fileA.ToString("D"), fileB.ToString("D")) < 0
                ? new ContentSimilarityCandidate(fileA, contentA, representationA, fileB, contentB, representationB, score)
                : new ContentSimilarityCandidate(fileB, contentB, representationB, fileA, contentA, representationA, score);
        }, cancellationToken,
            ("$root", rootId?.ToString("D") ?? (object)DBNull.Value), ("$aggregation", aggregationVersion), ("$model", modelName), ("$revision", modelRevision), ("$processor", processorVersion), ("$sampling", sourceSamplingVersion), ("$dtype", dtype), ("$dimension", dimension), ("$indexVersion", ContentSimilarityVectors.CandidateIndexVersion), ("$bucketCap", ContentSimilarityBucketCardinalityCap));

        var selected = new Dictionary<(Guid A, Guid B), ContentSimilarityCandidate>();
        foreach (var group in pairs.SelectMany(pair => new[]
                 {
                     (MediaFileId: pair.MediaFileAId, Candidate: pair),
                     (MediaFileId: pair.MediaFileBId, Candidate: pair)
                 }).GroupBy(item => item.MediaFileId))
        {
            foreach (var item in group.OrderByDescending(item => item.Candidate.MeanCosineSimilarity).ThenBy(item => item.Candidate.MediaFileAId).ThenBy(item => item.Candidate.MediaFileBId).Take(topK))
                selected[(item.Candidate.MediaFileAId, item.Candidate.MediaFileBId)] = item.Candidate;
        }
        return selected.Values.OrderByDescending(candidate => candidate.MeanCosineSimilarity).ThenBy(candidate => candidate.MediaFileAId).ThenBy(candidate => candidate.MediaFileBId).ToArray();
    }

    public async Task<ContentSimilarityEvidence?> GetContentSimilarityEvidenceAsync(Guid mediaFileAId, Guid mediaContentAId, Guid mediaFileBId, Guid mediaContentBId, string aggregationVersion, string thresholdVersion, CancellationToken cancellationToken)
    {
        CanonicalizeContentSimilarity(ref mediaFileAId, ref mediaContentAId, ref mediaFileBId, ref mediaContentBId);
        const string sql = "SELECT * FROM content_similarity_evidence WHERE media_file_a_id=$fileA AND media_content_a_id=$contentA AND media_file_b_id=$fileB AND media_content_b_id=$contentB AND aggregation_version=$aggregation AND threshold_version=$threshold ORDER BY verified_at DESC LIMIT 1;";
        await using var connection = await OpenAsync(cancellationToken);
        return await QuerySingleAsync(connection, null, sql, ReadContentSimilarityEvidence, cancellationToken, ("$fileA", mediaFileAId.ToString("D")), ("$contentA", mediaContentAId.ToString("D")), ("$fileB", mediaFileBId.ToString("D")), ("$contentB", mediaContentBId.ToString("D")), ("$aggregation", aggregationVersion), ("$threshold", thresholdVersion));
    }

    public async Task<bool> UpsertContentSimilarityEvidenceAsync(ContentSimilarityEvidence evidence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (string.CompareOrdinal(evidence.MediaFileAId.ToString("D"), evidence.MediaFileBId.ToString("D")) >= 0) throw new InvalidOperationException("Content similarity evidence pair must be canonicalized.");
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        const string currentSql = "SELECT COUNT(1) FROM content_similarity_representation a JOIN content_similarity_representation b JOIN media_file ma ON ma.media_file_id=a.media_file_id AND ma.media_content_id=a.media_content_id JOIN media_file mb ON mb.media_file_id=b.media_file_id AND mb.media_content_id=b.media_content_id WHERE a.content_similarity_representation_id=$repA AND b.content_similarity_representation_id=$repB AND a.media_file_id=$fileA AND a.media_content_id=$contentA AND b.media_file_id=$fileB AND b.media_content_id=$contentB AND a.status='Completed' AND b.status='Completed' AND a.coverage_ratio>=1.0 AND b.coverage_ratio>=1.0 AND ma.presence_state='Present' AND mb.presence_state='Present';";
        var current = await ScalarLongAsync(connection, transaction, currentSql, cancellationToken, ("$repA", evidence.ContentSimilarityRepresentationAId.ToString("D")), ("$repB", evidence.ContentSimilarityRepresentationBId.ToString("D")), ("$fileA", evidence.MediaFileAId.ToString("D")), ("$contentA", evidence.MediaContentAId.ToString("D")), ("$fileB", evidence.MediaFileBId.ToString("D")), ("$contentB", evidence.MediaContentBId.ToString("D")));
        if (current != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO content_similarity_evidence(content_similarity_evidence_id,media_file_a_id,media_content_a_id,content_similarity_representation_a_id,media_file_b_id,media_content_b_id,content_similarity_representation_b_id,representative_visual_sample_a_id,representative_visual_sample_b_id,model_name,model_revision,processor_version,source_sampling_version,aggregation_version,threshold_version,similarity_metric,mean_cosine_similarity,sample_aware_cosine_similarity,representative_cosine_similarity,coverage_sample_count_a,coverage_expected_count_a,coverage_sample_count_b,coverage_expected_count_b,evidence_digest,status,error_code,error_message,verified_at) VALUES($id,$fileA,$contentA,$repA,$fileB,$contentB,$repB,$sampleA,$sampleB,$model,$revision,$processor,$sampling,$aggregation,$threshold,$metric,$mean,$sampleAware,$representative,$availableA,$expectedA,$availableB,$expectedB,$digest,$status,$code,$message,$verified) ON CONFLICT(media_file_a_id,media_content_a_id,content_similarity_representation_a_id,media_file_b_id,media_content_b_id,content_similarity_representation_b_id,aggregation_version,threshold_version) DO UPDATE SET representative_visual_sample_a_id=excluded.representative_visual_sample_a_id,representative_visual_sample_b_id=excluded.representative_visual_sample_b_id,similarity_metric=excluded.similarity_metric,mean_cosine_similarity=excluded.mean_cosine_similarity,sample_aware_cosine_similarity=excluded.sample_aware_cosine_similarity,representative_cosine_similarity=excluded.representative_cosine_similarity,coverage_sample_count_a=excluded.coverage_sample_count_a,coverage_expected_count_a=excluded.coverage_expected_count_a,coverage_sample_count_b=excluded.coverage_sample_count_b,coverage_expected_count_b=excluded.coverage_expected_count_b,evidence_digest=excluded.evidence_digest,status=excluded.status,error_code=excluded.error_code,error_message=excluded.error_message,verified_at=excluded.verified_at;", cancellationToken,
            ("$id", evidence.ContentSimilarityEvidenceId.ToString("D")), ("$fileA", evidence.MediaFileAId.ToString("D")), ("$contentA", evidence.MediaContentAId.ToString("D")), ("$repA", evidence.ContentSimilarityRepresentationAId.ToString("D")), ("$fileB", evidence.MediaFileBId.ToString("D")), ("$contentB", evidence.MediaContentBId.ToString("D")), ("$repB", evidence.ContentSimilarityRepresentationBId.ToString("D")), ("$sampleA", evidence.RepresentativeVisualSampleAId.ToString("D")), ("$sampleB", evidence.RepresentativeVisualSampleBId.ToString("D")), ("$model", evidence.ModelName), ("$revision", evidence.ModelRevision), ("$processor", evidence.ProcessorVersion), ("$sampling", evidence.SourceSamplingVersion), ("$aggregation", evidence.AggregationVersion), ("$threshold", evidence.ThresholdVersion), ("$metric", evidence.SimilarityMetric), ("$mean", evidence.MeanCosineSimilarity), ("$sampleAware", evidence.SampleAwareCosineSimilarity), ("$representative", evidence.RepresentativeCosineSimilarity), ("$availableA", evidence.CoverageSampleCountA), ("$expectedA", evidence.CoverageExpectedCountA), ("$availableB", evidence.CoverageSampleCountB), ("$expectedB", evidence.CoverageExpectedCountB), ("$digest", evidence.EvidenceDigest), ("$status", evidence.Status), ("$code", (object?)evidence.ErrorCode ?? DBNull.Value), ("$message", (object?)evidence.ErrorMessage ?? DBNull.Value), ("$verified", ToDb(evidence.VerifiedAt)));
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<PagedResult<ContentSimilarityEvidence>> QueryContentSimilarityEvidenceAsync(Guid? rootId, bool includeIgnored, int offset, int limit, CancellationToken cancellationToken)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 500);
        const string baseSql = "SELECT ce.*,cj.decision judgment_decision FROM content_similarity_evidence ce JOIN media_file ma ON ma.media_file_id=ce.media_file_a_id AND ma.media_content_id=ce.media_content_a_id AND ma.presence_state<>'Missing' JOIN media_file mb ON mb.media_file_id=ce.media_file_b_id AND mb.media_content_id=ce.media_content_b_id AND mb.presence_state<>'Missing' LEFT JOIN content_similarity_judgment cj ON cj.content_similarity_evidence_id=ce.content_similarity_evidence_id WHERE ce.status='Verified' AND ($root IS NULL OR (ma.library_root_id=$root AND mb.library_root_id=$root)) AND EXISTS(SELECT 1 FROM library_root sr WHERE sr.library_root_id=ma.library_root_id AND sr.registration_state='Active') AND EXISTS(SELECT 1 FROM library_root sr WHERE sr.library_root_id=mb.library_root_id AND sr.registration_state='Active') AND ($includeIgnored=1 OR COALESCE(cj.decision,'Unreviewed') NOT IN ('Ignored','NotRelated')) AND NOT EXISTS(SELECT 1 FROM duplicate_member da JOIN duplicate_member db ON db.duplicate_group_id=da.duplicate_group_id JOIN duplicate_group dg ON dg.duplicate_group_id=da.duplicate_group_id AND dg.relation_type='Exact' WHERE da.media_file_id=ce.media_file_a_id AND da.media_content_id=ce.media_content_a_id AND db.media_file_id=ce.media_file_b_id AND db.media_content_id=ce.media_content_b_id) AND NOT EXISTS(SELECT 1 FROM near_duplicate_evidence ne WHERE ne.status='Verified' AND ne.media_file_a_id=ce.media_file_a_id AND ne.media_content_a_id=ce.media_content_a_id AND ne.media_file_b_id=ce.media_file_b_id AND ne.media_content_b_id=ce.media_content_b_id) ORDER BY CASE COALESCE(cj.decision,'Unreviewed') WHEN 'Unreviewed' THEN 0 WHEN 'Related' THEN 1 WHEN 'KeepBoth' THEN 2 ELSE 3 END,ce.sample_aware_cosine_similarity DESC,ce.verified_at DESC";
        await using var connection = await OpenAsync(cancellationToken);
        var count = (int)await ScalarLongAsync(connection, null, "SELECT COUNT(1) FROM (" + baseSql + ");", cancellationToken, ("$root", rootId?.ToString("D") ?? (object)DBNull.Value), ("$includeIgnored", includeIgnored ? 1 : 0));
        var rows = await QueryAsync(connection, null, baseSql + " LIMIT $limit OFFSET $offset;", ReadContentSimilarityEvidence, cancellationToken, ("$root", rootId?.ToString("D") ?? (object)DBNull.Value), ("$includeIgnored", includeIgnored ? 1 : 0), ("$limit", limit), ("$offset", offset));
        return new PagedResult<ContentSimilarityEvidence>(rows, count, offset, limit);
    }

    public async Task<IReadOnlyList<ContentSimilarityComparisonMember>> GetContentSimilarityComparisonMembersAsync(Guid contentSimilarityEvidenceId, CancellationToken cancellationToken)
    {
        const string sql = "WITH side AS (SELECT ce.content_similarity_evidence_id,1 is_side_a,ce.media_file_a_id media_file_id,ce.media_content_a_id media_content_id,ce.representative_visual_sample_a_id sample_id FROM content_similarity_evidence ce WHERE ce.content_similarity_evidence_id=$id UNION ALL SELECT ce.content_similarity_evidence_id,0,ce.media_file_b_id,ce.media_content_b_id,ce.representative_visual_sample_b_id FROM content_similarity_evidence ce WHERE ce.content_similarity_evidence_id=$id) SELECT side.is_side_a,ce.*,mf.*,vs.*,mm.media_file_id md_media_file_id,mm.container_format md_container_format,mm.duration_seconds md_duration_seconds,mm.width md_width,mm.height md_height,mm.video_codec md_video_codec,mm.fps_text md_fps_text,mm.bitrate md_bitrate,mm.audio_stream_count md_audio_stream_count,mm.subtitle_stream_count md_subtitle_stream_count,mm.raw_summary_json md_raw_summary_json,mm.updated_at md_updated_at,cj.decision judgment_decision,cj.note judgment_note FROM side JOIN content_similarity_evidence ce ON ce.content_similarity_evidence_id=side.content_similarity_evidence_id JOIN media_file mf ON mf.media_file_id=side.media_file_id AND mf.media_content_id=side.media_content_id JOIN visual_sample vs ON vs.visual_sample_id=side.sample_id LEFT JOIN media_metadata mm ON mm.media_file_id=mf.media_file_id LEFT JOIN content_similarity_judgment cj ON cj.content_similarity_evidence_id=ce.content_similarity_evidence_id WHERE mf.presence_state<>'Missing' ORDER BY side.is_side_a DESC;";
        await using var connection = await OpenAsync(cancellationToken);
        return await QueryAsync(connection, null, sql, reader =>
        {
            var evidence = ReadContentSimilarityEvidence(reader);
            var media = ReadMediaFile(reader);
            var decision = GetNullableString(reader, "judgment_decision") is { } text ? Enum.Parse<ContentSimilarityJudgmentDecision>(text) : ContentSimilarityJudgmentDecision.Unreviewed;
            return new ContentSimilarityComparisonMember(evidence.ContentSimilarityEvidenceId, media.MediaFileId, media.MediaContentId, reader.GetInt64(reader.GetOrdinal("is_side_a")) != 0, media, ReadJoinedMetadata(reader, media.MediaFileId), ReadVisualSample(reader), evidence, decision, GetNullableString(reader, "judgment_note"));
        }, cancellationToken, ("$id", contentSimilarityEvidenceId.ToString("D")));
    }

    public async Task<ContentSimilarityJudgment> UpsertContentSimilarityJudgmentAsync(Guid contentSimilarityEvidenceId, ContentSimilarityJudgmentDecision decision, string? note, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var exists = await ScalarLongAsync(connection, transaction, "SELECT COUNT(1) FROM content_similarity_evidence WHERE content_similarity_evidence_id=$id;", cancellationToken, ("$id", contentSimilarityEvidenceId.ToString("D")));
        if (exists != 1) throw new InvalidOperationException("The content similarity edge does not exist.");
        var existing = await QuerySingleAsync(connection, transaction, "SELECT * FROM content_similarity_judgment WHERE content_similarity_evidence_id=$id;", ReadContentSimilarityJudgment, cancellationToken, ("$id", contentSimilarityEvidenceId.ToString("D")));
        var now = DateTimeOffset.UtcNow;
        var judgment = new ContentSimilarityJudgment(existing?.ContentSimilarityJudgmentId ?? Guid.NewGuid(), contentSimilarityEvidenceId, decision, "Human", string.IsNullOrWhiteSpace(note) ? null : note.Trim(), existing?.CreatedAt ?? now, now);
        await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO content_similarity_judgment(content_similarity_judgment_id,content_similarity_evidence_id,decision,origin,note,created_at,updated_at) VALUES($id,$edge,$decision,'Human',$note,$created,$updated) ON CONFLICT(content_similarity_evidence_id) DO UPDATE SET decision=excluded.decision,origin='Human',note=excluded.note,updated_at=excluded.updated_at;", cancellationToken, ("$id", judgment.ContentSimilarityJudgmentId.ToString("D")), ("$edge", contentSimilarityEvidenceId.ToString("D")), ("$decision", decision.ToString()), ("$note", (object?)judgment.Note ?? DBNull.Value), ("$created", ToDb(judgment.CreatedAt)), ("$updated", ToDb(judgment.UpdatedAt)));
        await transaction.CommitAsync(cancellationToken);
        return judgment;
    }

    public async Task<IReadOnlyList<ContentSimilarityJudgment>> GetContentSimilarityJudgmentsAsync(Guid contentSimilarityEvidenceId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await QueryAsync(connection, null, "SELECT * FROM content_similarity_judgment WHERE content_similarity_evidence_id=$id ORDER BY updated_at DESC;", ReadContentSimilarityJudgment, cancellationToken, ("$id", contentSimilarityEvidenceId.ToString("D")));
    }

    private static ContentSimilarityRepresentation ReadContentSimilarityRepresentation(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(reader.GetOrdinal("content_similarity_representation_id"))), Guid.Parse(reader.GetString(reader.GetOrdinal("media_file_id"))), Guid.Parse(reader.GetString(reader.GetOrdinal("media_content_id"))), reader.GetString(reader.GetOrdinal("model_name")), reader.GetString(reader.GetOrdinal("model_revision")), reader.GetString(reader.GetOrdinal("processor_version")), reader.GetString(reader.GetOrdinal("source_sampling_version")), reader.GetString(reader.GetOrdinal("aggregation_version")), reader.GetString(reader.GetOrdinal("source_embedding_digest")), reader.GetString(reader.GetOrdinal("dtype")), reader.GetInt32(reader.GetOrdinal("dimension")), reader.GetInt64(reader.GetOrdinal("normalized")) != 0, reader.GetInt32(reader.GetOrdinal("available_sample_count")), reader.GetInt32(reader.GetOrdinal("expected_sample_count")), reader.GetDouble(reader.GetOrdinal("coverage_ratio")), reader.IsDBNull(reader.GetOrdinal("vector_float32")) ? null : reader.GetFieldValue<byte[]>(reader.GetOrdinal("vector_float32")), reader.GetString(reader.GetOrdinal("status")), GetNullableString(reader, "error_code"), GetNullableString(reader, "error_message"), ParseTime(reader.GetString(reader.GetOrdinal("created_at"))), ParseTime(reader.GetString(reader.GetOrdinal("updated_at"))));

    private static ContentSimilarityEvidence ReadContentSimilarityEvidence(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(reader.GetOrdinal("content_similarity_evidence_id"))), Guid.Parse(reader.GetString(reader.GetOrdinal("media_file_a_id"))), Guid.Parse(reader.GetString(reader.GetOrdinal("media_content_a_id"))), Guid.Parse(reader.GetString(reader.GetOrdinal("content_similarity_representation_a_id"))), Guid.Parse(reader.GetString(reader.GetOrdinal("media_file_b_id"))), Guid.Parse(reader.GetString(reader.GetOrdinal("media_content_b_id"))), Guid.Parse(reader.GetString(reader.GetOrdinal("content_similarity_representation_b_id"))), Guid.Parse(reader.GetString(reader.GetOrdinal("representative_visual_sample_a_id"))), Guid.Parse(reader.GetString(reader.GetOrdinal("representative_visual_sample_b_id"))), reader.GetString(reader.GetOrdinal("model_name")), reader.GetString(reader.GetOrdinal("model_revision")), reader.GetString(reader.GetOrdinal("processor_version")), reader.GetString(reader.GetOrdinal("source_sampling_version")), reader.GetString(reader.GetOrdinal("aggregation_version")), reader.GetString(reader.GetOrdinal("threshold_version")), reader.GetString(reader.GetOrdinal("similarity_metric")), reader.GetDouble(reader.GetOrdinal("mean_cosine_similarity")), reader.GetDouble(reader.GetOrdinal("sample_aware_cosine_similarity")), reader.GetDouble(reader.GetOrdinal("representative_cosine_similarity")), reader.GetInt32(reader.GetOrdinal("coverage_sample_count_a")), reader.GetInt32(reader.GetOrdinal("coverage_expected_count_a")), reader.GetInt32(reader.GetOrdinal("coverage_sample_count_b")), reader.GetInt32(reader.GetOrdinal("coverage_expected_count_b")), reader.GetString(reader.GetOrdinal("evidence_digest")), reader.GetString(reader.GetOrdinal("status")), GetNullableString(reader, "error_code"), GetNullableString(reader, "error_message"), ParseTime(reader.GetString(reader.GetOrdinal("verified_at"))));

    private static ContentSimilarityJudgment ReadContentSimilarityJudgment(SqliteDataReader reader) => new(Guid.Parse(reader.GetString(reader.GetOrdinal("content_similarity_judgment_id"))), Guid.Parse(reader.GetString(reader.GetOrdinal("content_similarity_evidence_id"))), Enum.Parse<ContentSimilarityJudgmentDecision>(reader.GetString(reader.GetOrdinal("decision"))), reader.GetString(reader.GetOrdinal("origin")), GetNullableString(reader, "note"), ParseTime(reader.GetString(reader.GetOrdinal("created_at"))), ParseTime(reader.GetString(reader.GetOrdinal("updated_at"))));

    private static void CanonicalizeContentSimilarity(ref Guid fileA, ref Guid contentA, ref Guid fileB, ref Guid contentB)
    {
        if (string.CompareOrdinal(fileA.ToString("D"), fileB.ToString("D")) < 0) return;
        (fileA, fileB) = (fileB, fileA);
        (contentA, contentB) = (contentB, contentA);
    }
}
