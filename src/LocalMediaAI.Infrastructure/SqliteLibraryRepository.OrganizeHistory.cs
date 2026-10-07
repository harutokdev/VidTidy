using LocalMediaAI.Core.Organize;

namespace LocalMediaAI.Infrastructure;

public sealed partial class SqliteLibraryRepository
{
    /// <summary>Finished batches with the files that were actually moved or recycled, newest first.</summary>
    public async Task<IReadOnlyList<CleanupBatchSummary>> GetCleanupBatchesAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        const string sql = "SELECT b.batch_operation_id,b.operation_type,b.completed_at,b.created_at,COUNT(i.batch_item_id),COALESCE(SUM(m.size_bytes),0) FROM batch_operation b JOIN batch_item i ON i.batch_operation_id=b.batch_operation_id AND i.batch_item_state='Completed' JOIN media_file m ON m.media_file_id=i.media_file_id WHERE b.completed_at IS NOT NULL GROUP BY b.batch_operation_id ORDER BY b.completed_at DESC LIMIT $limit;";
        return await QueryAsync(connection, null, sql,
            reader => new CleanupBatchSummary(Guid.Parse(reader.GetString(0)), reader.GetString(1), ParseBatchDate(reader.GetString(2)), ParseBatchDate(reader.GetString(3)), reader.GetInt32(4), reader.GetInt64(5)),
            cancellationToken, ("$limit", Math.Clamp(limit, 1, 2000)));
    }

    /// <summary>Original paths of the files completed in the given batches.</summary>
    public async Task<IReadOnlyList<string>> GetCleanupBatchFilesAsync(IReadOnlyList<Guid> batchIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batchIds);
        var result = new List<string>();
        await using var connection = await OpenAsync(cancellationToken);
        foreach (var id in batchIds)
            result.AddRange(await QueryAsync(connection, null, "SELECT source_path FROM batch_item WHERE batch_operation_id=$id AND batch_item_state='Completed' ORDER BY sequence;",
                reader => reader.GetString(0), cancellationToken, ("$id", id.ToString("D"))));
        return result;
    }

    /// <summary>Present files whose latest analysis job failed and has not succeeded since.</summary>
    public async Task<IReadOnlyList<UnanalyzedFile>> GetUnanalyzedFilesAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        const string sql = "SELECT mf.media_file_id,mf.library_root_id,mf.file_name,mf.path,j.last_error_code,j.last_error_message FROM job j JOIN media_file mf ON mf.media_file_id=json_extract(j.payload_json,'$.MediaFileId') JOIN library_root lr ON lr.library_root_id=mf.library_root_id WHERE j.state='Failed' AND mf.presence_state='Present' AND mf.cleanup_committed_at IS NULL AND lr.registration_state='Active' AND NOT EXISTS(SELECT 1 FROM job k WHERE k.job_type=j.job_type AND json_extract(k.payload_json,'$.MediaFileId')=mf.media_file_id AND k.state='Completed' AND k.updated_at>j.updated_at) GROUP BY mf.media_file_id ORDER BY MAX(j.updated_at) DESC LIMIT $limit;";
        return await QueryAsync(connection, null, sql,
            reader => new UnanalyzedFile(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5)),
            cancellationToken, ("$limit", Math.Clamp(limit, 1, 1000)));
    }
}
