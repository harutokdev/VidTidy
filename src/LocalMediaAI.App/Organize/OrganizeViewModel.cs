using LocalMediaAI.Core;
using System.Collections.ObjectModel;
using LocalMediaAI.App.ViewModels;
using LocalMediaAI.Core.Organize;

namespace LocalMediaAI.App.Organize;

/// <summary>
/// State of the organize screen: duplicate groups, the file kept in each, and the cleanup list.
/// Nothing here touches files; running the cleanup list is a separate confirmed step.
/// </summary>
public sealed partial class OrganizeViewModel : ViewModelBase
{
    private readonly IOrganizeDataSource _source;
    private readonly CleanupList _queue;
    private readonly List<OrganizeFile> _singles = [];
    private OrganizeSettings _settings = OrganizeSettings.Defaults;
    private GroupRow? _selected;

    public OrganizeViewModel(IOrganizeDataSource source, CleanupList? queue = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _queue = queue ?? new CleanupList();
    }

    /// <summary>Runs 確認して実行. Null until the host provides one; the run button stays disabled.</summary>
    public ICleanupExecutor? Executor { get; init; }

    /// <summary>Folder that thumbnail paths are relative to. Null uses the app's default cache.</summary>
    public string? ThumbnailCacheRoot { get; init; }

    /// <summary>Keeps the display language for the next start. Null hides the language choice in Settings.</summary>
    public Action<UiLanguage>? SaveLanguage { get; init; }

    /// <summary>The saved display language (null when none was chosen yet), so Settings shows the choice for the next start.</summary>
    public Func<UiLanguage?>? ReadLanguage { get; init; }

    /// <summary>Records a failed language save (the user sees a short message).</summary>
    public Action<Exception>? ReportLanguageSaveFailure { get; init; }

    public ObservableCollection<FolderRow> Folders { get; } = [];
    public ObservableCollection<GroupRow> ExactGroups { get; } = [];
    public ObservableCollection<GroupRow> SimilarGroups { get; } = [];
    public ObservableCollection<GroupRow> NeedsCheckGroups { get; } = [];
    public ObservableCollection<JudgmentRecord> Judgments { get; } = [];

    public OrganizeSettings Settings => _settings;
    public KeepRule Rule => _settings.ToKeepRule();
    public int ExactLimit => _queue.ExactLimit;
    public int OtherLimit => _queue.OtherLimit;

    public GroupRow? Selected { get => _selected; set => Set(ref _selected, value); }

    private IEnumerable<GroupRow> AllGroups => ExactGroups.Concat(SimilarGroups).Concat(NeedsCheckGroups);
    private IEnumerable<GroupRow> OpenGroups => AllGroups.Where(g => g.Status == GroupStatus.Open);

    public int OpenGroupCount => OpenGroups.Count();
    public int ExactOpenCount => ExactGroups.Count(g => g.IsOpen);
    public int SimilarOpenCount => SimilarGroups.Count(g => g.IsOpen);
    public int NeedsCheckOpenCount => NeedsCheckGroups.Count(g => g.IsOpen);
    public long CleanableBytes => OpenGroups.Sum(g => g.CleanupBytes);
    public int WaitingForDriveCount => OpenGroups.Count(g => !g.IsReady);
    public IReadOnlyList<GroupRow> QueuedGroups => AllGroups.Where(g => g.Status == GroupStatus.Queued).ToArray();
    public int QueueFileCount => _queue.TotalCount;
    public int QueueExactFileCount => _queue.Count(CleanupKind.Exact);
    public int QueueOtherFileCount => _queue.OtherCount;
    public long QueueBytes => QueuedGroups.Sum(g => g.CleanupBytes) + _singles.Sum(f => f.SizeBytes);

    /// <summary>Files added one by one from すべてのファイル; they have no kept copy and are checked row by row.</summary>
    public IReadOnlyList<OrganizeFile> QueuedSingles => _singles;

    /// <summary>How full this run is, 0..100: the fuller of the exact and the other limit.</summary>
    public double QueueMeter => Math.Min(100, Math.Max((double)QueueExactFileCount / ExactLimit, (double)QueueOtherFileCount / OtherLimit) * 100);

