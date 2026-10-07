using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;
using LocalMediaAI.WorkerProtocol;

namespace LocalMediaAI.App.ViewModels;

public sealed partial class MainViewModel : ViewModelBase, IDisposable
{
    private const int PageSize = 100;
    private const int DuplicateGroupPageSize = 100;
    private const int ContentSimilarityPageSize = 100;
    private const int TagReviewPageSize = 100;
    private const int UnifiedReviewPageSize = 100;
    private readonly ILibraryRepository _library;
    private readonly IJobStore _jobs;
    private readonly MediaPipeline _pipeline;
    private readonly WorkerProcessManager _worker;
    private readonly ISemanticSearchService _semanticSearch;
    private readonly IMediaPreviewService _preview;
    private readonly IStructuredLogger _log;
    private readonly SafeFileOperationService? _fileOperations;
    public string ThumbnailCacheRoot => _pipeline.ThumbnailCacheRoot;
    private readonly object _pipelineObservationGate = new();
    private readonly object _shutdownGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _semanticSearchCts;
    private CancellationTokenSource? _previewCts;
    private CancellationTokenSource? _unifiedReviewCts;
    private long _searchRequestId;
    private long _semanticSearchRequestId;
    private long _previewRequestId;
    private long _unifiedReviewRequestId;
    private readonly Dictionary<Guid, MediaTagSummary> _semanticTagSummaries = [];
    private string _searchText = string.Empty;
    private string _semanticSearchText = string.Empty;
    private string _semanticSearchStatus = L.T("AI検索語を入力してください。");
    private string _selectedSemanticHumanMetadata = L.T("Human-reviewed metadataは検索結果を選択すると読み込みます。");
    private bool _isSemanticSearchBusy;
    private bool _isPreviewBusy;
    private bool _isPreviewOpen;
    private bool _isPreviewAvailable;
    private long _previewSeekRequestId;
    private long _previewPlaybackRequestId;
    private bool _previewPlaybackRequested;
    private SemanticSearchCandidate? _selectedSemanticResult;
    private DuplicateGroup? _selectedDuplicateGroup;
    private DuplicateComparisonMember? _selectedDuplicateMember;
    private DuplicateComparisonMember? _pinnedDuplicateMember;
    private NearDuplicateComparisonMember? _selectedNearDuplicateMember;
    private NearDuplicateComparisonMember? _pinnedNearDuplicateMember;
    private ContentSimilarityEvidence? _selectedContentSimilarityEvidence;
    private ContentSimilarityComparisonMember? _selectedContentSimilarityMember;
    private ContentSimilarityComparisonMember? _pinnedContentSimilarityMember;
    private TagReviewRow? _selectedTagReview;
    private UnifiedReviewRow? _selectedUnifiedReview;
    private string _tagJudgmentNote = string.Empty;
    private string _manualTagText = string.Empty;
    private string _manualTagNote = string.Empty;
    private string _tagReviewStatus = L.T("Suggested Tagを選択すると、AI evidenceとhuman reviewを確認できます。");
    private string _unifiedReviewNote = string.Empty;
    private string _unifiedReviewStatus = L.T("Unified Review Queueはrelation別evidenceとHuman decisionを統合表示します。ファイル操作は実行しません。");
    private UnifiedReviewFilter _unifiedReviewFilter = UnifiedReviewFilter.All;
    private UnifiedReviewSort _unifiedReviewSort = UnifiedReviewSort.Priority;
    private UnifiedReviewAction _unifiedReviewBulkAction = UnifiedReviewAction.KeepBoth;
    private FileOperationType? _operationPlanningType;
    private string _operationDestinationPath = string.Empty;
    private bool _operationPlanningAcknowledgement;
    private bool _operationPlanningFinalConfirmation;
    private FileOperationDryRun? _currentOperationDryRun;
    private ControlledBatchItemPresentation? _selectedControlledBatchItem;
    private FileOperationType _controlledBatchType = FileOperationType.Recycle;
    private string _controlledBatchStatus = L.T("Batch Review Listは空です。一覧でPresent mediaを1件選択して追加してください。PermanentDelete batchはDeferredです。");
    private string _controlledBatchDestinationDirectory = string.Empty;
    private bool _controlledBatchAcknowledgement;
    private bool _controlledBatchAcknowledgementRecorded;
    private bool _controlledBatchFinalConfirmation;
    private BatchOperationDryRun? _currentControlledBatchDryRun;
    private string _controlledBatchSelectedItemAudit = L.T("Batch itemを選択すると、既存のper-item operation auditを表示します。");
    private string _operationPlanningStatus = L.T("Operation Planningは明示選択、Dry Run acknowledgement、Final Execution Confirmationを分離します。Human judgmentはファイル操作命令ではありません。");
    private string _tagFilterText = string.Empty;
    private bool _tagFilterIncludeSuggested = true;
    private bool _bulkTagConfirmation;
    private bool _bulkUnifiedReviewConfirmation;
    private int _tagReviewPage;
    private int _totalTagReviewCount;
    private int _unifiedReviewPage;
    private int _totalUnifiedReviewCount;
    private PreviewTarget? _previewTarget;
    private string _duplicateJudgmentNote = string.Empty;
    private string _contentSimilarityJudgmentNote = string.Empty;
    private string _contentSimilarityJudgmentStatus = L.T("Content Similar candidateを選択すると、embedding similarity evidenceと人間の判断を確認できます。");
    private string _duplicateJudgmentStatus = L.T("Exact duplicate groupを選択すると、SHA-256 evidenceと人間の判断を確認できます。");
    private int _duplicateGroupPage;
    private int _totalDuplicateGroupCount;
    private int _contentSimilarityPage;
    private int _totalContentSimilarityCount;
    private string _previewStatus = L.T("検索結果を選択すると、matched frameとbest matched sampleを確認できます。");
    private LibraryRoot? _selectedRoot;
    private MediaListItem? _selectedMedia;
    private string _statusText = L.T("準備中");
    private string _fileRetentionText = "30";
    private string _folderRetentionText = "60";
    private string _driveRetentionText = "365";
    private string _errorText = string.Empty;
    private bool _isBusy;
    private int _currentPage;
    private int _totalMediaCount;
    private Guid? _selectedMediaFileId;
    private WorkerRuntimeStatus _workerStatus = new(WorkerAvailability.NotStarted, null, null, null, null, null, DateTimeOffset.UtcNow);
    private bool _readinessInitialized;
    private int _readinessMediaCount;
    private int _readinessIndexedMediaCount;
    private SemanticEmbeddingCompleteness _embeddingCompleteness = new(0, 0, 0);
    private IReadOnlyList<RootScanCurrentness> _rootScanCurrentness = [];
    private LibraryScopePresentation _currentLibraryScope = LibraryScopePresentation.All;
    private long _scopeVersion;

    private MediaFile? _operationTargetMedia => OperationTarget?.Media;
    private Guid? _scopeMediaSelectionAnchor;
    private JobIssuePresentation? _selectedJobIssue;
    private string _selectedJobEventStatus = L.T("処理を選択するとevent履歴を読み込みます。");
    private long _jobEventRequestId;
    private ProcessingRunState _processingRunState = ProcessingRunState.EnabledIdle;
    private Task? _observedPipelineTask;
    private Task? _terminalPipelineDrain;
    private TaskCompletionSource<bool>? _shutdownCompletion;
    private bool _isShuttingDown;
    private bool _disposed;

    public MainViewModel(ILibraryRepository library, IJobStore jobs, MediaPipeline pipeline, WorkerProcessManager worker, ISemanticSearchService semanticSearch, IStructuredLogger log, IMediaPreviewService? preview = null, SafeFileOperationService? fileOperations = null)
    {
        _library = library;
        _jobs = jobs;
        _pipeline = pipeline;
        _worker = worker;
        _semanticSearch = semanticSearch;
        _preview = preview ?? new LocalMediaPreviewService();
        _log = log;
        _fileOperations = fileOperations;
        ControlledBatchSelection = new ReadOnlyObservableCollection<MediaListItem>(_controlledBatchSelection);
        _worker.StatusChanged += OnWorkerStatusChanged;
        _pipeline.RunStateChanged += OnPipelineRunStateChanged;
        _pipeline.ThumbnailReady += OnThumbnailReady;
        RegisterRootCommand = new AsyncCommand(RegisterRootAsync);
        RescanCommand = new AsyncCommand(RescanAsync, () => SelectedRoot is not null);
        UnregisterRootCommand = new AsyncCommand(UnregisterRootAsync, () => SelectedRoot is not null && !IsBusy);
        SaveRetentionSettingsCommand = new AsyncCommand(SaveRetentionSettingsAsync, () => !IsBusy);
        RefreshCommand = new AsyncCommand(RefreshAsync);
        WorkerDiagnosticCommand = new AsyncCommand(RunWorkerDiagnosticAsync, () => !IsBusy);
        RescanSelectedIssueRootCommand = new AsyncCommand(RescanSelectedIssueRootAsync, CanRescanSelectedIssueRoot);
        SemanticSearchCommand = new AsyncCommand(
            RunSemanticSearchAsync,
            () => !IsBusy && !string.IsNullOrWhiteSpace(SemanticSearchText),
            allowConcurrentExecutions: true);
        CancelSemanticSearchCommand = new AsyncCommand(CancelSemanticSearchAsync, () => IsSemanticSearchBusy);
        PreviousPageCommand = new AsyncCommand(() => MovePageAsync(-1), () => !IsBusy && HasPreviousPage);
        NextPageCommand = new AsyncCommand(() => MovePageAsync(1), () => !IsBusy && HasNextPage);
        SelectMediaForPreviewCommand = new AsyncCommand(SelectMediaForPreviewAsync, () => SelectedMedia is { PresenceState: PresenceState.Present } && !IsPreviewBusy);
        OpenPreviewCommand = new AsyncCommand(OpenPreviewAsync, () => _previewTarget is not null && !IsPreviewBusy);
        ClosePreviewCommand = new AsyncCommand(ClosePreviewAsync, () => IsPreviewOpen || IsPreviewBusy);
        SeekPreviewCommand = new AsyncCommand(SeekPreviewAsync, () => IsPreviewOpen && IsPreviewAvailable && _previewTarget is not null);
        TogglePreviewPlaybackCommand = new AsyncCommand(TogglePreviewPlaybackAsync, () => IsPreviewOpen && IsPreviewAvailable);
        PreviousDuplicateGroupPageCommand = new AsyncCommand(() => MoveDuplicateGroupPageAsync(-1), () => !IsBusy && HasPreviousDuplicateGroupPage);
        NextDuplicateGroupPageCommand = new AsyncCommand(() => MoveDuplicateGroupPageAsync(1), () => !IsBusy && HasNextDuplicateGroupPage);
        PreviousContentSimilarityPageCommand = new AsyncCommand(() => MoveContentSimilarityPageAsync(-1), () => !IsBusy && HasPreviousContentSimilarityPage);
        NextContentSimilarityPageCommand = new AsyncCommand(() => MoveContentSimilarityPageAsync(1), () => !IsBusy && HasNextContentSimilarityPage);
        ConfirmDuplicateGroupCommand = new AsyncCommand(() => SetGroupJudgmentAsync(DuplicateJudgmentDecision.ConfirmedDuplicate), () => SelectedDuplicateGroup is not null);
        KeepBothDuplicateGroupCommand = new AsyncCommand(() => SetGroupJudgmentAsync(DuplicateJudgmentDecision.KeepBoth), () => SelectedDuplicateGroup is not null);
        IgnoreDuplicateGroupCommand = new AsyncCommand(() => SetGroupJudgmentAsync(DuplicateJudgmentDecision.Ignored), () => SelectedDuplicateGroup is not null);
        NotDuplicateGroupCommand = new AsyncCommand(() => SetGroupJudgmentAsync(DuplicateJudgmentDecision.NotDuplicate), () => SelectedDuplicateGroup?.RelationType == DuplicateRelationType.NearDuplicate);
        ResetDuplicateGroupJudgmentCommand = new AsyncCommand(() => SetGroupJudgmentAsync(DuplicateJudgmentDecision.Unreviewed), () => SelectedDuplicateGroup is not null);
        SetPreferredDuplicateMemberCommand = new AsyncCommand(SetPreferredDuplicateMemberAsync, () => SelectedDuplicateMember is not null || SelectedNearDuplicateMember is not null);
        PinDuplicateMemberCommand = new AsyncCommand(PinDuplicateMemberAsync, () => SelectedDuplicateMember is not null || SelectedNearDuplicateMember is not null);
        RelatedContentSimilarityCommand = new AsyncCommand(() => SetContentSimilarityJudgmentAsync(ContentSimilarityJudgmentDecision.Related), () => SelectedContentSimilarityEvidence is not null);
        KeepBothContentSimilarityCommand = new AsyncCommand(() => SetContentSimilarityJudgmentAsync(ContentSimilarityJudgmentDecision.KeepBoth), () => SelectedContentSimilarityEvidence is not null);
        NotRelatedContentSimilarityCommand = new AsyncCommand(() => SetContentSimilarityJudgmentAsync(ContentSimilarityJudgmentDecision.NotRelated), () => SelectedContentSimilarityEvidence is not null);
        IgnoreContentSimilarityCommand = new AsyncCommand(() => SetContentSimilarityJudgmentAsync(ContentSimilarityJudgmentDecision.Ignored), () => SelectedContentSimilarityEvidence is not null);
        ResetContentSimilarityJudgmentCommand = new AsyncCommand(() => SetContentSimilarityJudgmentAsync(ContentSimilarityJudgmentDecision.Unreviewed), () => SelectedContentSimilarityEvidence is not null);
        PinContentSimilarityMemberCommand = new AsyncCommand(PinContentSimilarityMemberAsync, () => SelectedContentSimilarityMember is not null);
        PreviousTagReviewPageCommand = new AsyncCommand(() => MoveTagReviewPageAsync(-1), () => !IsBusy && HasPreviousTagReviewPage);
        NextTagReviewPageCommand = new AsyncCommand(() => MoveTagReviewPageAsync(1), () => !IsBusy && HasNextTagReviewPage);
        ConfirmTagCommand = new AsyncCommand(() => SetTagJudgmentAsync(MediaTagJudgmentDecision.Confirmed), () => SelectedTagReview is not null);
        RejectTagCommand = new AsyncCommand(() => SetTagJudgmentAsync(MediaTagJudgmentDecision.Rejected), () => SelectedTagReview is not null);
        IgnoreTagCommand = new AsyncCommand(() => SetTagJudgmentAsync(MediaTagJudgmentDecision.Ignored), () => SelectedTagReview is not null);
        ResetTagJudgmentCommand = new AsyncCommand(() => SetTagJudgmentAsync(MediaTagJudgmentDecision.Unreviewed), () => SelectedTagReview is not null);
        AddManualTagCommand = new AsyncCommand(AddManualTagAsync, () => SelectedTagReview is not null && !string.IsNullOrWhiteSpace(ManualTagText));
        RemoveManualTagCommand = new AsyncCommand(RemoveManualTagAsync, () => SelectedTagReview is not null && !string.IsNullOrWhiteSpace(ManualTagText));
        BulkConfirmTagsCommand = new AsyncCommand(BulkConfirmTagsAsync, () => !IsBusy);
        ApplyTagFilterCommand = new AsyncCommand(ApplyTagFilterAsync, () => !IsBusy);
        RefreshUnifiedReviewCommand = new AsyncCommand(() => LoadUnifiedReviewAsync(restoreAnchor: false), () => !IsBusy);
        PreviousUnifiedReviewPageCommand = new AsyncCommand(() => MoveUnifiedReviewPageAsync(-1), () => !IsBusy && HasPreviousUnifiedReviewPage);
        NextUnifiedReviewPageCommand = new AsyncCommand(() => MoveUnifiedReviewPageAsync(1), () => !IsBusy && HasNextUnifiedReviewPage);
        ConfirmUnifiedReviewCommand = new AsyncCommand(() => ApplySelectedUnifiedDecisionAsync(UnifiedReviewAction.ConfirmDuplicate), () => CanApplyUnifiedAction(UnifiedReviewAction.ConfirmDuplicate));
        ConfirmNearUnifiedReviewCommand = new AsyncCommand(() => ApplySelectedUnifiedDecisionAsync(UnifiedReviewAction.ConfirmNearDuplicate), () => CanApplyUnifiedAction(UnifiedReviewAction.ConfirmNearDuplicate));
        KeepBothUnifiedReviewCommand = new AsyncCommand(() => ApplySelectedUnifiedDecisionAsync(UnifiedReviewAction.KeepBoth), () => CanApplyUnifiedAction(UnifiedReviewAction.KeepBoth));
        IgnoreUnifiedReviewCommand = new AsyncCommand(() => ApplySelectedUnifiedDecisionAsync(UnifiedReviewAction.Ignore), () => CanApplyUnifiedAction(UnifiedReviewAction.Ignore));
        NotDuplicateUnifiedReviewCommand = new AsyncCommand(() => ApplySelectedUnifiedDecisionAsync(UnifiedReviewAction.NotDuplicate), () => CanApplyUnifiedAction(UnifiedReviewAction.NotDuplicate));
        RelatedUnifiedReviewCommand = new AsyncCommand(() => ApplySelectedUnifiedDecisionAsync(UnifiedReviewAction.Related), () => CanApplyUnifiedAction(UnifiedReviewAction.Related));
        NotRelatedUnifiedReviewCommand = new AsyncCommand(() => ApplySelectedUnifiedDecisionAsync(UnifiedReviewAction.NotRelated), () => CanApplyUnifiedAction(UnifiedReviewAction.NotRelated));
        ConfirmTagUnifiedReviewCommand = new AsyncCommand(() => ApplySelectedUnifiedDecisionAsync(UnifiedReviewAction.ConfirmTag), () => CanApplyUnifiedAction(UnifiedReviewAction.ConfirmTag));
        RejectTagUnifiedReviewCommand = new AsyncCommand(() => ApplySelectedUnifiedDecisionAsync(UnifiedReviewAction.RejectTag), () => CanApplyUnifiedAction(UnifiedReviewAction.RejectTag));
        ResetUnifiedReviewCommand = new AsyncCommand(() => ApplySelectedUnifiedDecisionAsync(UnifiedReviewAction.ResetToUnreviewed), () => CanApplyUnifiedAction(UnifiedReviewAction.ResetToUnreviewed));
        BulkUnifiedReviewCommand = new AsyncCommand(BulkApplyUnifiedReviewAsync, CanBulkApplyUnifiedReview);
        CreateOperationDryRunCommand = new AsyncCommand(() => RunOperationActionAsync(CreateOperationDryRunAsync, recreateSingle: true), CanCreateOperationDryRun);
        HandoffLibraryMediaCommand = new AsyncCommand(async () => { if (SelectedMedia is { } media) await HandoffMediaToOperationAsync(media.MediaFileId, "Library explicit handoff"); }, () => OperationEditorsEnabled && SelectedMedia is not null);
        HandoffReviewPrimaryMediaCommand = new AsyncCommand(async () => { if (SelectedUnifiedReview is { Item.PrimaryMediaFileId: { } id } review) await HandoffMediaToOperationAsync(id, "Review primary explicit handoff", review.ReviewItemId); }, () => OperationEditorsEnabled && SelectedUnifiedReview?.Item.PrimaryMediaFileId is not null);
        HandoffReviewSecondaryMediaCommand = new AsyncCommand(async () => { if (SelectedUnifiedReview?.Item.SecondaryMediaFileId is { } id) await HandoffMediaToOperationAsync(id, "Review secondary explicit handoff", SelectedUnifiedReview.ReviewItemId); }, () => OperationEditorsEnabled && SelectedUnifiedReview?.Item.SecondaryMediaFileId is not null);
        HandoffExactMemberCommand = new AsyncCommand(async () => { if (SelectedDuplicateMember is { } member) await HandoffMediaToOperationAsync(member.Media.MediaFileId, "Exact member explicit handoff"); }, () => OperationEditorsEnabled && SelectedDuplicateMember is not null);
        HandoffNearMemberCommand = new AsyncCommand(async () => { if (SelectedNearDuplicateMember is { } member) await HandoffMediaToOperationAsync(member.Media.MediaFileId, "Near member explicit handoff"); }, () => OperationEditorsEnabled && SelectedNearDuplicateMember is not null);
        HandoffContentMemberCommand = new AsyncCommand(async () => { if (SelectedContentSimilarityMember is { } member) await HandoffMediaToOperationAsync(member.Media.MediaFileId, "Content member explicit handoff"); }, () => OperationEditorsEnabled && SelectedContentSimilarityMember is not null);
        HandoffSuggestedTagMediaCommand = new AsyncCommand(async () => { if (SelectedTagReview is { } tag) await HandoffMediaToOperationAsync(tag.Evidence.MediaFileId, "Suggested Tag media explicit handoff"); }, () => OperationEditorsEnabled && SelectedTagReview is not null);
        AcknowledgeOperationDryRunCommand = new AsyncCommand(() => RunOperationActionAsync(AcknowledgeOperationDryRunAsync), CanAcknowledgeOperationDryRun);
        ConfirmOperationFinalExecutionCommand = new AsyncCommand(() => RunOperationActionAsync(ConfirmOperationFinalExecutionAsync), CanConfirmOperationFinalExecution);
        ExecuteProductionSameVolumeOperationCommand = new AsyncCommand(() => RunOperationActionAsync(ExecuteProductionSameVolumeOperationAsync, executing: true), CanExecuteProductionSameVolumeOperation);
        ExecuteProductionCrossVolumeMoveOperationCommand = new AsyncCommand(() => RunOperationActionAsync(ExecuteProductionCrossVolumeMoveOperationAsync, executing: true), CanExecuteProductionCrossVolumeMoveOperation);
        ExecuteProductionRecycleOperationCommand = new AsyncCommand(() => RunOperationActionAsync(ExecuteProductionRecycleOperationAsync, executing: true), CanExecuteProductionRecycleOperation);
        ExecuteProductionPermanentDeleteOperationCommand = new AsyncCommand(() => RunOperationActionAsync(ExecuteProductionPermanentDeleteOperationAsync, executing: true), CanExecuteProductionPermanentDeleteOperation);
        AddSelectedToControlledBatchCommand = new AsyncCommand(AddSelectedToControlledBatchAsync, CanAddSelectedToControlledBatch);
        RemoveSelectedFromControlledBatchCommand = new AsyncCommand(RemoveSelectedFromControlledBatchAsync, () => OperationEditorsEnabled && ControlledBatchSelectedItem is not null);
        ClearControlledBatchCommand = new AsyncCommand(ClearControlledBatchAsync, () => OperationEditorsEnabled && ControlledBatchSelection.Count > 0);
        CreateControlledBatchDryRunCommand = new AsyncCommand(() => RunOperationActionAsync(CreateControlledBatchDryRunAsync, recreateBatch: true, batchAction: true), CanCreateControlledBatchDryRun);
        AcknowledgeControlledBatchDryRunCommand = new AsyncCommand(() => RunOperationActionAsync(AcknowledgeControlledBatchDryRunAsync, batchAction: true), CanAcknowledgeControlledBatchDryRun);
        ConfirmControlledBatchFinalExecutionCommand = new AsyncCommand(() => RunOperationActionAsync(ConfirmControlledBatchFinalExecutionAsync, batchAction: true), CanConfirmControlledBatchFinalExecution);
        ExecuteControlledBatchCommand = new AsyncCommand(() => RunOperationActionAsync(ExecuteControlledBatchAsync, executing: true, batchAction: true), CanExecuteControlledBatch);
        CancelControlledBatchCommand = new AsyncCommand(CancelControlledBatchAsync, CanCancelControlledBatch);
        ProcessingControlCommand = new AsyncCommand(ToggleProcessingAsync, CanToggleProcessing, allowConcurrentExecutions: true);
    }

