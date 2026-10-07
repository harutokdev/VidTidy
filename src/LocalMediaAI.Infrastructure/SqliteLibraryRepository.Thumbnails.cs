namespace LocalMediaAI.Infrastructure;

public sealed partial class SqliteLibraryRepository
{
    // Current content only. The caller checks cache-file existence outside the DB and
    // schedules the existing bounded Thumbnail job when a rebuildable artifact is lost.
    public async Task<IReadOnlyList<(Guid MediaFileId, string? RelativePath)>> GetCurrentThumbnailCandidatesAsync(
        Guid libraryRootId, string configHash, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        const string sql = "SELECT mf.media_file_id,(SELECT a.relative_path FROM artifact a JOIN analysis an ON an.analysis_id=a.analysis_id WHERE an.media_content_id=mf.media_content_id AND an.analysis_type='Thumbnail' AND an.status='Completed' AND an.config_hash=$config AND a.artifact_kind='Thumbnail' ORDER BY a.created_at DESC LIMIT 1) FROM media_file mf JOIN library_root lr ON lr.library_root_id=mf.library_root_id WHERE mf.library_root_id=$root AND mf.presence_state='Present' AND mf.cleanup_committed_at IS NULL AND lr.registration_state='Active';";
        return await QueryAsync(connection, null, sql,
            reader => (Guid.Parse(reader.GetString(0)), reader.IsDBNull(1) ? null : reader.GetString(1)),
            cancellationToken, ("$root", libraryRootId.ToString("D")), ("$config", configHash));
    }

    public async Task<string?> GetCompletedThumbnailArtifactPathAsync(Guid mediaContentId, string configHash, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        const string sql = "SELECT a.relative_path FROM artifact a JOIN analysis an ON an.analysis_id=a.analysis_id WHERE an.media_content_id=$content AND an.analysis_type='Thumbnail' AND an.status='Completed' AND an.config_hash=$config AND a.artifact_kind='Thumbnail' ORDER BY a.created_at DESC LIMIT 1;";
        return await ScalarStringAsync(connection, null, sql, cancellationToken,
            ("$content", mediaContentId.ToString("D")), ("$config", configHash));
    }
}
