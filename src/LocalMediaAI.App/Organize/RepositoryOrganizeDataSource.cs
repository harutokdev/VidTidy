using LocalMediaAI.Core;
using LocalMediaAI.Core.Organize;

namespace LocalMediaAI.App.Organize;

/// <summary>Reads duplicate groups for the organize screen from the library repository. Read-only apart from review judgments and settings.</summary>
/// <param name="thumbnailConfigHash">Which thumbnails count as current. Null skips thumbnails.</param>
public sealed class RepositoryOrganizeDataSource(ILibraryRepository library, string? thumbnailConfigHash = null) : IOrganizeDataSource
{
    private const int PageSize = 200;
    public const double SimilarThreshold = 0.85;

    private static readonly HashSet<DuplicateJudgmentDecision> SettledDecisions =
        [DuplicateJudgmentDecision.NotDuplicate, DuplicateJudgmentDecision.KeepBoth, DuplicateJudgmentDecision.Ignored];

    private readonly ILibraryRepository _library = library ?? throw new ArgumentNullException(nameof(library));

    public async Task<OrganizeSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        var roots = (await _library.GetLibraryRootsAsync(cancellationToken))
            .Where(r => r.RegistrationState == RootRegistrationState.Active)
            .OrderBy(r => r.CreatedAt)
            .ToArray();
        var folders = new List<OrganizeFolder>(roots.Length);
        foreach (var r in roots)
        {
            var count = (await _library.QueryMediaAsync(r.LibraryRootId, null, 0, 1, cancellationToken)).TotalCount;
            folders.Add(new OrganizeFolder(r.LibraryRootId, r.DisplayName, r.ConfiguredPath, r.Availability != RootAvailability.Offline, count));
        }
        var rootIds = folders.Select(f => f.Id).ToHashSet();
        var rootsById = roots.ToDictionary(r => r.LibraryRootId);
        var thumbnails = await LoadThumbnailsAsync(roots, cancellationToken);

