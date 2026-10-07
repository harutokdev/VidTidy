namespace LocalMediaAI.Core.Organize;

public enum KeepCriterion { FolderPriority, Date }

public enum KeepDateDirection { Oldest, Newest }

/// <summary>One file of a duplicate group, reduced to what the keep rule needs.</summary>
public sealed record KeepCandidate(Guid MediaFileId, Guid LibraryRootId, DateTimeOffset LastWriteTimeUtc, long SizeBytes);

/// <summary>
/// Decides which file of an exact-duplicate group is kept. Criteria are applied in order;
/// folder priority narrows the candidates to the best-ranked folder, the date criterion then picks one file.
/// </summary>
public sealed class KeepRule
{
    public static KeepRule Default { get; } = new([KeepCriterion.FolderPriority, KeepCriterion.Date], KeepDateDirection.Oldest);

    public KeepRule(IReadOnlyList<KeepCriterion> criteria, KeepDateDirection dateDirection)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        if (criteria.Count != 2 || criteria.Distinct().Count() != 2)
            throw new ArgumentException("Both criteria must appear exactly once.", nameof(criteria));
        Criteria = [.. criteria];
        DateDirection = dateDirection;
    }

    public IReadOnlyList<KeepCriterion> Criteria { get; }
    public KeepDateDirection DateDirection { get; }

    /// <param name="folderRanks">1 is the highest priority. Folders missing from the map rank last.</param>
    public Guid Choose(IReadOnlyList<KeepCandidate> candidates, IReadOnlyDictionary<Guid, int> folderRanks)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(folderRanks);
        if (candidates.Count == 0) throw new ArgumentException("A duplicate group needs at least one file.", nameof(candidates));

        IEnumerable<KeepCandidate> remaining = candidates;
        foreach (var criterion in Criteria)
        {
            if (criterion == KeepCriterion.FolderPriority)
            {
                var best = remaining.Min(c => RankOf(c, folderRanks));
                remaining = remaining.Where(c => RankOf(c, folderRanks) == best).ToArray();
            }
            else
            {
                return OrderByDate(remaining).First().MediaFileId;
            }
        }
        return OrderByDate(remaining).First().MediaFileId;
    }

    private IOrderedEnumerable<KeepCandidate> OrderByDate(IEnumerable<KeepCandidate> files) =>
        DateDirection == KeepDateDirection.Oldest
            ? files.OrderBy(c => c.LastWriteTimeUtc).ThenBy(c => c.MediaFileId)
            : files.OrderByDescending(c => c.LastWriteTimeUtc).ThenBy(c => c.MediaFileId);

    private static int RankOf(KeepCandidate candidate, IReadOnlyDictionary<Guid, int> ranks) =>
        ranks.TryGetValue(candidate.LibraryRootId, out var rank) ? rank : int.MaxValue;
}
