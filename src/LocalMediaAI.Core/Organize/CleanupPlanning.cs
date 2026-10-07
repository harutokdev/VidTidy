namespace LocalMediaAI.Core.Organize;

public enum CleanupKind { Exact, Similar, Single }

/// <summary>The kept file of a group and the files that would be cleaned up.</summary>
public sealed record GroupCleanupPlan(KeepCandidate Keep, IReadOnlyList<KeepCandidate> CleanUp);

public sealed record FolderCleanupCount(Guid LibraryRootId, int FileCount, long Bytes, int SameFolderCount);

/// <summary>
/// Approval summary counted per file, so a group spanning several folders is never counted twice.
/// </summary>
public sealed class CleanupSummary
{
    private readonly IReadOnlyDictionary<Guid, FolderCleanupCount> _perFolder;

    private CleanupSummary(int groupCount, int fileCount, long totalBytes, IReadOnlyDictionary<Guid, FolderCleanupCount> perFolder)
    {
        GroupCount = groupCount;
        FileCount = fileCount;
        TotalBytes = totalBytes;
        _perFolder = perFolder;
    }

    public int GroupCount { get; }
    public int FileCount { get; }
    public long TotalBytes { get; }

    public FolderCleanupCount For(Guid libraryRootId) =>
        _perFolder.TryGetValue(libraryRootId, out var count) ? count : new FolderCleanupCount(libraryRootId, 0, 0, 0);

    public static CleanupSummary Create(IEnumerable<GroupCleanupPlan> plans)
    {
        ArgumentNullException.ThrowIfNull(plans);
        var groups = 0;
        var perFolder = new Dictionary<Guid, FolderCleanupCount>();
        foreach (var plan in plans)
        {
            if (plan.CleanUp.Count == 0) continue;
            groups++;
            foreach (var file in plan.CleanUp)
            {
                var current = perFolder.TryGetValue(file.LibraryRootId, out var existing)
                    ? existing
                    : new FolderCleanupCount(file.LibraryRootId, 0, 0, 0);
                perFolder[file.LibraryRootId] = current with
                {
                    FileCount = current.FileCount + 1,
                    Bytes = current.Bytes + file.SizeBytes,
                    SameFolderCount = current.SameFolderCount + (file.LibraryRootId == plan.Keep.LibraryRootId ? 1 : 0)
                };
            }
        }
        return new CleanupSummary(groups, perFolder.Values.Sum(x => x.FileCount), perFolder.Values.Sum(x => x.Bytes), perFolder);
    }
}

/// <summary>
/// The "整理リスト". Exact duplicates are approved by rule and may hold more files per run;
/// similar and individually chosen files are checked one by one, so they keep the smaller limit.
/// </summary>
public sealed class CleanupList
{
    public const int DefaultExactLimit = 100;
    public const int DefaultOtherLimit = 25;

    private readonly int _exactLimit;
    private readonly int _otherLimit;
    private readonly Dictionary<Guid, (CleanupKind Kind, IReadOnlyList<Guid> Files)> _groups = [];

    public CleanupList(int exactLimit = DefaultExactLimit, int otherLimit = DefaultOtherLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(exactLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(otherLimit);
        _exactLimit = exactLimit;
        _otherLimit = otherLimit;
    }

    public int ExactLimit => _exactLimit;
    public int OtherLimit => _otherLimit;
    public int TotalCount => _groups.Values.Sum(g => g.Files.Count);
    public int OtherCount => _groups.Values.Where(g => g.Kind != CleanupKind.Exact).Sum(g => g.Files.Count);
    public IEnumerable<Guid> GroupIds => _groups.Keys;

    public int Count(CleanupKind kind) => _groups.Values.Where(g => g.Kind == kind).Sum(g => g.Files.Count);

    public bool Contains(Guid mediaFileId) => _groups.Values.Any(g => g.Files.Contains(mediaFileId));

    public bool ContainsGroup(Guid groupId) => _groups.ContainsKey(groupId);

    public bool CanAdd(CleanupKind kind, int fileCount) => kind == CleanupKind.Exact
        ? Count(CleanupKind.Exact) + fileCount <= _exactLimit
        : OtherCount + fileCount <= _otherLimit;

    public bool TryAddGroup(Guid groupId, CleanupKind kind, IReadOnlyList<Guid> mediaFileIds)
    {
        ArgumentNullException.ThrowIfNull(mediaFileIds);
        if (mediaFileIds.Count == 0 || _groups.ContainsKey(groupId) || mediaFileIds.Any(Contains)) return false;
        if (!CanAdd(kind, mediaFileIds.Count)) return false;
        _groups[groupId] = (kind, [.. mediaFileIds]);
        return true;
    }

    public bool RemoveGroup(Guid groupId) => _groups.Remove(groupId);

    public void Clear() => _groups.Clear();
}