    public ObservableCollection<LibraryRoot> Roots { get; } = [];
    public ObservableCollection<MediaListItem> MediaItems { get; } = [];
    public ObservableCollection<Job> RecentJobs { get; } = [];
    public ObservableCollection<Job> ActiveJobs { get; } = [];
    public ObservableCollection<JobIssuePresentation> JobIssues { get; } = [];
    public ObservableCollection<JobEventPresentation> SelectedJobEvents { get; } = [];
    public ObservableCollection<SemanticSearchCandidate> SemanticResults { get; } = [];
    public ObservableCollection<DuplicateGroup> DuplicateGroups { get; } = [];
    public ObservableCollection<DuplicateComparisonMember> DuplicateMembers { get; } = [];
    public ObservableCollection<NearDuplicateComparisonMember> NearDuplicateMembers { get; } = [];
    public ObservableCollection<NearFrameEvidenceItem> NearDuplicateFrameEvidence { get; } = [];
    public ObservableCollection<ContentSimilarityEvidence> ContentSimilarityEdges { get; } = [];
    public ObservableCollection<ContentSimilarityComparisonMember> ContentSimilarityMembers { get; } = [];
    public ObservableCollection<TagReviewRow> TagReviewItems { get; } = [];
    public ObservableCollection<UnifiedReviewRow> UnifiedReviewItems { get; } = [];
    private readonly ObservableCollection<MediaListItem> _controlledBatchSelection = [];
    public ReadOnlyObservableCollection<MediaListItem> ControlledBatchSelection { get; }
    public ObservableCollection<ControlledBatchItemPresentation> ControlledBatchReviewItems { get; } = [];
    public AsyncCommand RegisterRootCommand { get; }
    public AsyncCommand RescanCommand { get; }
    public AsyncCommand UnregisterRootCommand { get; }
    public AsyncCommand SaveRetentionSettingsCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand WorkerDiagnosticCommand { get; }
    public AsyncCommand RescanSelectedIssueRootCommand { get; }
    public AsyncCommand SemanticSearchCommand { get; }
    public AsyncCommand CancelSemanticSearchCommand { get; }
    public AsyncCommand PreviousPageCommand { get; }
    public AsyncCommand NextPageCommand { get; }
    public AsyncCommand SelectMediaForPreviewCommand { get; }
    public AsyncCommand OpenPreviewCommand { get; }
    public AsyncCommand ClosePreviewCommand { get; }
    public AsyncCommand SeekPreviewCommand { get; }
    public AsyncCommand TogglePreviewPlaybackCommand { get; }
    public AsyncCommand PreviousDuplicateGroupPageCommand { get; }
    public AsyncCommand NextDuplicateGroupPageCommand { get; }
    public AsyncCommand PreviousContentSimilarityPageCommand { get; }
    public AsyncCommand NextContentSimilarityPageCommand { get; }
    public AsyncCommand ConfirmDuplicateGroupCommand { get; }
    public AsyncCommand KeepBothDuplicateGroupCommand { get; }
    public AsyncCommand IgnoreDuplicateGroupCommand { get; }
    public AsyncCommand NotDuplicateGroupCommand { get; }
    public AsyncCommand ResetDuplicateGroupJudgmentCommand { get; }
    public AsyncCommand SetPreferredDuplicateMemberCommand { get; }
    public AsyncCommand PinDuplicateMemberCommand { get; }
    public AsyncCommand RelatedContentSimilarityCommand { get; }
    public AsyncCommand KeepBothContentSimilarityCommand { get; }
    public AsyncCommand NotRelatedContentSimilarityCommand { get; }
    public AsyncCommand IgnoreContentSimilarityCommand { get; }
    public AsyncCommand ResetContentSimilarityJudgmentCommand { get; }
    public AsyncCommand PinContentSimilarityMemberCommand { get; }
    public AsyncCommand PreviousTagReviewPageCommand { get; }
    public AsyncCommand NextTagReviewPageCommand { get; }
    public AsyncCommand ConfirmTagCommand { get; }
    public AsyncCommand RejectTagCommand { get; }
    public AsyncCommand IgnoreTagCommand { get; }
    public AsyncCommand ResetTagJudgmentCommand { get; }
    public AsyncCommand AddManualTagCommand { get; }
    public AsyncCommand RemoveManualTagCommand { get; }
    public AsyncCommand BulkConfirmTagsCommand { get; }
    public AsyncCommand ApplyTagFilterCommand { get; }
    public AsyncCommand RefreshUnifiedReviewCommand { get; }
    public AsyncCommand PreviousUnifiedReviewPageCommand { get; }
    public AsyncCommand NextUnifiedReviewPageCommand { get; }
    public AsyncCommand ConfirmUnifiedReviewCommand { get; }
    public AsyncCommand ConfirmNearUnifiedReviewCommand { get; }
    public AsyncCommand KeepBothUnifiedReviewCommand { get; }
    public AsyncCommand IgnoreUnifiedReviewCommand { get; }
    public AsyncCommand NotDuplicateUnifiedReviewCommand { get; }
    public AsyncCommand RelatedUnifiedReviewCommand { get; }
    public AsyncCommand NotRelatedUnifiedReviewCommand { get; }
    public AsyncCommand ConfirmTagUnifiedReviewCommand { get; }
    public AsyncCommand RejectTagUnifiedReviewCommand { get; }
    public AsyncCommand ResetUnifiedReviewCommand { get; }
    public AsyncCommand BulkUnifiedReviewCommand { get; }
    public AsyncCommand CreateOperationDryRunCommand { get; }
    public AsyncCommand HandoffLibraryMediaCommand { get; }
    public AsyncCommand HandoffReviewPrimaryMediaCommand { get; }
    public AsyncCommand HandoffReviewSecondaryMediaCommand { get; }
    public AsyncCommand HandoffExactMemberCommand { get; }
    public AsyncCommand HandoffNearMemberCommand { get; }
    public AsyncCommand HandoffContentMemberCommand { get; }
    public AsyncCommand HandoffSuggestedTagMediaCommand { get; }
    public AsyncCommand AcknowledgeOperationDryRunCommand { get; }
    public AsyncCommand ConfirmOperationFinalExecutionCommand { get; }
    public AsyncCommand ExecuteProductionSameVolumeOperationCommand { get; }
    public AsyncCommand ExecuteProductionCrossVolumeMoveOperationCommand { get; }
    public AsyncCommand ExecuteProductionRecycleOperationCommand { get; }
    public AsyncCommand ExecuteProductionPermanentDeleteOperationCommand { get; }
    public AsyncCommand AddSelectedToControlledBatchCommand { get; }
    public AsyncCommand RemoveSelectedFromControlledBatchCommand { get; }
    public AsyncCommand ClearControlledBatchCommand { get; }
    public AsyncCommand CreateControlledBatchDryRunCommand { get; }
    public AsyncCommand AcknowledgeControlledBatchDryRunCommand { get; }
    public AsyncCommand ConfirmControlledBatchFinalExecutionCommand { get; }
    public AsyncCommand ExecuteControlledBatchCommand { get; }
    public AsyncCommand CancelControlledBatchCommand { get; }
    public AsyncCommand ProcessingControlCommand { get; }

    public string FileRetentionText { get => _fileRetentionText; set => Set(ref _fileRetentionText, value); }
    public string FolderRetentionText { get => _folderRetentionText; set => Set(ref _folderRetentionText, value); }
    public string DriveRetentionText { get => _driveRetentionText; set => Set(ref _driveRetentionText, value); }

    public ProcessingRunState PipelineRunState
    {
        get => _processingRunState;
        private set
        {
            if (!Set(ref _processingRunState, value)) return;
            Raise(nameof(ProcessingControlLabel));
            Raise(nameof(ProcessingLifecycleStatus));
            ProcessingControlCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsShuttingDown
    {
        get => _isShuttingDown;
        private set
        {
            if (!Set(ref _isShuttingDown, value)) return;
            Raise(nameof(IsInteractionEnabled));
            ProcessingControlCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsInteractionEnabled => !IsShuttingDown;

    public string ProcessingControlLabel => PipelineRunState == ProcessingRunState.UserStopped ? L.T("処理を開始") : L.T("処理を停止");
    public string ProcessingLifecycleStatus => PipelineRunState switch
    {
        ProcessingRunState.ShuttingDown => L.T("終了処理中 — 開始済みの処理を安全に完了しています。"),
        ProcessingRunState.StopRequested or ProcessingRunState.Draining => L.T("停止処理中 — 開始済みの処理を安全に完了しています。"),
        ProcessingRunState.UserStopped => L.T("処理は停止しています。未開始の処理は保持されています。"),
        ProcessingRunState.Running => L.T("処理中"),
        _ => L.T("処理待機中")
    };

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value)) return;
            ResetToFirstPageAndClearSelection();
            _ = SearchAsync();
        }
    }

    public string SemanticSearchText
    {
        get => _semanticSearchText;
        set
        {
            if (!Set(ref _semanticSearchText, value)) return;
            SemanticSearchCommand.RaiseCanExecuteChanged();
            NotifyAiSearchPresentation();
        }
    }

    public string SemanticSearchStatus { get => _semanticSearchStatus; private set => Set(ref _semanticSearchStatus, value); }
    public SemanticSearchCandidate? SelectedSemanticResult
    {
        get => _selectedSemanticResult;
        set
        {
            if (!Set(ref _selectedSemanticResult, value)) return;
            SetPreviewTarget(value is null
                ? null
                : new PreviewTarget(value.Path, value.TimestampSeconds, "Matched timestamp", L.T("Matched frameとbest matched sampleを確認できます。Open プレビューで動画をread-only再生します。")));
            Raise(nameof(SelectedSemanticResultFileName));
            Raise(nameof(SelectedSemanticResultPath));
            Raise(nameof(SelectedSemanticResultTimestamp));
            Raise(nameof(SelectedSemanticResultScore));
            Raise(nameof(SelectedSemanticResultFrame));
            UpdateSelectedSemanticHumanMetadata(value?.MediaFileId);
            RaisePreviewCommands();
            AiSearchLocalFeedback = string.Empty;
            NotifyAiSearchPresentation();
        }
    }
    public string SelectedSemanticResultFileName => SelectedSemanticResult?.FileName ?? L.T("検索結果を選択してください。");
    public string SelectedSemanticResultPath => SelectedSemanticResult?.Path ?? string.Empty;
    public string SelectedSemanticResultTimestamp => SelectedSemanticResult?.TimestampDisplay ?? "Matched timestamp unavailable";
    public string SelectedSemanticResultScore => SelectedSemanticResult?.ScoreDisplay ?? "Cosine Similarity unavailable";
    public string? SelectedSemanticResultFrame => SelectedSemanticResult?.MatchedFrameRelativePath;
    public string SelectedSemanticHumanMetadata { get => _selectedSemanticHumanMetadata; private set => Set(ref _selectedSemanticHumanMetadata, value); }
    public double PreviewTimestampSeconds => _previewTarget?.TimestampSeconds ?? 0d;
    public string? PreviewMediaPath => IsPreviewOpen && IsPreviewAvailable ? _previewTarget?.Path : null;
    public long PreviewSeekRequestId => _previewSeekRequestId;
    public long PreviewPlaybackRequestId => _previewPlaybackRequestId;
    public bool PreviewPlaybackRequested => _previewPlaybackRequested;
    public string PreviewStatus { get => _previewStatus; private set => Set(ref _previewStatus, value); }
    public bool IsPreviewBusy
    {
        get => _isPreviewBusy;
        private set
        {
            if (!Set(ref _isPreviewBusy, value)) return;
            RaisePreviewCommands();
        }
    }
    public bool IsPreviewOpen
    {
        get => _isPreviewOpen;
        private set
        {
            if (!Set(ref _isPreviewOpen, value)) return;
            Raise(nameof(PreviewMediaPath));
            RaisePreviewCommands();
        }
    }
    public bool IsPreviewAvailable
    {
        get => _isPreviewAvailable;
        private set
        {
            if (!Set(ref _isPreviewAvailable, value)) return;
            Raise(nameof(PreviewMediaPath));
            RaisePreviewCommands();
        }
    }
    public bool IsSemanticSearchBusy
    {
        get => _isSemanticSearchBusy;
        private set
        {
            if (!Set(ref _isSemanticSearchBusy, value)) return;
            CancelSemanticSearchCommand.RaiseCanExecuteChanged();
        }
    }
    public string SemanticModelStatusText => $"{_semanticSearch.SemanticModel.TechnicalStatusDisplay} — {_semanticSearch.SemanticModel.TechnicalApprovalStatusDisplay} — {_semanticSearch.SemanticModel.DistributionStatusDisplay}";
    public string SemanticExecutionStatusText => _semanticSearch.SemanticExecution.IsCpuFallback
        ? SemanticExecutionProfile.CpuFallbackDisplay
        : $"GPU candidate profile — {_semanticSearch.SemanticExecution.Dtype}";

    public LibraryRoot? SelectedRoot
    {
        get => _selectedRoot;
        set
        {
            if (!Set(ref _selectedRoot, value)) return;
            RescanCommand.RaiseCanExecuteChanged();
            UnregisterRootCommand.RaiseCanExecuteChanged();
            NotifyLibraryPresentation();
            RaiseReadiness();
        }
    }

    public IReadOnlyList<LibraryScopePresentation> LibraryScopeOptions { get; private set; } = [LibraryScopePresentation.All];
    public LibraryScopePresentation CurrentLibraryScope
    {
        get => _currentLibraryScope;
        set
        {
            if (value is null || (value.LibraryRootId is { } id && !Roots.Any(root => root.LibraryRootId == id && root.RegistrationState == RootRegistrationState.Active))) return;
            if (_currentLibraryScope.LibraryRootId == value.LibraryRootId) return;
            _currentLibraryScope = value;
            Raise();
            _scopeMediaSelectionAnchor = _selectedMediaFileId ?? _scopeMediaSelectionAnchor;
            SelectedMedia = null; MediaItems.Clear();
            Interlocked.Increment(ref _scopeVersion);
            Interlocked.Increment(ref _unifiedReviewRequestId);
            _unifiedReviewCts?.Cancel();
            Interlocked.Increment(ref _semanticSearchRequestId);
            _semanticSearchCts?.Cancel();
            IsSemanticSearchBusy = false;
            SemanticResults.Clear();
            SelectedSemanticResult = null;
            _semanticTagSummaries.Clear();
            SemanticSearchStatus = L.T("対象範囲が変更されました。新しい範囲で検索を実行してください。");
            AiSearchLocalFeedback = string.Empty;
            AiSearchState = AiSearchState == AiSearchViewState.BeforeSearch ? AiSearchViewState.BeforeSearch : AiSearchViewState.ScopeChanged;
            CurrentPage = 0;
            CurrentDuplicateGroupPage = 0;
            CurrentContentSimilarityPage = 0;
            CurrentTagReviewPage = 0;
            CurrentUnifiedReviewPage = 0;
            SelectedDuplicateGroup = null;
            SelectedContentSimilarityEvidence = null;
            SelectedTagReview = null;
            SelectedUnifiedReview = null;
            SetPreviewTarget(null);
            DuplicateGroups.Clear();
            ContentSimilarityEdges.Clear();
            TagReviewItems.Clear();
            UnifiedReviewItems.Clear();
            ScopeRefreshTask = RefreshScopeAsync();
        }
    }
    public Task ScopeRefreshTask { get; private set; } = Task.CompletedTask;

    private async Task RefreshScopeAsync()
    {
        var version = Volatile.Read(ref _scopeVersion);
        await SearchAsync();
        if (version != Volatile.Read(ref _scopeVersion)) return;
        await LoadDuplicateGroupsAsync(_lifetime.Token);
        if (version != Volatile.Read(ref _scopeVersion)) return;
        await LoadContentSimilarityEvidenceAsync(_lifetime.Token);
        if (version != Volatile.Read(ref _scopeVersion)) return;
        await LoadTagReviewAsync(_lifetime.Token);
        if (version != Volatile.Read(ref _scopeVersion)) return;
        await LoadUnifiedReviewAsync(restoreAnchor: false);
    }

    private void SetLibraryViewSelection(MediaListItem? value)
    {
        SelectedMedia = value;
    }

    public MediaListItem? SelectedMedia
    {
        get => _selectedMedia;
        set
        {

            if (Set(ref _selectedMedia, value))
            {
                // Stable ID, never the DataGrid row index or path, defines the current visible selection.
                _selectedMediaFileId = value?.MediaFileId;
                Raise(nameof(SelectedMediaFileId));
                Raise(nameof(OperationPlanningSelectedCount));
                Raise(nameof(OperationPlanningSelectedSource));
                Raise(nameof(OperationSafety));
                RaiseOperationPlanningCommands();
                SelectMediaForPreviewCommand.RaiseCanExecuteChanged();
                HandoffLibraryMediaCommand.RaiseCanExecuteChanged();
                AddSelectedToControlledBatchCommand.RaiseCanExecuteChanged();
                NotifyLibraryPresentation();
            }
        }
    }

    public Guid? SelectedMediaFileId => _selectedMediaFileId;
    public ControlledBatchItemPresentation? ControlledBatchSelectedItem
    {
        get => _selectedControlledBatchItem;
        set
        {
            if (!Set(ref _selectedControlledBatchItem, value)) return;
            RemoveSelectedFromControlledBatchCommand.RaiseCanExecuteChanged();
            _ = LoadControlledBatchSelectedItemAuditAsync(value);
        }
    }
    public IReadOnlyList<FileOperationType> ControlledBatchTypes { get; } = [FileOperationType.Move, FileOperationType.Rename, FileOperationType.Recycle];
    public FileOperationType ControlledBatchType { get => _controlledBatchType; set { if (_controlledBatchType != value) RequestOperationInputChange(() => _controlledBatchType = value, nameof(ControlledBatchType), batch: true); } }
    public string ControlledBatchDestinationDirectory { get => _controlledBatchDestinationDirectory; set { if (_controlledBatchDestinationDirectory != value) RequestOperationInputChange(() => _controlledBatchDestinationDirectory = value, nameof(ControlledBatchDestinationDirectory), batch: true); } }
    public bool ControlledBatchAcknowledgement { get => _controlledBatchAcknowledgement; set { if (Set(ref _controlledBatchAcknowledgement, value)) { RaiseControlledBatchCommands(); Raise(nameof(ControlledBatchSafety)); } } }
    public bool ControlledBatchFinalConfirmation { get => _controlledBatchFinalConfirmation; set { if (Set(ref _controlledBatchFinalConfirmation, value)) ConfirmControlledBatchFinalExecutionCommand.RaiseCanExecuteChanged(); } }
    public BatchOperationDryRun? CurrentControlledBatchDryRun
    {
        get => _currentControlledBatchDryRun;
        private set
        {
            if (!Set(ref _currentControlledBatchDryRun, value)) return;
            Raise(nameof(ControlledBatchItemStateSummary));
            Raise(nameof(ControlledBatchSafety));
            Raise(nameof(ControlledBatchFinalConfirmationEnabled));
            RefreshControlledBatchReviewItems();
            RaiseControlledBatchCommands();
        }
    }
    public string ControlledBatchItemStateSummary => CurrentControlledBatchDryRun is not { } dry ? "Per-item state: no frozen batch plan. The review list is not execution authority." : string.Join(Environment.NewLine, dry.Items.OrderBy(item => item.Sequence).Select(item => $"#{item.Sequence + 1}: {System.IO.Path.GetFileName(item.SourcePath)} — {item.State}; plan={item.PlanState}; revalidation={item.RevalidationState ?? "not recorded"}; error={item.ErrorCode ?? "none"}; recovery={item.RecoveryState ?? "none"}"));
    public int ControlledBatchSelectionCount => ControlledBatchSelection.Count;
    public string ControlledBatchStatus { get => _controlledBatchStatus; private set => Set(ref _controlledBatchStatus, value); }
    public string ControlledBatchSelectedItemAudit { get => _controlledBatchSelectedItemAudit; private set => Set(ref _controlledBatchSelectedItemAudit, value); }
    public ControlledBatchSafetyPresentation ControlledBatchSafety => ControlledBatchSafetyPresentation.Evaluate(new(
        ControlledBatchType,
        ControlledBatchSelectionCount,
        CurrentControlledBatchDryRun?.Batch.State,
        _controlledBatchAcknowledgementRecorded,
        CurrentControlledBatchDryRun?.Batch.CancellationRequested ?? false,
        ControlledBatchReviewItems,
        CurrentControlledBatchDryRun?.WarningText ?? "No Batch plan has been created. The review list is not filesystem authority."));
    public bool ControlledBatchFinalConfirmationEnabled => OperationEditorsEnabled && ControlledBatchSafety.CanOfferFinalConfirmation;

    public DuplicateGroup? SelectedDuplicateGroup
    {
        get => _selectedDuplicateGroup;
        set
        {
            if (!Set(ref _selectedDuplicateGroup, value)) return;
            if (value is not null && _selectedContentSimilarityEvidence is not null) SelectedContentSimilarityEvidence = null;
            _pinnedDuplicateMember = null;
            Raise(nameof(PinnedDuplicateMember));
            DuplicateMembers.Clear();
            NearDuplicateMembers.Clear();
            NearDuplicateFrameEvidence.Clear();
            SelectedDuplicateMember = null;
            SelectedNearDuplicateMember = null;
            DuplicateJudgmentStatus = value is null
                ? L.T("ExactまたはNear Duplicate groupを選択すると、relation別evidenceと人間の判断を確認できます。")
                : value.RelationType == DuplicateRelationType.Exact
                    ? L.F("SHA-256 verified Exact group: {0}。Exactは暗号学的hash一致のみを意味します。", value.EvidenceDigest)
                    : L.F("Near Duplicate candidate: {0} / {1}。視覚evidenceとhuman reviewを確認してください。", value.EvidenceAlgorithm, value.ProcessingVersion);
            RaiseDuplicateJudgmentCommands();
            if (value is not null)
            {
                if (value.RelationType == DuplicateRelationType.Exact) _ = LoadDuplicateMembersAsync(value.DuplicateGroupId, _lifetime.Token);
                else if (value.RelationType == DuplicateRelationType.NearDuplicate) _ = LoadNearDuplicateMembersAsync(value.DuplicateGroupId, _lifetime.Token);
            }
        }
    }
    public DuplicateComparisonMember? SelectedDuplicateMember
    {
        get => _selectedDuplicateMember;
        set
        {
            if (!Set(ref _selectedDuplicateMember, value)) return;
            HandoffExactMemberCommand.RaiseCanExecuteChanged();
            SetPreviewTarget(value is null ? null : new PreviewTarget(value.Media.Path, 0d, "Exact duplicate preview", L.T("Exact duplicate verified by SHA-256. Original Mediaをread-onlyで確認します。")));
            Raise(nameof(SelectedDuplicateMemberFileName));
            Raise(nameof(SelectedDuplicateMemberEvidence));
            Raise(nameof(SelectedDuplicateMemberPath));
            Raise(nameof(SelectedDuplicateMemberMetadata));
            RaiseDuplicateJudgmentCommands();
        }
    }
    public DuplicateComparisonMember? PinnedDuplicateMember => _pinnedDuplicateMember;
    public NearDuplicateComparisonMember? SelectedNearDuplicateMember
    {
        get => _selectedNearDuplicateMember;
        set
        {
            if (!Set(ref _selectedNearDuplicateMember, value)) return;
            HandoffNearMemberCommand.RaiseCanExecuteChanged();
            SetPreviewTarget(value is null ? null : new PreviewTarget(value.Media.Path, 0d, "Near Duplicate preview", L.T("Near Duplicateは複数frameのperceptual evidenceによる候補です。Original Mediaをread-onlyで確認します。")));
            Raise(nameof(SelectedNearDuplicateMemberFileName));
            Raise(nameof(SelectedNearDuplicateMemberPath));
            Raise(nameof(SelectedNearDuplicateMemberEvidence));
            Raise(nameof(SelectedNearDuplicateMemberMetadata));
            RaiseDuplicateJudgmentCommands();
        }
    }
    public NearDuplicateComparisonMember? PinnedNearDuplicateMember => _pinnedNearDuplicateMember;
    public string SelectedDuplicateMemberFileName => SelectedDuplicateMember?.Media.FileName ?? L.T("memberを選択してください。");
    public string SelectedDuplicateMemberPath => SelectedDuplicateMember?.Media.Path ?? string.Empty;
    public string SelectedDuplicateMemberEvidence => SelectedDuplicateMember?.FullHashStatusDisplay ?? "Full SHA-256 evidence unavailable";
    public string SelectedDuplicateMemberMetadata => SelectedDuplicateMember is { } member ? $"{member.Media.SizeBytes:N0} bytes | {member.DurationDisplay} | {member.Metadata?.ContainerFormat ?? "Container unavailable"} | {member.Metadata?.VideoCodec ?? "Codec unavailable"} | {member.ResolutionDisplay} | {member.Media.CreationTimeUtc:O} / {member.Media.LastWriteTimeUtc:O}" : string.Empty;
    public string PinnedDuplicateMemberDisplay => PinnedDuplicateMember is { } member ? $"Pinned Exact: {member.Media.FileName} | {member.FullHashStatusDisplay} | {member.Media.Path}" : L.T("Exact memberは未ピン留めです。");
    public string SelectedNearDuplicateMemberFileName => SelectedNearDuplicateMember?.Media.FileName ?? L.T("Near Duplicate memberを選択してください。");
    public string SelectedNearDuplicateMemberPath => SelectedNearDuplicateMember?.Media.Path ?? string.Empty;
    public string SelectedNearDuplicateMemberEvidence => SelectedNearDuplicateMember?.VisualEvidenceDisplay ?? "Near visual evidence unavailable";
    public string SelectedNearDuplicateMemberMetadata => SelectedNearDuplicateMember is { } member ? $"{member.Media.SizeBytes:N0} bytes | {member.DurationDisplay} | {member.Metadata?.ContainerFormat ?? "Container unavailable"} | {member.Metadata?.VideoCodec ?? "Codec unavailable"} | {member.ResolutionDisplay} | {member.Media.CreationTimeUtc:O} / {member.Media.LastWriteTimeUtc:O}" : string.Empty;
    public string PinnedNearDuplicateMemberDisplay => PinnedNearDuplicateMember is { } member ? $"Pinned Near: {member.Media.FileName} | {member.VisualEvidenceDisplay} | {member.Media.Path}" : L.T("Near Duplicate memberは未ピン留めです。");

