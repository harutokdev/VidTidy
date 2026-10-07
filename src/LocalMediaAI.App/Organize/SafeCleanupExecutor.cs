using LocalMediaAI.Core;
using LocalMediaAI.Core.Organize;
using LocalMediaAI.Infrastructure;

namespace LocalMediaAI.App.Organize;

/// <summary>
/// Sends cleanup targets to the recycle bin through the existing controlled batch gates
/// (full-hash plan, dry run, acknowledgement, final confirmation, sequential execution). Never deletes permanently.
/// </summary>
public sealed class SafeCleanupExecutor : ICleanupExecutor
{
    private readonly ILibraryRepository _repository;
    private readonly SafeFileOperationService _operations;
    private readonly IStructuredLogger _log;

    public SafeCleanupExecutor(ILibraryRepository repository, SafeFileOperationService operations, IStructuredLogger log)
    {
        _repository = repository;
        _operations = operations;
        _log = log;
    }

    public async Task<IReadOnlyList<CleanupCheckResult>> CheckAsync(IReadOnlyList<CleanupRequest> requests, CancellationToken cancellationToken)
    {
        var results = new List<CleanupCheckResult>(requests.Count);
        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reason = await Task.Run(() => ProblemWithAsync(request, cancellationToken), cancellationToken);
            results.Add(new CleanupCheckResult(request, reason is null, reason));
        }
        return results;
    }

    public async Task<IReadOnlyList<CleanupRunResult>> RunAsync(IReadOnlyList<CleanupRequest> requests, IProgress<CleanupProgress>? progress, CancellationToken cancellationToken)
    {
        var results = new List<CleanupRunResult>(requests.Count);
        foreach (var chunk in requests.Chunk(FileOperationPolicy.ControlledBatchMaxItems))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                results.AddRange(chunk.Select(r => new CleanupRunResult(r, false, L.T("中止したため実行していません"))));
                continue;
            }
            progress?.Report(new CleanupProgress(results.Count, requests.Count, chunk[0].Target.FileName));
            // Off the UI thread: hashing and the database gates are slow for large videos.
            results.AddRange(await Task.Run(() => RunChunkAsync(chunk, cancellationToken), CancellationToken.None));
        }
        progress?.Report(new CleanupProgress(results.Count, requests.Count, string.Empty));
        return results;
    }

    private async Task<IReadOnlyList<CleanupRunResult>> RunChunkAsync(IReadOnlyList<CleanupRequest> chunk, CancellationToken cancellationToken)
    {
        var failed = new List<CleanupRunResult>();
        var ready = new List<CleanupRequest>();
        foreach (var request in chunk)
        {
            var reason = await ProblemWithAsync(request, cancellationToken);
            if (reason is null) ready.Add(request);
            else failed.Add(new CleanupRunResult(request, false, reason));
        }
        if (ready.Count == 0) return failed;
        try
        {
            var dry = await _operations.CreateControlledBatchAsync(FileOperationType.Recycle,
                ready.Select(r => new ControlledBatchItemRequest(r.Target.MediaFileId, null, r.GroupId.ToString(), "Organize")).ToArray(), cancellationToken);
            if (dry.BlockedCount > 0)
            {
                // The batch refuses to skip blocked items, so report them and plan a fresh batch for the rest.
                var blocked = dry.Items.Where(i => i.State != BatchItemState.Ready).ToDictionary(i => i.MediaFileId);
                failed.AddRange(ready.Where(r => blocked.ContainsKey(r.Target.MediaFileId))
                    .Select(r => new CleanupRunResult(r, false, FriendlyReason(blocked[r.Target.MediaFileId].RevalidationState))));
                var rest = ready.Where(r => !blocked.ContainsKey(r.Target.MediaFileId)).ToArray();
                return rest.Length == 0 ? failed : failed.Concat(await RunChunkAsync(rest, cancellationToken)).ToArray();
            }
            await _operations.AcknowledgeControlledBatchDryRunAsync(dry.Batch.BatchOperationId, cancellationToken);
            await _operations.ConfirmControlledBatchExecutionAsync(dry.Batch.BatchOperationId, cancellationToken);
            await _operations.ExecuteControlledBatchAsync(dry.Batch.BatchOperationId, cancellationToken);
            var items = (await _repository.GetBatchItemsAsync(dry.Batch.BatchOperationId, CancellationToken.None)).ToDictionary(i => i.MediaFileId);
            return failed.Concat(ready.Select(r => items.TryGetValue(r.Target.MediaFileId, out var item) && item.State == BatchItemState.Completed
                ? new CleanupRunResult(r, true, null)
                : new CleanupRunResult(r, false, item is null ? L.T("実行されませんでした") : ItemReason(item)))).ToArray();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            _log.Write("Warning", "OrganizeCleanupChunkFailed", ex.Message);
            return failed.Concat(ready.Select(r => new CleanupRunResult(r, false,
                ex is OperationCanceledException ? L.T("中止したため実行していません") : L.T("安全確認で止めました。もう一度「最新の状態にする」を試してください")))).ToArray();
        }
    }

    /// <summary>Read-only check that the target and the file that stays still match what was analysed.</summary>
    private async Task<string?> ProblemWithAsync(CleanupRequest request, CancellationToken cancellationToken)
    {
        if (request.Keep is null && request.Kind != CleanupKind.Single) return L.T("残すファイルが決まっていません");
        if (request.Keep?.MediaFileId == request.Target.MediaFileId) return L.T("残すファイルと同じです");
        var target = await _repository.GetMediaFileAsync(request.Target.MediaFileId, cancellationToken);
        if (target is null || target.PresenceState != PresenceState.Present) return L.T("ライブラリにありません");
        if (!MatchesDisk(target)) return L.T("解析後にファイルが変わっています");
        if (request.Keep is null) return null; // a single file the user chose, checked row by row in the wizard
        var keep = await _repository.GetMediaFileAsync(request.Keep.MediaFileId, cancellationToken);
        if (keep is null || keep.PresenceState != PresenceState.Present || !System.IO.File.Exists(keep.Path)) return L.T("残すファイルが見つかりません");
        if (request.Kind == CleanupKind.Exact && (keep.FullSha256 is null || !string.Equals(keep.FullSha256, target.FullSha256, StringComparison.OrdinalIgnoreCase)))
            return L.T("残すファイルと中身が一致しません");
        return null;
    }

    private static bool MatchesDisk(MediaFile media)
    {
        try
        {
            var info = new System.IO.FileInfo(media.Path);
            return info.Exists && info.Length == media.SizeBytes && Math.Abs((info.LastWriteTimeUtc - media.LastWriteTimeUtc.UtcDateTime).TotalSeconds) < 2;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string ItemReason(BatchItem item) => item.State switch
    {
        BatchItemState.Cancelled => L.T("中止したため実行していません"),
        BatchItemState.RecoveryRequired => L.T("途中で止まりました。ファイルの状態を確認してください"),
        _ => FriendlyReason(item.RevalidationState)
    };

    private static string FriendlyReason(string? state) => state switch
    {
        nameof(FileOperationState.SourceChanged) => L.T("解析後にファイルが変わっています"),
        nameof(FileOperationState.SourceMissing) => L.T("ファイルが見つかりません"),
        nameof(FileOperationState.SourceUnavailable) => L.T("ドライブに接続できません"),
        nameof(FileOperationState.AccessDenied) => L.T("アクセスできません（使用中かもしれません）"),
        nameof(FileOperationState.RecycleUnavailable) => L.T("このドライブではごみ箱を使えません"),
        nameof(FileOperationState.RecycleFailed) => L.T("ごみ箱に移せませんでした"),
        _ => L.T("安全確認で止めました")
    };
}
