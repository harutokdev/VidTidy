using LocalMediaAI.Core.Organize;

namespace LocalMediaAI.App.Organize;

public enum GroupCategory { Exact, Similar, NeedsCheck }

public enum GroupStatus { Open, Queued, Judged, Done }

public enum NextActionKind { AllClear, ApproveExact, ReviewSimilar, RunQueue, ReviewNew, ApproveClear }

/// <summary>One step of 1. 承認 → 2. 実行 → 3. よく似ているを確認: not reached yet, the current one, or done.</summary>
public enum StepState { Later, Current, Done }

/// <summary>Order of the duplicate list: the space a group frees, largest first, or the order they were found.</summary>
public enum ListSort { Size, Found }

/// <summary>「D: を 50 GB 空けたい」. A null drive means every drive.</summary>
public sealed record CleanupGoal(string? Drive, long Bytes)
{
    public const long BytesPerGb = 1L << 30; // the same unit as the sizes shown everywhere else

    public string Label => L.F("{0}{1:0.##} GB 空ける", (Drive is null ? string.Empty : Drive + L.T(" を ")), Bytes / (double)BytesPerGb);

    public bool Covers(OrganizeFile file) =>
        Drive is null || string.Equals((System.IO.Path.GetPathRoot(file?.Path) ?? string.Empty).TrimEnd('\\'), Drive, StringComparison.OrdinalIgnoreCase);
}

/// <param name="Groups">Clear similar groups that fit this run.</param>
/// <param name="LeftForNextRun">Clear groups that did not fit under the limit.</param>
public sealed record ClearApprovalPlan(IReadOnlyList<GroupRow> Groups, int FileCount, long Bytes, int LeftForNextRun);

/// <summary>One folder on the analysis screen. Stage is 0..3 (see AnalysisStages), 4 when done.</summary>
/// <param name="ScanSeen">Files seen so far by this folder's change-check scan.</param>
/// <param name="ScanPercent">How far that scan is, against the files the folder had last time.</param>
public sealed record FolderProgress(FolderRow Folder, int Stage, double Percent, int Remaining, string State, LocalMediaAI.Core.Organize.LatestScan? Scan, int ScanSeen = 0, double ScanPercent = 0);

public enum SingleQueueResult { Added, AlreadyQueued, KeptInGroup, LimitReached, WaitingForDrive }

/// <summary>A registered folder as the organize screen needs it.</summary>
/// <param name="FileCount">Videos currently known in the folder, for the side panel.</param>
public sealed record OrganizeFolder(Guid Id, string Name, string Path, bool IsOnline, int FileCount = 0);

/// <summary>One file of a duplicate group, flattened from the library records.</summary>
public sealed record OrganizeFile(
    Guid MediaFileId,
    Guid RootId,
    string FileName,
    string Path,
    long SizeBytes,
    DateTimeOffset LastWriteTimeUtc,
    int? Width,
    int? Height,
    long? Bitrate,
    double? DurationSeconds,
    string? Codec,
    string? ThumbnailRelativePath = null,
    string? LocationLabel = null)
{
    public long PixelCount => (long)(Width ?? 0) * (Height ?? 0);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
    /// <summary>Where the file is, relative to its registered folder (e.g. "整理済み ＞ 旅行"). Falls back to the full folder path.</summary>
    public string Location => LocationLabel ?? Folder;
    public string ResolutionText => Width is { } w && Height is { } h ? $"{w}×{h}" : L.T("解像度不明");
    public string BitrateText => Bitrate is { } b ? $"{b / 1_000_000d:0.#} Mbps" : L.T("不明");
    public string DurationText => DurationSeconds is { } d ? MediaDisplayFormat.Duration(d) : L.T("不明");
    public string SizeText => MediaDisplayFormat.FileSize(SizeBytes);
    public string FormatText => System.IO.Path.GetExtension(FileName).TrimStart('.').ToUpperInvariant();
    public string DateText => LastWriteTimeUtc.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    public string FactsText => string.Join(L.T("・"), new[] { Width is null ? null : ResolutionText, Bitrate is null ? null : BitrateText, DurationSeconds is null ? null : DurationText, SizeText }.Where(x => x is not null));
    public KeepCandidate ToCandidate() => new(MediaFileId, RootId, LastWriteTimeUtc, SizeBytes);
}