        var groups = new List<OrganizeGroupData>();
        var exactGroupOfFile = new Dictionary<Guid, Guid>();
        await foreach (var group in QueryAsync(DuplicateRelationType.Exact, cancellationToken))
        {
            var members = await _library.GetDuplicateComparisonMembersAsync(group.DuplicateGroupId, cancellationToken);
            var files = members.Select(m => (m.Media, m.Metadata)).Where(m => IsCurrent(m.Media, rootIds)).Select(m => Map(m, rootsById, thumbnails)).ToArray();
            if (files.Length < 2) continue;
            groups.Add(new OrganizeGroupData(group.DuplicateGroupId, GroupCategory.Exact, null, files));
            foreach (var file in files) exactGroupOfFile[file.MediaFileId] = group.DuplicateGroupId;
        }
        await foreach (var group in QueryAsync(DuplicateRelationType.NearDuplicate, cancellationToken))
        {
            var members = await _library.GetNearDuplicateComparisonMembersAsync(group.DuplicateGroupId, cancellationToken);
            var current = members.Where(m => IsCurrent(m.Media, rootIds)).ToArray();
            var files = WithoutExactCopies(current.GroupBy(m => m.MediaFileId).Select(g => Map((g.First().Media, g.First().Metadata), rootsById, thumbnails)), exactGroupOfFile);
            if (files.Count < 2) continue;
            var ratio = current.Length == 0 ? 0 : current.Max(m => m.Evidence.VisualMatchScore);
            groups.Add(new OrganizeGroupData(group.DuplicateGroupId, ratio >= SimilarThreshold ? GroupCategory.Similar : GroupCategory.NeedsCheck, ratio, files,
                await LengthDifferenceAsync(current, cancellationToken)));
        }
        return new OrganizeSnapshot(folders, groups);
    }

    public Task<OrganizeSettings> LoadSettingsAsync(CancellationToken cancellationToken) => _library.GetOrganizeSettingsAsync(cancellationToken);

    public Task SaveSettingsAsync(OrganizeSettings settings, CancellationToken cancellationToken) => _library.SaveOrganizeSettingsAsync(settings, cancellationToken);

    public Task RecordNotDuplicateAsync(Guid groupId, CancellationToken cancellationToken) =>
        _library.UpsertDuplicateJudgmentAsync(groupId, null, DuplicateJudgmentDecision.NotDuplicate, null, cancellationToken);

    public Task RecordKeepAsIsAsync(Guid groupId, CancellationToken cancellationToken) =>
        _library.UpsertDuplicateJudgmentAsync(groupId, null, DuplicateJudgmentDecision.KeepBoth, null, cancellationToken);

    public Task<RetentionSettings> LoadRetentionAsync(CancellationToken cancellationToken) => _library.GetRetentionSettingsAsync(cancellationToken);

    public Task SaveRetentionAsync(RetentionSettings settings, CancellationToken cancellationToken) => _library.SaveRetentionSettingsAsync(settings.Validate(), cancellationToken);

    public async Task<IReadOnlyList<CleanupRun>> LoadHistoryAsync(CancellationToken cancellationToken) =>
        CleanupHistory.GroupIntoRuns(await _library.GetCleanupBatchesAsync(500, cancellationToken));

    public Task<IReadOnlyList<string>> LoadHistoryFilesAsync(CleanupRun run, CancellationToken cancellationToken) =>
        _library.GetCleanupBatchFilesAsync(run.BatchIds, cancellationToken);

    public Task<IReadOnlyList<UnanalyzedFile>> LoadUnanalyzedAsync(CancellationToken cancellationToken) => _library.GetUnanalyzedFilesAsync(200, cancellationToken);

    public async Task<IReadOnlyList<OrganizeFile>> LoadFilesAsync(Guid? folderId, int limit, CancellationToken cancellationToken)
    {
        var roots = (await _library.GetLibraryRootsAsync(cancellationToken)).Where(r => r.RegistrationState == RootRegistrationState.Active && (folderId is null || r.LibraryRootId == folderId)).ToArray();
        var files = new List<OrganizeFile>();
        foreach (var root in roots)
        {
            var page = await _library.QueryMediaAsync(root.LibraryRootId, null, 0, limit, cancellationToken);
            files.AddRange(page.Items.Select(m => new OrganizeFile(m.MediaFileId, root.LibraryRootId, m.FileName, m.Path, m.SizeBytes, m.LastWriteTimeUtc,
                m.Metadata?.Width, m.Metadata?.Height, m.Metadata?.Bitrate, m.Metadata?.DurationSeconds, m.Metadata?.VideoCodec, m.ThumbnailRelativePath, LocationOf(root, m.Path))));
        }
        return files.Take(limit).ToArray();
    }

    public async Task<IReadOnlyList<MatchedScene>> LoadMatchedScenesAsync(OrganizeGroupData group, CancellationToken cancellationToken)
    {
        if (group.Category == GroupCategory.Exact || group.Files.Count < 2) return [];
        var pair = new HashSet<Guid> { group.Files[0].MediaFileId, group.Files[1].MediaFileId };
        var members = await _library.GetNearDuplicateComparisonMembersAsync(group.GroupId, cancellationToken);
        var evidence = members.Select(m => m.Evidence).FirstOrDefault(e => pair.Contains(e.MediaFileAId) && pair.Contains(e.MediaFileBId)) ?? (members.Count > 0 ? members[0].Evidence : null);
        if (evidence is null) return [];
        var matches = await _library.GetNearDuplicateFrameMatchesAsync(evidence.NearDuplicateEvidenceId, cancellationToken);
        var framesA = await _library.GetCurrentNearDuplicateFramesAsync(evidence.MediaFileAId, evidence.MediaContentAId, evidence.SamplingVersion, PerceptualFrameFingerprint.Algorithm, cancellationToken);
        var framesB = await _library.GetCurrentNearDuplicateFramesAsync(evidence.MediaFileBId, evidence.MediaContentBId, evidence.SamplingVersion, PerceptualFrameFingerprint.Algorithm, cancellationToken);
        return MatchedScenes.Pick(evidence, matches, framesA, framesB);
    }

    public Task RemoveFolderAsync(Guid folderId, CancellationToken cancellationToken) => _library.UnregisterRootAsync(folderId, DateTimeOffset.UtcNow, cancellationToken);

    public Task ResetJudgmentAsync(Guid groupId, CancellationToken cancellationToken) =>
        _library.UpsertDuplicateJudgmentAsync(groupId, null, DuplicateJudgmentDecision.Unreviewed, null, cancellationToken);

    private async IAsyncEnumerable<DuplicateGroup> QueryAsync(DuplicateRelationType relation, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var offset = 0; ; offset += PageSize)
        {
            var page = await _library.QueryDuplicateGroupsByRelationAsync(null, relation, false, offset, PageSize, cancellationToken);
            foreach (var group in page.Items.Where(g => !SettledDecisions.Contains(g.ReviewDecision))) yield return group;
            if (page.Items.Count < PageSize) yield break;
        }
    }

    /// <summary>For a pair matched along the timeline: the longer file and where it has more.</summary>
    private async Task<LengthDifference?> LengthDifferenceAsync(IReadOnlyList<NearDuplicateComparisonMember> members, CancellationToken cancellationToken)
    {
        var evidence = members.Select(m => m.Evidence).FirstOrDefault(e => e.ThresholdVersion == TimelineAlignment.ThresholdVersion);
        if (evidence is null || await _library.GetTimelineEvidenceDetailAsync(evidence.NearDuplicateEvidenceId, cancellationToken) is not { } detail) return null;
        var longer = detail.ShorterMediaFileId == evidence.MediaFileAId ? evidence.MediaFileBId : evidence.MediaFileAId;
        return new LengthDifference(longer, detail.ExtraInLonger);
    }

    private static bool IsCurrent(MediaFile media, HashSet<Guid> rootIds) =>
        media.PresenceState is PresenceState.Present or PresenceState.OfflineRoot && rootIds.Contains(media.LibraryRootId);

    /// <summary>
    /// Exact copies are handled in the exact list, so a similar group keeps only one file per exact group.
    /// A similar group made only of exact copies becomes empty and is not shown twice.
    /// </summary>
    public static IReadOnlyList<OrganizeFile> WithoutExactCopies(IEnumerable<OrganizeFile> files, IReadOnlyDictionary<Guid, Guid> exactGroupOfFile) =>
        files.GroupBy(f => exactGroupOfFile.TryGetValue(f.MediaFileId, out var exact) ? exact : f.MediaFileId).Select(g => g.First()).ToArray();

    /// <summary>One query per folder, so thumbnails never cost a query per file.</summary>
    private async Task<Dictionary<Guid, string>> LoadThumbnailsAsync(IReadOnlyList<LibraryRoot> roots, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, string>();
        if (thumbnailConfigHash is null) return result;
        foreach (var root in roots)
            foreach (var (mediaFileId, relativePath) in await _library.GetCurrentThumbnailCandidatesAsync(root.LibraryRootId, thumbnailConfigHash, cancellationToken))
                if (relativePath is not null) result[mediaFileId] = relativePath;
        return result;
    }

    private static OrganizeFile Map((MediaFile Media, MediaMetadata? Metadata) item, Dictionary<Guid, LibraryRoot> roots, Dictionary<Guid, string> thumbnails) => new(
        item.Media.MediaFileId, item.Media.LibraryRootId, item.Media.FileName, item.Media.Path, item.Media.SizeBytes, item.Media.LastWriteTimeUtc,
        item.Metadata?.Width, item.Metadata?.Height, item.Metadata?.Bitrate, item.Metadata?.DurationSeconds, item.Metadata?.VideoCodec,
        ThumbnailRelativePath: thumbnails.GetValueOrDefault(item.Media.MediaFileId),
        LocationLabel: roots.TryGetValue(item.Media.LibraryRootId, out var root) ? LocationOf(root, item.Media.Path) : null);

    /// <summary>"整理済み ＞ 旅行" for D:\動画\整理済み\旅行.mp4 under the root D:\動画\整理済み.</summary>
    public static string LocationOf(LibraryRoot root, string filePath)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(filePath)) ?? string.Empty;
        var rootPath = System.IO.Path.GetFullPath(root.ConfiguredPath).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        var relative = directory.Length > rootPath.Length && directory.StartsWith(rootPath + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? directory[(rootPath.Length + 1)..]
            : string.Empty;
        var parts = new[] { root.DisplayName }.Concat(relative.Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries));
        return string.Join(L.T(" ＞ "), parts);
    }
}
