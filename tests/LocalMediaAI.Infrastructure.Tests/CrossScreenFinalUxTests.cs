using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Xml.Linq;
using LocalMediaAI.App;
using LocalMediaAI.App.ViewModels;
using LocalMediaAI.Core;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed partial class LibraryScopeFoundationTests
{
    [Fact]
    public async Task CrossScreenRealWpfRevealsOffPageRowAndRestoresInvokingFocus()
    {
        var root = await SeedRootAsync("FocusPaged", 121); await ReloadAsync();
        _viewModel.CurrentLibraryScope = Scope(root); await _viewModel.ScopeRefreshTask;
        var target = (await _library.QueryMediaAsync(root.LibraryRootId, null, 0, 500, CancellationToken.None)).Items[^1];
        _search.ImmediateResults = [AiCandidate(target)]; _viewModel.SemanticSearchText = "海"; await SearchAsync();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                window = new MainWindow { DataContext = _viewModel, Width = 1080, Height = 680, Left = -20000, ShowActivated = false, ShowInTaskbar = false }; window.Show();
                var tabs = (TabControl)window.FindName("TaskWorkspaceTabs"); tabs.SelectedIndex = 1; window.UpdateLayout();
                var search = (AiSearchWorkspace)window.FindName("SearchFirstWorkspace");
                ((CheckBox)search.FindName("EvidenceToggle")).IsChecked = true; window.UpdateLayout();
                var invoker = LibraryVisuals(search).OfType<Button>().Single(button => button.IsVisible && ReferenceEquals(button.Command, _viewModel.AiSearchPreviewCommand));
                invoker.Focus(); FocusManager.SetFocusedElement(window, invoker);
                var previewProperty = typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsPreviewOpen))!;
                previewProperty.SetValue(_viewModel, true); DrainCrossScreenUi();
                previewProperty.SetValue(_viewModel, false); DrainCrossScreenUi();
                Assert.Same(invoker, FocusManager.GetFocusedElement(window));
                previewProperty.SetValue(_viewModel, true); DrainCrossScreenUi();
                _viewModel.CurrentLibraryScope = Scope(_rootA); AiPumpUntil(() => _viewModel.ScopeRefreshTask.IsCompleted); DrainCrossScreenUi();
                Assert.False(_viewModel.AiSearchHasEvidence);
                Assert.Same(search.FindName("QueryInput"), FocusManager.GetFocusedElement(window));
                _viewModel.CurrentLibraryScope = Scope(root); AiPumpUntil(() => _viewModel.ScopeRefreshTask.IsCompleted);
                _viewModel.SemanticSearchCommand.Execute(null); AiPumpUntil(() => !_viewModel.IsSemanticSearchBusy);
                _viewModel.ShowAiSearchResultInLibraryCommand.Execute(null); AiPumpUntil(() => tabs.SelectedIndex == 0); DrainCrossScreenUi();
                var library = (LibraryWorkspace)window.FindName("MediaLibraryWorkspace"); var table = (DataGrid)library.FindName("MediaTable");
                Assert.Equal(1, _viewModel.CurrentPage); Assert.Equal(target.MediaFileId, _viewModel.SelectedMediaFileId);
                var row = Assert.IsType<DataGridRow>(table.ItemContainerGenerator.ContainerFromItem(_viewModel.SelectedMedia));
                Assert.True(row.IsVisible);
                var focusedCell = Assert.IsType<DataGridCell>(FocusManager.GetFocusedElement(window));
                Assert.Same(_viewModel.SelectedMedia, focusedCell.DataContext);
                var tableBounds = new Rect(new Point(), table.RenderSize); var rowBounds = new Rect(row.TranslatePoint(new Point(), table), row.RenderSize);
                Assert.True(tableBounds.IntersectsWith(rowBounds));
                var opening = (TextBox)library.FindName("MediaSearch"); opening.Focus(); FocusManager.SetFocusedElement(window, opening);
                var activity = (Expander)window.FindName("IssuesPane"); activity.IsExpanded = true; DrainCrossScreenUi(); activity.IsExpanded = false; DrainCrossScreenUi();
                Assert.Same(opening, FocusManager.GetFocusedElement(window));
                var handoff = _viewModel.HandoffMediaToOperationAsync(target.MediaFileId, "fixture"); AiPumpUntil(() => handoff.IsCompleted); Assert.True(handoff.GetAwaiter().GetResult()); DrainCrossScreenUi();
                var operation = (FileOperationWorkspace)window.FindName("ProgressiveOperationWorkspace");
                var selector = (ComboBox)operation.FindName("SingleOperationSelector"); Assert.Same(selector, FocusManager.GetFocusedElement(window));
                selector.Focus(); FocusManager.SetFocusedElement(window, selector);
                ((Expander)operation.FindName("TargetSelector")).IsExpanded = true; DrainCrossScreenUi();
                Assert.True(operation.DismissTargetSelector()); DrainCrossScreenUi();
                Assert.Same(selector, FocusManager.GetFocusedElement(window)); Assert.Null(_viewModel.OperationPlanningType);
                var back = LibraryVisuals(operation).OfType<Button>().Single(button => Equals(button.Content, "ライブラリから選択"));
                back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); DrainCrossScreenUi();
                Assert.Equal(0, tabs.SelectedIndex); Assert.Same(opening, FocusManager.GetFocusedElement(window));
                Assert.Equal(target.MediaFileId, _viewModel.OperationTarget!.MediaFileId);
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
            finally { if (window is not null) { window.DataContext = null; window.UpdateLayout(); window.AuthorizeFinalClose(); } }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); try { await done.Task; } finally { thread.Join(); }
    }
    private static void DrainCrossScreenUi()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame);
    }

    [Fact]
    public async Task CrossScreenRevealFindsPageByIdentityWithoutPathOrProcessingMutation()
    {
        var root = await SeedRootAsync("Paged", 121);
        await ReloadAsync();
        _viewModel.CurrentLibraryScope = Scope(root); await _viewModel.ScopeRefreshTask;
        var all = await _library.QueryMediaAsync(root.LibraryRootId, null, 0, 500, CancellationToken.None);
        var target = all.Items[^1];
        Assert.Equal(120, await _library.FindLibraryMediaOffsetAsync(target.MediaFileId, root.LibraryRootId, null, null, false, CancellationToken.None));
        Assert.DoesNotContain(_viewModel.MediaItems, item => item.MediaFileId == target.MediaFileId);
        _search.ImmediateResults = [AiCandidate(target) with { Path = @"E:\old\wrong.mp4" }];
        _viewModel.SemanticSearchText = "海"; await SearchAsync();
        var navigated = false; _viewModel.AiSearchLibraryRequested += (_, _) => navigated = true;
        var roots = await _library.GetLibraryRootsAsync(CancellationToken.None);
        _viewModel.ShowAiSearchResultInLibraryCommand.Execute(null); await WaitForAsync(() => navigated);
        Assert.Equal(1, _viewModel.CurrentPage);
        Assert.Equal(target.MediaFileId, _viewModel.SelectedMediaFileId);
        Assert.Equal(target.Path, _viewModel.SelectedMedia!.Path);
        Assert.Equal(root.LibraryRootId, _viewModel.CurrentLibraryScope.LibraryRootId);
        Assert.Null(_viewModel.OperationTarget);
        Assert.Empty(await _jobs.GetRecentAsync(50, CancellationToken.None));
        Assert.Equal(roots, await _library.GetLibraryRootsAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrossScreenFilteredRevealRequiresExplicitRecoveryAndKeepsScopeAndTarget(bool tag)
    {
        _viewModel.CurrentLibraryScope = Scope(_rootA); await _viewModel.ScopeRefreshTask;
        var target = _viewModel.MediaItems[0];
        Assert.True(await _viewModel.HandoffMediaToOperationAsync(target.MediaFileId, "fixture"));
        var operationTarget = _viewModel.OperationTarget;
        _search.ImmediateResults = [AiCandidate(target)];
        _viewModel.SemanticSearchText = "海"; await SearchAsync();
        if (tag) { _viewModel.TagFilterText = "no-such-tag"; _viewModel.ApplyTagFilterCommand.Execute(null); }
        else _viewModel.SearchText = "no-such-media";
        await WaitForAsync(() => !_viewModel.LibraryIsLoading && _viewModel.MediaItems.Count == 0);
        var navigations = 0; _viewModel.AiSearchLibraryRequested += (_, _) => navigations++;
        _viewModel.ShowAiSearchResultInLibraryCommand.Execute(null); await WaitForAsync(() => _viewModel.AiSearchCanClearLibraryFilters);
        Assert.Contains("現在の絞り込み", _viewModel.AiSearchLocalFeedback);
        Assert.Equal(tag ? "no-such-tag" : "", _viewModel.TagFilterText);
        Assert.Equal(tag ? "" : "no-such-media", _viewModel.SearchText);
        Assert.Equal(0, navigations);
        _viewModel.ClearLibraryFiltersAndRevealCommand.Execute(null); await WaitForAsync(() => navigations == 1);
        Assert.Equal(string.Empty, _viewModel.SearchText); Assert.Equal(string.Empty, _viewModel.TagFilterText);
        Assert.Equal(_rootA.LibraryRootId, _viewModel.CurrentLibraryScope.LibraryRootId);
        Assert.Equal(target.MediaFileId, _viewModel.SelectedMediaFileId);
        Assert.Same(operationTarget, _viewModel.OperationTarget);
        Assert.False(_viewModel.AiSearchCanClearLibraryFilters);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CrossScreenUnresolvableRevealDoesNotFallbackOrOfferFilterRecovery(int unavailable)
    {
        var target = _viewModel.MediaItems.First(item => item.FileName.StartsWith('B'));
        if (unavailable == 0) { _viewModel.CurrentLibraryScope = Scope(_rootA); await _viewModel.ScopeRefreshTask; }
        if (unavailable == 2) { await _library.UnregisterRootAsync(_rootB.LibraryRootId, DateTimeOffset.UtcNow, CancellationToken.None); await ReloadAsync(); }
        _search.ImmediateResults = [AiCandidate(target) with { MediaFileId = unavailable == 1 ? Guid.NewGuid() : target.MediaFileId, Path = _viewModel.MediaItems[0].Path }];
        _viewModel.SemanticSearchText = "海"; await SearchAsync();
        var scope = _viewModel.CurrentLibraryScope;
        var navigations = 0; _viewModel.AiSearchLibraryRequested += (_, _) => navigations++;
        _viewModel.ShowAiSearchResultInLibraryCommand.Execute(null); await WaitForAsync(() => _viewModel.AiSearchHasFeedback);
        Assert.Contains("現在のライブラリ", _viewModel.AiSearchLocalFeedback);
        Assert.Equal(0, navigations); Assert.Same(scope, _viewModel.CurrentLibraryScope);
        Assert.False(_viewModel.AiSearchCanClearLibraryFilters);
    }

    [Fact]
    public async Task CrossScreenOffsetQueryMatchesExistingTextAndTagOrderingAndMembership()
    {
        var media = _viewModel.MediaItems[1];
        await _library.AddManualMediaTagAsync(media.MediaFileId, "海", null, CancellationToken.None);
        foreach (var root in new Guid?[] { null, _rootA.LibraryRootId, _rootB.LibraryRootId })
        {
            foreach (var search in new[] { "A", "%", "", "A1" })
            {
                var page = await _library.QueryMediaAsync(root, search, 0, 500, CancellationToken.None);
                foreach (var item in _viewModel.MediaItems)
                {
                    var position = page.Items.ToList().FindIndex(row => row.MediaFileId == item.MediaFileId);
                    Assert.Equal(position < 0 ? (int?)null : position, await _library.FindLibraryMediaOffsetAsync(item.MediaFileId, root, search, null, false, CancellationToken.None));
                }
            }
            var tags = await _library.QueryMediaByTagAsync(root, "海", false, 0, 500, CancellationToken.None);
            foreach (var item in _viewModel.MediaItems)
            {
                var position = tags.Items.ToList().FindIndex(row => row.MediaFileId == item.MediaFileId);
                Assert.Equal(position < 0 ? (int?)null : position, await _library.FindLibraryMediaOffsetAsync(item.MediaFileId, root, "ignored-by-existing-tag-query", "海", false, CancellationToken.None));
            }
        }
    }

    [Fact]
    public void CrossScreenTerminologyAndSecondaryActivityHierarchyStayExplicit()
    {
        var directory = Path.Combine(LibraryRepositoryRoot(), "src", "LocalMediaAI.App");
        foreach (var file in new[] { "AiSearchWorkspace.xaml", "LibraryWorkspace.xaml", "ReviewWorkspace.xaml", "FileOperationWorkspace.xaml", "MainWindow.xaml" })
        {
            var document = XDocument.Load(Path.Combine(directory, file));
            foreach (var attribute in document.Descendants().Attributes().Where(attribute => attribute.Name.LocalName is "Content" or "Text" && !attribute.Value.StartsWith('{')))
            {
                Assert.DoesNotContain("Libraryで表示", attribute.Value);
                Assert.DoesNotContain("Preview", attribute.Value);
            }
        }
        var operation = File.ReadAllText(Path.Combine(directory, "FileOperationWorkspace.xaml"));
        Assert.Contains("操作対象と操作", operation); Assert.Contains("最大30件", operation);
        Assert.Contains("見つからない場合", operation); Assert.Contains("ライブラリから選択", operation);
        var main = XDocument.Load(Path.Combine(directory, "MainWindow.xaml"));
        var activity = main.Descendants().Single(element => (string?)element.Attribute("AutomationProperties.AutomationId") == "IssuesPane");
        Assert.Equal("False", (string?)activity.Attribute("IsExpanded"));
        var offline = ReadinessPresentation.Evaluate(new(true, 1, true, RootAvailability.Offline, true, 0, 0, 0, 0, false, WorkerProtocol.WorkerAvailability.NotStarted, 0));
        Assert.Equal(ReadinessAction.Rescan, offline.Action);
        Assert.Contains("GlobalActivity.CompactSummary", activity.ToString());
        Assert.Contains("GlobalActivity.CurrentConditions", activity.ToString());
    }
}

public sealed partial class OperationTargetFoundationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrossScreenReviewHandoffAndConfirmationRollbackFocusCurrentAction(bool rollback)
    {
        if (rollback) await PrepareAsync(final: true);
        else { var row = Review(_a.MediaFileId, _b.MediaFileId); _vm.UnifiedReviewItems.Add(row); _vm.SelectedUnifiedReview = row; await _vm.ReviewEvidenceLoadTask; }
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                window = new MainWindow { DataContext = _vm, Width = 1080, Height = 680, Left = -20000, ShowActivated = false, ShowInTaskbar = false }; window.Show();
                var tabs = (TabControl)window.FindName("TaskWorkspaceTabs");
                if (rollback) window.ShowOperationWorkspaceForRecovery(); else tabs.SelectedIndex = 2;
                window.UpdateLayout();
                var operation = (FileOperationWorkspace)window.FindName("ProgressiveOperationWorkspace");
                if (rollback)
                {
                    var confirmation = (TextBlock)operation.FindName("FinalConfirmationFocusTarget"); confirmation.Focus(); FocusManager.SetFocusedElement(window, confirmation);
                    _vm.OperationDestinationPath = Path.Combine(_root.ConfiguredPath, "updated-name.mp4");
                    CrossFocusPump(() => _vm.OperationInputChangeTask.IsCompleted); CrossFocusDrain();
                    Assert.Equal(1, _vm.OperationCurrentStep); Assert.False(confirmation.IsVisible);
                }
                else
                {
                    var review = _vm.SelectedUnifiedReview;
                    _vm.HandoffReviewSecondaryMediaCommand.Execute(null);
                    CrossFocusPump(() => window.IsOperationWorkspaceOpen); CrossFocusDrain();
                    Assert.Equal(_b.MediaFileId, _vm.OperationTarget!.MediaFileId); Assert.Null(_vm.OperationPlanningType);
                    Assert.Equal("Preferred", review!.HumanDecision);
                }
                var selector = (ComboBox)operation.FindName("SingleOperationSelector");
                Assert.True(selector.IsVisible); Assert.True(selector.IsEnabled); Assert.Same(selector, FocusManager.GetFocusedElement(window));
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
            finally { if (window is not null) { window.DataContext = null; window.UpdateLayout(); window.AuthorizeFinalClose(); } }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); try { await done.Task; } finally { thread.Join(); }
    }
    private static void CrossFocusDrain()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame);
    }
    private static void CrossFocusPump(Func<bool> ready)
    {
        var frame = new DispatcherFrame(); var deadline = DateTime.UtcNow.AddSeconds(10); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (ready() || DateTime.UtcNow > deadline) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Assert.True(ready());
    }
}