    public string StatsText
    {
        get
        {
            var text = L.F("重複 {0}組・片付けられる {1}", OpenGroupCount, MediaDisplayFormat.FileSize(CleanableBytes));
            return WaitingForDriveCount > 0 ? L.F("{0}・接続待ち {1}組", text, WaitingForDriveCount) : text;
        }
    }

    public string RuleText
    {
        get
        {
            var folders = string.Join(" → ", Folders.Select(f => $"{f.Rank}. {f.Name}"));
            var date = _settings.DateDirection == KeepDateDirection.Oldest ? L.T("更新日時が古い方") : L.T("更新日時が新しい方");
            var steps = _settings.Criteria.Select(c => c == KeepCriterion.FolderPriority ? L.F("フォルダーの優先順位が高い方（{0}）", folders) : date);
            return L.T("残す方のルール：") + string.Join(" → ", steps.Select((step, i) => $"{(i == 0 ? "①" : "②")} {step}"));
        }
    }

    public string KeepReason(GroupRow group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (group.IsManual) return L.T("あなたが選んだファイルです（ルールより優先されます）");
        if (group.Category != GroupCategory.Exact) return L.T("解像度・ビットレート・長さが最も良い方です");
        var folder = Folders.FirstOrDefault(f => f.Id == group.Keep.RootId);
        var ranksDiffer = group.Files.Select(f => f.RootId).Distinct().Count() > 1;
        var date = _settings.DateDirection == KeepDateDirection.Oldest ? L.T("更新日時が最も古い") : L.T("更新日時が最も新しい");
        return _settings.Criteria[0] == KeepCriterion.FolderPriority && ranksDiffer && folder is not null
            ? L.F("優先{0}のフォルダー（{1}）にあるため残します", folder.Rank, folder.Name)
            : L.F("{0}ため残します", date);
    }

