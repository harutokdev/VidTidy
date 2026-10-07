using LocalMediaAI.Core.Organize;

namespace LocalMediaAI.App.Organize;

/// <summary>One file to send to the recycle bin, with the file of its group that stays (none for a single file the user picked).</summary>
/// <param name="Approved">A similar group approved together with other clear ones; like exact duplicates it needs no check row by row.</param>
public sealed record CleanupRequest(Guid GroupId, CleanupKind Kind, OrganizeFile? Keep, OrganizeFile Target, bool Approved = false)
{
    public static IReadOnlyList<CleanupRequest> FromGroups(IEnumerable<GroupRow> groups) =>
        groups.SelectMany(g => g.CleanupFiles.Select(f => new CleanupRequest(g.GroupId, g.QueueKind, g.Keep, f, g.IsBulkApproved))).ToArray();
}

public sealed record CleanupCheckResult(CleanupRequest Request, bool IsReady, string? Reason);

public sealed record CleanupRunResult(CleanupRequest Request, bool Succeeded, string? Reason);

public sealed record CleanupProgress(int Done, int Total, string CurrentFile);

/// <summary>Checks and runs cleanup. Production goes through the existing safe file operation gates.</summary>
public interface ICleanupExecutor
{
    /// <summary>Read-only check that each target and its kept file are still as analysed. Never changes files.</summary>
    Task<IReadOnlyList<CleanupCheckResult>> CheckAsync(IReadOnlyList<CleanupRequest> requests, CancellationToken cancellationToken);

    /// <summary>Sends the targets to the recycle bin one at a time. Stops starting new items when cancelled.</summary>
    Task<IReadOnlyList<CleanupRunResult>> RunAsync(IReadOnlyList<CleanupRequest> requests, IProgress<CleanupProgress>? progress, CancellationToken cancellationToken);
}
