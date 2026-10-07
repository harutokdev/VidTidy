using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using LocalMediaAI.App.ViewModels;

namespace LocalMediaAI.App;

public partial class MainWindow : Window
{
    public static readonly DependencyProperty IsOperationWorkspaceOpenProperty = DependencyProperty.Register(
        nameof(IsOperationWorkspaceOpen), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
    public static readonly DependencyProperty OperationReturnLabelProperty = DependencyProperty.Register(
        nameof(OperationReturnLabel), typeof(string), typeof(MainWindow), new PropertyMetadata(L.T("← ライブラリへ戻る")));
    public bool IsOperationWorkspaceOpen
    {
        get => (bool)GetValue(IsOperationWorkspaceOpenProperty);
        private set => SetValue(IsOperationWorkspaceOpenProperty, value);
    }
    public string OperationReturnLabel
    {
        get => (string)GetValue(OperationReturnLabelProperty);
        private set => SetValue(OperationReturnLabelProperty, value);
    }
    private MainViewModel? _viewModel;
    private int _operationReturnWorkspace;
    private bool _finalCloseAuthorized;
    private IInputElement? _previewReturnFocus;
    private int _previewReturnWorkspace;
    private object? _previewReturnSelection;
    private IInputElement? _activityReturnFocus;

    public Func<Task>? GracefulShutdownRequest { get; set; }
    public async void ShowLibraryForTargetSelection()
    {
        if (!await LeaveOperationWorkspaceAsync(0)) return;
        TaskWorkspaceTabs.SelectedIndex = 0;
        Dispatcher.BeginInvoke(() => MediaLibraryWorkspace.FocusSearch());
    }
    // Internal recovery and render-test entry; no primary-navigation control calls this.
    public void ShowOperationWorkspaceForRecovery() => OnOperationHandoffApplied(this, EventArgs.Empty);

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        IssuesPane.Focusable = true;
        IssuesPane.IsTabStop = false;
        IssuesPane.Expanded += (_, e) => { if (ReferenceEquals(e.OriginalSource, IssuesPane)) _activityReturnFocus = Keyboard.FocusedElement ?? FocusManager.GetFocusedElement(this); };
        IssuesPane.Collapsed += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, IssuesPane)) return;
            Dispatcher.BeginInvoke(() => { if (_activityReturnFocus is UIElement { IsVisible: true, IsEnabled: true } control) control.Focus(); else IssuesPane.Focus(); });
        };
    }

    public void AuthorizeFinalClose()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(AuthorizeFinalClose);
            return;
        }
        _finalCloseAuthorized = true;
        Close();
    }

    private async void Window_OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (_finalCloseAuthorized) return;
        eventArgs.Cancel = true;
        var request = GracefulShutdownRequest;
        if (request is null) return;
        try { await request(); }
        catch { /* The coordinator reports failure and deliberately withholds final close. */ }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs eventArgs)
    {
        if (_viewModel is not null) { _viewModel.PropertyChanged -= OnViewModelPropertyChanged; _viewModel.OperationHandoffApplied -= OnOperationHandoffApplied; _viewModel.OperationLibraryRequested -= OnOperationLibraryRequested; _viewModel.AiSearchLibraryRequested -= OnAiSearchLibraryRequested; }
        _viewModel = eventArgs.NewValue as MainViewModel;
        if (_viewModel is not null) { _viewModel.PropertyChanged += OnViewModelPropertyChanged; _viewModel.OperationHandoffApplied += OnOperationHandoffApplied; _viewModel.OperationLibraryRequested += OnOperationLibraryRequested; _viewModel.AiSearchLibraryRequested += OnAiSearchLibraryRequested; }
    }

    private void OnOperationHandoffApplied(object? sender, EventArgs eventArgs)
    {
        if (!IsOperationWorkspaceOpen)
        {
            _operationReturnWorkspace = TaskWorkspaceTabs.SelectedIndex is >= 0 and <= 2
                ? TaskWorkspaceTabs.SelectedIndex
                : _viewModel?.OperationTarget?.SourceContext.StartsWith("Review", StringComparison.OrdinalIgnoreCase) == true ? 2 : 0;
            OperationReturnLabel = _operationReturnWorkspace switch { 1 => L.T("← AI検索へ戻る"), 2 => L.T("← レビューへ戻る"), _ => L.T("← ライブラリへ戻る") };
        }
        IsOperationWorkspaceOpen = true;
        TaskWorkspaceTabs.SelectedIndex = -1;
        Dispatcher.BeginInvoke(() => { if (IsOperationWorkspaceOpen) ProgressiveOperationWorkspace.FocusOperationSelector(); });
    }
    private async void OnOperationLibraryRequested(object? sender, EventArgs eventArgs)
    {
        if (await LeaveOperationWorkspaceAsync(0)) TaskWorkspaceTabs.SelectedIndex = 0;
    }
    private async void OperationWorkspaceBack_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        var destination = _operationReturnWorkspace;
        if (!await LeaveOperationWorkspaceAsync(destination)) return;
        if (destination == 2) Dispatcher.BeginInvoke(() => EvidenceReviewWorkspace.FocusQueue());
        else Dispatcher.BeginInvoke(() => MediaLibraryWorkspace.FocusTable());
    }
    private async Task<bool> LeaveOperationWorkspaceAsync(int destination)
    {
        if (!IsOperationWorkspaceOpen) return true;
        if (_viewModel is null || !await _viewModel.TryLeaveOperationWorkspaceAsync()) return false;
        IsOperationWorkspaceOpen = false;
        TaskWorkspaceTabs.SelectedIndex = destination;
        return true;
    }
    private void OnAiSearchLibraryRequested(object? sender, EventArgs eventArgs)
    {
        var mediaFileId = _viewModel?.SelectedMediaFileId;
        TaskWorkspaceTabs.SelectedIndex = 0;
        Dispatcher.BeginInvoke(() => { if (TaskWorkspaceTabs.SelectedIndex == 0 && _viewModel?.SelectedMediaFileId == mediaFileId) MediaLibraryWorkspace.RevealSelectedMedia(); });
    }
    private object? CurrentPreviewSelection() => TaskWorkspaceTabs.SelectedIndex switch
    {
        0 => _viewModel?.SelectedMediaFileId,
        1 => _viewModel?.SelectedSemanticResult,
        2 => _viewModel?.SelectedUnifiedReview,
        _ => null
    };

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (_viewModel is null) return;
        if (eventArgs.PropertyName == nameof(MainViewModel.IsPreviewOpen))
        {
            if (_viewModel.IsPreviewOpen)
            {
                _previewReturnFocus = Keyboard.FocusedElement ?? FocusManager.GetFocusedElement(this);
                _previewReturnWorkspace = TaskWorkspaceTabs.SelectedIndex;
                _previewReturnSelection = CurrentPreviewSelection();
                Dispatcher.BeginInvoke(() => PreviewCloseButton.Focus());
            }
            else
            {
                PreviewMediaElement.Stop();
                Dispatcher.BeginInvoke(() =>
                {
                    if (_previewReturnWorkspace == TaskWorkspaceTabs.SelectedIndex && Equals(_previewReturnSelection, CurrentPreviewSelection()) && _previewReturnFocus is UIElement { IsVisible: true, IsEnabled: true } element) { (element as FrameworkElement)?.BringIntoView(); element.Focus(); }
                    else if (TaskWorkspaceTabs.SelectedIndex == 1) SearchFirstWorkspace.FocusResultsOrQuery();
                    else if (TaskWorkspaceTabs.SelectedIndex == 2) EvidenceReviewWorkspace.FocusQueue();
                    else if (TaskWorkspaceTabs.SelectedIndex == 0) MediaLibraryWorkspace.FocusTable();
                });
            }
        }
        if (eventArgs.PropertyName == nameof(MainViewModel.PreviewMediaPath))
        {
            PreviewMediaElement.Stop();
            PreviewMediaElement.Source = string.IsNullOrWhiteSpace(_viewModel.PreviewMediaPath)
                ? null
                : new Uri(_viewModel.PreviewMediaPath, UriKind.Absolute);
        }
        else if (eventArgs.PropertyName == nameof(MainViewModel.PreviewSeekRequestId))
        {
            ApplyMatchedTimestamp();
        }
        else if (eventArgs.PropertyName == nameof(MainViewModel.PreviewPlaybackRequestId))
        {
            if (_viewModel.PreviewPlaybackRequested) PreviewMediaElement.Play();
            else PreviewMediaElement.Pause();
        }
    }

    private void PreviewMediaElement_OnMediaOpened(object sender, RoutedEventArgs eventArgs) => ApplyMatchedTimestamp();

    private void PreviewMediaElement_OnMediaFailed(object sender, ExceptionRoutedEventArgs eventArgs)
    {
        PreviewMediaElement.Stop();
        _viewModel?.ReportPreviewPlaybackFailure();
    }

    private void ApplyMatchedTimestamp()
    {
        if (_viewModel is null || PreviewMediaElement.Source is null || !PreviewMediaElement.NaturalDuration.HasTimeSpan) return;
        var requested = TimeSpan.FromSeconds(Math.Max(0d, _viewModel.PreviewTimestampSeconds));
        PreviewMediaElement.Position = requested <= PreviewMediaElement.NaturalDuration.TimeSpan
            ? requested
            : PreviewMediaElement.NaturalDuration.TimeSpan;
    }

    private async void ReadinessAction_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_viewModel is null) return;
        var action = _viewModel.Readiness.Action;
        if (IsOperationWorkspaceOpen && (action is ReadinessAction.OpenLibraryRoots or ReadinessAction.OpenAiSearch or ReadinessAction.OpenReview) &&
            !await LeaveOperationWorkspaceAsync(_operationReturnWorkspace)) return;
        switch (action)
        {
            case ReadinessAction.RegisterFolder:
                Execute(_viewModel.RegisterRootCommand);
                break;
            case ReadinessAction.OpenLibraryRoots:
                TaskWorkspaceTabs.SelectedIndex = 0;
                Dispatcher.BeginInvoke(() => MediaLibraryWorkspace.FocusRegisteredFolders());
                break;
            case ReadinessAction.Refresh:
                Execute(_viewModel.RefreshCommand);
                break;
            case ReadinessAction.Rescan:
                Execute(_viewModel.RescanCommand);
                break;
            case ReadinessAction.RecheckWorker:
                Execute(_viewModel.WorkerDiagnosticCommand);
                break;
            case ReadinessAction.OpenAiSearch:
                TaskWorkspaceTabs.SelectedIndex = 1;
                Dispatcher.BeginInvoke(() => SearchFirstWorkspace.FocusQuery());
                break;
            case ReadinessAction.OpenReview:
                TaskWorkspaceTabs.SelectedIndex = 2;
                Dispatcher.BeginInvoke(() => EvidenceReviewWorkspace.FocusQueue());
                break;
            case ReadinessAction.OpenJobs:
                IssuesPane.IsExpanded = true;
                _viewModel.SelectOnlyActionRequiredJobIssue();
                Dispatcher.BeginInvoke(() =>
                {
                    IssuesPane.BringIntoView();
                    if (_viewModel.SelectedJobIssue is null) RecentJobsList.Focus();
                    else JobIssueDetail.Focus();
                });
                break;
        }
    }

    private async void ScanCurrentProblemRescan_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_viewModel is null || sender is not FrameworkElement { DataContext: CurrentProblemPresentation { LibraryRootId: { } rootId } }) return;
        await _viewModel.RescanCurrentProblemRootAsync(rootId);
    }

    private static void Execute(AsyncCommand command)
    {
        if (command.CanExecute(null)) command.Execute(null);
    }

    private Organize.OrganizeViewModel? _organize;

    /// <summary>Shows the redesigned organize screen on top of the legacy workspaces, which stay reachable from its menu.</summary>
    public void AttachOrganize(Organize.OrganizeViewModel organize)
    {
        _organize = organize;
        OrganizeScreen.DataContext = organize;
        OrganizeScreen.AddFolderRequested += (_, _) => { if (_viewModel?.RegisterRootCommand.CanExecute(null) == true) _viewModel.RegisterRootCommand.Execute(null); };
        OrganizeScreen.OpenLegacyRequested += (_, _) => ShowLegacyScreen();
        BackToOrganizeButton.Visibility = Visibility.Visible;
        LegacyScreen.Visibility = Visibility.Collapsed;
        OrganizeScreen.Visibility = Visibility.Visible;
    }

    private void ShowLegacyScreen()
    {
        OrganizeScreen.Visibility = Visibility.Collapsed;
        LegacyScreen.Visibility = Visibility.Visible;
    }

    private async void BackToOrganize_OnClick(object sender, RoutedEventArgs e)
    {
        LegacyScreen.Visibility = Visibility.Collapsed;
        OrganizeScreen.Visibility = Visibility.Visible;
        if (_organize is null) return;
        try { await _organize.LoadAsync(CancellationToken.None); }
        catch (Exception ex) { MessageBox.Show(this, L.T("整理画面を最新の状態にできませんでした。") + Environment.NewLine + ex.Message, "VidTidy", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (_viewModel is null || OrganizeScreen.Visibility == Visibility.Visible) return;
        if (eventArgs.Key == Key.Escape && TaskWorkspaceTabs.SelectedIndex == 0 && MediaLibraryWorkspace.DismissSettings())
        {
            eventArgs.Handled = true;
            return;
        }
        if (HandleSearchShortcut(eventArgs.Key, Keyboard.Modifiers))
        {
            eventArgs.Handled = true;
            return;
        }
        if (eventArgs.Key == Key.Enter && SemanticSearchInput.IsKeyboardFocusWithin && _viewModel.SemanticSearchCommand.CanExecute(null))
        {
            _viewModel.SemanticSearchCommand.Execute(null);
            eventArgs.Handled = true;
            return;
        }
        if (eventArgs.Key == Key.Enter && (SemanticResultsDataGrid.IsKeyboardFocusWithin || DuplicateMembersDataGrid.IsKeyboardFocusWithin || NearDuplicateMembersDataGrid.IsKeyboardFocusWithin || ContentSimilarityMembersDataGrid.IsKeyboardFocusWithin || TagReviewDataGrid.IsKeyboardFocusWithin) && _viewModel.OpenPreviewCommand.CanExecute(null))
        {
            _viewModel.OpenPreviewCommand.Execute(null);
            eventArgs.Handled = true;
            return;
        }
        if (eventArgs.Key == Key.Escape)
        {
            if (_viewModel.IsPreviewOpen && _viewModel.ClosePreviewCommand.CanExecute(null))
                _viewModel.ClosePreviewCommand.Execute(null);
            else if (IsOperationWorkspaceOpen && ProgressiveOperationWorkspace.DismissTargetSelector()) { }
            else if (IssuesPane.IsExpanded) IssuesPane.IsExpanded = false;
            else if (_viewModel.IsSemanticSearchBusy && _viewModel.CancelSemanticSearchCommand.CanExecute(null))
                _viewModel.CancelSemanticSearchCommand.Execute(null);
            eventArgs.Handled = true;
        }
    }

    public bool HandleSearchShortcut(Key key, ModifierKeys modifiers)
    {
        if (key != Key.F || !modifiers.HasFlag(ModifierKeys.Control)) return false;
        if (IsOperationWorkspaceOpen)
        {
            if (modifiers.HasFlag(ModifierKeys.Shift)) return false;
            ProgressiveOperationWorkspace.FocusTargetSearch();
            return true;
        }
        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            TaskWorkspaceTabs.SelectedIndex = 1;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                if (TaskWorkspaceTabs.SelectedIndex == 1) SearchFirstWorkspace.FocusQuery();
            });
        }
        else switch (TaskWorkspaceTabs.SelectedIndex)
        {
            case 0: MediaLibraryWorkspace.FocusSearch(); break;
            case 1: SearchFirstWorkspace.FocusQuery(); break;
            case 2: EvidenceReviewWorkspace.FocusFilter(); break;
        }
        return true;
    }
}
