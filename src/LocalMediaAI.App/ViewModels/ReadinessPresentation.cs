using LocalMediaAI.Core;
using LocalMediaAI.WorkerProtocol;

namespace LocalMediaAI.App.ViewModels;

public enum ReadinessAction
{
    None,
    RegisterFolder,
    OpenLibraryRoots,
    Refresh,
    Rescan,
    RecheckWorker,
    OpenAiSearch,
    OpenReview,
    OpenJobs
}

public sealed record ReadinessInput(
    bool IsInitialized,
    int RootCount,
    bool HasSelectedRoot,
    RootAvailability SelectedRootAvailability,
    bool HasCompletedScan,
    int TotalMediaCount,
    int IndexedMediaCount,
    int ActiveJobCount,
    int ActiveScanJobCount,
    bool HasUiError,
    WorkerAvailability WorkerAvailability,
    int ReviewTargetCount);

public sealed record ReadinessPresentation(
    string State,
    string Title,
    string Meaning,
    string ActionLabel,
    ReadinessAction Action,
    string AccentBrush)
{
    public bool HasAction => Action != ReadinessAction.None;

    public static ReadinessPresentation Evaluate(ReadinessInput input)
    {
        if (!input.IsInitialized)
            return new("Initializing", L.T("ライブラリの状態を確認しています"), L.T("起動処理が完了すると、利用可能な機能と次の操作を案内します。"), string.Empty, ReadinessAction.None, "#386C9C");

        if (input.RootCount == 0)
            return new("NoRoot", L.T("メディアフォルダーがまだ登録されていません"), L.T("最初に、管理したい動画が入っているフォルダーをライブラリへ追加します。"), L.T("フォルダーを追加"), ReadinessAction.RegisterFolder, "#D7A84B");

        if (!input.HasSelectedRoot)
            return new("RootNotSelected", L.T("登録フォルダーを選択してください"), L.T("管理対象の登録フォルダーを選ぶと、そのフォルダーの準備状態を確認できます。メディアの表示対象範囲とは別の設定です。"), L.T("登録フォルダーを見る"), ReadinessAction.OpenLibraryRoots, "#6F9FD8");

        if (input.SelectedRootAvailability == RootAvailability.Offline)
            return new("RootOffline", L.T("選択したメディアフォルダーを現在利用できません"), L.T("ドライブやフォルダーが接続されているか確認してから、選択したフォルダーを再スキャンしてください。"), L.T("再スキャン"), ReadinessAction.Rescan, "#D98282");

        if (input.ActiveJobCount > 0)
        {
            var scan = input.ActiveScanJobCount > 0;
            return new(
                scan ? "Scanning" : "Processing",
                scan ? L.T("メディアをスキャンしています") : L.T("メディアを解析しています"),
                L.F("{0:N0}件の処理が進行中です。ライブラリは利用できますが、AI検索やレビューの結果は処理完了まで増えることがあります。", input.ActiveJobCount),
                L.T("現在の状態を更新"),
                ReadinessAction.Refresh,
                "#D7A84B");
        }

        if (input.TotalMediaCount == 0)
            return input.HasCompletedScan
                ? new("ScanCompleteEmpty", L.T("スキャンは完了しましたが、表示できるメディアがありません"), L.T("選択した登録フォルダーの内容や絞り込みを確認し、必要なら同じフォルダーを再スキャンしてください。"), L.T("再スキャン"), ReadinessAction.Rescan, "#D7A84B")
                : new("RootReadyToScan", L.T("メディアフォルダーを登録しました"), L.T("このフォルダーをスキャンすると、ライブラリへメディアが追加され、解析処理が始まります。"), L.T("スキャンを開始"), ReadinessAction.Rescan, "#6F9FD8");

        if (input.WorkerAvailability == WorkerAvailability.Unavailable)
            return new("WorkerUnavailable", L.T("AI機能を利用できません"), L.T("ライブラリの閲覧は続けられます。AI検索や新しいAI解析を使う前にAI機能を再確認してください。"), L.T("AI機能を再確認"), ReadinessAction.RecheckWorker, "#D98282");

        if (input.HasUiError)
            return new("UiError", L.T("直前の操作を完了できませんでした"), L.T("「処理と問題」で利用者向け案内を確認してください。安全な状態のまま次の操作を選べます。"), L.T("処理と問題を確認"), ReadinessAction.OpenJobs, "#D98282");

        if (input.WorkerAvailability is WorkerAvailability.Starting or WorkerAvailability.Stopping)
            return new("WorkerTransition", L.T("AI機能を準備しています"), L.T("AI機能の準備が完了するまでライブラリを利用できます。少し待ってから状態を更新してください。"), L.T("現在の状態を更新"), ReadinessAction.Refresh, "#D7A84B");

        if (input.ReviewTargetCount > 0)
        {
            var aiText = input.IndexedMediaCount > 0
                ? L.T("AI検索とレビューを利用できます。AI機能が未起動の場合は、AI機能の利用時に起動します。")
                : L.T("レビュー対象を確認できます。AI検索の対象は解析完了に応じて利用可能になります。");
            return new("ReviewReady", L.T("スキャンと解析が完了しました"), aiText, L.T("レビューを開く"), ReadinessAction.OpenReview, "#5E9E72");
        }

        if (input.IndexedMediaCount > 0)
            return new("AiReady", L.T("AI検索を利用できます"), L.T("解析済みメディアを検索できます。AI機能が未起動の場合は、AI検索の利用時に起動します。"), L.T("AI検索を開く"), ReadinessAction.OpenAiSearch, "#5E9E72");

        if (input.WorkerAvailability is WorkerAvailability.NotStarted or WorkerAvailability.Stopped)
            return new("WorkerNotStarted", L.T("ライブラリの準備ができました"), L.T("メディアは閲覧できます。AI解析を使う場合はAI機能の利用可否を確認できます。未起動は通常の待機状態です。"), L.T("AI機能を確認"), ReadinessAction.RecheckWorker, "#6F9FD8");

        return new("LibraryReady", L.T("スキャンが完了しました"), L.T("ライブラリでメディアを閲覧できます。AI解析結果は準備完了後にAI検索またはレビューへ表示されます。"), L.T("ライブラリを開く"), ReadinessAction.OpenLibraryRoots, "#5E9E72");
    }
}
