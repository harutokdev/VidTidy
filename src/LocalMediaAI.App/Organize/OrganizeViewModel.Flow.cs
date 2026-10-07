using System.Collections.ObjectModel;
using LocalMediaAI.Core.Organize;

namespace LocalMediaAI.App.Organize;

/// <summary>Three steps shown above the list: 1. 承認, 2. 実行, 3. よく似ているを確認.</summary>
public sealed record StepStates(StepState Approve, StepState Run, StepState Review);

/// <summary>One folder in the rule preview: files it gives up now and under the rule being edited.</summary>
public sealed record RulePreviewRow(FolderRow Folder, int Rank, int Now, int Next, long NextBytes);

/// <summary>
/// What the list shows and in which order: open groups by section (largest space first unless the user chose the found
/// order), the cleanup list as its own section, and only the cleanup list while it waits to be run.
/// </summary>
public sealed partial class OrganizeViewModel
{
    private CleanupGoal? _goal;
    private bool _peekLater;
    private bool _queueExpanded;

    /// <summary>Open groups as listed, per section.</summary>
    public ObservableCollection<GroupRow> ExactList { get; } = [];
    public ObservableCollection<GroupRow> SimilarList { get; } = [];
    public ObservableCollection<GroupRow> CheckList { get; } = [];

    /// <summary>Groups in the cleanup list (singles are in <see cref="QueuedSingles"/>).</summary>
    public ObservableCollection<GroupRow> QueueList { get; } = [];

    public ListSort Sort => _settings.ListInFoundOrder ? ListSort.Found : ListSort.Size;

    /// <summary>「D: を 50 GB 空けたい」 for this session. Groups that free space there come first and a line marks where the goal is reached.</summary>
    public CleanupGoal? Goal { get => _goal; set { if (Set(ref _goal, value)) RaiseSummary(); } }

    /// <summary>「先に見る」: while only the cleanup list is shown, also show the groups left for after the run.</summary>
    public bool PeekLater { get => _peekLater; set { if (Set(ref _peekLater, value)) RaiseSummary(); } }

    /// <summary>Whether the cleanup list section is open while other groups are listed too.</summary>
    public bool QueueExpanded { get => _queueExpanded; set { if (Set(ref _queueExpanded, value)) RaiseSummary(); } }

    /// <summary>
    /// The cleanup list waits to be run: approved identical files are in it, or nothing that can be chosen is left.
    /// The list then shows only what will be cleaned up.
    /// </summary>
    public bool IsRunOnly => QueueFileCount > 0 && !ExactGroups.Any(g => g.IsOpen && g.IsReady)
        && (QueueExactFileCount > 0 || !OpenGroups.Any(g => g.IsReady));

    public StepStates Steps
    {
        get
        {
            var approve = ExactGroups.Any(g => g.IsOpen && g.IsReady) ? StepState.Current : StepState.Done;
            var run = approve == StepState.Current ? StepState.Later : QueueExactFileCount > 0 ? StepState.Current : StepState.Done;
            var reviewLeft = SimilarGroups.Concat(NeedsCheckGroups).Any(g => g.IsOpen) || QueueOtherFileCount > 0;
            var review = approve == StepState.Done && run == StepState.Done ? reviewLeft ? StepState.Current : StepState.Done : StepState.Later;
            return new StepStates(approve, run, review);
        }
    }

    /// <summary>Open groups that wait for after the run while only the cleanup list is shown.</summary>
    public int LaterSimilarCount => SimilarOpenCount;
    public int LaterCheckCount => NeedsCheckOpenCount;
    public int LaterWaitingExactCount => ExactGroups.Count(g => g.IsOpen && !g.IsReady);

    /// <summary>Clear similar groups that can be approved together (see <see cref="ClearSimilarRule"/>).</summary>
    public IReadOnlyList<GroupRow> ClearGroups => Ordered(SimilarGroups.Where(g => g.IsOpen && g.IsReady && g.IsClear)).ToArray();

    /// <summary>Space the cleanup list frees on the goal's drive (every drive without a goal).</summary>
    public long GoalQueuedBytes => QueuedGroups.Sum(GoalBytesOf) + _singles.Where(Covers).Sum(f => f.SizeBytes);

    /// <summary>Space freed on the goal's drive if everything listed were cleaned up.</summary>
    public long GoalReachableBytes => GoalQueuedBytes + OpenGroups.Where(g => g.IsReady).Sum(GoalBytesOf);

    /// <summary>The groups in list order, for J・K and for choosing what is selected first.</summary>
    public IReadOnlyList<GroupRow> ListedGroups =>
        IsRunOnly
            ? (PeekLater ? QueueList.Concat(ExactList).Concat(SimilarList).Concat(CheckList) : QueueList).ToArray()
            : (QueueExpanded ? QueueList : Enumerable.Empty<GroupRow>()).Concat(ExactList).Concat(SimilarList).Concat(CheckList).ToArray();

    public long GoalBytesOf(GroupRow group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return group.CleanupFiles.Where(Covers).Sum(f => f.SizeBytes);
    }

    public Task SetSortAsync(ListSort sort, CancellationToken cancellationToken) =>
        sort == Sort ? Task.CompletedTask : SaveSettingsAsync(_settings with { ListInFoundOrder = sort == ListSort.Found }, cancellationToken);

    public Task SetFolderHintHiddenAsync(bool hidden, CancellationToken cancellationToken) =>
        hidden == _settings.HideFolderHint ? Task.CompletedTask : SaveSettingsAsync(_settings with { HideFolderHint = hidden }, cancellationToken);

