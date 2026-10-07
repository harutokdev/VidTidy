using LocalMediaAI.Core;
using LocalMediaAI.Core.Organize;

namespace LocalMediaAI.Infrastructure;

public sealed partial class SqliteLibraryRepository
{
    /// <summary>Unfinished jobs with their folder and, for file jobs, the file; plus the latest scan of each active folder.</summary>
    public async Task<AnalysisProgress> GetAnalysisProgressAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        const string jobsSql = "SELECT j.job_type,j.state,j.progress_current,j.progress_total,COALESCE(json_extract(j.payload_json,'$.LibraryRootId'),mf.library_root_id),mf.file_name,mf.size_bytes,json_extract(j.payload_json,'$.EligibleStages') FROM job j LEFT JOIN media_file mf ON mf.media_file_id=json_extract(j.payload_json,'$.MediaFileId') WHERE j.state IN ('Pending','Running','Waiting','RetryWaiting');";
        var jobs = await QueryAsync(connection, null, jobsSql, reader => new ActiveAnalysisJob(
            reader.IsDBNull(4) || !Guid.TryParse(reader.GetString(4), out var root) ? null : root,
            Enum.TryParse<JobType>(reader.GetString(0), out var type) ? type : JobType.Scan,
            Enum.TryParse<JobState>(reader.GetString(1), out var state) ? state : JobState.Pending,
            reader.GetInt32(2), reader.GetInt32(3),
            reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetInt64(6),
            reader.IsDBNull(7) ? ProtectedSourceStageEligibility.None : (ProtectedSourceStageEligibility)reader.GetInt32(7)), cancellationToken);
        const string scansSql = "SELECT s.library_root_id,s.state,s.started_at,s.completed_at,s.files_seen,s.files_added,s.files_updated,s.files_unavailable FROM library_scan s JOIN library_root r ON r.library_root_id=s.library_root_id WHERE r.registration_state='Active' AND s.started_at=(SELECT MAX(started_at) FROM library_scan x WHERE x.library_root_id=s.library_root_id);";
        var scans = await QueryAsync(connection, null, scansSql, reader => new LatestScan(
            Guid.Parse(reader.GetString(0)), reader.GetString(1), ParseBatchDate(reader.GetString(2)), reader.IsDBNull(3) ? null : ParseBatchDate(reader.GetString(3)),
            reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7)), cancellationToken);
        return new AnalysisProgress(jobs, scans);
    }
}
