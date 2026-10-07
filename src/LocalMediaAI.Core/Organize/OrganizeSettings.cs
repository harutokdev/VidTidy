namespace LocalMediaAI.Core.Organize;

/// <summary>User choices for the organize workflow. Stored as one app setting.</summary>
/// <param name="FolderOrder">Library root ids from highest to lowest priority. Roots not listed rank after these.</param>
/// <param name="ShowTechnicalDetails">Shows ids, hashes and match evidence for troubleshooting.</param>
/// <param name="VaultPath">Folder the user created for keeping files instead of the recycle bin; null until they create one.</param>
/// <param name="PriorityConfirmed">True once the user has confirmed the folder priority after the first analysis.</param>
/// <param name="HideFolderHint">True once the user closed the explanation under the folder list.</param>
/// <param name="ListInFoundOrder">True to list duplicates in the order found instead of largest space first.</param>
public sealed record OrganizeSettings(
    IReadOnlyList<Guid> FolderOrder,
    IReadOnlyList<KeepCriterion> Criteria,
    KeepDateDirection DateDirection,
    bool CheckChangesOnStartup,
    bool ShowTechnicalDetails = false,
    string? VaultPath = null,
    bool PriorityConfirmed = false,
    bool HideFolderHint = false,
    bool ListInFoundOrder = false)
{
    public static OrganizeSettings Defaults { get; } = new([], [KeepCriterion.FolderPriority, KeepCriterion.Date], KeepDateDirection.Oldest, true);

    public KeepRule ToKeepRule() => new(Criteria, DateDirection);

    /// <summary>Ranks (1 = highest) for the given roots; roots missing from <see cref="FolderOrder"/> keep their incoming order.</summary>
    public IReadOnlyDictionary<Guid, int> RanksFor(IEnumerable<Guid> registeredRootIds)
    {
        ArgumentNullException.ThrowIfNull(registeredRootIds);
        var registered = registeredRootIds.Distinct().ToArray();
        var ordered = FolderOrder.Where(registered.Contains).Concat(registered.Where(id => !FolderOrder.Contains(id)));
        return ordered.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index + 1);
    }
}