    public ClearApprovalPlan PrepareClearApproval()
    {
        var clear = ClearGroups;
        var room = _queue.OtherLimit - _queue.OtherCount;
        var take = new List<GroupRow>();
        var used = 0;
        foreach (var group in clear)
        {
            var count = group.CleanupFiles.Count;
            if (used + count > room) continue;
            used += count;
            take.Add(group);
        }
        return new ClearApprovalPlan(take, used, take.Sum(g => g.CleanupBytes), clear.Count - take.Count);
    }

    /// <summary>Puts the clear similar groups into the cleanup list; their files need no check one by one when running.</summary>
    public int ApproveClear()
    {
        var added = 0;
        foreach (var group in PrepareClearApproval().Groups)
        {
            if (!AddToQueue(group)) continue;
            group.IsBulkApproved = true;
            added++;
        }
        RaiseSummary();
        return added;
    }

    /// <summary>「このまま残す」: nothing is cleaned up for this group; it leaves the list and can be undone from the judgment log.</summary>
    public async Task KeepAsIsAsync(GroupRow group, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (group.Status != GroupStatus.Open) return;
        var next = ReferenceEquals(Selected, group) ? NextListed(group) : Selected;
        await _source.RecordKeepAsIsAsync(group.GroupId, cancellationToken);
        group.Status = GroupStatus.Judged;
        Judgments.Insert(0, new JudgmentRecord(group, L.T("このまま残す")));
        Selected = next;
        RaiseSummary();
    }

    private GroupRow? NextListed(GroupRow group)
    {
        var listed = ListedGroups;
        var index = listed.ToList().IndexOf(group);
        return listed.Skip(index + 1).Concat(listed.Take(Math.Max(index, 0))).FirstOrDefault(g => g.IsOpen && !ReferenceEquals(g, group));
    }

    /// <summary>
    /// How many files each folder would give up among the identical groups waiting for approval if <paramref name="candidate"/>
    /// were the rule, next to the current rule. Groups the user chose by hand keep their choice. Folders in the candidate's order.
    /// </summary>
    public IReadOnlyList<RulePreviewRow> PreviewRule(OrganizeSettings candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var ranks = candidate.RanksFor(Folders.Select(f => f.Id));
        var rule = candidate.ToKeepRule();
        var now = new Dictionary<Guid, int>();
        var next = new Dictionary<Guid, (int Count, long Bytes)>();
        foreach (var group in ExactGroups.Where(g => g.IsOpen))
        {
            var keepNext = group.IsManual ? group.KeepId : rule.Choose(group.Files.Select(f => f.ToCandidate()).ToArray(), ranks);
            foreach (var file in group.Files)
            {
                if (file.MediaFileId != group.KeepId) now[file.RootId] = now.GetValueOrDefault(file.RootId) + 1;
                if (file.MediaFileId == keepNext) continue;
                var (count, bytes) = next.GetValueOrDefault(file.RootId);
                next[file.RootId] = (count + 1, bytes + file.SizeBytes);
            }
        }
        return Folders.OrderBy(f => ranks[f.Id])
            .Select(f => new RulePreviewRow(f, ranks[f.Id], now.GetValueOrDefault(f.Id), next.GetValueOrDefault(f.Id).Count, next.GetValueOrDefault(f.Id).Bytes))
            .ToArray();
    }

    private bool Covers(OrganizeFile file) => _goal?.Covers(file) ?? true;

    /// <summary>
    /// Largest first by the space a group frees with the file the rule would keep, so a group does not jump around
    /// while the user picks a different file to keep.
    /// </summary>
    private IEnumerable<GroupRow> Ordered(IEnumerable<GroupRow> groups) => Sort == ListSort.Found
        ? groups
        : groups.OrderByDescending(g => g.Files.Where(f => f.MediaFileId != AutoKeep(g.Data) && Covers(f)).Sum(f => f.SizeBytes));

    private void RefreshLists()
    {
        var exact = Ordered(ExactGroups.Where(g => g.IsOpen)).ToArray();
        var similar = Ordered(SimilarGroups.Where(g => g.IsOpen)).ToArray();
        var check = Ordered(NeedsCheckGroups.Where(g => g.IsOpen)).ToArray();
        Sync(ExactList, exact);
        Sync(SimilarList, similar);
        Sync(CheckList, check);
        Sync(QueueList, Ordered(AllGroups.Where(g => g.Status == GroupStatus.Queued)).ToArray());
        foreach (var group in AllGroups)
        {
            group.ListBytes = GoalBytesOf(group);
            group.IsGoalLine = false;
        }
        if (_goal is null) return;
        var reached = GoalQueuedBytes;
        if (reached >= _goal.Bytes) return;
        foreach (var group in exact.Concat(similar).Concat(check).Where(g => g.IsReady))
        {
            reached += GoalBytesOf(group);
            if (reached < _goal.Bytes) continue;
            group.IsGoalLine = true;
            return;
        }
    }

    /// <summary>Keeps the first listed group selected when the selection left the list, so the right side is never empty.</summary>
    private void EnsureSelection()
    {
        var listed = ListedGroups;
        if (Selected is null || !listed.Contains(Selected)) Selected = listed.Count > 0 ? listed[0] : null;
    }

    /// <summary>Moves, inserts and removes only what changed, so rows (and their thumbnails) are not rebuilt.</summary>
    private static void Sync(ObservableCollection<GroupRow> target, GroupRow[] items)
    {
        for (var i = 0; i < items.Length; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], items[i])) continue;
            var at = target.IndexOf(items[i]);
            if (at >= 0) target.Move(at, i);
            else target.Insert(i, items[i]);
        }
        while (target.Count > items.Length) target.RemoveAt(target.Count - 1);
    }
}