    public ContentSimilarityEvidence? SelectedContentSimilarityEvidence
    {
        get => _selectedContentSimilarityEvidence;
        set
        {
            if (!Set(ref _selectedContentSimilarityEvidence, value)) return;
            if (value is not null && _selectedDuplicateGroup is not null) SelectedDuplicateGroup = null;
            _pinnedContentSimilarityMember = null;
            Raise(nameof(PinnedContentSimilarityMember));
            Raise(nameof(PinnedContentSimilarityMemberDisplay));
        Raise(nameof(PinnedContentSimilarityMemberGeneralDisplay));
            ContentSimilarityMembers.Clear();
            SelectedContentSimilarityMember = null;
            ContentSimilarityJudgmentStatus = value is null
                ? L.T("Content Similar candidateを選択すると、embedding similarity evidenceと人間の判断を確認できます。")
                : L.F("Content Similar candidate: {0} / {1}。これはDuplicateではなく、non-transitive pair evidenceです。", value.SimilarityMetric, value.AggregationVersion);
            RaiseContentSimilarityCommands();
            if (value is not null) _ = LoadContentSimilarityMembersAsync(value.ContentSimilarityEvidenceId, _lifetime.Token);
        }
    }

    public ContentSimilarityComparisonMember? SelectedContentSimilarityMember
    {
        get => _selectedContentSimilarityMember;
        set
        {
            if (!Set(ref _selectedContentSimilarityMember, value)) return;
            HandoffContentMemberCommand.RaiseCanExecuteChanged();
            SetPreviewTarget(value is null ? null : new PreviewTarget(value.Media.Path, value.RepresentativeSample.PresentationTimestampSeconds, "Content Similar representative timestamp", L.T("Content Similar evidenceの代表frame時刻を既存read-only playerで確認します。")));
            Raise(nameof(SelectedContentSimilarityMemberFileName));
            Raise(nameof(SelectedContentSimilarityMemberPath));
            Raise(nameof(SelectedContentSimilarityMemberEvidence));
            Raise(nameof(SelectedContentSimilarityMemberMetadata));
            RaiseContentSimilarityCommands();
        }
    }

    public ContentSimilarityComparisonMember? PinnedContentSimilarityMember => _pinnedContentSimilarityMember;
    public TagReviewRow? SelectedTagReview
    {
        get => _selectedTagReview;
        set
        {
            if (!Set(ref _selectedTagReview, value)) return;
            HandoffSuggestedTagMediaCommand.RaiseCanExecuteChanged();
            TagJudgmentNote = value?.Item.JudgmentNote ?? string.Empty;
            SetPreviewTarget(value is null ? null : new PreviewTarget(value.Path, value.Item.RepresentativeTimestampSeconds ?? 0d, "Suggested Tag representative timestamp", L.T("Suggested Tag evidenceの代表frame時刻を既存read-only playerで確認します。")));
            TagReviewStatus = value is null
                ? L.T("Suggested Tagを選択すると、AI evidenceとhuman reviewを確認できます。")
                : L.F("{0}: {1}。Suggested TagはHuman-confirmed Tagではありません。", value.DisplayName, value.ScoreDisplay);
            RaiseTagCommands();
        }
    }
    public UnifiedReviewRow? SelectedUnifiedReview
    {
        get => _selectedUnifiedReview;
        set
        {
            if (!Set(ref _selectedUnifiedReview, value)) return;
            SetPreviewTarget(null);
            SelectReviewEvidence(value);
            UnifiedReviewNote = value?.HumanNote ?? string.Empty;
            UnifiedReviewStatus = value is null
                ? L.T("Unified Review rowを選択すると、relation別evidence・source availability・human decisionを確認できます。")
                : L.F("{0} | {1} | Human decision: {2}。{3}", value.ReviewTypeDisplay, value.EvidenceStateDisplay, value.HumanDecision, value.PriorityReason);
            RaiseUnifiedReviewCommands();
            HandoffReviewPrimaryMediaCommand.RaiseCanExecuteChanged();
            HandoffReviewSecondaryMediaCommand.RaiseCanExecuteChanged();
        }
    }
    public UnifiedReviewFilter UnifiedReviewFilter
    {
        get => _unifiedReviewFilter;
        set
        {
            if (!Set(ref _unifiedReviewFilter, value)) return;
            SelectedUnifiedReview = null;
            UnifiedReviewItems.Clear();
            CurrentUnifiedReviewPage = 0;
            _ = LoadUnifiedReviewAsync(restoreAnchor: false);
        }
    }
    public UnifiedReviewSort UnifiedReviewSort
    {
        get => _unifiedReviewSort;
        set
        {
            if (!Set(ref _unifiedReviewSort, value)) return;
            CurrentUnifiedReviewPage = 0;
            _ = LoadUnifiedReviewAsync(restoreAnchor: false);
        }
    }
    public string UnifiedReviewNote { get => _unifiedReviewNote; set => Set(ref _unifiedReviewNote, value); }
    public string UnifiedReviewStatus { get => _unifiedReviewStatus; private set => Set(ref _unifiedReviewStatus, value); }
    public IReadOnlyList<FileOperationType> OperationPlanningTypes { get; } = Enum.GetValues<FileOperationType>();
    public FileOperationType? OperationPlanningType
    {
        get => _operationPlanningType;
        set { if (_operationPlanningType != value) RequestOperationInputChange(() => _operationPlanningType = value, nameof(OperationPlanningType), batch: false); }
    }
    public string OperationDestinationPath
    {
        get => _operationDestinationPath;
        set { if (_operationDestinationPath != value) RequestOperationInputChange(() => _operationDestinationPath = value, nameof(OperationDestinationPath), batch: false); }
    }
    public bool OperationPlanningAcknowledgement
    {
        get => _operationPlanningAcknowledgement;
        set
        {
            if (value && (!OperationEditorsEnabled || OperationPlanningType is null || CurrentOperationDryRun is null)) { Raise(); return; }
            if (!Set(ref _operationPlanningAcknowledgement, value)) return;
            RaiseOperationPlanningCommands();
        }
    }
    public bool OperationPlanningFinalConfirmation
    {
        get => _operationPlanningFinalConfirmation;
        set
        {
            if (value && !OperationPlanningFinalConfirmationEnabled) { Raise(); return; }
            if (!Set(ref _operationPlanningFinalConfirmation, value)) return;
            RaiseOperationPlanningCommands();
        }
    }
    public FileOperationDryRun? CurrentOperationDryRun
    {
        get => _currentOperationDryRun;
        private set
        {
            if (!Set(ref _currentOperationDryRun, value)) return;
            Raise(nameof(OperationPlanningValidationState));
            Raise(nameof(OperationPlanningSelectedSource));
            Raise(nameof(OperationPlanningWarning));
            Raise(nameof(OperationPlanningRecoverability));
            Raise(nameof(OperationPlanningSummary));
            Raise(nameof(OperationPlanningSameVolumeState));
            Raise(nameof(OperationPlanningCrossVolumeState));
            Raise(nameof(OperationPlanningRecycleState));
            Raise(nameof(OperationPlanningRecycleSnapshot));
            Raise(nameof(OperationPlanningRecycleWarning));
            Raise(nameof(OperationPlanningPermanentDeleteState));
            Raise(nameof(OperationPlanningPermanentDeleteSnapshot));
            Raise(nameof(OperationPlanningPermanentDeleteWarning));
            Raise(nameof(OperationPlanningCrossVolumeProgress));
            Raise(nameof(OperationPlanningCrossVolumeSnapshot));
            Raise(nameof(OperationPlanningFullHashRequirement));
            Raise(nameof(OperationPlanningTemporaryArtifactState));
            Raise(nameof(OperationPlanningSourceRemovalWarning));
            Raise(nameof(OperationPlanningFinalConfirmationState));
            Raise(nameof(OperationPlanningFinalConfirmationInstruction));
            Raise(nameof(OperationPlanningFinalConfirmationEnabled));
            Raise(nameof(OperationPlanningExecutionReadiness));
            Raise(nameof(ProductionOperationExecutionEnabled));
            Raise(nameof(ProductionSameVolumeOperationExecutionEnabled));
            Raise(nameof(ProductionCrossVolumeMoveExecutionEnabled));
            Raise(nameof(ProductionRecycleExecutionEnabled));
            Raise(nameof(ProductionPermanentDeleteExecutionEnabled));
            Raise(nameof(ProductionOperationExecutionDisabledReason));
            Raise(nameof(OperationSafety));
            RaiseOperationPlanningCommands();
        }
    }
    public string OperationPlanningStatus { get => _operationPlanningStatus; private set => Set(ref _operationPlanningStatus, value); }
    public int OperationPlanningSelectedCount => _operationTargetMedia is null ? 0 : 1;
    public string OperationPlanningSelectedSource => CurrentOperationDryRun?.Plan.SourcePath ?? _operationTargetMedia?.Path ?? L.T("ライブラリまたはレビューから「ファイルを整理」で対象を渡してください。");
    public string OperationPlanningValidationState => CurrentOperationDryRun?.Preflight.State.ToString() ?? "Not planned";
    public string OperationPlanningWarning => CurrentOperationDryRun?.WarningText ?? "No plan has been created. No filesystem operation is authorized.";
    public string OperationPlanningRecoverability => CurrentOperationDryRun?.RecoverabilitySummary ?? L.T("Operation plan未作成");
    public string OperationPlanningSameVolumeState => CurrentOperationDryRun is null ? "Same-volume: not validated" : CurrentOperationDryRun.Plan.ProductionSameVolumeCapability && CurrentOperationDryRun.Preflight.SameVolumeVerified ? "Same-volume: verified from Windows volume identity" : "Same-volume: not the active execution capability";
    public string OperationPlanningCrossVolumeState => CurrentOperationDryRun is null ? "Cross-volume: not validated" : CurrentOperationDryRun.Plan.ProductionCrossVolumeMoveCapability && CurrentOperationDryRun.Preflight.CrossVolumeVerified ? "Cross-volume: verified; source and destination are on different Windows volumes" : "Cross-volume: not verified or not an explicit Cross-Volume Move plan; Execute is blocked";
    public string OperationPlanningRecycleState => CurrentOperationDryRun is not { } dry ? "Recycle: not planned" : dry.Plan.ProductionRecycleCapability ? dry.Preflight.State == FileOperationState.Ready ? "Recycle: Windows Recycle Bin capability verified for this explicit single file" : $"Recycle: blocked — {dry.Preflight.Code}" : "Recycle: not the active execution capability";
    public string OperationPlanningRecycleSnapshot => CurrentOperationDryRun is not { } dry || !dry.Plan.ProductionRecycleCapability ? "Recycle snapshot: not applicable" : $"Recycle snapshot: source={dry.Plan.SourcePath}; filename={System.IO.Path.GetFileName(dry.Plan.SourcePath)}; size={dry.Plan.SourceSizeBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes; selected count=1; Windows volume={dry.Plan.SourceVolumeIdentity ?? "unknown"}.";
    public string OperationPlanningRecycleWarning => CurrentOperationDryRun is not { } dry || !dry.Plan.ProductionRecycleCapability ? "Recycle warning: not applicable" : "RECYCLE WARNING: this moves only the displayed single source to the Windows Recycle Bin. It is NOT Permanent Delete. Full SHA-256 is revalidated immediately before Windows Shell execution; source absence is observed before C# lifecycle publish; audit and history are retained.";
    public string OperationPlanningPermanentDeleteState => CurrentOperationDryRun is not { } dry ? "Permanent Delete: not planned" : dry.Plan.ProductionPermanentDeleteCapability ? dry.Preflight.State == FileOperationState.Ready ? "Permanent Delete: irreversible single-file capability verified; Windows Recycle Bin is not used" : $"Permanent Delete: blocked — {dry.Preflight.Code}" : "Permanent Delete: not the active execution capability";
    public string OperationPlanningPermanentDeleteSnapshot => CurrentOperationDryRun is not { } dry || !dry.Plan.ProductionPermanentDeleteCapability ? "Permanent Delete snapshot: not applicable" : $"PERMANENT DELETE — CANNOT BE UNDONE. source={dry.Plan.SourcePath}; filename={System.IO.Path.GetFileName(dry.Plan.SourcePath)}; size={dry.Plan.SourceSizeBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes; selected count={dry.Plan.SelectedMediaCount}; Windows volume={dry.Plan.SourceVolumeIdentity ?? "unknown"}; Full SHA-256={(string.IsNullOrWhiteSpace(dry.Plan.SourceFullSha256) ? "unavailable" : "captured")}.";
    public string OperationPlanningPermanentDeleteWarning => CurrentOperationDryRun is not { } dry || !dry.Plan.ProductionPermanentDeleteCapability ? "Permanent Delete warning: not applicable" : "IRREVERSIBLE WARNING: Windows Recycle Bin is NOT used. After execution, the app cannot undo this operation and no normal Restore feature exists. Full SHA-256 is revalidated before independent irreversible Final Confirmation and again immediately before the single-file delete call. Recycle failure never triggers this operation.";
    public string OperationPlanningCrossVolumeProgress => CurrentOperationDryRun is not { } dry ? "Cross-volume transaction stage: not planned" : $"Cross-volume transaction stage: {dry.Plan.CrossVolumeMoveStage}";
    public string OperationPlanningCrossVolumeSnapshot => CurrentOperationDryRun is not { } dry || !dry.Plan.ProductionCrossVolumeMoveCapability
        ? "Cross-volume snapshot: not applicable"
        : $"Cross-volume snapshot: source volume={dry.Plan.SourceVolumeIdentity ?? "unknown"}; destination volume={dry.Plan.DestinationVolumeIdentity ?? "unknown"}; source size={dry.Plan.SourceSizeBytes:N0} bytes; observed destination free space={dry.Preflight.DestinationFreeBytes?.ToString("N0", CultureInfo.InvariantCulture) ?? "unknown"} bytes; required free space={dry.Preflight.RequiredDestinationFreeBytes?.ToString("N0", CultureInfo.InvariantCulture) ?? "unknown"} bytes.";
    public string OperationPlanningFullHashRequirement => CurrentOperationDryRun is not { } dry || (!dry.Plan.ProductionCrossVolumeMoveCapability && !dry.Plan.ProductionRecycleCapability && !dry.Plan.ProductionPermanentDeleteCapability)
        ? "Full SHA-256: capability-specific policy applies"
        : string.IsNullOrWhiteSpace(dry.Plan.SourceFullSha256)
            ? dry.Plan.ProductionPermanentDeleteCapability ? "Full SHA-256: REQUIRED but unavailable; Permanent Delete Execute is blocked." : dry.Plan.ProductionRecycleCapability ? "Full SHA-256: REQUIRED but unavailable; Recycle Execute is blocked." : "Full SHA-256: REQUIRED but unavailable; Cross-Volume Execute is blocked."
            : dry.Plan.ProductionPermanentDeleteCapability
                ? "Full SHA-256: REQUIRED and captured in the immutable Permanent Delete plan; source is revalidated before irreversible Final Confirmation and immediately before the delete call."
                : dry.Plan.ProductionRecycleCapability
                    ? "Full SHA-256: REQUIRED and captured in the immutable Recycle plan; source is revalidated immediately before Windows Recycle Bin execution."
                    : "Full SHA-256: REQUIRED and captured in the immutable plan; temporary and final destination must match before source removal.";
    public string OperationPlanningTemporaryArtifactState => CurrentOperationDryRun is not { } dry || !dry.Plan.ProductionCrossVolumeMoveCapability ? "Temporary destination: not applicable" : dry.Preflight.TemporaryDestinationAvailable is true ? "Temporary destination: operation-specific path is available; any existing artifact blocks execution" : "Temporary destination: unavailable or not validated; Execute is blocked";
    public string OperationPlanningSourceRemovalWarning => CurrentOperationDryRun is not { } dry || !dry.Plan.ProductionCrossVolumeMoveCapability ? "Source removal warning: not applicable" : "SOURCE REMOVAL WARNING: original source is removed only after temporary copy and final destination both pass Full SHA-256 verification, then source identity/content is revalidated again. Recovery never infers or retries source removal.";
    public string OperationPlanningFinalConfirmationState => CurrentOperationDryRun is null ? "Final confirmation: not recorded" : CurrentOperationDryRun.Plan.FinalExecutionConfirmedAt is null ? "Final confirmation: not recorded; Execute is blocked" : $"Final confirmation: recorded at {CurrentOperationDryRun.Plan.FinalExecutionConfirmedAt:O}";
    public string OperationPlanningFinalConfirmationInstruction => CurrentOperationDryRun is { Plan.ProductionPermanentDeleteCapability: true } ? L.T("表示されたsource full path、filename、size、selected count=1、Full SHA-256 validation、Windows Recycle Binを使用しないこと、アプリから元に戻せず通常のRestore機能がない不可逆操作であることを最終確認しました。Executeはこの1件だけをPermanent Deleteします。") : CurrentOperationDryRun is { Plan.ProductionRecycleCapability: true } ? L.T("表示されたsource、filename、size、selected count=1、Windows Recycle Binへの移動、Full SHA-256 validation、recoverability、audit保持を最終確認しました。これはPermanent Deleteではありません。Executeはこの1件だけに適用されます。") : CurrentOperationDryRun is { Plan.ProductionCrossVolumeMoveCapability: true } ? L.T("表示されたsource、destination、Cross-Volume Move、temporary copy、Full SHA-256 verification、source removal warningを最終確認しました。Executeはこの1件だけに適用されます。") : L.T("表示されたsource、destination、operation type、same-volume状態を最終確認しました。Executeはこの1件だけに適用されます。");
    public bool OperationPlanningFinalConfirmationEnabled => OperationEditorsEnabled && CurrentOperationDryRun is { } dry && dry.Plan.Mode == FileOperationMode.ProductionDryRun && (dry.Plan.ProductionSameVolumeCapability || dry.Plan.ProductionCrossVolumeMoveCapability || dry.Plan.ProductionRecycleCapability || dry.Plan.ProductionPermanentDeleteCapability) && dry.Plan.State == FileOperationState.ConfirmationRequired && dry.Plan.UserAcknowledged && dry.Plan.ConfirmedAt is not null && dry.Plan.FinalExecutionConfirmedAt is null;
    public string OperationPlanningSummary => CurrentOperationDryRun is { } dry ? $"{dry.Plan.OperationType} | {dry.Plan.SourcePath} → {dry.Plan.DestinationPath ?? L.T("destinationなし")} | {dry.ExpectedDiskEffect}" : $"{OperationPlanningType?.ToString() ?? L.T("未選択")} | {OperationPlanningSelectedSource} → {OperationDestinationPath}";
    public bool ProductionOperationExecutionEnabled => CurrentOperationDryRun is { } dryRun && FileOperationPolicy.CanExecute(dryRun.Plan, isDisposableFixture: false, out _, out _);
    public bool ProductionSameVolumeOperationExecutionEnabled => CurrentOperationDryRun is { } dryRun && dryRun.Plan.ProductionSameVolumeCapability && FileOperationPolicy.CanExecute(dryRun.Plan, isDisposableFixture: false, out _, out _);
    public bool ProductionCrossVolumeMoveExecutionEnabled => CurrentOperationDryRun is { } dryRun && dryRun.Plan.ProductionCrossVolumeMoveCapability && FileOperationPolicy.CanExecute(dryRun.Plan, isDisposableFixture: false, out _, out _);
    public bool ProductionRecycleExecutionEnabled => CurrentOperationDryRun is { } dryRun && dryRun.Plan.ProductionRecycleCapability && FileOperationPolicy.CanExecute(dryRun.Plan, isDisposableFixture: false, out _, out _);
    public bool ProductionPermanentDeleteExecutionEnabled => CurrentOperationDryRun is { } dryRun && dryRun.Plan.ProductionPermanentDeleteCapability && dryRun.Plan.SelectedMediaCount == 1 && FileOperationPolicy.CanExecute(dryRun.Plan, isDisposableFixture: false, out _, out _);
    public string OperationPlanningExecutionReadiness => ProductionPermanentDeleteExecutionEnabled ? "READY TO EXECUTE — PERMANENT DELETE — CANNOT BE UNDONE for the displayed explicit single file only: StrongFullHash revalidation, one normal-file delete API call without Windows Recycle Bin, source-absence observation, C# permanent lifecycle publish, and audit. No Restore, batch, recursive delete, secure erase, or fallback is used." : ProductionRecycleExecutionEnabled ? "READY TO EXECUTE — MOVE TO WINDOWS RECYCLE BIN for the displayed single file only: Full SHA-256 revalidation, Windows Shell Recycle, source-path observation, C# lifecycle publish, and audit; this is NOT Permanent Delete." : ProductionCrossVolumeMoveExecutionEnabled ? "READY TO EXECUTE — Cross-Volume Move only: temporary copy, Full SHA-256 verification, final publish, source revalidation, source removal, and C# DB publish; overwrite is prohibited." : ProductionSameVolumeOperationExecutionEnabled ? "READY TO EXECUTE — same-volume Move/Rename only; overwrite is prohibited." : "EXECUTE BLOCKED — Final Confirmation and capability-specific revalidation are required.";
    public OperationSafetyPresentation OperationSafety => OperationSafetyPresentation.Evaluate(new(
        OperationPlanningType,
        OperationPlanningSelectedCount,
        CurrentOperationDryRun?.Plan.SourcePath ?? OperationPlanningSelectedSource,
        CurrentOperationDryRun?.Plan.DestinationPath ?? OperationDestinationPath,
        CurrentOperationDryRun is not null,
        CurrentOperationDryRun?.Plan.State,
        CurrentOperationDryRun?.Plan.UserAcknowledged ?? false,
        CurrentOperationDryRun?.Plan.ConfirmedAt,
        CurrentOperationDryRun?.Plan.FinalExecutionConfirmedAt,
        ProductionOperationExecutionEnabled,
        OperationPlanningValidationState,
        OperationPlanningWarning,
        OperationPlanningRecoverability,
        CurrentOperationDryRun?.Plan.ProductionSameVolumeCapability ?? false,
        CurrentOperationDryRun?.Plan.ProductionCrossVolumeMoveCapability ?? false,
        CurrentOperationDryRun?.Plan.ProductionRecycleCapability ?? false,
        CurrentOperationDryRun?.Plan.ProductionPermanentDeleteCapability ?? false));
    public string ProductionOperationExecutionDisabledReason
    {
        get
        {
            if (CurrentOperationDryRun is not { } dryRun) return "No current plan. Explicit selection and a new Dry Run are required.";
            return FileOperationPolicy.CanExecute(dryRun.Plan, isDisposableFixture: false, out _, out var message)
                ? dryRun.Plan.ProductionPermanentDeleteCapability
                    ? "Irreversible Final Confirmation and StrongFullHash revalidation are recorded. Execute affects only the displayed explicit single source, does not use Windows Recycle Bin, and cannot be undone."
                    : "Final Confirmation and capability-specific revalidation are recorded. Execute affects only the displayed explicit operation target."
                : message;
        }
    }
    public bool BulkUnifiedReviewConfirmation
    {
        get => _bulkUnifiedReviewConfirmation;
        set
        {
            if (!Set(ref _bulkUnifiedReviewConfirmation, value)) return;
            BulkUnifiedReviewCommand.RaiseCanExecuteChanged();
        }
    }
    public UnifiedReviewAction UnifiedReviewBulkAction
    {
        get => _unifiedReviewBulkAction;
        set
        {
            if (!Set(ref _unifiedReviewBulkAction, value)) return;
            Raise(nameof(UnifiedReviewBulkPreview));
            BulkUnifiedReviewCommand.RaiseCanExecuteChanged();
        }
    }
    public IReadOnlyList<UnifiedReviewFilter> UnifiedReviewFilters { get; } = Enum.GetValues<UnifiedReviewFilter>();
    public IReadOnlyList<UnifiedReviewSort> UnifiedReviewSorts { get; } = Enum.GetValues<UnifiedReviewSort>();
    public IReadOnlyList<UnifiedReviewAction> UnifiedReviewBulkActions { get; } = Enum.GetValues<UnifiedReviewAction>();
    public int CurrentUnifiedReviewPage { get => _unifiedReviewPage; private set => SetUnifiedReviewPageState(ref _unifiedReviewPage, value, nameof(CurrentUnifiedReviewPage)); }
    public int TotalUnifiedReviewCount { get => _totalUnifiedReviewCount; private set { if (SetUnifiedReviewPageState(ref _totalUnifiedReviewCount, value, nameof(TotalUnifiedReviewCount))) RaiseReadiness(); } }
    public bool HasPreviousUnifiedReviewPage => CurrentUnifiedReviewPage > 0;
    public bool HasNextUnifiedReviewPage => ((long)CurrentUnifiedReviewPage + 1) * UnifiedReviewPageSize < TotalUnifiedReviewCount;
    public string UnifiedReviewPageStatus => TotalUnifiedReviewCount == 0 ? L.T("Unified Review candidateはありません。") : L.F("Unified Review {0:N0} / {1:N0}（{2:N0} item）", CurrentUnifiedReviewPage + 1, ((TotalUnifiedReviewCount - 1) / UnifiedReviewPageSize) + 1, TotalUnifiedReviewCount);
    public int SelectedUnifiedReviewBulkCount => UnifiedReviewItems.Count(item => item.IsBulkSelected);
    public string UnifiedReviewBulkPreview
    {
        get
        {
            var selected = UnifiedReviewItems.Where(item => item.IsBulkSelected).ToArray();
            if (selected.Length == 0) return L.T("一括action preview: 選択なし。保存は実行されません。");
            var types = selected.Select(item => item.ReviewType).Distinct().ToArray();
            return types.Length != 1
                ? L.F("一括action preview: {0:N0}件が複数review typeにまたがるため保存不可。", selected.Length)
                : L.F("一括action preview: {0}の明示選択{1:N0}件だけに{2}を保存します。", types[0], selected.Length, UnifiedReviewBulkAction);
        }
    }
    public string TagJudgmentNote { get => _tagJudgmentNote; set => Set(ref _tagJudgmentNote, value); }
    public string ManualTagText
    {
        get => _manualTagText;
        set
        {
            if (!Set(ref _manualTagText, value)) return;
            AddManualTagCommand.RaiseCanExecuteChanged();
            RemoveManualTagCommand.RaiseCanExecuteChanged();
        }
    }
    public string ManualTagNote { get => _manualTagNote; set => Set(ref _manualTagNote, value); }
    public string TagReviewStatus { get => _tagReviewStatus; private set => Set(ref _tagReviewStatus, value); }
    public string TagFilterText { get => _tagFilterText; set => Set(ref _tagFilterText, value); }
    public bool TagFilterIncludeSuggested { get => _tagFilterIncludeSuggested; set => Set(ref _tagFilterIncludeSuggested, value); }
    public bool BulkTagConfirmation { get => _bulkTagConfirmation; set => Set(ref _bulkTagConfirmation, value); }
    public int CurrentTagReviewPage { get => _tagReviewPage; private set => SetTagReviewPageState(ref _tagReviewPage, value, nameof(CurrentTagReviewPage)); }
    public int TotalTagReviewCount { get => _totalTagReviewCount; private set { if (SetTagReviewPageState(ref _totalTagReviewCount, value, nameof(TotalTagReviewCount))) RaiseReadiness(); } }
    public bool HasPreviousTagReviewPage => CurrentTagReviewPage > 0;
    public bool HasNextTagReviewPage => ((long)CurrentTagReviewPage + 1) * TagReviewPageSize < TotalTagReviewCount;
    public string TagReviewPageStatus => TotalTagReviewCount == 0 ? L.T("Suggested Tag review candidateはありません。") : L.F("Suggested Tag candidates {0:N0} / {1:N0}（{2:N0} evidence）", CurrentTagReviewPage + 1, ((TotalTagReviewCount - 1) / TagReviewPageSize) + 1, TotalTagReviewCount);
    public string SelectedContentSimilarityMemberFileName => SelectedContentSimilarityMember?.Media.FileName ?? L.T("Content Similar memberを選択してください。");
    public string SelectedContentSimilarityMemberPath => SelectedContentSimilarityMember?.Media.Path ?? string.Empty;
    public string SelectedContentSimilarityMemberEvidence => SelectedContentSimilarityMember?.SimilarityDisplay ?? "Content Similarity evidence unavailable";
    public string SelectedContentSimilarityMemberMetadata => SelectedContentSimilarityMember is { } member ? $"{member.Media.SizeBytes:N0} bytes | {member.DurationDisplay} | {member.Metadata?.ContainerFormat ?? "Container unavailable"} | {member.Metadata?.VideoCodec ?? "Codec unavailable"} | {member.ResolutionDisplay} | representative timestamp {member.RepresentativeSample.PresentationTimestampSeconds:F2}s | coverage {member.Evidence.CoverageSampleCountA}/{member.Evidence.CoverageExpectedCountA} and {member.Evidence.CoverageSampleCountB}/{member.Evidence.CoverageExpectedCountB}" : string.Empty;
    public string PinnedContentSimilarityMemberDisplay => PinnedContentSimilarityMember is { } member ? $"Pinned Content Similar: {member.Media.FileName} | {member.SimilarityDisplay} | {member.Media.Path}" : L.T("Content Similar memberは未ピン留めです。");
    public string ContentSimilarityJudgmentNote { get => _contentSimilarityJudgmentNote; set => Set(ref _contentSimilarityJudgmentNote, value); }
    public string ContentSimilarityJudgmentStatus { get => _contentSimilarityJudgmentStatus; private set => Set(ref _contentSimilarityJudgmentStatus, value); }
    public int CurrentContentSimilarityPage { get => _contentSimilarityPage; private set => SetContentSimilarityPageState(ref _contentSimilarityPage, value, nameof(CurrentContentSimilarityPage)); }
    public int TotalContentSimilarityCount { get => _totalContentSimilarityCount; private set { if (SetContentSimilarityPageState(ref _totalContentSimilarityCount, value, nameof(TotalContentSimilarityCount))) RaiseReadiness(); } }
    public bool HasPreviousContentSimilarityPage => CurrentContentSimilarityPage > 0;
    public bool HasNextContentSimilarityPage => ((long)CurrentContentSimilarityPage + 1) * ContentSimilarityPageSize < TotalContentSimilarityCount;
    public string ContentSimilarityPageStatus => TotalContentSimilarityCount == 0 ? L.T("Content Similar candidateはありません。") : L.F("Content Similar candidates {0:N0} / {1:N0}（{2:N0} edge）", CurrentContentSimilarityPage + 1, ((TotalContentSimilarityCount - 1) / ContentSimilarityPageSize) + 1, TotalContentSimilarityCount);

