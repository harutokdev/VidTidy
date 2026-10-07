using System.Collections.ObjectModel;
using LocalMediaAI.Core.Organize;

namespace LocalMediaAI.App.Organize;

/// <summary>Change check, analysis progress and drive space for the organize screen.</summary>
public sealed partial class OrganizeViewModel
{
    private readonly AnalysisEta _eta = new();
    private IReadOnlyList<DriveSpace> _driveBase = [];
    private LocalMediaAI.Core.Organize.AnalysisProgress _progress = LocalMediaAI.Core.Organize.AnalysisProgress.Empty;
    private readonly Dictionary<Guid, int> _baseline = [];
    private int _overallBaseline;
    private DateTimeOffset? _checkStartedAt;
    private HashSet<Guid>? _groupsBeforeCheck;
    private HashSet<Guid>? _heldGroupsBefore;
    private int _newGroupsFound;
    private DateTimeOffset? _lastChecked;
    private int _remainingWork;
    private bool _hasNewResults;
    private readonly FinishClock _finish = new();
    private bool? _finishForScan;
    private double _shownPercent;
    private IReadOnlyList<ScanPart> _scanParts = [];

    /// <summary>When the running check or analysis began (as seen by this app); null when idle.</summary>
    public DateTimeOffset? AnalysisStartedAt { get; private set; }

    /// <summary>True while a change check is still looking through the folders (before new files are analysed).</summary>
    public bool IsScanning => IsAnalyzing && IsChangeCheck && FolderProgress.Any(f => f.State == "run" && f.Stage == 0);

    /// <summary>Percent of the current step (the folder check, then the analysis). Never goes back within a step.</summary>
    public double ProgressPercent => IsAnalyzing ? _shownPercent : 100;

    /// <summary>Time left in the current step, counting down smoothly between polls; null until it can be estimated.</summary>
    public TimeSpan? TimeLeft(DateTimeOffset now) => IsAnalyzing ? _finish.Left(now) : null;

    public DateTimeOffset? FinishAt => IsAnalyzing ? _finish.FinishAt : null;

    /// <summary>Background analysis. Null in tests that do not need it; the change check is then unavailable.</summary>
    public IOrganizeBackground? Background { get; init; }

    public ObservableCollection<DriveSpace> Drives { get; } = [];

    public Task<LocalMediaAI.Core.Processing.ProcessingSettings> LoadProcessingAsync(CancellationToken cancellationToken) =>
        Background?.LoadProcessingAsync(cancellationToken) ?? Task.FromResult(LocalMediaAI.Core.Processing.ProcessingSettings.Defaults);

    public Task SaveProcessingAsync(LocalMediaAI.Core.Processing.ProcessingSettings settings, CancellationToken cancellationToken) =>
        Background?.SaveProcessingAsync(settings, cancellationToken) ?? Task.CompletedTask;

    /// <summary>"ドライブを休ませています（14:35 ごろ再開）" during a rest, or the idle-only wait; null while working normally.</summary>
    public string? RestText => Background switch
    {
        { RestingUntil: { } until } when until > DateTimeOffset.UtcNow => L.F("ドライブを休ませています（{0:HH:mm} ごろ再開）", until.ToLocalTime()),
        { WaitingForUser: true } => L.T("パソコンを使っていない間に解析します（操作が3分止まると再開）"),
        _ => null
    };

    public bool IsAnalyzing => _remainingWork > 0;

    /// <summary>"前回の確認：たった今" etc. Only checks made while the app runs are known.</summary>
    public string LastCheckText => L.T("前回の確認：") + (IsAnalyzing && (IsChangeCheck || _lastChecked is null) ? L.T("確認中…") : _lastChecked is { } at ? Ago(at) : "—");

    private static string Ago(DateTimeOffset at)
    {
        var span = DateTimeOffset.UtcNow - at;
        return span < TimeSpan.FromMinutes(1) ? L.T("たった今") : span < TimeSpan.FromHours(1) ? L.F("{0}分前", (int)span.TotalMinutes) : span < TimeSpan.FromDays(1) ? L.F("{0}時間前", (int)span.TotalHours) : L.F("{0}日前", (int)span.TotalDays);
    }

    /// <summary>True when analysis finished while the cleanup list was in use, so the list was not reloaded under the user.</summary>
    public bool HasNewResults { get => _hasNewResults; private set => Set(ref _hasNewResults, value); }

    /// <summary>True while a change check (startup or 変更を確認) is running.</summary>
    public bool IsChangeCheck { get; private set; }

    /// <summary>True when the change check was started automatically at startup.</summary>
    public bool IsAutoCheck { get; private set; }

    /// <summary>Groups that appeared with the last change check; shown until the user looks at them.</summary>
    public int NewGroupsFound { get => _newGroupsFound; private set { if (Set(ref _newGroupsFound, value)) Raise(nameof(NextAction)); } }

    public IReadOnlyList<FolderProgress> FolderProgress { get; private set; } = [];

    public double OverallPercent => _overallBaseline == 0 ? 100 : Math.Round((1 - (double)_remainingWork / _overallBaseline) * 100);