/// <param name="MatchRatio">0..1 visual match for similar groups; null for exact duplicates.</param>
/// <param name="Length">For a copy of different length: which file is longer and the parts only it has.</param>
public sealed record OrganizeGroupData(Guid GroupId, GroupCategory Category, double? MatchRatio, IReadOnlyList<OrganizeFile> Files, LengthDifference? Length = null);

/// <summary>One file contains the other: <paramref name="LongerFileId"/> has <paramref name="ExtraInLonger"/> besides (seconds from its start).</summary>
public sealed record LengthDifference(Guid LongerFileId, IReadOnlyList<LocalMediaAI.Core.TimelineSpan> ExtraInLonger);

public sealed record OrganizeSnapshot(IReadOnlyList<OrganizeFolder> Folders, IReadOnlyList<OrganizeGroupData> Groups);

public sealed record NextActionInfo(NextActionKind Kind, string Title, string Description, string ButtonLabel);

public sealed record ExactApprovalPlan(IReadOnlyList<GroupRow> Groups, CleanupSummary Summary, int LeftForNextRun, int WaitingForDrive, int ManualCount);

public sealed record ApproveResult(int Added, int LeftForNextRun, int WaitingForDrive);

public sealed record JudgmentRecord(GroupRow Group, string Label);

/// <summary>Everything the organize screen reads or records. Implemented over the library repository in production.</summary>
public interface IOrganizeDataSource
{
    Task<OrganizeSnapshot> LoadAsync(CancellationToken cancellationToken);
    Task<OrganizeSettings> LoadSettingsAsync(CancellationToken cancellationToken);
    Task SaveSettingsAsync(OrganizeSettings settings, CancellationToken cancellationToken);
    Task RecordNotDuplicateAsync(Guid groupId, CancellationToken cancellationToken);
    Task ResetJudgmentAsync(Guid groupId, CancellationToken cancellationToken);

    /// <summary>「このまま残す」: the group stays as it is and leaves the list. Undone with <see cref="ResetJudgmentAsync"/>.</summary>
    Task RecordKeepAsIsAsync(Guid groupId, CancellationToken cancellationToken) => RecordNotDuplicateAsync(groupId, cancellationToken);

    /// <summary>How long information about missing files, folders and drives is kept (null = no limit).</summary>
    Task<LocalMediaAI.Core.RetentionSettings> LoadRetentionAsync(CancellationToken cancellationToken) => Task.FromResult(new LocalMediaAI.Core.RetentionSettings(30, 90, null));

    Task SaveRetentionAsync(LocalMediaAI.Core.RetentionSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Past runs of 確認して実行, newest first.</summary>
    Task<IReadOnlyList<CleanupRun>> LoadHistoryAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CleanupRun>>([]);

    Task<IReadOnlyList<string>> LoadHistoryFilesAsync(CleanupRun run, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>Files the analysis could not finish (broken, unreadable, access denied).</summary>
    Task<IReadOnlyList<UnanalyzedFile>> LoadUnanalyzedAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<UnanalyzedFile>>([]);

    /// <summary>Files of one folder (or all folders) for the すべてのファイル drawer.</summary>
    Task<IReadOnlyList<OrganizeFile>> LoadFilesAsync(Guid? folderId, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<OrganizeFile>>([]);

    /// <summary>Scenes found in both of the first two files of a similar group.</summary>
    Task<IReadOnlyList<MatchedScene>> LoadMatchedScenesAsync(OrganizeGroupData group, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MatchedScene>>([]);

    /// <summary>Stops organizing a folder. Files on disk are never touched.</summary>
    Task RemoveFolderAsync(Guid folderId, CancellationToken cancellationToken) => throw new NotSupportedException();
}