    public string DuplicateJudgmentNote { get => _duplicateJudgmentNote; set => Set(ref _duplicateJudgmentNote, value); }
    public string DuplicateJudgmentStatus { get => _duplicateJudgmentStatus; private set => Set(ref _duplicateJudgmentStatus, value); }
    public int CurrentDuplicateGroupPage { get => _duplicateGroupPage; private set => SetDuplicateGroupPageState(ref _duplicateGroupPage, value, nameof(CurrentDuplicateGroupPage)); }
    public int TotalDuplicateGroupCount { get => _totalDuplicateGroupCount; private set { if (SetDuplicateGroupPageState(ref _totalDuplicateGroupCount, value, nameof(TotalDuplicateGroupCount))) RaiseReadiness(); } }
    public bool HasPreviousDuplicateGroupPage => CurrentDuplicateGroupPage > 0;
    public bool HasNextDuplicateGroupPage => ((long)CurrentDuplicateGroupPage + 1) * DuplicateGroupPageSize < TotalDuplicateGroupCount;
    public string DuplicateGroupPageStatus => TotalDuplicateGroupCount == 0 ? L.T("Exact duplicate groupはありません。") : L.F("重複group {0:N0} / {1:N0}（{2:N0} group）", CurrentDuplicateGroupPage + 1, ((TotalDuplicateGroupCount - 1) / DuplicateGroupPageSize) + 1, TotalDuplicateGroupCount);