    public string EtaText => TimeLeft(DateTimeOffset.UtcNow) is { } left ? L.F("残り {0}", AnalysisEta.Format(left)) : L.T("残り時間を計算しています…");

    /// <summary>What is being processed now: the file of a running job, or the folder being checked.</summary>
    public string CurrentWorkText
    {
        get
        {
            var file = _progress.Jobs.Where(j => j.FileName is not null).OrderByDescending(j => j.State == LocalMediaAI.Core.JobState.Running).FirstOrDefault();
            if (file is not null) return L.F("{0}（{1}）", file.FileName, MediaDisplayFormat.FileSize(file.SizeBytes ?? 0));
            var scanning = FolderProgress.FirstOrDefault(f => f.State == "run" && f.Stage == 0);
            return scanning is null ? string.Empty : L.F("{0}（{1:N0}本 確認済み）", scanning.Folder.Name, scanning.ScanSeen);
        }
    }

    public string AnalysisText
    {
        get
        {
            if (IsAnalyzing)
            {
                var running = FolderProgress.Where(f => f.State == "run").Select(f => f.Folder.Name).ToArray();
                var where = running.Length > 0 ? string.Join(L.T("、"), running) : L.T("登録したフォルダー");
                if (RestText is { } rest) return L.F("{0}・{1}（あと {2}件）", rest, where, _remainingWork);
                if (IsChangeCheck)
                {
                    var scanning = FolderProgress.FirstOrDefault(f => f.State == "run" && f.Stage == 0);
                    return (IsAutoCheck ? L.T("起動時の確認・") : string.Empty) + (scanning is not null
                        ? L.F("変更を確認中：{0}（{1:N0}本）", scanning.Folder.Name, scanning.ScanSeen)
                        : L.F("新しいファイルを解析中：{0}（あと {1}件・{2}）", where, _remainingWork, EtaText));
                }
                return L.F("解析中：{0}・{1}（目安）", where, EtaText);
            }
            return HasNewResults ? L.T("解析が終わりました。新しい結果は、整理リストを実行するか空にすると一覧に表示されます") : string.Empty;
        }
    }

    /// <summary>
    /// Shows results that were held back because the cleanup list had files. Reloading rebuilds the list,
    /// so the cleanup list is emptied (no file is touched); the view asks the user first.
    /// </summary>
    public Task ShowNewResultsNowAsync(CancellationToken cancellationToken) => ShowResultsAsync(_heldGroupsBefore, cancellationToken);

    private async Task ShowResultsAsync(HashSet<Guid>? groupsBefore, CancellationToken cancellationToken)
    {
        _heldGroupsBefore = null;
        await LoadAsync(cancellationToken);
        if (groupsBefore is not null)
            NewGroupsFound = ExactGroups.Concat(SimilarGroups).Concat(NeedsCheckGroups).Count(g => !groupsBefore.Contains(g.GroupId));
    }

    /// <summary>Clears the "new groups" banner once the user has looked at them.</summary>
    public void AcknowledgeNewGroups() => NewGroupsFound = 0;