    public NextActionInfo NextAction
    {
        get
        {
            if (NewGroupsFound > 0 && QueueFileCount == 0 && OpenGroupCount > 0)
                return new(NextActionKind.ReviewNew, L.F("前回から新しい重複が {0}組 見つかりました", NewGroupsFound),
                    L.T("起動時の確認で、新しいファイルを解析しました。新しく見つかった組が一覧に並んでいます。"), L.T("最初の組を見る"));
            var exact = ExactGroups.Where(g => g.IsOpen && g.IsReady).ToArray();
            if (exact.Length > 0)
                return new(NextActionKind.ApproveExact,
                    L.F("完全に同じファイルが {0}組 あります（{1}）", exact.Length, MediaDisplayFormat.FileSize(exact.Sum(g => g.CleanupBytes))),
                    L.T("残す方は、決めてあるルールで選んであります。下のボタンで、ルールと各フォルダーから片付ける本数を確かめてから承認します。"),
                    L.T("ルールを確認して承認"));
            if (IsRunOnly)
                return new(NextActionKind.RunQueue, L.F("整理リストに {0}件（{1}）あります", QueueFileCount, MediaDisplayFormat.FileSize(QueueBytes)),
                    L.T("一覧には、これから片付けるものだけを表示しています。「確認して実行」で、もう一度内容を確かめてから片付けます。"), L.T("確認して実行 →"));
            var similar = SimilarGroups.Concat(NeedsCheckGroups).Count(g => g.IsOpen);
            var clear = ClearGroups;
            if (clear.Count > 0)
                return new(NextActionKind.ApproveClear, L.F("よく似ている動画が {0}組 あります", similar),
                    L.F("そのうち {0}組（{1}）は、長さも中身もほぼ同じで、残す方の画質が同じか上です。まとめて承認できます。残りは1組ずつ見比べます。", clear.Count, MediaDisplayFormat.FileSize(clear.Sum(g => g.CleanupBytes))),
                    L.F("はっきりした {0}組をまとめて承認", clear.Count));
            if (similar > 0)
                return new(NextActionKind.ReviewSimilar, L.F("よく似ている動画が {0}組 あります", similar),
                    L.T("別々のファイルなので、右で見比べて残す方を選び、「この組を整理リストへ」を押してください。")
                    + (QueueFileCount > 0 ? L.F("整理リストに入れた {0}件は、下の「確認して実行」でまとめて片付けます。", QueueFileCount) : string.Empty),
                    string.Empty);
            if (QueueFileCount > 0)
                return new(NextActionKind.RunQueue, L.F("整理リストに {0}件（{1}）あります", QueueFileCount, MediaDisplayFormat.FileSize(QueueBytes)),
                    L.T("「確認して実行」で、内容を確認してから片付けます。"), L.T("確認して実行 →"));
            return new(NextActionKind.AllClear, L.T("片付ける重複はありません"), string.Empty, string.Empty);
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _source.LoadAsync(cancellationToken);
        _settings = await _source.LoadSettingsAsync(cancellationToken);
        var ranks = _settings.RanksFor(snapshot.Folders.Select(f => f.Id));
        Folders.Clear();
        foreach (var folder in snapshot.Folders.OrderBy(f => ranks[f.Id])) Folders.Add(new FolderRow(folder, ranks[folder.Id]));
        _queue.Clear();
        _singles.Clear();
        Judgments.Clear();
        ExactGroups.Clear();
        SimilarGroups.Clear();
        NeedsCheckGroups.Clear();
        var online = snapshot.Folders.Where(f => f.IsOnline).Select(f => f.Id).ToHashSet();
        foreach (var data in snapshot.Groups.Where(g => g.Files.Count >= 2))
        {
            var row = new GroupRow(data, AutoKeep(data)) { IsReady = data.Files.All(f => online.Contains(f.RootId)) };
            TargetList(data.Category).Add(row);
        }
        Selected = null;
        HasNewResults = false;
        RefreshDrives();
        RaiseSummary();
        Raise(nameof(AnalysisText));
        await LoadRecordsAsync(cancellationToken);
    }

    public void ChooseKeep(GroupRow group, Guid mediaFileId)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (group.Status != GroupStatus.Open || group.Files.All(f => f.MediaFileId != mediaFileId)) return;
        group.KeepId = mediaFileId;
        group.IsManual = mediaFileId != AutoKeep(group.Data);
        RaiseSummary();
    }

    public void ResetToRule(GroupRow group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.IsManual = false;
        group.KeepId = AutoKeep(group.Data);
        RaiseSummary();
    }

    public ExactApprovalPlan PrepareExactApproval()
    {
        var open = ExactGroups.Where(g => g.IsOpen).ToArray();
        var ready = open.Where(g => g.IsReady).ToArray();
        var room = _queue.ExactLimit - _queue.Count(CleanupKind.Exact);
        var take = new List<GroupRow>();
        var used = 0;
        foreach (var group in ready)
        {
            var count = group.CleanupFiles.Count;
            if (used + count > room) continue;
            used += count;
            take.Add(group);
        }
        return new ExactApprovalPlan(take, CleanupSummary.Create(take.Select(g => g.ToPlan())),
            ready.Length - take.Count, open.Length - ready.Length, take.Count(g => g.IsManual));
    }

    public ApproveResult ApproveExact()
    {
        var plan = PrepareExactApproval();
        var added = plan.Groups.Count(AddToQueue);
        RaiseSummary();
        return new ApproveResult(added, plan.LeftForNextRun + plan.Groups.Count - added, plan.WaitingForDrive);
    }

    public bool Enqueue(GroupRow group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (!AddToQueue(group)) return false;
        if (ReferenceEquals(Selected, group)) Selected = NextOpenForReview(group);
        RaiseSummary();
        return true;
    }

    public void Dequeue(GroupRow group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (!_queue.RemoveGroup(group.GroupId)) return;
        group.Status = GroupStatus.Open;
        group.IsBulkApproved = false;
        RaiseSummary();
    }

    public void ClearQueue()
    {
        foreach (var group in AllGroups.Where(g => g.Status == GroupStatus.Queued)) { group.Status = GroupStatus.Open; group.IsBulkApproved = false; }
        _queue.Clear();
        _singles.Clear();
        RaiseSummary();
    }

