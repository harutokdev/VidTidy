namespace LocalMediaAI.App.Organize;

/// <summary>Free space of one drive that holds registered folders.</summary>
/// <param name="RecycleBinBytes">What Windows reports for this drive's recycle bin; null when it cannot be read.</param>
/// <param name="QueueBytes">Files in the cleanup list on this drive (they free space once the recycle bin is emptied).</param>
public sealed record DriveSpace(string Drive, long FreeBytes, long TotalBytes, long? RecycleBinBytes, long QueueBytes = 0)
{
    private long Bin => Math.Max(0, RecycleBinBytes ?? 0);
    public string Label => Drive.TrimEnd('\\');
    public string FreeText => L.F("空き {0}", MediaDisplayFormat.FileSize(FreeBytes));
    public string? RecycleText => Bin > 0 ? L.F("ごみ箱に {0}。空にすると 約{1} 空きます", MediaDisplayFormat.FileSize(Bin), MediaDisplayFormat.FileSize(FreeBytes + Bin)) : null;

    /// <summary>"ごみ箱に 6.2 GB・整理リスト 1.1 GB"; null when both are empty.</summary>
    public string? PendingText => Bin == 0 && QueueBytes == 0 ? null : string.Join(L.T("・"), new[]
    {
        Bin > 0 ? L.F("ごみ箱に {0}", MediaDisplayFormat.FileSize(Bin)) : null,
        QueueBytes > 0 ? L.F("整理リスト {0}", MediaDisplayFormat.FileSize(QueueBytes)) : null
    }.Where(x => x is not null));

    public string AfterText => L.F("空にすると 約{0}", MediaDisplayFormat.FileSize(FreeBytes + Bin + QueueBytes));

    // Bar segments as star widths: used by other files, recycle bin, cleanup list, free.
    public System.Windows.GridLength UsedStar => Star(TotalBytes - FreeBytes - Bin - QueueBytes);
    public System.Windows.GridLength BinStar => Star(Bin);
    public System.Windows.GridLength QueueStar => Star(QueueBytes);
    public System.Windows.GridLength FreeStar => Star(FreeBytes);

    private static System.Windows.GridLength Star(long bytes) => new(Math.Max(0, bytes), System.Windows.GridUnitType.Star);
}

/// <summary>Background analysis as the organize screen needs it: start a change check, see how much is left, read drive space.</summary>
public interface IOrganizeBackground
{
    /// <summary>Queues a rescan of the given folders. Unchanged files keep their earlier analysis.</summary>
    Task RequestChangeCheckAsync(IReadOnlyList<Guid> folderIds, CancellationToken cancellationToken);

    /// <summary>Number of analysis jobs not finished yet.</summary>
    Task<int> GetRemainingWorkAsync(CancellationToken cancellationToken);

    IReadOnlyList<DriveSpace> GetDriveSpace(IEnumerable<string> folderPaths);

    /// <summary>Unfinished jobs per folder and the latest scan results. Defaults to anonymous jobs from the remaining count.</summary>
    async Task<LocalMediaAI.Core.Organize.AnalysisProgress> GetProgressAsync(CancellationToken cancellationToken) => FromCount(await GetRemainingWorkAsync(cancellationToken));

    /// <summary>How hard analysis may work the drives holding the originals.</summary>
    Task<LocalMediaAI.Core.Processing.ProcessingSettings> LoadProcessingAsync(CancellationToken cancellationToken) =>
        Task.FromResult(LocalMediaAI.Core.Processing.ProcessingSettings.Defaults);

    /// <summary>Saves the pace and applies it to running analysis at once.</summary>
    Task SaveProcessingAsync(LocalMediaAI.Core.Processing.ProcessingSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>When a drive is resting between work periods, the time the rest ends; otherwise null.</summary>
    DateTimeOffset? RestingUntil => null;

    /// <summary>True while reading originals waits for the user to stop using the PC ("only while idle").</summary>
    bool WaitingForUser => false;

    static LocalMediaAI.Core.Organize.AnalysisProgress FromCount(int remaining) =>
        new(Enumerable.Range(0, remaining).Select(_ => new LocalMediaAI.Core.Organize.ActiveAnalysisJob(null, LocalMediaAI.Core.JobType.FullHash, LocalMediaAI.Core.JobState.Pending, 0, 0, null, null)).ToArray(), []);
}