    /// <summary>Loads the list, then checks the folders for changes when the setting says so.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        if (Settings.CheckChangesOnStartup) _ = await CheckChangesAsync(cancellationToken, automatic: true);
    }

    /// <summary>Rescans every connected folder. Files that did not change keep their analysis.</summary>
    /// <returns>False when nothing was started: no background, no connected folder, or analysis is already running.
    /// A second request would queue every scan again and restart the elapsed time and the estimate.</returns>
    public async Task<bool> CheckChangesAsync(CancellationToken cancellationToken, bool automatic = false)
    {
        if (Background is null || IsAnalyzing) return false;
        var online = Folders.Where(f => f.IsOnline).Select(f => f.Id).ToArray();
        if (online.Length == 0) return false;
        IsChangeCheck = true;
        IsAutoCheck = automatic;
        _checkStartedAt = DateTimeOffset.UtcNow;
        AnalysisStartedAt = _checkStartedAt;
        _groupsBeforeCheck = ExactGroups.Concat(SimilarGroups).Concat(NeedsCheckGroups).Select(g => g.GroupId).ToHashSet();
        await Background.RequestChangeCheckAsync(online, cancellationToken);
        await PollAnalysisAsync(DateTimeOffset.UtcNow, cancellationToken);
        return true;
    }

    /// <summary>Called on a timer. When analysis finishes, reloads the list unless the user has files in the cleanup list.</summary>
    public async Task PollAnalysisAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (Background is null) return;
        var wasAnalyzing = IsAnalyzing;
        _progress = await Background.GetProgressAsync(cancellationToken);
        _remainingWork = _progress.Jobs.Count;
        _eta.Observe(now, _remainingWork);
        UpdateFolderProgress();
        if (IsAnalyzing) UpdateCountdown(now);
        if (wasAnalyzing && !IsAnalyzing)
        {
            AnalysisStartedAt = null;
            _finish.Reset();
            _finishForScan = null;
            _shownPercent = 0;
            _lastChecked = now;
            var before = IsChangeCheck ? _groupsBeforeCheck : null;
            if (QueueFileCount == 0) await ShowResultsAsync(before, cancellationToken);
            else
            {
                HasNewResults = true;
                _heldGroupsBefore = before;
            }
            IsChangeCheck = false;
            _groupsBeforeCheck = null;
            _baseline.Clear();
            _overallBaseline = 0;
            UpdateFolderProgress();
        }
        else if (!IsAnalyzing && HasNewResults && QueueFileCount == 0)
            await ShowResultsAsync(_heldGroupsBefore, cancellationToken); // the cleanup list was run or emptied
        foreach (var name in new[] { nameof(IsAnalyzing), nameof(AnalysisText), nameof(RestText), nameof(LastCheckText), nameof(FolderProgress), nameof(OverallPercent), nameof(EtaText), nameof(CurrentWorkText), nameof(IsChangeCheck), nameof(ProgressPercent), nameof(IsScanning) })
            Raise(name);
    }

    /// <summary>The folder check and the analysis after it each get their own percent and countdown.</summary>
    private void UpdateCountdown(DateTimeOffset now)
    {
        AnalysisStartedAt ??= now;
        var scanPhase = IsScanning;
        if (_finishForScan != scanPhase)
        {
            _finish.Reset();
            _finishForScan = scanPhase;
            _shownPercent = 0;
        }
        _finish.Observe(now, scanPhase ? ChangeCheckEstimate.Remaining(_scanParts, now) : _eta.Remaining);
        _shownPercent = Math.Max(_shownPercent, scanPhase ? ChangeCheckEstimate.Percent(_scanParts) : OverallPercent);
    }


    /// <summary>Per-folder percent against the most work seen for that folder since this analysis began.</summary>
    private void UpdateFolderProgress()
    {
        var byRoot = _progress.Jobs.Where(j => j.RootId is not null).GroupBy(j => j.RootId!.Value).ToDictionary(g => g.Key, g => g.ToArray());
        _overallBaseline = Math.Max(_overallBaseline, _remainingWork);
        var rows = new List<FolderProgress>();
        var parts = new List<ScanPart>();
        foreach (var folder in Folders)
        {
            var jobs = byRoot.GetValueOrDefault(folder.Id) ?? [];
            _baseline[folder.Id] = Math.Max(_baseline.GetValueOrDefault(folder.Id), jobs.Length);
            var baseline = _baseline[folder.Id];
            var percent = baseline == 0 ? 100 : Math.Round((1 - (double)jobs.Length / baseline) * 100);
            var stage = LocalMediaAI.Core.Organize.AnalysisStages.Current(jobs);
            var scan = _progress.Scans.FirstOrDefault(s => s.RootId == folder.Id && (_checkStartedAt is null || s.StartedAt >= _checkStartedAt.Value.AddSeconds(-5)));
            var state = jobs.Length > 0 ? "run" : baseline > 0 || scan is not null ? "done" : IsAnalyzing && IsChangeCheck ? "wait" : "done";
            var part = ScanPartOf(folder, jobs, scan);
            if (folder.IsOnline) parts.Add(part);
            rows.Add(new FolderProgress(folder, stage, percent, jobs.Length, state, scan, part.Seen, ChangeCheckEstimate.Percent([part])));
            folder.AnalysisPercent = percent;
            folder.AnalysisText = jobs.Length > 0 ? L.F("解析中 {0}%・", percent) + (TimeLeft(DateTimeOffset.UtcNow) is { } left ? L.F("残り{0}", AnalysisEta.Format(left)) : L.T("残り時間を計算中")) : null;
        }
        FolderProgress = rows;
        _scanParts = parts;
    }

    /// <summary>The scan job reports the files it has seen every few files; the scan record has the total once it ends.</summary>
    private static ScanPart ScanPartOf(FolderRow folder, IReadOnlyList<ActiveAnalysisJob> jobs, LatestScan? scan)
    {
        var scanJob = jobs.Where(j => j.Type == LocalMediaAI.Core.JobType.Scan).OrderByDescending(j => j.State == LocalMediaAI.Core.JobState.Running).FirstOrDefault();
        var done = scanJob is null && scan?.CompletedAt is not null;
        var running = scanJob?.State == LocalMediaAI.Core.JobState.Running;
        var seen = done ? scan!.Seen : running ? scanJob!.Current : 0;
        return new ScanPart(folder.Folder.FileCount, seen, done, done || running ? scan?.StartedAt : null, done ? scan!.CompletedAt : null);
    }

    private void RefreshDrives()
    {
        _driveBase = Background is null ? [] : Background.GetDriveSpace(Folders.Where(f => f.IsOnline).Select(f => f.Path));
        RefreshDriveView();
    }

    /// <summary>Rebuilds the drive bars with the cleanup list share, without asking Windows again.</summary>
    private void RefreshDriveView()
    {
        var queued = QueuedGroups.SelectMany(g => g.CleanupFiles)
            .GroupBy(f => System.IO.Path.GetPathRoot(f.Path) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(f => f.SizeBytes), StringComparer.OrdinalIgnoreCase);
        Drives.Clear();
        foreach (var drive in _driveBase) Drives.Add(drive with { QueueBytes = queued.GetValueOrDefault(drive.Drive) });
    }
}
