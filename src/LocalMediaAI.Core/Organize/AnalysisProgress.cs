namespace LocalMediaAI.Core.Organize;

/// <summary>An unfinished analysis job, attributed to its folder when known. <paramref name="Stages"/> is set for per-file orchestration jobs.</summary>
public sealed record ActiveAnalysisJob(Guid? RootId, JobType Type, JobState State, int Current, int Total, string? FileName, long? SizeBytes,
    ProtectedSourceStageEligibility Stages = ProtectedSourceStageEligibility.None);

/// <summary>The most recent scan of a folder (what the change check found).</summary>
public sealed record LatestScan(Guid RootId, string State, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, int Seen, int Added, int Updated, int Unavailable);

public sealed record AnalysisProgress(IReadOnlyList<ActiveAnalysisJob> Jobs, IReadOnlyList<LatestScan> Scans)
{
    public static AnalysisProgress Empty { get; } = new([], []);
}

/// <summary>The four steps shown to the user: ファイルの一覧 → 動画の情報 → 完全に同じか確認 → よく似ているか確認.</summary>
public static class AnalysisStages
{
    public static IReadOnlyList<string> Names => [L.T("ファイルの一覧"), L.T("動画の情報"), L.T("完全に同じか確認"), L.T("よく似ているか確認")];

    public static int Of(JobType type) => type switch
    {
        JobType.Scan => 0,
        JobType.Metadata or JobType.Thumbnail => 1,
        JobType.FullHash => 2,
        _ => 3
    };

    /// <summary>
    /// Step of one job. Per-file orchestration is split into a quick pass (動画の情報) and a deep pass, which counts as
    /// 完全に同じか確認 while it still has to hash the file and as よく似ているか確認 otherwise.
    /// </summary>
    public static int Of(ActiveAnalysisJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Type != JobType.ProtectedSourceOrchestration || job.Stages == ProtectedSourceStageEligibility.None) return Of(job.Type);
        if (Processing.JobLanes.IsQuickPass(job.Stages)) return 1;
        return job.Stages.HasFlag(ProtectedSourceStageEligibility.FullHash) ? 2 : 3;
    }

    /// <summary>The earliest step that still has work; <see cref="Names"/>.Count when nothing is left.</summary>
    public static int Current(IEnumerable<ActiveAnalysisJob> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        return jobs.Select(Of).DefaultIfEmpty(Names.Count).Min();
    }
}