    public int CurrentPage { get => _currentPage; private set => SetPageState(ref _currentPage, value, nameof(CurrentPage)); }
    public int TotalMediaCount { get => _totalMediaCount; private set => SetPageState(ref _totalMediaCount, value, nameof(TotalMediaCount)); }
    public bool HasPreviousPage => CurrentPage > 0;
    public bool HasNextPage => ((long)CurrentPage + 1) * PageSize < TotalMediaCount;
    public string PageStatus => TotalMediaCount == 0
        ? L.T("表示するメディアはありません。")
        : L.F("ページ {0:N0} / {1:N0}（{2:N0}件）", CurrentPage + 1, ((TotalMediaCount - 1) / PageSize) + 1, TotalMediaCount);
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string ErrorText { get => _errorText; private set { if (Set(ref _errorText, value)) RaiseReadiness(); } }
    public JobIssuePresentation? SelectedJobIssue
    {
        get => _selectedJobIssue;
        set
        {
            if (!Set(ref _selectedJobIssue, value)) return;
            Raise(nameof(HasSelectedJobIssue));
            RescanSelectedIssueRootCommand.RaiseCanExecuteChanged();
            _ = LoadSelectedJobEventsAsync(IsDeveloperMode && IsJobDiagnosticsExpanded ? value?.Job.JobId : null);
        }
    }
    public bool HasSelectedJobIssue => SelectedJobIssue is not null;
    public string JobIssueSummary
    {
        get
        {
            var terminal = JobIssues.Count(item => item.Job.State is JobState.Completed or JobState.Partial or JobState.Failed or JobState.Cancelled);
            var problems = JobIssues.Count(item => item.Job.State is JobState.Failed or JobState.Partial);
            return L.F("履歴 {0:N0}件 / 問題の履歴 {1:N0}件 / 完了・終了 {2:N0}件", JobIssues.Count, problems, terminal);
        }
    }
    public string SelectedJobEventStatus { get => _selectedJobEventStatus; private set => Set(ref _selectedJobEventStatus, value); }
    public ReadinessPresentation Readiness
    {
        get
        {
            var activeJobs = ActiveJobs.ToArray();
            var selectedAvailability = SelectedRoot?.Availability ?? RootAvailability.Unknown;
            var reviewTargets = Math.Max(TotalUnifiedReviewCount, TotalDuplicateGroupCount + TotalContentSimilarityCount + TotalTagReviewCount);
            return ReadinessPresentation.Evaluate(new ReadinessInput(
                _readinessInitialized,
                Roots.Count,
                SelectedRoot is not null,
                selectedAvailability,
                SelectedRoot?.LastScanAt is not null,
                _readinessMediaCount,
                _readinessIndexedMediaCount,
                activeJobs.Length,
                activeJobs.Count(job => job.JobType == JobType.Scan),
                !string.IsNullOrWhiteSpace(ErrorText),
                WorkerStatus.Availability,
                reviewTargets));
        }
    }
    public GlobalActivityPresentation GlobalActivity => GlobalActivityPresentation.Create(Roots, ActiveJobs, RecentJobs, WorkerStatus, _embeddingCompleteness, _rootScanCurrentness);
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            WorkerDiagnosticCommand.RaiseCanExecuteChanged();
            UnregisterRootCommand.RaiseCanExecuteChanged();
            SaveRetentionSettingsCommand.RaiseCanExecuteChanged();
            RescanSelectedIssueRootCommand.RaiseCanExecuteChanged();
            SemanticSearchCommand.RaiseCanExecuteChanged();
            PreviousPageCommand.RaiseCanExecuteChanged();
            NextPageCommand.RaiseCanExecuteChanged();
            PreviousDuplicateGroupPageCommand.RaiseCanExecuteChanged();
            NextDuplicateGroupPageCommand.RaiseCanExecuteChanged();
            PreviousContentSimilarityPageCommand.RaiseCanExecuteChanged();
            NextContentSimilarityPageCommand.RaiseCanExecuteChanged();
            PreviousTagReviewPageCommand.RaiseCanExecuteChanged();
            NextTagReviewPageCommand.RaiseCanExecuteChanged();
            BulkConfirmTagsCommand.RaiseCanExecuteChanged();
            ApplyTagFilterCommand.RaiseCanExecuteChanged();
            RefreshUnifiedReviewCommand.RaiseCanExecuteChanged();
            PreviousUnifiedReviewPageCommand.RaiseCanExecuteChanged();
            NextUnifiedReviewPageCommand.RaiseCanExecuteChanged();
            BulkUnifiedReviewCommand.RaiseCanExecuteChanged();
            RaiseUnifiedReviewCommands();
            NotifyAiSearchPresentation();
        }
    }

    public WorkerRuntimeStatus WorkerStatus { get => _workerStatus; private set { if (Set(ref _workerStatus, value)) { Raise(nameof(WorkerStatusText)); Raise(nameof(IsWorkerUnavailable)); RaiseReadiness(); Raise(nameof(GlobalActivity)); NotifyAiSearchPresentation(); } } }
    public bool IsWorkerUnavailable => WorkerStatus.Availability == WorkerAvailability.Unavailable;
    public string WorkerStatusText => WorkerStatus.Availability == WorkerAvailability.Available
        ? L.F("Worker: 利用可能 (v{0}, Python {1})", WorkerStatus.Worker?.WorkerVersion, WorkerStatus.Worker?.PythonVersion)
        : WorkerStatus.ErrorCode is { } code ? $"Worker: {code}" : $"Worker: {WorkerStatus.Availability}";

    public async Task InitializeAsync()
    {
        await _library.InitializeAsync(_lifetime.Token);
        await _jobs.RecoverInterruptedJobsAsync(_lifetime.Token);
        await LoadRetentionSettingsAsync(_lifetime.Token);
        await LoadPresentationSettingsAsync();
        _pipeline.RequestRetentionCleanup("Startup");
        await LoadRootsAsync(_lifetime.Token);
        await Task.Run(() => _pipeline.EnqueueMissingThumbnailJobsAsync(_lifetime.Token), _lifetime.Token);
        await LoadUnifiedReviewAsync(restoreAnchor: true);
        await RefreshAsync();
        _readinessInitialized = true;
        RaiseReadiness();
        RequestPipelineRun();
    }

    private async Task RegisterRootAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = L.T("ライブラリとして登録するフォルダーを選択") };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var root = await _library.AddOrGetLibraryRootAsync(dialog.FolderName, _lifetime.Token);
            await LoadRootsAsync(_lifetime.Token);
            SelectedRoot = Roots.FirstOrDefault(x => x.LibraryRootId == root.LibraryRootId);
            await EnqueueScanAsync(root.LibraryRootId);
        }
        catch (TestLabViolationException ex) { LibraryFolderFeedback = L.T("テスト用モードでは、テスト用フォルダー（TestLab）の中だけ登録できます。"); ReportUserError(L.T("テスト用フォルダーの外は登録できません。"), ex); }
        catch (Exception ex) { LibraryFolderFeedback = L.T("フォルダーを登録できませんでした。アクセスを確認して再試行してください。"); ReportUserError(L.T("フォルダーを登録できませんでした。"), ex); }
    }

    private async Task RescanAsync()
    {
        if (SelectedRoot is null) return;
        await EnqueueScanAsync(SelectedRoot.LibraryRootId);
    }

    private async Task UnregisterRootAsync()
    {
        if (SelectedRoot is null) return;
        var answer = MessageBox.Show(L.T("このフォルダーの登録を解除します。元の画像・動画には一切触れません。保持期間中は同一性を確認できた再登録で情報を再利用できます。"), L.T("フォルダーの登録解除"), MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;
        try
        {
            await _library.UnregisterRootAsync(SelectedRoot.LibraryRootId, DateTimeOffset.UtcNow, _lifetime.Token);
            SelectedRoot = null;
            await LoadRootsAsync(_lifetime.Token);
            await RefreshAsync();
            StatusText = L.T("Rootの登録を解除しました。元Mediaは変更していません。");
            LibraryFolderFeedback = L.T("✓ フォルダーの登録を解除しました。元ファイルは変更していません。");
        }
        catch (Exception ex) { LibraryFolderFeedback = L.T("フォルダーの登録を解除できませんでした。技術情報を確認して再試行できます。"); ReportUserError(L.T("Rootの登録を解除できませんでした。"), ex); }
    }

    private async Task LoadRetentionSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await _library.GetRetentionSettingsAsync(cancellationToken);
        FileRetentionText = FormatRetention(settings.FileDays);
        FolderRetentionText = FormatRetention(settings.FolderDays);
        DriveRetentionText = FormatRetention(settings.DriveDays);
    }

    private async Task SaveRetentionSettingsAsync()
    {
        try
        {
            var settings = new RetentionSettings(ParseRetention(FileRetentionText), ParseRetention(FolderRetentionText), ParseRetention(DriveRetentionText)).Validate();
            await _library.SaveRetentionSettingsAsync(settings, _lifetime.Token);
            StatusText = L.T("保持期間を保存しました。削除は次回の通常cleanupで再評価されます。");
            LibrarySettingsFeedback = L.T("✓ 保持期間を保存しました。保存時には削除処理を行いません。");
        }
        catch (Exception ex) { LibrarySettingsFeedback = L.T("保持期間を保存できませんでした。正の整数日数または「無制限」を確認して再試行してください。"); ReportUserError(L.T("保持期間を保存できませんでした。正の整数日数または「無制限」を入力してください。"), ex); }
    }

    private static long? ParseRetention(string value)
    {
        var normalized = value.Trim();
        if (string.Equals(normalized, L.T("無制限"), StringComparison.OrdinalIgnoreCase) || string.Equals(normalized, "Unlimited", StringComparison.OrdinalIgnoreCase)) return null;
        if (!long.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out var days)) throw new FormatException("Retention value is invalid.");
        return RetentionSettings.Validate(days, nameof(value));
    }

    private static string FormatRetention(long? days) => days?.ToString(CultureInfo.InvariantCulture) ?? L.T("無制限");

    private bool CanRescanSelectedIssueRoot()
        => !IsBusy && SelectedJobIssue is { CanRescan: true, LibraryRootId: { } rootId } && Roots.Any(root => root.LibraryRootId == rootId);

    private async Task RescanSelectedIssueRootAsync()
    {
        if (!CanRescanSelectedIssueRoot() || SelectedJobIssue?.LibraryRootId is not { } rootId) return;
        var root = Roots.FirstOrDefault(item => item.LibraryRootId == rootId);
        if (root is null) return;
        SelectedRoot = root;
        await RescanAsync();
    }

    public async Task RescanCurrentProblemRootAsync(Guid rootId)
    {
        var problem = GlobalActivity.CurrentConditions.FirstOrDefault(item =>
            item.Kind == CurrentProblemKind.ScanIncomplete && item.LibraryRootId == rootId);
        var root = Roots.FirstOrDefault(item => item.LibraryRootId == rootId);
        if (problem is null || root is null || IsBusy) return;
        SelectedRoot = root;
        await EnqueueScanAsync(rootId);
    }

    public void SelectOnlyActionRequiredJobIssue()
    {
        var candidates = JobIssues.Where(item => item.IsActionRequired).Take(2).ToArray();
        SelectedJobIssue = candidates.Length == 1 ? candidates[0] : null;
    }

    private async Task EnqueueScanAsync(Guid rootId)
    {
        try
        {
            await _pipeline.EnqueueScanAsync(rootId, _lifetime.Token);
            StatusText = L.T("スキャンをキューへ登録しました。");
            LibraryFolderFeedback = L.T("スキャンを待機列に登録しました。進行状況は現在の処理で確認できます。");
            RequestPipelineRun();
            await RefreshJobsAsync(_lifetime.Token);
        }
        catch (Exception ex) { LibraryFolderFeedback = L.T("スキャンを開始する処理を登録できませんでした。技術情報を確認して再試行できます。"); ReportUserError(L.T("スキャンを登録できませんでした。"), ex); }
    }

    private async Task RefreshAsync()
    {
        await LoadRootsAsync(_lifetime.Token);
        // Refresh keeps the current page and MediaFileId selection when that item remains visible.
        await SearchAsync();
        await LoadDuplicateGroupsAsync(_lifetime.Token);
        await LoadContentSimilarityEvidenceAsync(_lifetime.Token);
        await LoadTagReviewAsync(_lifetime.Token);
        await LoadUnifiedReviewAsync(restoreAnchor: false);
        await RefreshJobsAsync(_lifetime.Token);
    }

    private async Task LoadDuplicateGroupsAsync(CancellationToken cancellationToken)
    {
        var scopeVersion = Volatile.Read(ref _scopeVersion);
        try
        {
            var previousGroupId = SelectedDuplicateGroup?.DuplicateGroupId;
            var offset = checked(CurrentDuplicateGroupPage * DuplicateGroupPageSize);
            var result = await _library.QueryDuplicateGroupsByRelationAsync(CurrentLibraryScope.LibraryRootId, relationType: null, includeIgnored: false, offset, DuplicateGroupPageSize, cancellationToken);
            if (scopeVersion != Volatile.Read(ref _scopeVersion)) return;
            TotalDuplicateGroupCount = result.TotalCount;
            if (CurrentDuplicateGroupPage > 0 && result.Items.Count == 0)
            {
                CurrentDuplicateGroupPage--;
                await LoadDuplicateGroupsAsync(cancellationToken);
                return;
            }
            DuplicateGroups.Clear();
            foreach (var group in result.Items) DuplicateGroups.Add(group);
            SelectedDuplicateGroup = previousGroupId is { } selectedId ? DuplicateGroups.FirstOrDefault(group => group.DuplicateGroupId == selectedId) : DuplicateGroups.FirstOrDefault();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportUserError(L.T("Duplicate groupを読み込めませんでした。"), ex); }
    }

    private async Task LoadDuplicateMembersAsync(Guid duplicateGroupId, CancellationToken cancellationToken)
    {
        try
        {
            var members = await _library.GetDuplicateComparisonMembersAsync(duplicateGroupId, cancellationToken);
            if (SelectedDuplicateGroup?.DuplicateGroupId != duplicateGroupId) return;
            DuplicateMembers.Clear();
            foreach (var member in members) DuplicateMembers.Add(member);
            SelectedDuplicateMember = members.Count > 0 ? members[0] : null;
            DuplicateJudgment? groupJudgment = null;
            foreach (var judgment in await _library.GetDuplicateJudgmentsAsync(duplicateGroupId, cancellationToken))
            {
                if (judgment.MediaFileId is not null) continue;
                groupJudgment = judgment;
                break;
            }
            DuplicateJudgmentNote = groupJudgment?.Note ?? string.Empty;
            if (groupJudgment is not null)
                DuplicateJudgmentStatus = L.F("Human judgment: {0} at {1:O}。Exact relationと判断は別に保存されます。", groupJudgment.Decision, groupJudgment.UpdatedAt);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportUserError(L.T("Exact duplicate memberを読み込めませんでした。"), ex); }
    }

    private async Task LoadNearDuplicateMembersAsync(Guid duplicateGroupId, CancellationToken cancellationToken)
    {
        try
        {
            var members = await _library.GetNearDuplicateComparisonMembersAsync(duplicateGroupId, cancellationToken);
            if (SelectedDuplicateGroup?.DuplicateGroupId != duplicateGroupId || SelectedDuplicateGroup.RelationType != DuplicateRelationType.NearDuplicate) return;
            NearDuplicateMembers.Clear();
            foreach (var member in members) NearDuplicateMembers.Add(member);
            SelectedNearDuplicateMember = members.Count > 0 ? members[0] : null;
            NearDuplicateFrameEvidence.Clear();
            if (members.Count == 2)
            {
                var evidence = members[0].Evidence;
                var matches = await _library.GetNearDuplicateFrameMatchesAsync(evidence.NearDuplicateEvidenceId, cancellationToken);
                var framesA = (await _library.GetCurrentNearDuplicateFramesAsync(evidence.MediaFileAId, evidence.MediaContentAId, evidence.SamplingVersion, evidence.EvidenceAlgorithm, cancellationToken)).ToDictionary(frame => frame.NearDuplicateFrameId);
                var framesB = (await _library.GetCurrentNearDuplicateFramesAsync(evidence.MediaFileBId, evidence.MediaContentBId, evidence.SamplingVersion, evidence.EvidenceAlgorithm, cancellationToken)).ToDictionary(frame => frame.NearDuplicateFrameId);
                foreach (var match in matches)
                    if (framesA.TryGetValue(match.NearDuplicateFrameAId, out var frameA) && framesB.TryGetValue(match.NearDuplicateFrameBId, out var frameB))
                        NearDuplicateFrameEvidence.Add(new NearFrameEvidenceItem(match.SampleIndex, frameA.NormalizedPosition, frameA.PresentationTimestampSeconds, frameA.FrameRelativePath, frameB.FrameRelativePath, match.HammingDistance, match.IsMatch));
            }
            DuplicateJudgment? groupJudgment = null;
            foreach (var judgment in await _library.GetDuplicateJudgmentsAsync(duplicateGroupId, cancellationToken))
            {
                if (judgment.MediaFileId is not null) continue;
                groupJudgment = judgment;
                break;
            }
            DuplicateJudgmentNote = groupJudgment?.Note ?? string.Empty;
            DuplicateJudgmentStatus = groupJudgment is null
                ? L.T("Near Duplicate evidenceを確認し、人間の判断を保存できます。Near relationはExactではありません。")
                : L.F("Human judgment: {0} at {1:O}。Near relationと判断は別に保存されます。", groupJudgment.Decision, groupJudgment.UpdatedAt);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportUserError(L.T("Near Duplicate memberを読み込めませんでした。"), ex); }
    }

    private async Task LoadContentSimilarityEvidenceAsync(CancellationToken cancellationToken)
    {
        var scopeVersion = Volatile.Read(ref _scopeVersion);
        try
        {
            var previousEdgeId = SelectedContentSimilarityEvidence?.ContentSimilarityEvidenceId;
            var offset = checked(CurrentContentSimilarityPage * ContentSimilarityPageSize);
            var result = await _library.QueryContentSimilarityEvidenceAsync(CurrentLibraryScope.LibraryRootId, includeIgnored: false, offset, ContentSimilarityPageSize, cancellationToken);
            if (scopeVersion != Volatile.Read(ref _scopeVersion)) return;
            TotalContentSimilarityCount = result.TotalCount;
            if (CurrentContentSimilarityPage > 0 && result.Items.Count == 0)
            {
                CurrentContentSimilarityPage--;
                await LoadContentSimilarityEvidenceAsync(cancellationToken);
                return;
            }
            ContentSimilarityEdges.Clear();
            foreach (var edge in result.Items) ContentSimilarityEdges.Add(edge);
            SelectedContentSimilarityEvidence = previousEdgeId is { } selectedId ? ContentSimilarityEdges.FirstOrDefault(edge => edge.ContentSimilarityEvidenceId == selectedId) : ContentSimilarityEdges.FirstOrDefault();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportUserError(L.T("Content Similar candidateを読み込めませんでした。"), ex); }
    }

    private async Task LoadContentSimilarityMembersAsync(Guid contentSimilarityEvidenceId, CancellationToken cancellationToken)
    {
        try
        {
            var members = await _library.GetContentSimilarityComparisonMembersAsync(contentSimilarityEvidenceId, cancellationToken);
            if (SelectedContentSimilarityEvidence?.ContentSimilarityEvidenceId != contentSimilarityEvidenceId) return;
            ContentSimilarityMembers.Clear();
            foreach (var member in members) ContentSimilarityMembers.Add(member);
            SelectedContentSimilarityMember = members.Count > 0 ? members[0] : null;
            var judgments = await _library.GetContentSimilarityJudgmentsAsync(contentSimilarityEvidenceId, cancellationToken);
            var judgment = judgments.Count > 0 ? judgments[0] : null;
            ContentSimilarityJudgmentNote = judgment?.Note ?? string.Empty;
            ContentSimilarityJudgmentStatus = judgment is null
                ? L.T("Content SimilarityはDuplicateではありません。representative frameとCosine similarityを確認してhuman judgmentを保存できます。")
                : L.F("Human judgment: {0} at {1:O}。ContentSimilar edge evidenceと判断は別に保存されます。", judgment.Decision, judgment.UpdatedAt);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportUserError(L.T("Content Similar memberを読み込めませんでした。"), ex); }
    }

    private async Task LoadTagReviewAsync(CancellationToken cancellationToken)
    {
        var scopeVersion = Volatile.Read(ref _scopeVersion);
        try
        {
            var previousEvidenceId = SelectedTagReview?.Evidence.MediaTagEvidenceId;
            var offset = checked(CurrentTagReviewPage * TagReviewPageSize);
            var result = await _library.QueryMediaTagReviewAsync(CurrentLibraryScope.LibraryRootId, includeHidden: false, offset, TagReviewPageSize, cancellationToken);
            if (scopeVersion != Volatile.Read(ref _scopeVersion)) return;
            TotalTagReviewCount = result.TotalCount;
            if (CurrentTagReviewPage > 0 && result.Items.Count == 0)
            {
                CurrentTagReviewPage--;
                await LoadTagReviewAsync(cancellationToken);
                return;
            }
            TagReviewItems.Clear();
            foreach (var item in result.Items) TagReviewItems.Add(new TagReviewRow(item));
            SelectedTagReview = previousEvidenceId is { } selectedId
                ? TagReviewItems.FirstOrDefault(row => row.Evidence.MediaTagEvidenceId == selectedId)
                : TagReviewItems.FirstOrDefault();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportUserError(L.T("Suggested Tag reviewを読み込めませんでした。"), ex); }
    }

    private void RaiseControlledBatchCommands()
    {
        NotifyOperationProgress();
        AddSelectedToControlledBatchCommand?.RaiseCanExecuteChanged();
        RemoveSelectedFromControlledBatchCommand?.RaiseCanExecuteChanged();
        ClearControlledBatchCommand?.RaiseCanExecuteChanged();
        CreateControlledBatchDryRunCommand?.RaiseCanExecuteChanged();
        AcknowledgeControlledBatchDryRunCommand?.RaiseCanExecuteChanged();
        ConfirmControlledBatchFinalExecutionCommand?.RaiseCanExecuteChanged();
        ExecuteControlledBatchCommand?.RaiseCanExecuteChanged();
        CancelControlledBatchCommand?.RaiseCanExecuteChanged();
    }

    private void InvalidateControlledBatch(string reason)
    {
        _batchPresentationUnverified = false;
        _unpresentedBatchPlanIds.Clear(); _unpresentedBatchId = null;
        _controlledBatchAcknowledgementRecorded = false;
        ControlledBatchAcknowledgement = false;
        ControlledBatchFinalConfirmation = false;
        CurrentControlledBatchDryRun = null;
        ControlledBatchStatus = reason;
        Raise(nameof(ControlledBatchSafety));
        Raise(nameof(ControlledBatchFinalConfirmationEnabled));
        RaiseControlledBatchCommands();
    }

    private bool CanCreateControlledBatchDryRun()
        => OperationEditorsEnabled && _fileOperations is not null && ControlledBatchSelection.Count is > 0 and <= FileOperationPolicy.ControlledBatchMaxItems && (ControlledBatchType == FileOperationType.Recycle || !string.IsNullOrWhiteSpace(ControlledBatchDestinationDirectory));
    private bool CanAcknowledgeControlledBatchDryRun()
        => OperationEditorsEnabled && _fileOperations is not null && ControlledBatchAcknowledgement && !_controlledBatchAcknowledgementRecorded && CurrentControlledBatchDryRun is { Batch.State: BatchOperationState.DryRunCompleted, BlockedCount: 0 };
    private bool CanConfirmControlledBatchFinalExecution()
        => OperationEditorsEnabled && _fileOperations is not null && ControlledBatchFinalConfirmation && ControlledBatchFinalConfirmationEnabled;
    private bool CanExecuteControlledBatch()
        => OperationEditorsEnabled && _fileOperations is not null && CurrentControlledBatchDryRun is { Batch.State: BatchOperationState.FinalConfirmationRequired };
    private bool CanCancelControlledBatch()
        => !IsBusy && _fileOperations is not null && CurrentControlledBatchDryRun is { Batch.State: BatchOperationState.FinalConfirmationRequired or BatchOperationState.Executing or BatchOperationState.RecoveryRequired };

    private async Task CreateControlledBatchDryRunAsync()
    {
        if (_fileOperations is null || ControlledBatchSelection.Count is < 1 or > FileOperationPolicy.ControlledBatchMaxItems) return;
        try
        {
            _controlledBatchAcknowledgementRecorded = false;
            ControlledBatchAcknowledgement = false;
            ControlledBatchFinalConfirmation = false;
            var destination = ControlledBatchType == FileOperationType.Recycle ? null : System.IO.Path.GetFullPath(ControlledBatchDestinationDirectory.Trim());
            var requests = ControlledBatchSelection.Select(item => new ControlledBatchItemRequest(item.MediaFileId, destination is null ? null : System.IO.Path.Combine(destination, item.FileName), null, "Explicit Batch Review List selection; review, AI, and preferred state are not filesystem authorization.")).ToArray();
            CurrentControlledBatchDryRun = await _fileOperations.CreateControlledBatchAsync(ControlledBatchType, requests, _lifetime.Token,
                onPlanCreated: plan => _unpresentedBatchPlanIds.Add(plan.OperationId), onBatchCreated: batch => _unpresentedBatchId = batch.BatchOperationId);
            _unpresentedBatchPlanIds.Clear(); _unpresentedBatchId = null;
            ControlledBatchStatus = $"BATCH DRY RUN: {CurrentControlledBatchDryRun.ReadyCount:N0} ready / {CurrentControlledBatchDryRun.BlockedCount:N0} blocked. No filesystem mutation occurred. Every item must be ready; blocked items are not skipped.";
        }
        catch (Exception ex)
        {
            ControlledBatchStatus = L.T("Controlled Batch Dry Runを作成できませんでした。明示selection、destination、source availability、reparse/overwrite状態を確認してください。");
            _log.Write("Warning", "ControlledBatchDryRunFailed", new { ControlledBatchType, Count = ControlledBatchSelection.Count }, ex);
        }
    }

    private async Task AcknowledgeControlledBatchDryRunAsync()
    {
        if (_fileOperations is null || CurrentControlledBatchDryRun is null || !ControlledBatchAcknowledgement) return;
        try
        {
            await _fileOperations.AcknowledgeControlledBatchDryRunAsync(CurrentControlledBatchDryRun.Batch.BatchOperationId, _lifetime.Token);
            _controlledBatchAcknowledgementRecorded = true;
            ControlledBatchStatus = L.T("Batch Dry Run acknowledgementを記録しました。filesystemは変更されません。次に全itemを対象とする独立Final Confirmationが必要です。");
            Raise(nameof(ControlledBatchSafety));
            Raise(nameof(ControlledBatchFinalConfirmationEnabled));
            RaiseControlledBatchCommands();
        }
        catch (Exception ex) { ControlledBatchStatus = L.T("Batch Dry Run acknowledgementを記録できませんでした。"); _log.Write("Warning", "ControlledBatchAcknowledgementFailed", new { CurrentControlledBatchDryRun.Batch.BatchOperationId }, ex); }
    }

    private async Task ConfirmControlledBatchFinalExecutionAsync()
    {
        if (_fileOperations is null || CurrentControlledBatchDryRun is null || !ControlledBatchFinalConfirmation) return;
        try
        {
            var batch = await _fileOperations.ConfirmControlledBatchExecutionAsync(CurrentControlledBatchDryRun.Batch.BatchOperationId, _lifetime.Token);
            await RefreshCurrentControlledBatchAsync(batch, _lifetime.Token);
            ControlledBatchStatus = "BATCH FINAL CONFIRMATION RECORDED: every frozen item was revalidated. Execute is sequential (concurrency 1); PermanentDelete batch remains Deferred.";
        }
        catch (Exception ex) { ControlledBatchFinalConfirmation = false; ControlledBatchStatus = L.T("Batch Final Confirmationを記録できませんでした。全itemのDry Run acknowledgement、source、destinationを確認してください。"); _log.Write("Warning", "ControlledBatchFinalConfirmationFailed", new { CurrentControlledBatchDryRun.Batch.BatchOperationId }, ex); }
    }

    private async Task ExecuteControlledBatchAsync()
    {
        if (_fileOperations is null || CurrentControlledBatchDryRun is null) return;
        var executingBatchId = CurrentControlledBatchDryRun.Batch.BatchOperationId;
        try
        {
            var batch = await _fileOperations.ExecuteControlledBatchAsync(executingBatchId, _lifetime.Token);
            if (CurrentControlledBatchDryRun?.Batch.BatchOperationId != executingBatchId) return;
            await RefreshCurrentControlledBatchAsync(batch, CancellationToken.None);
            ControlledBatchFinalConfirmation = false;
            ControlledBatchStatus = $"BATCH EXECUTION: {batch.State}. No automatic retry or resume of not-started items occurs; inspect per-item audit/recovery state.";
            await SearchAsync();
        }
        catch (OperationCanceledException) { ControlledBatchStatus = "Batch execution cancellation was forwarded to the current item. Restart recovery must observe state; not-started items are not auto-resumed."; }
        catch (Exception ex) { ControlledBatchStatus = L.T("Controlled Batchを開始できませんでした。filesystemを一括再実行せずaudit/recovery stateを確認してください。"); _log.Write("Warning", "ControlledBatchExecutionFailed", new { CurrentControlledBatchDryRun.Batch.BatchOperationId }, ex); }
    }

    private async Task CancelControlledBatchAsync()
    {
        if (_fileOperations is null || CurrentControlledBatchDryRun is null) return;
        try
        {
            var batch = await _fileOperations.RequestControlledBatchCancellationAsync(CurrentControlledBatchDryRun.Batch.BatchOperationId, _lifetime.Token);
            await RefreshCurrentControlledBatchAsync(batch, _lifetime.Token);
            ControlledBatchStatus = "Cancellation request was durably recorded. Completed items are not rolled back; no new item may start after cancellation is observed.";
        }
        catch (Exception ex) { ControlledBatchStatus = L.T("Batch cancellation requestを記録できませんでした。"); _log.Write("Warning", "ControlledBatchCancellationFailed", new { CurrentControlledBatchDryRun.Batch.BatchOperationId }, ex); }
    }

    private bool CanAddSelectedToControlledBatch()
        => OperationEditorsEnabled && SelectedMedia is { PresenceState: PresenceState.Present } && ControlledBatchSelection.Count < FileOperationPolicy.ControlledBatchMaxItems && !ControlledBatchSelection.Any(item => item.MediaFileId == SelectedMedia.MediaFileId);

    private async Task AddSelectedToControlledBatchAsync()
    {
        if (!CanAddSelectedToControlledBatch() || SelectedMedia is not { } selected) return;
        await ChangeOperationInputAsync(() =>
        {
        _controlledBatchSelection.Add(selected);
        Raise(nameof(ControlledBatchSelectionCount));
        ControlledBatchStatus = L.F("Batch Review List: {0:N0}/{1:N0} explicit Present media。PermanentDelete batchはDeferredです。", ControlledBatchSelection.Count, FileOperationPolicy.ControlledBatchMaxItems);
        AddSelectedToControlledBatchCommand.RaiseCanExecuteChanged();
        ClearControlledBatchCommand.RaiseCanExecuteChanged();
        }, nameof(ControlledBatchSelectionCount), batch: true);
    }

    private async Task RemoveSelectedFromControlledBatchAsync()
    {
        if (ControlledBatchSelectedItem is not { } selected) return;
        var source = ControlledBatchSelection.FirstOrDefault(item => item.MediaFileId == selected.MediaFileId);
        if (source is null) return;
        await ChangeOperationInputAsync(() =>
        {
        _controlledBatchSelection.Remove(source);
        ControlledBatchSelectedItem = null;
        Raise(nameof(ControlledBatchSelectionCount));
        ControlledBatchStatus = ControlledBatchSelection.Count == 0 ? L.T("Batch Review Listは空です。filesystem操作は承認されません。") : L.F("Batch Review List: {0:N0}/{1:N0} explicit media。", ControlledBatchSelection.Count, FileOperationPolicy.ControlledBatchMaxItems);
        AddSelectedToControlledBatchCommand.RaiseCanExecuteChanged();
        ClearControlledBatchCommand.RaiseCanExecuteChanged();
        }, nameof(ControlledBatchSelectionCount), batch: true);
    }

    private async Task ClearControlledBatchAsync()
    {
        await ChangeOperationInputAsync(() =>
        {
        _controlledBatchSelection.Clear();
        ControlledBatchSelectedItem = null;
        Raise(nameof(ControlledBatchSelectionCount));
        ControlledBatchStatus = L.T("Batch Review Listをclearしました。既存single-file Operation Planningとfilesystemは変更されません。");
        AddSelectedToControlledBatchCommand.RaiseCanExecuteChanged();
        ClearControlledBatchCommand.RaiseCanExecuteChanged();
        }, nameof(ControlledBatchSelectionCount), batch: true);
    }

    private void RefreshControlledBatchReviewItems()
    {
        var selectedMediaId = ControlledBatchSelectedItem?.MediaFileId;
        var items = CurrentControlledBatchDryRun is { } dry
            ? dry.Items.OrderBy(item => item.Sequence).Select(ControlledBatchItemPresentation.FromBatchItem).ToArray()
            : ControlledBatchSelection.Select((item, index) => ControlledBatchItemPresentation.FromSelection(item, index, ControlledBatchType, ControlledBatchDestinationDirectory)).ToArray();
        ControlledBatchReviewItems.Clear();
        foreach (var item in items) ControlledBatchReviewItems.Add(item);
        ControlledBatchSelectedItem = selectedMediaId is { } id ? ControlledBatchReviewItems.FirstOrDefault(item => item.MediaFileId == id) : null;
        Raise(nameof(ControlledBatchSafety));
        Raise(nameof(ControlledBatchFinalConfirmationEnabled));
    }

    private async Task RefreshCurrentControlledBatchAsync(BatchOperation batch, CancellationToken cancellationToken)
    {
        if (CurrentControlledBatchDryRun is not { } current) return;
        var items = await _library.GetBatchItemsAsync(batch.BatchOperationId, cancellationToken);
        CurrentControlledBatchDryRun = current with { Batch = batch, Items = items };
    }

    private async Task LoadControlledBatchSelectedItemAuditAsync(ControlledBatchItemPresentation? selected)
    {
        if (selected?.OperationId is not { } operationId)
        {
            ControlledBatchSelectedItemAudit = selected is null
                ? L.T("Batch itemを選択すると、既存のper-item operation auditを表示します。")
                : L.T("このreview-list itemにはdurable operation auditがまだありません。Batch Dry Run後に確認できます。");
            return;
        }
        try
        {
            var audit = await _library.GetFileOperationAuditAsync(operationId, _lifetime.Token);
            if (ControlledBatchSelectedItem?.OperationId != operationId) return;
            ControlledBatchSelectedItemAudit = audit.Count == 0
                ? L.T("既存per-item operation auditはまだありません。")
                : string.Join(Environment.NewLine, audit.OrderByDescending(item => item.OccurredAt).Select(item => $"{item.OccurredAt:O} — {item.EventName}: {item.DetailJson ?? L.T("detailなし")}"));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ControlledBatchSelectedItemAudit = L.T("per-item operation auditを読み込めませんでした。新しいrecovery actionは実行されません。");
            _log.Write("Warning", "ControlledBatchItemAuditLoadFailed", new { OperationId = operationId }, ex);
        }
    }

    private bool CanCreateOperationDryRun()
        => OperationEditorsEnabled && _fileOperations is not null && OperationPlanningType is not null && _operationTargetMedia is { PresenceState: PresenceState.Present } && (OperationPlanningType is not (FileOperationType.Move or FileOperationType.Rename) || !string.IsNullOrWhiteSpace(OperationDestinationPath)) && (OperationPlanningType != FileOperationType.PermanentDelete || OperationPlanningSelectedCount == 1);

    private bool CanAcknowledgeOperationDryRun()
        => OperationEditorsEnabled && _fileOperations is not null && OperationPlanningAcknowledgement && CurrentOperationDryRun?.Plan.Mode == FileOperationMode.ProductionDryRun && CurrentOperationDryRun.Plan.State == FileOperationState.ConfirmationRequired && !CurrentOperationDryRun.Plan.UserAcknowledged;

    private bool CanConfirmOperationFinalExecution()
        => OperationEditorsEnabled && _fileOperations is not null && OperationPlanningFinalConfirmation && CurrentOperationDryRun is { } dry && dry.Plan.Mode == FileOperationMode.ProductionDryRun && (dry.Plan.ProductionSameVolumeCapability || dry.Plan.ProductionCrossVolumeMoveCapability || dry.Plan.ProductionRecycleCapability || dry.Plan.ProductionPermanentDeleteCapability) && dry.Plan.State == FileOperationState.ConfirmationRequired && dry.Plan.UserAcknowledged && dry.Plan.ConfirmedAt is not null && dry.Plan.FinalExecutionConfirmedAt is null;

    private bool CanExecuteProductionSameVolumeOperation()
        => OperationEditorsEnabled && _fileOperations is not null && CurrentOperationDryRun is { } dry && dry.Plan.ProductionSameVolumeCapability && FileOperationPolicy.CanExecute(dry.Plan, isDisposableFixture: false, out _, out _);

    private bool CanExecuteProductionCrossVolumeMoveOperation()
        => OperationEditorsEnabled && _fileOperations is not null && CurrentOperationDryRun is { } dry && dry.Plan.ProductionCrossVolumeMoveCapability && FileOperationPolicy.CanExecute(dry.Plan, isDisposableFixture: false, out _, out _);

    private bool CanExecuteProductionRecycleOperation()
        => OperationEditorsEnabled && _fileOperations is not null && CurrentOperationDryRun is { } dry && dry.Plan.ProductionRecycleCapability && FileOperationPolicy.CanExecute(dry.Plan, isDisposableFixture: false, out _, out _);

    private bool CanExecuteProductionPermanentDeleteOperation()
        => OperationEditorsEnabled && _fileOperations is not null && CurrentOperationDryRun is { } dry && dry.Plan.ProductionPermanentDeleteCapability && dry.Plan.SelectedMediaCount == 1 && FileOperationPolicy.CanExecute(dry.Plan, isDisposableFixture: false, out _, out _);

    private void RaiseOperationPlanningCommands()
    {
        NotifyOperationProgress();
        CreateOperationDryRunCommand?.RaiseCanExecuteChanged();
        AcknowledgeOperationDryRunCommand?.RaiseCanExecuteChanged();
        ConfirmOperationFinalExecutionCommand?.RaiseCanExecuteChanged();
        ExecuteProductionSameVolumeOperationCommand?.RaiseCanExecuteChanged();
        ExecuteProductionCrossVolumeMoveOperationCommand?.RaiseCanExecuteChanged();
        ExecuteProductionRecycleOperationCommand?.RaiseCanExecuteChanged();
        ExecuteProductionPermanentDeleteOperationCommand?.RaiseCanExecuteChanged();
    }



    private async Task CreateOperationDryRunAsync()
    {
        var target = OperationTarget;
        var selected = target?.Media;
        if (selected is null || _fileOperations is null || OperationPlanningType is not { } operationType) return;
        try
        {
            OperationPlanningAcknowledgement = false;
            OperationPlanningFinalConfirmation = false;
            selected = await _library.GetMediaFileAsync(target!.MediaFileId, _lifetime.Token) ?? throw new InvalidOperationException("Operation Target is no longer available.");
            _operationTarget = target with { Media = selected };
            Raise(nameof(OperationTarget)); Raise(nameof(OperationPlanningSelectedSource));
            var reviewContext = target.SourceContext + "; human judgment is not filesystem authorization.";
            var destination = OperationPlanningType is FileOperationType.Move or FileOperationType.Rename ? OperationDestinationPath.Trim() : null;
            var plan = await _fileOperations.CreatePlanAsync(selected.MediaFileId, operationType, FileOperationMode.ProductionDryRun, destination, target.ReviewItemId, reviewContext, operationType is FileOperationType.Move or FileOperationType.Rename or FileOperationType.PermanentDelete ? SourceValidationStrength.StrongFullHash : SourceValidationStrength.StatAndStableIdentity, _lifetime.Token);
            _unpresentedOperationPlanId = plan.OperationId;
            CurrentOperationDryRun = await _fileOperations.RunDryRunAsync(plan.OperationId, _lifetime.Token);
            _unpresentedOperationPlanId = null;
            OperationPlanningStatus = L.F("DRY RUN: {0} / {1}。Dry Runはfilesystemを変更しません。", CurrentOperationDryRun.Preflight.State, CurrentOperationDryRun.Preflight.Code);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            OperationPlanningStatus = L.T("Operation Dry Runを作成できませんでした。対象、destination、source availabilityを確認してください。");
            _log.Write("Warning", "OperationDryRunFailed", new { Selected = selected.MediaFileId, OperationPlanningType }, ex);
        }
    }

    private async Task AcknowledgeOperationDryRunAsync()
    {
        if (_fileOperations is null || CurrentOperationDryRun is null || !OperationPlanningAcknowledgement) return;
        try
        {
            var acknowledged = await _fileOperations.AcknowledgeProductionDryRunAsync(CurrentOperationDryRun.Plan.OperationId, _lifetime.Token);
            CurrentOperationDryRun = CurrentOperationDryRun with { Plan = acknowledged, ExecutionEnabled = false, WarningText = L.T("Dry Run acknowledgementを記録しました。Final Execution Confirmationを行うまでfilesystemは変更されません。") };
            OperationPlanningFinalConfirmation = false;
            OperationPlanningStatus = acknowledged.ProductionPermanentDeleteCapability
                ? L.T("Dry Run acknowledgementを記録しました。PERMANENT DELETE — CANNOT BE UNDONE、Windows Recycle Binを使用しないこと、source full path、filename、size、selected count=1、Full SHA-256、通常Restoreなしを再確認する不可逆Final Execution Confirmationが別途必要です。")
                : acknowledged.ProductionRecycleCapability
                    ? L.T("Dry Run acknowledgementを記録しました。Windows Recycle Bin、NOT Permanent Delete、source、size、Full SHA-256、recoverability、audit保持を再確認するFinal Execution Confirmationが別途必要です。")
                    : acknowledged.ProductionCrossVolumeMoveCapability
                    ? L.T("Dry Run acknowledgementを記録しました。Cross-Volume source/destination、temporary artifact、Full SHA-256、source removal warningを再検証するFinal Execution Confirmationが別途必要です。")
                    : acknowledged.ProductionSameVolumeCapability
                        ? L.T("Dry Run acknowledgementを記録しました。same-volume revalidationを伴うFinal Execution Confirmationが別途必要です。")
                        : L.T("Dry Run acknowledgementを記録しました。このoperation typeはProduction executionを実装していません。");
        }
        catch (Exception ex)
        {
            OperationPlanningStatus = L.T("Dry Run確認を記録できませんでした。");
            _log.Write("Warning", "OperationDryRunAcknowledgementFailed", new { OperationId = CurrentOperationDryRun?.Plan.OperationId }, ex);
        }
    }

    private async Task ConfirmOperationFinalExecutionAsync()
    {
        if (_fileOperations is null || CurrentOperationDryRun is null || !OperationPlanningFinalConfirmation) return;
        try
        {
            var prior = CurrentOperationDryRun.Plan;
            var confirmed = prior.ProductionPermanentDeleteCapability
                ? await _fileOperations.ConfirmProductionPermanentDeleteExecutionAsync(prior.OperationId, _lifetime.Token)
                : prior.ProductionRecycleCapability
                    ? await _fileOperations.ConfirmProductionRecycleExecutionAsync(prior.OperationId, _lifetime.Token)
                    : prior.ProductionCrossVolumeMoveCapability
                    ? await _fileOperations.ConfirmProductionCrossVolumeMoveExecutionAsync(prior.OperationId, _lifetime.Token)
                    : await _fileOperations.ConfirmProductionSameVolumeExecutionAsync(prior.OperationId, _lifetime.Token);
            var warning = confirmed.ProductionPermanentDeleteCapability
                ? L.T("IRREVERSIBLE FINAL CONFIRMATION RECORDED: Executeは表示中の明示選択1件をWindows Recycle Binを使用せずPermanent Deleteします。Full SHA-256を直前に再検証し、source absence observation後にC# permanent lifecycle/auditをpublishします。アプリから元に戻せず、通常Restoreはありません。")
                : confirmed.ProductionRecycleCapability
                    ? L.T("Final Execution Confirmationを記録しました。Executeは表示中の1件をWindows Recycle Binへ移動します。Full SHA-256を直前に再検証し、source absence observation後にC# lifecycle/auditをpublishします。これはPermanent Deleteではありません。")
                    : confirmed.ProductionCrossVolumeMoveCapability
                    ? L.T("Final Execution Confirmationを記録しました。Executeは表示中の1件をtemporary copy、Full SHA-256 verification、final publish、source revalidation、source removal、C# DB publishの順でCross-Volume Moveします。")
                    : L.T("Final Execution Confirmationを記録しました。Executeは表示中のsourceからdestinationへのsame-volume Move/Renameだけを実行します。");
            CurrentOperationDryRun = CurrentOperationDryRun with { Plan = confirmed, ExecutionEnabled = FileOperationPolicy.CanExecute(confirmed, false, out _, out _), WarningText = warning };
            OperationPlanningStatus = confirmed.ProductionPermanentDeleteCapability
                ? L.T("IRREVERSIBLE FINAL CONFIRMATION RECORDED: source identity、authorized root、single selection=1、Full SHA-256、Windows Recycle Bin非使用、source absence/lifecycle/audit contractを再検証済みです。")
                : confirmed.ProductionRecycleCapability
                    ? L.T("FINAL CONFIRMATION RECORDED: Windows Recycle Bin only、source identity、Full SHA-256、authorized root、recoverability、audit保持を再検証済みです。")
                    : confirmed.ProductionCrossVolumeMoveCapability
                    ? L.T("FINAL CONFIRMATION RECORDED: Cross-Volume source/destination volume、temporary artifact、free-space、Full SHA-256を再検証済みです。")
                    : L.T("FINAL CONFIRMATION RECORDED: Execute前にsource/destination/Windows volume identityを再検証済みです。");
        }
        catch (Exception ex)
        {
            OperationPlanningFinalConfirmation = false;
            OperationPlanningStatus = CurrentOperationDryRun?.Plan.ProductionPermanentDeleteCapability == true
                ? L.T("不可逆Final Execution Confirmationを記録できませんでした。source、selected count=1、authorized root、reparse safety、Windows identity、Full SHA-256、access状態を再確認してください。")
                : CurrentOperationDryRun?.Plan.ProductionRecycleCapability == true
                    ? L.T("Final Execution Confirmationを記録できませんでした。source、authorized root、Windows Recycle Bin availability、Full SHA-256、access状態を再確認してください。")
                    : L.T("Final Execution Confirmationを記録できませんでした。source、destination、volume状態、temporary artifact、free-spaceを再確認してください。");
            _log.Write("Warning", "OperationFinalConfirmationFailed", new { OperationId = CurrentOperationDryRun?.Plan.OperationId }, ex);
        }
    }

    private async Task ExecuteProductionSameVolumeOperationAsync()
    {
        if (_fileOperations is null || CurrentOperationDryRun is null) return;
        var executionDryRun = CurrentOperationDryRun;
        var executionTarget = OperationTarget?.MediaFileId;
        var executionId = executionDryRun.Plan.OperationId;
        try
        {
            var executed = await _fileOperations.ExecuteProductionSameVolumeAsync(executionId, _lifetime.Token);
            if (executed.State == FileOperationState.Completed) await SearchAsync();
            if (CurrentOperationDryRun?.Plan.OperationId != executionId || OperationTarget?.MediaFileId != executionTarget) return;
            CurrentOperationDryRun = executionDryRun with { Plan = executed, ExecutionEnabled = false, WarningText = executed.State == FileOperationState.Completed ? "COMPLETED: destination identity and C# DB publish were verified." : $"{executed.State}: filesystem state was not treated as completed without verification." };
            OperationPlanningFinalConfirmation = false;
            OperationPlanningStatus = executed.State == FileOperationState.Completed
                ? L.T("COMPLETED: same-volume operation、destination identity verification、C# DB path publishを完了しました。")
                : L.F("{0}: filesystemを再実行せず、audit/recovery状態を確認してください。", executed.State);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            OperationPlanningStatus = L.T("Production same-volume operationを開始できませんでした。filesystemは再実行されていません。");
            _log.Write("Warning", "ProductionSameVolumeOperationFailed", new { OperationId = CurrentOperationDryRun?.Plan.OperationId }, ex);
        }
    }

    private async Task ExecuteProductionCrossVolumeMoveOperationAsync()
    {
        if (_fileOperations is null || CurrentOperationDryRun is null) return;
        var executionDryRun = CurrentOperationDryRun;
        var executionTarget = OperationTarget?.MediaFileId;
        var executionId = executionDryRun.Plan.OperationId;
        try
        {
            var executed = await _fileOperations.ExecuteProductionCrossVolumeMoveAsync(executionId, _lifetime.Token);
            if (executed.State == FileOperationState.Completed) await SearchAsync();
            if (CurrentOperationDryRun?.Plan.OperationId != executionId || OperationTarget?.MediaFileId != executionTarget) return;
            CurrentOperationDryRun = executionDryRun with { Plan = executed, ExecutionEnabled = false, WarningText = executed.State == FileOperationState.Completed ? "COMPLETED: Cross-Volume final destination Full SHA-256, source removal, and C# DB publish were verified." : $"{executed.State}: no automatic retry, cleanup, or source removal will occur; inspect audit/recovery state." };
            OperationPlanningFinalConfirmation = false;
            OperationPlanningStatus = executed.State == FileOperationState.Completed
                ? L.T("COMPLETED: Cross-Volume temporary copy、Full SHA-256 verification、final publish、source revalidation/removal、C# DB path publishを完了しました。")
                : L.F("{0}: no automatic retry, cleanup, or source removal was performed. audit/recovery stateを確認してください。", executed.State);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            OperationPlanningStatus = L.T("Production Cross-Volume Moveを開始できませんでした。filesystemを再実行せず、source、temporary artifact、destination、auditを確認してください。");
            _log.Write("Warning", "ProductionCrossVolumeMoveFailed", new { OperationId = CurrentOperationDryRun?.Plan.OperationId }, ex);
        }
    }

    private async Task ExecuteProductionRecycleOperationAsync()
    {
        if (_fileOperations is null || CurrentOperationDryRun is null) return;
        var executionDryRun = CurrentOperationDryRun;
        var executionTarget = OperationTarget?.MediaFileId;
        var executionId = executionDryRun.Plan.OperationId;
        try
        {
            var executed = await _fileOperations.ExecuteProductionRecycleAsync(executionId, _lifetime.Token);
            if (executed.State == FileOperationState.Recycled) await SearchAsync();
            if (CurrentOperationDryRun?.Plan.OperationId != executionId || OperationTarget?.MediaFileId != executionTarget) return;
            CurrentOperationDryRun = executionDryRun with
            {
                Plan = executed,
                ExecutionEnabled = false,
                WarningText = executed.State == FileOperationState.Recycled
                    ? "RECYCLED: the displayed source was moved to the Windows Recycle Bin, source absence was observed, and C# lifecycle/audit history were retained. This was not Permanent Delete."
                    : $"{executed.State}: Windows Recycle Bin was not retried and PermanentDelete fallback was not used; inspect audit/recovery state."
            };
            OperationPlanningFinalConfirmation = false;
            OperationPlanningStatus = executed.State == FileOperationState.Recycled
                ? L.T("RECYCLED: Windows Recycle Binへの移動、source absence observation、C# lifecycle publish、audit保持を完了しました。Permanent Deleteではありません。Windows側から復元した場合は次scanで再認識します。")
                : L.F("{0}: Windows Recycle Binの再実行もPermanentDelete fallbackも行われませんでした。audit/recovery stateを確認してください。", executed.State);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            OperationPlanningStatus = L.T("Production Recycleを開始できませんでした。Windows Recycle Binの再実行およびPermanentDelete fallbackは行われていません。source、access、audit/recovery stateを確認してください。");
            _log.Write("Warning", "ProductionRecycleOperationFailed", new { OperationId = CurrentOperationDryRun?.Plan.OperationId }, ex);
        }
    }

    private async Task ExecuteProductionPermanentDeleteOperationAsync()
    {
        if (_fileOperations is null || CurrentOperationDryRun is null) return;
        var executionDryRun = CurrentOperationDryRun;
        var executionTarget = OperationTarget?.MediaFileId;
        var executionId = executionDryRun.Plan.OperationId;
        try
        {
            var executed = await _fileOperations.ExecuteProductionPermanentDeleteAsync(executionId, _lifetime.Token);
            if (executed.State == FileOperationState.PermanentlyDeleted) await SearchAsync();
            if (CurrentOperationDryRun?.Plan.OperationId != executionId || OperationTarget?.MediaFileId != executionTarget) return;
            CurrentOperationDryRun = executionDryRun with
            {
                Plan = executed,
                ExecutionEnabled = false,
                WarningText = executed.State == FileOperationState.PermanentlyDeleted
                    ? "PERMANENTLY DELETED: the displayed source was removed without Windows Recycle Bin after source absence observation; C# permanent lifecycle and audit/history were retained. The app cannot undo this operation."
                    : $"{executed.State}: PermanentDelete was not retried; Windows Recycle Bin fallback was not used; inspect audit/recovery state."
            };
            OperationPlanningFinalConfirmation = false;
            OperationPlanningStatus = executed.State == FileOperationState.PermanentlyDeleted
                ? L.T("PERMANENTLY DELETED: Windows Recycle Binを使用しない単一ファイル削除、source absence observation、C# permanent lifecycle publish、human/history/audit保持を完了しました。アプリから元に戻せず通常Restoreはありません。")
                : L.F("{0}: PermanentDeleteの再実行もRecycle fallbackも行われませんでした。audit/recovery stateを確認してください。", executed.State);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            OperationPlanningStatus = L.T("Production PermanentDeleteを開始できませんでした。PermanentDeleteの再実行およびRecycle fallbackは行われていません。source、selected count、access、audit/recovery stateを確認してください。");
            _log.Write("Warning", "ProductionPermanentDeleteOperationFailed", new { OperationId = CurrentOperationDryRun?.Plan.OperationId }, ex);
        }
    }

    private async Task LoadUnifiedReviewAsync(bool restoreAnchor)
    {
        var requestId = Interlocked.Increment(ref _unifiedReviewRequestId);
        _unifiedReviewCts?.Cancel();
        _unifiedReviewCts?.Dispose();
        _unifiedReviewCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var cancellationToken = _unifiedReviewCts.Token;
        IsReviewLoading = true;
        try
        {
            string? anchor = SelectedUnifiedReview?.ReviewItemId;
            if (restoreAnchor)
            {
                var resume = await _library.GetUnifiedReviewResumeStateAsync(cancellationToken);
                _unifiedReviewFilter = resume.Filter;
                _unifiedReviewSort = resume.Sort;
                Raise(nameof(UnifiedReviewFilter));
                Raise(nameof(UnifiedReviewSort));
                CurrentUnifiedReviewPage = resume.Offset / UnifiedReviewPageSize;
                anchor = resume.AnchorReviewItemId;
            }
            var result = await _library.QueryUnifiedReviewAsync(new UnifiedReviewQuery(CurrentLibraryScope.LibraryRootId, UnifiedReviewFilter, UnifiedReviewSort, checked(CurrentUnifiedReviewPage * UnifiedReviewPageSize), UnifiedReviewPageSize), cancellationToken);
            if (requestId != Volatile.Read(ref _unifiedReviewRequestId) || cancellationToken.IsCancellationRequested) return;
            TotalUnifiedReviewCount = result.TotalCount;
            if (CurrentUnifiedReviewPage > 0 && result.Items.Count == 0)
            {
                CurrentUnifiedReviewPage--;
                await LoadUnifiedReviewAsync(restoreAnchor: false);
                return;
            }
            UnifiedReviewItems.Clear();
            foreach (var item in result.Items)
            {
                var row = new UnifiedReviewRow(item);
                if (item.PrimaryMediaFileId is { } mediaId && await _library.GetMediaFileAsync(mediaId, cancellationToken) is { } media)
                    row.GeneralMediaLabel = media.FileName + (item.InvolvedMediaCount > 1 ? L.F(" · ほか{0}件", item.InvolvedMediaCount - 1) : string.Empty);
                if (requestId != Volatile.Read(ref _unifiedReviewRequestId) || cancellationToken.IsCancellationRequested) return;
                row.PropertyChanged += (_, eventArgs) =>
                {
                    if (string.Equals(eventArgs.PropertyName, nameof(UnifiedReviewRow.IsBulkSelected), StringComparison.Ordinal))
                    {
                        Raise(nameof(SelectedUnifiedReviewBulkCount));
                        Raise(nameof(ReviewCompatibleBulkActions)); Raise(nameof(ReviewBulkSummary));
                        BulkUnifiedReviewConfirmation = false;
                        Raise(nameof(UnifiedReviewBulkPreview));
                        BulkUnifiedReviewCommand.RaiseCanExecuteChanged();
                    }
                };
                UnifiedReviewItems.Add(row);
            }
            Raise(nameof(SelectedUnifiedReviewBulkCount));
            Raise(nameof(ReviewCompatibleBulkActions)); Raise(nameof(ReviewBulkSummary)); Raise(nameof(ReviewPageStatus));
            BulkUnifiedReviewConfirmation = false;
            Raise(nameof(UnifiedReviewBulkPreview));
            BulkUnifiedReviewCommand.RaiseCanExecuteChanged();
            SelectedUnifiedReview = anchor is { Length: > 0 }
                ? UnifiedReviewItems.FirstOrDefault(row => string.Equals(row.ReviewItemId, anchor, StringComparison.Ordinal)) ?? UnifiedReviewItems.FirstOrDefault()
                : UnifiedReviewItems.FirstOrDefault();
            await UpdateReviewQueueMessageAsync(requestId, result, cancellationToken);
            await SaveUnifiedReviewResumeAsync(cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReviewQueueMessage = L.T("候補を取得できませんでした。更新して再試行できます。"); ReportUserError(L.T("Unified Review Queueを読み込めませんでした。"), ex); }
        finally { if (requestId == Volatile.Read(ref _unifiedReviewRequestId)) IsReviewLoading = false; }
    }

    private async Task MoveUnifiedReviewPageAsync(int delta)
    {
        var target = CurrentUnifiedReviewPage + delta;
        if (target < 0 || target == CurrentUnifiedReviewPage || (delta > 0 && !HasNextUnifiedReviewPage)) return;
        SelectedUnifiedReview = null;
        CurrentUnifiedReviewPage = target;
        await LoadUnifiedReviewAsync(restoreAnchor: false);
    }

    private bool CanApplyUnifiedAction(UnifiedReviewAction action) => !IsBusy && SelectedUnifiedReview is { IsActionable: true } row && UnifiedReviewPolicy.IsAllowed(row.ReviewType, action);

    private async Task ApplySelectedUnifiedDecisionAsync(UnifiedReviewAction action)
    {
        var selected = SelectedUnifiedReview;
        if (selected is null || !selected.IsActionable || !UnifiedReviewPolicy.IsAllowed(selected.ReviewType, action)) return;
        try
        {
            var result = await _library.ApplyUnifiedReviewDecisionAsync(new UnifiedReviewDecisionRequest(selected.ReviewItemId, action, UnifiedReviewNote), _lifetime.Token);
            UnifiedReviewStatus = L.F("Human decisionを保存しました: {0} ({1:O})。{2} evidenceと判断を保持し、削除・移動・rename・overwriteは実行していません。", result.CurrentDecision, result.UpdatedAt, selected.ReviewTypeDisplay);
            ReviewJudgmentFeedback = L.T("✓ 判断を保存しました");
            await LoadUnifiedReviewAsync(restoreAnchor: false);
            await RefreshRelatedReviewSourcesAsync(selected.Item.ReviewType);
            ReviewJudgmentFeedback = L.T("✓ 判断を保存しました");
        }
        catch (Exception ex) { ReviewJudgmentFeedback = L.T("判断を保存できませんでした。対象を確認し、同じ判断操作から再試行できます。"); ReportUserError(L.T("Unified Reviewのhuman decisionを保存できませんでした。"), ex); }
    }

    private bool CanBulkApplyUnifiedReview() => !IsBusy && BulkUnifiedReviewConfirmation && UnifiedReviewItems.Any(row => row.IsBulkSelected);

    private async Task BulkApplyUnifiedReviewAsync()
    {
        var selected = UnifiedReviewItems.Where(row => row.IsBulkSelected).ToArray();
        if (selected.Length == 0)
        {
            ReviewJudgmentFeedback = UnifiedReviewStatus = L.T("一括判断の対象を明示的にチェックしてください。");
            return;
        }
        if (!BulkUnifiedReviewConfirmation)
        {
            ReviewJudgmentFeedback = UnifiedReviewStatus = L.T("一括保存前に『明示選択した同一review typeだけに適用する』確認チェックを有効にしてください。");
            return;
        }
        var types = selected.Select(row => row.ReviewType).Distinct().ToArray();
        if (types.Length != 1)
        {
            ReviewJudgmentFeedback = UnifiedReviewStatus = L.T("一括判断はrelation typeをまたげません。Exact、Near、Content Similarity、Suggested Tagを別々に選択してください。");
            return;
        }
        var action = UnifiedReviewBulkAction;
        if (selected.Any(row => !row.IsActionable || !UnifiedReviewPolicy.IsAllowed(row.ReviewType, action)))
        {
            ReviewJudgmentFeedback = UnifiedReviewStatus = L.T("選択中にcurrent sourceではないrow、またはrelation typeに非互換のactionがあります。一括保存は実行していません。");
            return;
        }
        try
        {
            foreach (var row in selected)
                await _library.ApplyUnifiedReviewDecisionAsync(new UnifiedReviewDecisionRequest(row.ReviewItemId, action, UnifiedReviewNote), _lifetime.Token);
            BulkUnifiedReviewConfirmation = false;
            UnifiedReviewStatus = L.F("{0:N0}件の明示選択された{1} rowだけに{2}を保存しました。evidenceを削除せず、ファイル操作は実行していません。", selected.Length, types[0], action);
            await LoadUnifiedReviewAsync(restoreAnchor: false);
            await RefreshRelatedReviewSourcesAsync(types[0]);
            ReviewJudgmentFeedback = L.F("✓ {0:N0} 件の判断を保存しました", selected.Length);
        }
        catch (Exception ex) { ReviewJudgmentFeedback = L.T("一括判断をすべて保存できませんでした。判断の記録を確認してから再試行してください。"); ReportUserError(L.T("Unified Reviewの一括human decisionを保存できませんでした。"), ex); }
    }

    private async Task RefreshRelatedReviewSourcesAsync(UnifiedReviewType reviewType)
    {
        if (reviewType is UnifiedReviewType.ExactDuplicate or UnifiedReviewType.NearDuplicate) await LoadDuplicateGroupsAsync(_lifetime.Token);
        else if (reviewType == UnifiedReviewType.ContentSimilar) await LoadContentSimilarityEvidenceAsync(_lifetime.Token);
        else if (reviewType == UnifiedReviewType.SuggestedTag) await LoadTagReviewAsync(_lifetime.Token);
    }

    private async Task LoadUnifiedPreviewTargetAsync(UnifiedReviewItem item)
    {
        if (item.PrimaryMediaFileId is not { } mediaFileId) return;
        try
        {
            var media = await _library.GetMediaFileAsync(mediaFileId, _lifetime.Token);
            if (!string.Equals(SelectedUnifiedReview?.ReviewItemId, item.ReviewItemId, StringComparison.Ordinal)) return;
            if (media is null || media.PresenceState != PresenceState.Present)
            {
                SetPreviewTarget(null);
                return;
            }
            SetPreviewTarget(new PreviewTarget(media.Path, item.RepresentativeTimestampSeconds ?? 0d, "Unified Review evidence", L.T("Selected evidence sourceをexisting read-only プレビューで確認します。")));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportUserError(L.T("Unified Review preview対象を読み込めませんでした。"), ex); }
    }

    private async Task SaveUnifiedReviewResumeAsync(CancellationToken cancellationToken)
    {
        await _library.SaveUnifiedReviewResumeStateAsync(new UnifiedReviewResumeState(UnifiedReviewFilter, UnifiedReviewSort, checked(CurrentUnifiedReviewPage * UnifiedReviewPageSize), SelectedUnifiedReview?.ReviewItemId, DateTimeOffset.UtcNow), cancellationToken);
    }

    private void RaiseUnifiedReviewCommands()
    {
        ConfirmUnifiedReviewCommand.RaiseCanExecuteChanged();
        ConfirmNearUnifiedReviewCommand.RaiseCanExecuteChanged();
        KeepBothUnifiedReviewCommand.RaiseCanExecuteChanged();
        IgnoreUnifiedReviewCommand.RaiseCanExecuteChanged();
        NotDuplicateUnifiedReviewCommand.RaiseCanExecuteChanged();
        RelatedUnifiedReviewCommand.RaiseCanExecuteChanged();
        NotRelatedUnifiedReviewCommand.RaiseCanExecuteChanged();
        ConfirmTagUnifiedReviewCommand.RaiseCanExecuteChanged();
        RejectTagUnifiedReviewCommand.RaiseCanExecuteChanged();
        ResetUnifiedReviewCommand.RaiseCanExecuteChanged();
    }

    private async Task MoveTagReviewPageAsync(int delta)
    {
        var target = CurrentTagReviewPage + delta;
        if (target < 0 || target == CurrentTagReviewPage || (delta > 0 && !HasNextTagReviewPage)) return;
        SelectedTagReview = null;
        CurrentTagReviewPage = target;
        await LoadTagReviewAsync(_lifetime.Token);
    }

    private async Task SetTagJudgmentAsync(MediaTagJudgmentDecision decision)
    {
        var selected = SelectedTagReview;
        if (selected is null) return;
        try
        {
            var judgment = await _library.UpsertMediaTagJudgmentAsync(selected.Evidence.MediaTagEvidenceId, decision, TagJudgmentNote, _lifetime.Token);
            TagReviewStatus = L.F("Human judgmentを保存しました: {0} ({1:O})。Suggested Tag evidenceとHuman tag stateは別に保存され、ファイル操作は実行していません。", judgment.Decision, judgment.UpdatedAt);
            await LoadTagReviewAsync(_lifetime.Token);
            await SearchAsync();
        }
        catch (Exception ex) { ReportUserError(L.T("Suggested Tagのhuman judgmentを保存できませんでした。"), ex); }
    }

    private async Task AddManualTagAsync()
    {
        var selected = SelectedTagReview;
        if (selected is null) return;
        try
        {
            var tag = await _library.AddManualMediaTagAsync(selected.Evidence.MediaFileId, ManualTagText, ManualTagNote, _lifetime.Token);
            TagReviewStatus = L.F("Manual Tagを保存しました: {0}。これはAI Suggested TagではなくHuman metadataです。ファイル操作は実行していません。", tag.DisplayTag);
            await SearchAsync();
        }
        catch (Exception ex) { ReportUserError(L.T("Manual Tagを保存できませんでした。"), ex); }
    }

    private async Task RemoveManualTagAsync()
    {
        var selected = SelectedTagReview;
        if (selected is null) return;
        try
        {
            var removed = await _library.RemoveManualMediaTagAsync(selected.Evidence.MediaFileId, ManualTagText, _lifetime.Token);
            TagReviewStatus = removed ? L.T("Manual Tagをmetadataから外しました。Original Mediaには変更していません。") : L.T("指定されたManual Tagはありません。");
            await SearchAsync();
        }
        catch (Exception ex) { ReportUserError(L.T("Manual Tagを外せませんでした。"), ex); }
    }

    private async Task BulkConfirmTagsAsync()
    {
        var selected = TagReviewItems.Where(row => row.IsBulkSelected).ToArray();
        if (selected.Length == 0)
        {
            TagReviewStatus = L.T("一括Confirmedの対象を明示的にチェックしてください。");
            return;
        }
        if (!BulkTagConfirmation)
        {
            TagReviewStatus = L.T("一括Confirmed前に『選択した候補のみをHuman Confirmedとして保存する』確認チェックを有効にしてください。");
            return;
        }
        try
        {
            foreach (var row in selected)
                await _library.UpsertMediaTagJudgmentAsync(row.Evidence.MediaTagEvidenceId, MediaTagJudgmentDecision.Confirmed, TagJudgmentNote, _lifetime.Token);
            BulkTagConfirmation = false;
            TagReviewStatus = L.F("{0:N0}件の明示選択されたSuggested TagだけをHuman Confirmedとして保存しました。削除・移動・rename・overwriteは実行していません。", selected.Length);
            await LoadTagReviewAsync(_lifetime.Token);
            await SearchAsync();
        }
        catch (Exception ex) { ReportUserError(L.T("Selected Tagの一括Confirmedを保存できませんでした。"), ex); }
    }

    private async Task ApplyTagFilterAsync()
    {
        CurrentPage = 0;
        await SearchAsync();
    }

    private async Task MoveContentSimilarityPageAsync(int delta)
    {
        var target = CurrentContentSimilarityPage + delta;
        if (target < 0 || target == CurrentContentSimilarityPage || (delta > 0 && !HasNextContentSimilarityPage)) return;
        SelectedContentSimilarityEvidence = null;
        CurrentContentSimilarityPage = target;
        await LoadContentSimilarityEvidenceAsync(_lifetime.Token);
    }

    private async Task SetContentSimilarityJudgmentAsync(ContentSimilarityJudgmentDecision decision)
    {
        var edge = SelectedContentSimilarityEvidence;
        if (edge is null) return;
        try
        {
            var judgment = await _library.UpsertContentSimilarityJudgmentAsync(edge.ContentSimilarityEvidenceId, decision, ContentSimilarityJudgmentNote, _lifetime.Token);
            ContentSimilarityJudgmentStatus = L.F("Human judgmentを保存しました: {0} ({1:O})。Content Similarityは整理支援のedgeであり、ファイルの削除・移動・renameは実行していません。", judgment.Decision, judgment.UpdatedAt);
            await LoadContentSimilarityEvidenceAsync(_lifetime.Token);
        }
        catch (Exception ex) { ReportUserError(L.T("Content Similarityの判断を保存できませんでした。"), ex); }
    }

    private Task PinContentSimilarityMemberAsync()
    {
        _pinnedContentSimilarityMember = SelectedContentSimilarityMember;
        Raise(nameof(PinnedContentSimilarityMember));
        Raise(nameof(PinnedContentSimilarityMemberDisplay));
        Raise(nameof(PinnedContentSimilarityMemberGeneralDisplay));
        ContentSimilarityJudgmentStatus = _pinnedContentSimilarityMember is null
            ? L.T("Content Similar memberを選択してください。")
            : L.F("比較用に{0}をピン留めしました。代表frame evidenceと既存read-only プレビューを確認できます。", _pinnedContentSimilarityMember.Media.FileName);
        return Task.CompletedTask;
    }

    private async Task MoveDuplicateGroupPageAsync(int delta)
    {
        var target = CurrentDuplicateGroupPage + delta;
        if (target < 0 || target == CurrentDuplicateGroupPage || (delta > 0 && !HasNextDuplicateGroupPage)) return;
        SelectedDuplicateGroup = null;
        CurrentDuplicateGroupPage = target;
        await LoadDuplicateGroupsAsync(_lifetime.Token);
    }

    private async Task SetGroupJudgmentAsync(DuplicateJudgmentDecision decision)
    {
        var group = SelectedDuplicateGroup;
        if (group is null) return;
        try
        {
            var judgment = await _library.UpsertDuplicateJudgmentAsync(group.DuplicateGroupId, null, decision, DuplicateJudgmentNote, _lifetime.Token);
            DuplicateJudgmentStatus = L.F("Human judgmentを保存しました: {0} ({1:O})。ファイルの削除・移動・renameは実行していません。", judgment.Decision, judgment.UpdatedAt);
            await LoadDuplicateGroupsAsync(_lifetime.Token);
        }
        catch (Exception ex) { ReportUserError(L.T("Exact duplicateの判断を保存できませんでした。"), ex); }
    }

    private async Task SetPreferredDuplicateMemberAsync()
    {
        var group = SelectedDuplicateGroup;
        var memberId = SelectedDuplicateMember?.Media.MediaFileId ?? SelectedNearDuplicateMember?.Media.MediaFileId;
        var memberName = SelectedDuplicateMember?.Media.FileName ?? SelectedNearDuplicateMember?.Media.FileName;
        if (group is null || memberId is null || memberName is null) return;
        try
        {
            var judgment = await _library.UpsertDuplicateJudgmentAsync(group.DuplicateGroupId, memberId, DuplicateJudgmentDecision.Preferred, DuplicateJudgmentNote, _lifetime.Token);
            DuplicateJudgmentStatus = L.F("Preferred memberを保存しました: {0} ({1:O})。他memberへの削除・移動・renameは実行していません。", memberName, judgment.UpdatedAt);
            if (group.RelationType == DuplicateRelationType.Exact) await LoadDuplicateMembersAsync(group.DuplicateGroupId, _lifetime.Token);
            else if (group.RelationType == DuplicateRelationType.NearDuplicate) await LoadNearDuplicateMembersAsync(group.DuplicateGroupId, _lifetime.Token);
        }
        catch (Exception ex) { ReportUserError(L.T("Preferred memberを保存できませんでした。"), ex); }
    }

    private Task PinDuplicateMemberAsync()
    {
        _pinnedDuplicateMember = SelectedDuplicateMember;
        _pinnedNearDuplicateMember = SelectedNearDuplicateMember;
        Raise(nameof(PinnedDuplicateMember));
        Raise(nameof(PinnedNearDuplicateMember));
        Raise(nameof(PinnedDuplicateMemberDisplay));
        Raise(nameof(PinnedDuplicateMemberGeneralDisplay));
        Raise(nameof(PinnedNearDuplicateMemberDisplay));
        Raise(nameof(PinnedNearDuplicateMemberGeneralDisplay));
        var name = _pinnedDuplicateMember?.Media.FileName ?? _pinnedNearDuplicateMember?.Media.FileName;
        DuplicateJudgmentStatus = name is null ? L.T("比較用memberを選択してください。") : L.F("比較用に{0}をピン留めしました。frame evidenceと既存read-only プレビューを確認できます。", name);
        return Task.CompletedTask;
    }

    private async Task MovePageAsync(int delta)
    {
        var targetPage = CurrentPage + delta;
        if (targetPage < 0 || targetPage == CurrentPage || (delta > 0 && !HasNextPage)) return;

        // A page change replaces the visible result set. Clear rather than silently retargeting by row index.
        ClearSelection();
        CurrentPage = targetPage;
        await SearchAsync();
    }

    private async Task SearchAsync()
    {
        var selectionAnchor = _scopeMediaSelectionAnchor ?? _selectedMediaFileId;
        var scopeVersion = Volatile.Read(ref _scopeVersion);
        var requestId = Interlocked.Increment(ref _searchRequestId);
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var cancellationToken = _searchCts.Token;
        _libraryLoading = true; _libraryLoadFailed = false; NotifyLibraryPresentation();
        try
        {
            await Task.Delay(180, cancellationToken);
            IsBusy = true;
            var offset = checked(CurrentPage * PageSize);
            var result = string.IsNullOrWhiteSpace(TagFilterText)
                ? await _library.QueryMediaAsync(CurrentLibraryScope.LibraryRootId, SearchText, offset, PageSize, cancellationToken)
                : await _library.QueryMediaByTagAsync(CurrentLibraryScope.LibraryRootId, TagFilterText, TagFilterIncludeSuggested, offset, PageSize, cancellationToken);
            // Stale asynchronous results cannot overwrite the newest search result.
            if (requestId != Volatile.Read(ref _searchRequestId) || cancellationToken.IsCancellationRequested) return;

            TotalMediaCount = result.TotalCount;
            var items = new List<MediaListItem>();
            foreach (var item in result.Items)
            {
                var indexingStatus = await _library.GetSemanticIndexStatusAsync(item.MediaFileId, _semanticSearch.SemanticModel, _semanticSearch.SemanticExecution, cancellationToken);
                items.Add(item with { IndexingStatus = indexingStatus });
            }
            if (requestId != Volatile.Read(ref _searchRequestId) || scopeVersion != Volatile.Read(ref _scopeVersion) || cancellationToken.IsCancellationRequested) return;
            MediaItems.Clear(); foreach (var item in items) MediaItems.Add(item);
            if (string.IsNullOrWhiteSpace(SearchText) && string.IsNullOrWhiteSpace(TagFilterText))
            {
                _readinessMediaCount = result.TotalCount;
                _readinessIndexedMediaCount = MediaItems.Count(item => item.IndexingStatus == SemanticIndexStatus.Indexed);
            }
            else
            {
                var readinessResult = await _library.QueryMediaAsync(CurrentLibraryScope.LibraryRootId, string.Empty, 0, PageSize, cancellationToken);
                var indexedCount = 0;
                foreach (var item in readinessResult.Items)
                {
                    if (await _library.GetSemanticIndexStatusAsync(item.MediaFileId, _semanticSearch.SemanticModel, _semanticSearch.SemanticExecution, cancellationToken) == SemanticIndexStatus.Indexed)
                        indexedCount++;
                }
                if (requestId != Volatile.Read(ref _searchRequestId) || cancellationToken.IsCancellationRequested) return;
                _readinessMediaCount = readinessResult.TotalCount;
                _readinessIndexedMediaCount = indexedCount;
            }
            RaiseReadiness();
            SetLibraryViewSelection(selectionAnchor is { } selectedId ? MediaItems.FirstOrDefault(x => x.MediaFileId == selectedId) : null);
            _scopeMediaSelectionAnchor = null;
            var tagScope = string.IsNullOrWhiteSpace(TagFilterText) ? string.Empty : L.F(" Tag filter: {0} ({1})。", TagFilterText, (TagFilterIncludeSuggested ? L.T("Suggestedを含む") : L.T("Human Confirmed/Manualのみ")));
            StatusText = L.F("{0:N0}件中 {1:N0}件を表示しています。{2}{3}", result.TotalCount, result.Items.Count, PageStatus, tagScope);
            ErrorText = string.Empty;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (requestId == Volatile.Read(ref _searchRequestId)) { _libraryLoadFailed = true; ReportUserError(L.T("一覧を読み込めませんでした。"), ex); } }
        finally { if (requestId == Volatile.Read(ref _searchRequestId)) { IsBusy = false; _libraryLoading = false; NotifyLibraryPresentation(); } }
    }

    private void ResetToFirstPageAndClearSelection()
    {
        ClearSelection();
        CurrentPage = 0;
    }

    private void ClearSelection()
    {
        if (SelectedMedia is not null)
        {
            SelectedMedia = null;
            return;
        }
        if (_selectedMediaFileId is null) return;
        _selectedMediaFileId = null;
        Raise(nameof(SelectedMediaFileId));
    }

    private bool SetDuplicateGroupPageState(ref int field, int value, string propertyName)
    {
        if (field == value) return false;
        field = value;
        Raise(propertyName);
        Raise(nameof(HasPreviousDuplicateGroupPage));
        Raise(nameof(HasNextDuplicateGroupPage));
        Raise(nameof(DuplicateGroupPageStatus));
        PreviousDuplicateGroupPageCommand?.RaiseCanExecuteChanged();
        NextDuplicateGroupPageCommand?.RaiseCanExecuteChanged();
        return true;
    }

    private bool SetTagReviewPageState(ref int field, int value, string propertyName)
    {
        if (field == value) return false;
        field = value;
        Raise(propertyName);
        Raise(nameof(HasPreviousTagReviewPage));
        Raise(nameof(HasNextTagReviewPage));
        Raise(nameof(TagReviewPageStatus));
        PreviousTagReviewPageCommand?.RaiseCanExecuteChanged();
        NextTagReviewPageCommand?.RaiseCanExecuteChanged();
        return true;
    }

    private bool SetUnifiedReviewPageState(ref int field, int value, string propertyName)
    {
        if (field == value) return false;
        field = value;
        Raise(propertyName);
        Raise(nameof(HasPreviousUnifiedReviewPage));
        Raise(nameof(HasNextUnifiedReviewPage));
        Raise(nameof(UnifiedReviewPageStatus));
        PreviousUnifiedReviewPageCommand?.RaiseCanExecuteChanged();
        NextUnifiedReviewPageCommand?.RaiseCanExecuteChanged();
        return true;
    }

    private void RaiseTagCommands()
    {
        ConfirmTagCommand.RaiseCanExecuteChanged();
        RejectTagCommand.RaiseCanExecuteChanged();
        IgnoreTagCommand.RaiseCanExecuteChanged();
        ResetTagJudgmentCommand.RaiseCanExecuteChanged();
        AddManualTagCommand.RaiseCanExecuteChanged();
        RemoveManualTagCommand.RaiseCanExecuteChanged();
    }

    private bool SetContentSimilarityPageState(ref int field, int value, string propertyName)
    {
        if (field == value) return false;
        field = value;
        Raise(propertyName);
        Raise(nameof(HasPreviousContentSimilarityPage));
        Raise(nameof(HasNextContentSimilarityPage));
        Raise(nameof(ContentSimilarityPageStatus));
        PreviousContentSimilarityPageCommand?.RaiseCanExecuteChanged();
        NextContentSimilarityPageCommand?.RaiseCanExecuteChanged();
        return true;
    }

    private bool SetPageState(ref int field, int value, string propertyName)
    {
        if (field == value) return false;
        field = value;
        Raise(propertyName);
        Raise(nameof(HasPreviousPage));
        Raise(nameof(HasNextPage));
        Raise(nameof(PageStatus));
        PreviousPageCommand?.RaiseCanExecuteChanged();
        NextPageCommand?.RaiseCanExecuteChanged();
        return true;
    }

    private async Task LoadRootsAsync(CancellationToken cancellationToken)
    {
        var selectedRootId = SelectedRoot?.LibraryRootId;
        var roots = await _library.GetLibraryRootsAsync(cancellationToken);
        Roots.Clear();
        foreach (var root in roots) Roots.Add(root);
        _libraryRegisteredFolders = roots.Where(root => root.RegistrationState == RootRegistrationState.Active).ToArray();
        LibraryScopeOptions = new[] { LibraryScopePresentation.All }.Concat(roots
            .Where(root => root.RegistrationState == RootRegistrationState.Active)
            .Select(root => new LibraryScopePresentation(root.LibraryRootId, root.DisplayName))).ToArray();
        if (_currentLibraryScope.LibraryRootId is { } scopeId && !roots.Any(root => root.LibraryRootId == scopeId))
            CurrentLibraryScope = LibraryScopePresentation.All;
        _currentLibraryScope = LibraryScopeOptions.First(option => option.LibraryRootId == _currentLibraryScope.LibraryRootId);
        Raise(nameof(LibraryScopeOptions));
        Raise(nameof(CurrentLibraryScope));
        if (selectedRootId is not null)
            SelectedRoot = Roots.FirstOrDefault(x => x.LibraryRootId == selectedRootId.Value);
        RaiseReadiness();
        Raise(nameof(GlobalActivity));
        NotifyLibraryPresentation();
    }

    private async Task RefreshJobsAsync(CancellationToken cancellationToken)
    {
        var selectedJobId = SelectedJobIssue?.Job.JobId;
        var historyTask = _jobs.GetRecentAsync(50, cancellationToken);
        var activeTask = _jobs.GetActiveAsync(cancellationToken);
        var completenessTask = _library.GetCurrentSemanticEmbeddingCompletenessAsync(_semanticSearch.SemanticModel, _semanticSearch.SemanticExecution, null, cancellationToken);
        var scanCurrentnessTask = _library.GetCurrentRootScanCurrentnessAsync(cancellationToken);
        await Task.WhenAll(historyTask, activeTask, completenessTask, scanCurrentnessTask);
        var jobs = await historyTask;
        var activeJobs = await activeTask;
        _embeddingCompleteness = await completenessTask;
        _rootScanCurrentness = await scanCurrentnessTask;
        RecentJobs.Clear();
        foreach (var job in jobs) RecentJobs.Add(job);
        ActiveJobs.Clear();
        foreach (var job in activeJobs) ActiveJobs.Add(job);
        var roots = Roots.ToDictionary(root => root.LibraryRootId);
        JobIssues.Clear();
        foreach (var job in jobs) JobIssues.Add(JobIssuePresentation.Create(job, roots));
        SelectedJobIssue = selectedJobId is { } id ? JobIssues.FirstOrDefault(item => item.Job.JobId == id) : null;
        Raise(nameof(JobIssueSummary));
        Raise(nameof(GlobalActivity));
        RescanSelectedIssueRootCommand.RaiseCanExecuteChanged();
        NotifyLibraryPresentation();
        RaiseReadiness();
    }

    private async Task LoadSelectedJobEventsAsync(Guid? jobId)
    {
        var requestId = Interlocked.Increment(ref _jobEventRequestId);
        SelectedJobEvents.Clear();
        if (jobId is null)
        {
            SelectedJobEventStatus = L.T("処理を選択するとevent履歴を読み込みます。");
            return;
        }
        SelectedJobEventStatus = L.T("Event履歴を読み込んでいます。");
        try
        {
            var events = await _jobs.GetEventsAsync(jobId.Value, _lifetime.Token);
            if (requestId != Volatile.Read(ref _jobEventRequestId)) return;
            foreach (var item in events) SelectedJobEvents.Add(JobEventPresentation.Create(item));
            SelectedJobEventStatus = events.Count == 0 ? L.T("Event履歴はありません。") : L.F("Event履歴 {0:N0}件", events.Count);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (requestId != Volatile.Read(ref _jobEventRequestId)) return;
            SelectedJobEventStatus = L.T("Event履歴を読み込めませんでした。");
            _log.Write("Warning", "JobEventHistoryLoadFailed", new { JobId = jobId }, ex);
        }
    }

    private void UpdateSelectedSemanticHumanMetadata(Guid? mediaFileId)
    {
        if (mediaFileId is null)
        {
            SelectedSemanticHumanMetadata = L.T("Human-reviewed metadataは検索結果を選択すると読み込みます。");
            return;
        }
        if (!_semanticTagSummaries.TryGetValue(mediaFileId.Value, out var summary))
        {
            SelectedSemanticHumanMetadata = L.T("Human-reviewed metadataはありません。Suggested Tagは現状のモデル制限によりAI candidateとしてのみ扱います。");
            return;
        }
        var confirmedOrManual = summary.ConfirmedOrManualTags.Count == 0 ? L.T("なし") : string.Join(", ", summary.ConfirmedOrManualTags);
        var rejected = summary.RejectedTags.Count == 0 ? L.T("なし") : string.Join(", ", summary.RejectedTags);
        var suggested = summary.SuggestedTags.Count == 0 ? L.T("なし") : string.Join(", ", summary.SuggestedTags);
        SelectedSemanticHumanMetadata = L.F("Human confirmed / Manual: {0} | Human rejected: {1} | AI Suggested (未確認): {2}", confirmedOrManual, rejected, suggested);
    }

    private async Task RunSemanticSearchAsync()
    {
        var requestId = Interlocked.Increment(ref _semanticSearchRequestId);
        _semanticSearchCts?.Cancel();
        _semanticSearchCts?.Dispose();
        _semanticSearchCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        try
        {
            IsSemanticSearchBusy = true;
            AiSearchLocalFeedback = string.Empty;
            AiSearchState = AiSearchViewState.Searching;
            SemanticSearchStatus = L.T("AI検索語を処理しています。");
            var query = SemanticSearchText.Trim();
            var selectedRootId = CurrentLibraryScope.LibraryRootId;
            var results = await _semanticSearch.SearchAsync(query, 100, _semanticSearchCts.Token, selectedRootId);
            if (requestId != Volatile.Read(ref _semanticSearchRequestId)) return;
            if (_semanticSearchCts.IsCancellationRequested)
            {
                // A non-cooperative service may return after Cancel was invoked. Its result
                // remains suppressed, but the user must still see that cancellation settled.
                SemanticSearchStatus = L.T("AI検索をキャンセルしました。");
                AiSearchState = AiSearchViewState.Cancelled;
                return;
            }
            var filteredResults = results;
            if (!string.IsNullOrWhiteSpace(TagFilterText))
            {
                var orderedIds = results.Select(result => result.MediaFileId).ToArray();
                var acceptedIds = (await _library.FilterMediaIdsByTagAsync(orderedIds, TagFilterText, TagFilterIncludeSuggested, _semanticSearchCts.Token)).ToHashSet();
                filteredResults = results.Where(result => acceptedIds.Contains(result.MediaFileId)).ToArray();
            }
            var resultMediaIds = filteredResults.Select(result => result.MediaFileId).Distinct().ToArray();
            IReadOnlyList<MediaTagSummary> summaries = Array.Empty<MediaTagSummary>();
            if (resultMediaIds.Length > 0)
            {
                try { summaries = await _library.GetMediaTagSummariesAsync(resultMediaIds, _semanticSearchCts.Token); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _log.Write("Warning", "SemanticReviewMetadataUnavailable", new { Count = resultMediaIds.Length }, ex); }
            }
            if (requestId != Volatile.Read(ref _semanticSearchRequestId) || _semanticSearchCts.IsCancellationRequested) return;
            _semanticTagSummaries.Clear();
            foreach (var summary in summaries) _semanticTagSummaries[summary.MediaFileId] = summary;
            SemanticResults.Clear();
            foreach (var result in filteredResults) SemanticResults.Add(result);
            SelectedSemanticResult = filteredResults.Count > 0 ? filteredResults[0] : null;
            AiSearchState = filteredResults.Count > 0 ? AiSearchViewState.Results : AiSearchViewState.NoResults;
            var scopeText = selectedRootId is null ? L.T("すべての登録フォルダー") : L.T("現在の対象範囲内");
            SemanticSearchStatus = filteredResults.Count == 0
                ? L.T("一致する候補が見つかりませんでした。解析済みメディア、検索語、対象範囲、またはTag filterを確認してください。")
                : L.F("{0}で{1:N0}件の意味検索結果を表示しています。Tag filterが有効な場合もsemantic ranking順は保持します。Matched timestampはbest matched sampleであり、scene boundaryではありません。", scopeText, filteredResults.Count);
            ErrorText = string.Empty;
        }
        catch (OperationCanceledException)
        {
            if (requestId == Volatile.Read(ref _semanticSearchRequestId)) { SemanticSearchStatus = L.T("AI検索をキャンセルしました。"); AiSearchState = AiSearchViewState.Cancelled; }
        }
        catch (SemanticSearchException ex)
        {
            if (requestId != Volatile.Read(ref _semanticSearchRequestId)) return;
            SemanticSearchStatus = ex.UserMessage;
            AiSearchState = AiSearchViewState.Failed;
            ErrorText = ex.UserMessage;
            _log.Write("Warning", "SemanticSearchFailed", new { ex.Code }, ex);
        }
        catch (Exception ex) { if (requestId == Volatile.Read(ref _semanticSearchRequestId)) { AiSearchState = AiSearchViewState.Failed; ReportUserError(L.T("AI検索を実行できませんでした。"), ex); } }
        finally { if (requestId == Volatile.Read(ref _semanticSearchRequestId)) IsSemanticSearchBusy = false; }
    }

    private Task CancelSemanticSearchAsync()
    {
        _semanticSearchCts?.Cancel();
        SemanticSearchStatus = L.T("AI検索をキャンセルしています。");
        return Task.CompletedTask;
    }

    private Task SelectMediaForPreviewAsync()
    {
        if (SelectedMedia is not { PresenceState: PresenceState.Present } media) return Task.CompletedTask;
        SetPreviewTarget(new PreviewTarget(media.Path, 0d, "Library media", L.T("選択メディアをread-only プレビュー対象にしました。Open プレビューで内容を確認できます。")));
        return Task.CompletedTask;
    }

    private async Task OpenPreviewAsync()
    {
        var target = _previewTarget;
        if (target is null) return;
        CancelPreviewRequest();
        var requestId = Interlocked.Increment(ref _previewRequestId);
        var previewCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _previewCts = previewCts;
        try
        {
            IsPreviewBusy = true;
            IsPreviewOpen = true;
            IsPreviewAvailable = false;
            PreviewStatus = L.T("プレビューを準備しています。元ファイルは変更しません。");
            var availability = await _preview.CheckAvailabilityAsync(target.Path, previewCts.Token);
            if (requestId != Volatile.Read(ref _previewRequestId) || previewCts.IsCancellationRequested || !Equals(target, _previewTarget)) return;

            IsPreviewAvailable = availability.IsAvailable;
            if (!availability.IsAvailable)
            {
                IsPreviewOpen = false;
                PreviewStatus = availability.UserMessage;
                return;
            }

            PreviewStatus = L.T("プレビューを開きました。内容を確認してください。");
            await SeekPreviewAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (requestId != Volatile.Read(ref _previewRequestId)) return;
            IsPreviewOpen = false;
            IsPreviewAvailable = false;
            PreviewStatus = L.T("プレビューを開けませんでした。ローカルmediaの利用可否を確認してください。");
            _log.Write("Warning", "MediaPreviewFailed", new { Path = target.Path, target.Label }, ex);
        }
        finally
        {
            if (requestId == Volatile.Read(ref _previewRequestId)) IsPreviewBusy = false;
        }
    }

    private Task ClosePreviewAsync()
    {
        CancelPreviewRequest();
        IsPreviewOpen = false;
        IsPreviewAvailable = false;
        PreviewStatus = L.T("プレビューを閉じました。");
        return Task.CompletedTask;
    }

    private Task SeekPreviewAsync()
    {
        if (!IsPreviewOpen || !IsPreviewAvailable || _previewTarget is null) return Task.CompletedTask;
        _previewSeekRequestId = Interlocked.Increment(ref _previewSeekRequestId);
        Raise(nameof(PreviewSeekRequestId));
        PreviewStatus = string.Equals(_previewTarget.Label, "Matched timestamp", StringComparison.Ordinal)
            ? L.F("Matched timestamp {0:F2}sへ移動しました。Best matched sampleを再生して確認してください。", PreviewTimestampSeconds)
            : L.F("{0} {1:F2}sへ移動しました。Original Mediaはread-onlyで確認します。", _previewTarget.Label, PreviewTimestampSeconds);
        return Task.CompletedTask;
    }

    private Task TogglePreviewPlaybackAsync()
    {
        if (!IsPreviewOpen || !IsPreviewAvailable) return Task.CompletedTask;
        _previewPlaybackRequested = !_previewPlaybackRequested;
        _previewPlaybackRequestId = Interlocked.Increment(ref _previewPlaybackRequestId);
        Raise(nameof(PreviewPlaybackRequested));
        Raise(nameof(PreviewPlaybackRequestId));
        PreviewStatus = _previewPlaybackRequested
            ? L.T("プレビューを再生しています。Original Mediaはread-onlyです。人間が内容を確認してください。")
            : L.F("プレビューを一時停止しました。{0}を確認できます。", _previewTarget?.Label ?? L.T("選択位置"));
        return Task.CompletedTask;
    }

    public void ReportPreviewPlaybackFailure()
    {
        IsPreviewOpen = false;
        IsPreviewAvailable = false;
        PreviewStatus = L.T("プレビューを再生できませんでした。ローカルmediaのcodecまたは利用可否を確認してください。");
    }

    private void SetPreviewTarget(PreviewTarget? target)
    {
        CancelPreviewRequest();
        _previewTarget = target;
        IsPreviewOpen = false;
        IsPreviewAvailable = false;
        PreviewStatus = target?.InitialStatus ?? L.T("検索結果またはExact duplicate memberを選択すると、read-only プレビューを利用できます。");
        Raise(nameof(PreviewTimestampSeconds));
        Raise(nameof(PreviewMediaPath));
        RaisePreviewCommands();
    }

    private void CancelPreviewRequest()
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = null;
        IsPreviewBusy = false;
        Interlocked.Increment(ref _previewRequestId);
        _previewPlaybackRequested = false;
        _previewPlaybackRequestId = Interlocked.Increment(ref _previewPlaybackRequestId);
        Raise(nameof(PreviewPlaybackRequested));
        Raise(nameof(PreviewPlaybackRequestId));
    }

    private void RaisePreviewCommands()
    {
        _libraryPreviewCommand?.RaiseCanExecuteChanged();
        _aiSearchPreviewCommand?.RaiseCanExecuteChanged();
        SelectMediaForPreviewCommand.RaiseCanExecuteChanged();
        OpenPreviewCommand.RaiseCanExecuteChanged();
        ClosePreviewCommand.RaiseCanExecuteChanged();
        SeekPreviewCommand.RaiseCanExecuteChanged();
        TogglePreviewPlaybackCommand.RaiseCanExecuteChanged();
    }

    private void RaiseContentSimilarityCommands()
    {
        RelatedContentSimilarityCommand.RaiseCanExecuteChanged();
        KeepBothContentSimilarityCommand.RaiseCanExecuteChanged();
        NotRelatedContentSimilarityCommand.RaiseCanExecuteChanged();
        IgnoreContentSimilarityCommand.RaiseCanExecuteChanged();
        ResetContentSimilarityJudgmentCommand.RaiseCanExecuteChanged();
        PinContentSimilarityMemberCommand.RaiseCanExecuteChanged();
    }

    private void RaiseDuplicateJudgmentCommands()
    {
        ConfirmDuplicateGroupCommand.RaiseCanExecuteChanged();
        KeepBothDuplicateGroupCommand.RaiseCanExecuteChanged();
        IgnoreDuplicateGroupCommand.RaiseCanExecuteChanged();
        NotDuplicateGroupCommand.RaiseCanExecuteChanged();
        ResetDuplicateGroupJudgmentCommand.RaiseCanExecuteChanged();
        SetPreferredDuplicateMemberCommand.RaiseCanExecuteChanged();
        PinDuplicateMemberCommand.RaiseCanExecuteChanged();
    }

    public sealed record NearFrameEvidenceItem(int SampleIndex, double NormalizedPosition, double PresentationTimestampSeconds, string? FrameARelativePath, string? FrameBRelativePath, int HammingDistance, bool IsMatch)
    {
        public string TimestampDisplay => $"{PresentationTimestampSeconds:F2}s ({NormalizedPosition:P0})";
        public string HammingDisplay => $"Hamming {HammingDistance}/64";
        public string MatchDisplay => IsMatch ? "Frame match" : "Frame mismatch";
    }

    private sealed record PreviewTarget(string Path, double TimestampSeconds, string Label, string InitialStatus);

    private async Task RunWorkerDiagnosticAsync()
    {
        try
        {
            IsBusy = true;
            var status = await _worker.EnsureStartedAsync(_lifetime.Token);
            WorkerStatus = status;
            if (status.Availability == WorkerAvailability.Available)
            {
                StatusText = L.T("Python WorkerのHealth/Versionを確認しました。");
                ErrorText = string.Empty;
            }
            else
            {
                ErrorText = status.UserMessage ?? L.T("Python Workerを利用できません。");
                StatusText = L.T("Workerの状態とローカルログを確認してください。");
            }
        }
        catch (Exception ex) { ReportUserError(L.T("Python Workerを確認できませんでした。"), ex); }
        finally { IsBusy = false; }
    }

    private void OnWorkerStatusChanged(object? sender, WorkerRuntimeStatus status)
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            _ = dispatcher.InvokeAsync(() => ApplyWorkerStatus(status));
            return;
        }
        ApplyWorkerStatus(status);
    }

    private void ApplyWorkerStatus(WorkerRuntimeStatus status)
    {
        WorkerStatus = status;
        if (status.Availability == WorkerAvailability.Unavailable && status.ErrorCode is not null)
        {
            ErrorText = status.UserMessage ?? L.T("Python Workerを利用できません。");
            StatusText = L.T("Workerの状態とローカルログを確認してください。");
        }
    }

    private void RaiseReadiness() => Raise(nameof(Readiness));

    private void RequestPipelineRun()
    {
        var task = _pipeline.RequestRunAsync(_lifetime.Token);
        lock (_pipelineObservationGate)
        {
            if (ReferenceEquals(_observedPipelineTask, task)) return;
            _observedPipelineTask = task;
        }
        _ = ObservePipelineAsync(task);
    }

    private async Task ObservePipelineAsync(Task task)
    {
        try
        {
            await task;
            lock (_pipelineObservationGate)
                if (!ReferenceEquals(_observedPipelineTask, task)) return;
            await RefreshAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { ReportUserError(L.T("バックグラウンド処理を完了できませんでした。"), ex); }
    }

    private bool CanToggleProcessing() => !IsShuttingDown && PipelineRunState is ProcessingRunState.EnabledIdle or ProcessingRunState.Running or ProcessingRunState.UserStopped;

    private async Task ToggleProcessingAsync()
    {
        if (PipelineRunState == ProcessingRunState.UserStopped)
        {
            StatusText = L.T("処理を開始しています。");
            await _pipeline.StartProcessingAsync(_lifetime.Token);
            await RefreshAsync();
            return;
        }

        if (PipelineRunState is not (ProcessingRunState.EnabledIdle or ProcessingRunState.Running)) return;
        await _pipeline.RequestStopAndDrainAsync(_lifetime.Token);
        await RefreshJobsAsync(_lifetime.Token);
    }

    private void OnPipelineRunStateChanged(object? sender, ProcessingRunStateChangedEventArgs eventArgs)
    {
        void Apply()
        {
            PipelineRunState = eventArgs.State;
            if (eventArgs.State is ProcessingRunState.Running or ProcessingRunState.StopRequested or ProcessingRunState.Draining or ProcessingRunState.ShuttingDown) IsBusy = true;
            else IsBusy = false;
            if (eventArgs.State == ProcessingRunState.ShuttingDown)
                StatusText = L.T("終了処理中 — 開始済みの処理を安全に完了しています。");
            else if (eventArgs.State is ProcessingRunState.StopRequested or ProcessingRunState.Draining)
                StatusText = L.T("停止処理中 — 開始済みの処理を安全に完了しています。");
            else if (eventArgs.State == ProcessingRunState.UserStopped)
                StatusText = L.T("処理は停止しています。未開始の処理は保持されています。");
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(Apply);
        else Apply();
    }

    private void OnThumbnailReady(object? sender, ThumbnailReadyEventArgs eventArgs)
    {
        _ = UpdateVisibleThumbnailAsync(eventArgs);
    }

    private async Task UpdateVisibleThumbnailAsync(ThumbnailReadyEventArgs ready)
    {
        try
        {
            if (_lifetime.IsCancellationRequested) return;
            var current = await _library.GetMediaFileAsync(ready.MediaFileId, _lifetime.Token);
            if (current is not { PresenceState: PresenceState.Present } || current.MediaContentId != ready.MediaContentId) return;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) return;
            await dispatcher.InvokeAsync(() =>
            {
                if (_lifetime.IsCancellationRequested) return;
                for (var index = 0; index < MediaItems.Count; index++)
                {
                    var item = MediaItems[index];
                    if (item.MediaFileId != ready.MediaFileId || !string.Equals(item.Path, current.Path, StringComparison.OrdinalIgnoreCase)) continue;
                    var updated = item with { ThumbnailRelativePath = ready.RelativePath };
                    MediaItems[index] = updated;
                    if (SelectedMedia?.MediaFileId == ready.MediaFileId) SelectedMedia = updated;
                    break;
                }
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.Write("Warning", "LibraryThumbnailRefreshFailed", new { ready.MediaFileId }, ex); }
    }

    private void ReportUserError(string message, Exception ex)
    {
        ErrorText = message;
        StatusText = L.T("エラーが発生しました。下部のJobs/Errorsとローカルログを確認してください。");
        _log.Write("Error", "UiOperationFailed", new { Operation = message }, ex);
    }

    public void BeginTerminalShutdown()
    {
        lock (_shutdownGate)
        {
            if (_terminalPipelineDrain is not null) return;
            IsShuttingDown = true;
            StatusText = L.T("終了処理中 — 開始済みの処理を安全に完了しています。");
            _searchCts?.Cancel();
            _semanticSearchCts?.Cancel();
            _previewCts?.Cancel();
            _unifiedReviewCts?.Cancel();
            _terminalPipelineDrain = _pipeline.RequestShutdownAndDrainAsync();
            _log.Write("Information", "GracefulShutdownProcessingDrainRequested", new { });
        }
    }

    public Task ShutdownAsync()
    {
        BeginTerminalShutdown();
        TaskCompletionSource<bool> completion;
        lock (_shutdownGate)
        {
            if (_shutdownCompletion is not null) return _shutdownCompletion.Task;
            completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _shutdownCompletion = completion;
        }
        _ = ShutdownCoreAsync(completion);
        return completion.Task;
    }

    private async Task ShutdownCoreAsync(TaskCompletionSource<bool> completion)
    {
        try
        {
            await _terminalPipelineDrain!;
            _log.Write("Information", "GracefulShutdownProcessingDrainCompleted", new { });
            await _worker.DisposeAsync();
            _log.Write("Information", "GracefulShutdownWorkerDisposed", new { });
            _pipeline.Dispose();
            _log.Write("Information", "GracefulShutdownPipelineDisposed", new { });
            _worker.StatusChanged -= OnWorkerStatusChanged;
            _pipeline.RunStateChanged -= OnPipelineRunStateChanged;
            _pipeline.ThumbnailReady -= OnThumbnailReady;
            _lifetime.Cancel();
            _searchCts?.Dispose();
            _semanticSearchCts?.Dispose();
            CancelPreviewRequest();
            _unifiedReviewCts?.Dispose();
            _lifetime.Dispose();
            _disposed = true;
            _log.Write("Information", "GracefulShutdownLifetimeDisposed", new { });
            completion.TrySetResult(true);
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    public void ReportGracefulShutdownFailure(Exception exception)
    {
        ErrorText = L.T("終了処理を安全に完了できませんでした。アプリケーションは開いたままです。");
        StatusText = L.T("終了処理で問題が発生しました。ローカルログを確認してください。");
        _log.Write("Error", "GracefulShutdownFailed", new { Operation = "ApplicationShutdown" }, exception);
    }

    public void Dispose()
    {
        if (_disposed) return;
        // Normal WPF close already awaited ShutdownAsync. This synchronous fallback is
        // retained for startup-failure/test cleanup and runs away from the UI context.
        Task.Run(ShutdownAsync).GetAwaiter().GetResult();
    }
}