    /// <summary>The group a file is shown in, for switching between its files in the player.</summary>
    public GroupRow? GroupOf(Guid mediaFileId) => AllGroups.FirstOrDefault(g => g.Files.Any(f => f.MediaFileId == mediaFileId));

    public IReadOnlyList<CleanupRequest> CreateCleanupRequests() =>
        CleanupRequest.FromGroups(QueuedGroups).Concat(_singles.Select(f => new CleanupRequest(f.MediaFileId, CleanupKind.Single, null, f))).ToArray();

    /// <summary>Adds one file the user chose. A file kept in a duplicate group or already queued is refused.</summary>
    public SingleQueueResult EnqueueSingle(OrganizeFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (_queue.Contains(file.MediaFileId)) return SingleQueueResult.AlreadyQueued;
        if (QueuedGroups.Any(g => g.KeepId == file.MediaFileId)) return SingleQueueResult.KeptInGroup;
        if (!Folders.Any(f => f.Id == file.RootId && f.IsOnline)) return SingleQueueResult.WaitingForDrive;
        if (!_queue.TryAddGroup(file.MediaFileId, CleanupKind.Single, [file.MediaFileId])) return SingleQueueResult.LimitReached;
        _singles.Add(file);
        RaiseSummary();
        return SingleQueueResult.Added;
    }

    public void DequeueSingle(Guid mediaFileId)
    {
        if (!_queue.RemoveGroup(mediaFileId)) return;
        _singles.RemoveAll(f => f.MediaFileId == mediaFileId);
        RaiseSummary();
    }

    public CleanupWizardViewModel? CreateWizard() =>
        Executor is null || QueueFileCount == 0 ? null : new CleanupWizardViewModel(CreateCleanupRequests(), Executor);

    /// <summary>
    /// After a run: fully cleaned groups leave the list; groups with any file not done go back to the list as open,
    /// keeping the user's keep choice.
    /// </summary>
    public void ApplyCleanupResults(IReadOnlyCollection<Guid> completedFileIds)
    {
        ArgumentNullException.ThrowIfNull(completedFileIds);
        var done = completedFileIds.ToHashSet();
        foreach (var single in _singles) _queue.RemoveGroup(single.MediaFileId);
        _singles.Clear();
        foreach (var group in QueuedGroups)
        {
            _queue.RemoveGroup(group.GroupId);
            if (group.CleanupFiles.All(f => done.Contains(f.MediaFileId)))
            {
                group.Status = GroupStatus.Done;
                TargetList(group.Category).Remove(group);
                if (ReferenceEquals(Selected, group)) Selected = null;
            }
            else group.Status = GroupStatus.Open;
            group.IsBulkApproved = false;
        }
        RaiseSummary();
    }

    public GroupRow? FirstOpenForReview() => SimilarGroups.Concat(NeedsCheckGroups).FirstOrDefault(g => g.IsOpen);

    /// <summary>Moves the selection through the groups in list order.</summary>
    public void SelectRelative(int step)
    {
        var all = ListedGroups.ToList();
        if (all.Count == 0) return;
        var index = Selected is null ? -1 : all.IndexOf(Selected);
        Selected = all[Math.Clamp(index + step, 0, all.Count - 1)];
    }

    public async Task UndoJudgmentAsync(JudgmentRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        await _source.ResetJudgmentAsync(record.Group.GroupId, cancellationToken);
        record.Group.Status = GroupStatus.Open;
        Judgments.Remove(record);
        RaiseSummary();
    }

    /// <summary>Moves a folder to <paramref name="newIndex"/> in the priority list, saves it and re-applies the rule to groups not chosen by hand.</summary>
    public async Task MoveFolderAsync(Guid folderId, int newIndex, CancellationToken cancellationToken)
    {
        var row = Folders.FirstOrDefault(f => f.Id == folderId);
        if (row is null) return;
        var target = Math.Clamp(newIndex, 0, Folders.Count - 1);
        Folders.Move(Folders.IndexOf(row), target);
        for (var i = 0; i < Folders.Count; i++) Folders[i].Rank = i + 1;
        await SaveSettingsAsync(_settings with { FolderOrder = Folders.Select(f => f.Id).ToArray() }, cancellationToken);
    }

