namespace LocalMediaAI.Core.Organize;

/// <summary>Completed items of one controlled batch (at most 25 files).</summary>
public sealed record CleanupBatchSummary(Guid BatchId, string Operation, DateTimeOffset CompletedAt, DateTimeOffset CreatedAt, int FileCount, long Bytes);

/// <summary>One run of 確認して実行, which may span several batches.</summary>
public sealed record CleanupRun(DateTimeOffset CompletedAt, string Operation, int FileCount, long Bytes, IReadOnlyList<Guid> BatchIds);

/// <summary>A file the analysis could not finish, with the reason shown to the user.</summary>
public sealed record UnanalyzedFile(Guid MediaFileId, Guid LibraryRootId, string FileName, string Path, string? ErrorCode, string? ErrorMessage);

public static class CleanupHistory
{
    /// <summary>Batches started within this gap after the previous one finished belong to the same run.</summary>
    public static readonly TimeSpan RunGap = TimeSpan.FromMinutes(2);

    /// <summary>Joins consecutive batches of the same operation into runs, newest first.</summary>
    public static IReadOnlyList<CleanupRun> GroupIntoRuns(IEnumerable<CleanupBatchSummary> batches)
    {
        ArgumentNullException.ThrowIfNull(batches);
        var runs = new List<List<CleanupBatchSummary>>();
        foreach (var batch in batches.Where(b => b.FileCount > 0).OrderBy(b => b.CreatedAt))
        {
            var last = runs.LastOrDefault();
            if (last is not null && last[^1].Operation == batch.Operation && batch.CreatedAt - last[^1].CompletedAt <= RunGap) last.Add(batch);
            else runs.Add([batch]);
        }
        return runs
            .Select(r => new CleanupRun(r.Max(b => b.CompletedAt), r[0].Operation, r.Sum(b => b.FileCount), r.Sum(b => b.Bytes), r.Select(b => b.BatchId).ToArray()))
            .OrderByDescending(r => r.CompletedAt)
            .ToArray();
    }
}
