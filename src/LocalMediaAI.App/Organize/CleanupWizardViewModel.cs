using System.Collections.ObjectModel;
using LocalMediaAI.App.ViewModels;
using LocalMediaAI.Core.Organize;

namespace LocalMediaAI.App.Organize;

public enum CleanupWizardStep { Review, Checking, Ready, Running, Finished }

public enum CleanupRowState { Pending, Ready, Blocked, Done, Failed }

public sealed class CleanupWizardRow : ViewModelBase
{
    private readonly Action _changed;
    private bool _isChecked;
    private bool _isIncluded = true;
    private CleanupRowState _state = CleanupRowState.Pending;
    private string? _reason;

    public CleanupWizardRow(CleanupRequest request, Action changed)
    {
        Request = request;
        _changed = changed;
        _isChecked = !NeedsManualCheck;
    }

    public CleanupRequest Request { get; }
    /// <summary>Exact duplicates were approved by rule and clear similar groups were approved together; everything else is checked row by row.</summary>
    public bool NeedsManualCheck => Request.Kind != CleanupKind.Exact && !Request.Approved;
    public bool IsChecked { get => _isChecked; set { if (Set(ref _isChecked, value)) _changed(); } }
    /// <summary>False when the user takes the row out of this run. It goes back to the list untouched.</summary>
    public bool IsIncluded { get => _isIncluded; set { if (Set(ref _isIncluded, value)) _changed(); } }
    public CleanupRowState State { get => _state; internal set => Set(ref _state, value); }
    public string? Reason { get => _reason; internal set => Set(ref _reason, value); }
    public bool WillRun => IsIncluded && State == CleanupRowState.Ready;
}

/// <summary>内容を確認 → チェック → 実行. Holds no file system logic; the executor does all changes.</summary>
public sealed class CleanupWizardViewModel : ViewModelBase
{
    private readonly ICleanupExecutor _executor;
    private CleanupWizardStep _step = CleanupWizardStep.Review;
    private CleanupProgress? _progress;

    public CleanupWizardViewModel(IReadOnlyList<CleanupRequest> requests, ICleanupExecutor executor)
    {
        _executor = executor;
        Rows = new ObservableCollection<CleanupWizardRow>(requests.Select(r => new CleanupWizardRow(r, RaiseCounts)));
    }

    public ObservableCollection<CleanupWizardRow> Rows { get; }
    public CleanupWizardStep Step { get => _step; private set { if (Set(ref _step, value)) RaiseCounts(); } }
    public CleanupProgress? Progress { get => _progress; private set => Set(ref _progress, value); }

    public long TotalBytes => Rows.Sum(r => r.Request.Target.SizeBytes);
    public int ReadyCount => Rows.Count(r => r.State == CleanupRowState.Ready);
    public int BlockedCount => Rows.Count(r => r.State == CleanupRowState.Blocked);
    public int ManualCount => Rows.Count(r => r.NeedsManualCheck);
    public int RunCount => Rows.Count(r => r.WillRun);
    public long RunBytes => Rows.Where(r => r.WillRun).Sum(r => r.Request.Target.SizeBytes);
    public int DoneCount => Rows.Count(r => r.State == CleanupRowState.Done);
    public long DoneBytes => Rows.Where(r => r.State == CleanupRowState.Done).Sum(r => r.Request.Target.SizeBytes);
    public int FailedCount => Rows.Count(r => r.State == CleanupRowState.Failed);
    public int UncheckedCount => Rows.Count(r => r.WillRun && !r.IsChecked);
    public IReadOnlyList<Guid> CompletedFileIds => Rows.Where(r => r.State == CleanupRowState.Done).Select(r => r.Request.Target.MediaFileId).ToArray();

    public bool CanRun => Step == CleanupWizardStep.Ready && RunCount > 0 && UncheckedCount == 0;

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        if (Step is not (CleanupWizardStep.Review or CleanupWizardStep.Ready)) throw new InvalidOperationException("Check can only run before execution.");
        Step = CleanupWizardStep.Checking;
        try
        {
            var results = await _executor.CheckAsync(Rows.Select(r => r.Request).ToArray(), cancellationToken);
            foreach (var row in Rows)
            {
                var result = results.FirstOrDefault(r => r.Request == row.Request);
                row.State = result is { IsReady: true } ? CleanupRowState.Ready : CleanupRowState.Blocked;
                row.Reason = result is null ? L.T("確認できませんでした") : result.Reason;
            }
        }
        finally
        {
            Step = CleanupWizardStep.Ready;
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!CanRun) throw new InvalidOperationException("Every row must be checked and ready before running.");
        var runRows = Rows.Where(r => r.WillRun).ToArray();
        Step = CleanupWizardStep.Running;
        try
        {
            var results = await _executor.RunAsync(runRows.Select(r => r.Request).ToArray(), new SyncProgress(p => Progress = p), cancellationToken);
            foreach (var row in runRows)
            {
                var result = results.FirstOrDefault(r => r.Request == row.Request);
                row.State = result is { Succeeded: true } ? CleanupRowState.Done : CleanupRowState.Failed;
                row.Reason = result is null ? L.T("実行されませんでした") : result.Reason;
            }
        }
        finally
        {
            Step = CleanupWizardStep.Finished;
        }
    }

    private void RaiseCounts()
    {
        foreach (var name in new[] { nameof(ReadyCount), nameof(BlockedCount), nameof(RunCount), nameof(RunBytes), nameof(DoneCount),
                     nameof(DoneBytes), nameof(FailedCount), nameof(UncheckedCount), nameof(CanRun) })
            Raise(name);
    }

    /// <summary>Reports on the caller's thread; the view marshals to the dispatcher itself.</summary>
    private sealed class SyncProgress(Action<CleanupProgress> report) : IProgress<CleanupProgress>
    {
        public void Report(CleanupProgress value) => report(value);
    }
}