    public async Task SaveSettingsAsync(OrganizeSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _source.SaveSettingsAsync(settings, cancellationToken);
        _settings = settings;
        var ranks = settings.RanksFor(Folders.Select(f => f.Id));
        foreach (var folder in Folders.OrderBy(f => ranks[f.Id]).ToArray()) Folders.Move(Folders.IndexOf(folder), ranks[folder.Id] - 1);
        foreach (var folder in Folders) folder.Rank = ranks[folder.Id];
        foreach (var group in ExactGroups.Where(g => g.IsOpen && !g.IsManual)) group.KeepId = AutoKeep(group.Data);
        Raise(nameof(Settings));
        Raise(nameof(Rule));
        RaiseSummary();
    }

    public Task<LocalMediaAI.Core.RetentionSettings> LoadRetentionAsync(CancellationToken cancellationToken) => _source.LoadRetentionAsync(cancellationToken);

    public Task SaveRetentionAsync(LocalMediaAI.Core.RetentionSettings settings, CancellationToken cancellationToken) => _source.SaveRetentionAsync(settings, cancellationToken);

    /// <summary>Stops organizing a folder (files are not touched), then reloads the list without it.</summary>
    public async Task RemoveFolderAsync(Guid folderId, CancellationToken cancellationToken)
    {
        await _source.RemoveFolderAsync(folderId, cancellationToken);
        await LoadAsync(cancellationToken);
    }

    private bool AddToQueue(GroupRow group)
    {
        if (group.Status != GroupStatus.Open || !group.IsReady) return false;
        if (!_queue.TryAddGroup(group.GroupId, group.QueueKind, group.CleanupFiles.Select(f => f.MediaFileId).ToArray())) return false;
        group.Status = GroupStatus.Queued;
        return true;
    }

    private GroupRow? NextOpenForReview(GroupRow after)
    {
        var review = SimilarGroups.Concat(NeedsCheckGroups).ToList();
        var start = review.IndexOf(after);
        return review.Skip(start + 1).Concat(review.Take(Math.Max(start, 0))).FirstOrDefault(g => g.IsOpen);
    }

    private Guid AutoKeep(OrganizeGroupData data)
    {
        if (data.Category == GroupCategory.Exact)
            return Rule.Choose(data.Files.Select(f => f.ToCandidate()).ToArray(), Folders.ToDictionary(f => f.Id, f => f.Rank));
        return data.Files
            .OrderByDescending(f => f.PixelCount)
            .ThenByDescending(f => f.Bitrate ?? 0)
            .ThenByDescending(f => f.DurationSeconds ?? 0)
            .First().MediaFileId;
    }

    private ObservableCollection<GroupRow> TargetList(GroupCategory category) => category switch
    {
        GroupCategory.Exact => ExactGroups,
        GroupCategory.Similar => SimilarGroups,
        _ => NeedsCheckGroups
    };

    private void RaiseSummary()
    {
        RefreshDriveView();
        RefreshLists();
        Raise(nameof(QueueMeter));
        foreach (var name in new[] { nameof(OpenGroupCount), nameof(ExactOpenCount), nameof(SimilarOpenCount), nameof(NeedsCheckOpenCount), nameof(CleanableBytes), nameof(WaitingForDriveCount), nameof(QueuedGroups),
                     nameof(QueueFileCount), nameof(QueueExactFileCount), nameof(QueueOtherFileCount), nameof(QueueBytes), nameof(NextAction),
                     nameof(StatsText), nameof(RuleText), nameof(IsRunOnly), nameof(Steps), nameof(ClearGroups), nameof(GoalQueuedBytes), nameof(GoalReachableBytes),
                     nameof(LaterSimilarCount), nameof(LaterCheckCount), nameof(LaterWaitingExactCount), nameof(Sort), nameof(ListedGroups) })
            Raise(name);
        EnsureSelection();
    }
}
