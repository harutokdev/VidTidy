using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LocalMediaAI.App;
using LocalMediaAI.App.ViewModels;
using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;
using LocalMediaAI.WorkerProtocol;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed partial class LibraryScopeFoundationTests
{
    private static SemanticSearchCandidate AiCandidate(MediaListItem media, double score = 0.9, double time = 12.345) => new(
        media.MediaFileId, media.FileName, media.Path, time, score, Guid.NewGuid(), Guid.NewGuid(), null, 45);

    [Fact]
    public async Task AiSearchRedesignBeforeSearchRankingSelectionAndScopeTransition()
    {
        Assert.Equal(AiSearchViewState.BeforeSearch, _viewModel.AiSearchState);
        Assert.Contains("何を探しますか", _viewModel.AiSearchStateMessage); Assert.False(_viewModel.AiSearchHasResults);
        var media = _viewModel.MediaItems.ToArray();
        _viewModel.SelectedMedia = media[2];
        await _viewModel.HandoffMediaToOperationAsync(media[2].MediaFileId, "test");
        var target = _viewModel.OperationTarget;
        _search.ImmediateResults = [AiCandidate(media[0], 0.94), AiCandidate(media[1], 0.80)];
        _viewModel.SemanticSearchText = "海辺の犬";
        Assert.Empty(_search.RequestedRoots);
        _viewModel.CurrentLibraryScope = Scope(_rootA); await _viewModel.ScopeRefreshTask;
        Assert.Equal(AiSearchViewState.BeforeSearch, _viewModel.AiSearchState);
        await SearchAsync();
        Assert.Equal(AiSearchViewState.Results, _viewModel.AiSearchState);
        Assert.Equal(_search.ImmediateResults, _viewModel.SemanticResults);
        Assert.Equal(_search.ImmediateResults[0], _viewModel.SelectedSemanticResult); Assert.False(_viewModel.IsPreviewOpen);
        var librarySelection = _viewModel.SelectedMedia;
        _viewModel.SelectedSemanticResult = _search.ImmediateResults[1];
        Assert.Same(librarySelection, _viewModel.SelectedMedia); Assert.Same(target, _viewModel.OperationTarget);
        Assert.True(_viewModel.AiSearchHasEvidence); Assert.Contains("00:00:12.345", _viewModel.AiSearchEvidenceTime);
        _viewModel.CurrentLibraryScope = Scope(_rootB); await _viewModel.ScopeRefreshTask;
        Assert.Equal("海辺の犬", _viewModel.SemanticSearchText); Assert.Single(_search.RequestedRoots);
        Assert.Equal(AiSearchViewState.ScopeChanged, _viewModel.AiSearchState);
        Assert.Empty(_viewModel.SemanticResults); Assert.Null(_viewModel.SelectedSemanticResult);
        Assert.False(_viewModel.AiSearchHasEvidence); Assert.False(_viewModel.AiSearchPreviewCommand.CanExecute(null));
        Assert.Same(target, _viewModel.OperationTarget);
    }

    [Fact]
    public async Task AiSearchRedesignEmptyFailureAndCancelledAreDistinctAndErrorsAreGeneral()
    {
        _viewModel.SemanticSearchText = "海"; _search.ImmediateResults = [];
        await SearchAsync(); Assert.Equal(AiSearchViewState.NoResults, _viewModel.AiSearchState);
        _search.Failure = new InvalidOperationException("RAW_INTERNAL_SECRET");
        await SearchAsync(); Assert.Equal(AiSearchViewState.Failed, _viewModel.AiSearchState);
        Assert.DoesNotContain("RAW_INTERNAL_SECRET", _viewModel.AiSearchStateMessage); Assert.Contains("再試行", _viewModel.AiSearchStateMessage);
        _search.Failure = null; var completion = new TaskCompletionSource<IReadOnlyList<SemanticSearchCandidate>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _search.DelayedResponse = completion; _viewModel.SemanticSearchCommand.Execute(null);
        Assert.Equal(AiSearchViewState.Searching, _viewModel.AiSearchState); Assert.False(_viewModel.AiSearchHasResults);
        _viewModel.CancelSemanticSearchCommand.Execute(null); completion.SetResult([AiCandidate(_viewModel.MediaItems.First())]);
        await WaitForAsync(() => !_viewModel.IsSemanticSearchBusy);
        Assert.Equal(AiSearchViewState.Cancelled, _viewModel.AiSearchState); Assert.False(_viewModel.AiSearchHasEvidence);
    }

    [Fact]
    public async Task AiSearchRedesignNewSearchAndScopeChangeSuppressOldPresentationCompletion()
    {
        var a = AiCandidate(_viewModel.MediaItems[0]); var b = AiCandidate(_viewModel.MediaItems[2]);
        var old = new TaskCompletionSource<IReadOnlyList<SemanticSearchCandidate>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _search.DelayedResponse = old; _viewModel.SemanticSearchText = "old"; _viewModel.SemanticSearchCommand.Execute(null);
        var current = new TaskCompletionSource<IReadOnlyList<SemanticSearchCandidate>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _search.DelayedResponse = current; _viewModel.SemanticSearchText = "new"; _viewModel.SemanticSearchCommand.Execute(null);
        current.SetResult([a]); await WaitForAsync(() => !_viewModel.IsSemanticSearchBusy);
        old.SetResult([b]); await Task.Delay(100);
        Assert.Equal(a, Assert.Single(_viewModel.SemanticResults)); Assert.Equal(AiSearchViewState.Results, _viewModel.AiSearchState);
        var stale = new TaskCompletionSource<IReadOnlyList<SemanticSearchCandidate>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _search.DelayedResponse = stale; _viewModel.SemanticSearchCommand.Execute(null);
        Assert.False(_viewModel.AiSearchHasEvidence); // Old results are not current while the new search runs.
        _viewModel.CurrentLibraryScope = Scope(_rootB); await _viewModel.ScopeRefreshTask;
        stale.SetException(new InvalidOperationException("STALE_FAILURE")); await Task.Delay(100);
        Assert.Equal(AiSearchViewState.ScopeChanged, _viewModel.AiSearchState); Assert.Empty(_viewModel.SemanticResults);
        Assert.Null(_viewModel.SelectedSemanticResult); Assert.Equal("new", _viewModel.SemanticSearchText);
    }

    [Fact]
    public async Task AiSearchRedesignPreviewUsesSearchEvidenceNotOtherWorkspacePreviewTarget()
    {
        var media = _viewModel.MediaItems.ToArray(); _search.ImmediateResults = [AiCandidate(media[0])];
        _viewModel.SemanticSearchText = "犬"; await SearchAsync(); Assert.False(_viewModel.IsPreviewOpen);
        _viewModel.SelectedMedia = media[1]; _viewModel.SelectMediaForPreviewCommand.Execute(null);
        _viewModel.AiSearchPreviewCommand.Execute(null); await WaitForAsync(() => !_viewModel.IsPreviewBusy && _viewModel.IsPreviewOpen);
        Assert.Equal(media[0].Path, _viewModel.PreviewMediaPath); Assert.Equal(12.345, _viewModel.PreviewTimestampSeconds);
        _viewModel.ClosePreviewCommand.Execute(null); await WaitForAsync(() => !_viewModel.IsPreviewOpen);
        Assert.Equal(media[0].MediaFileId, _viewModel.SelectedSemanticResult!.MediaFileId);
        Assert.Equal(media[1].MediaFileId, _viewModel.SelectedMedia!.MediaFileId); Assert.Null(_viewModel.OperationTarget);
    }

    [Fact]
    public async Task AiSearchRedesignLibraryNavigationUsesIdentityAndPreservesDisplayConditions()
    {
        var media = _viewModel.MediaItems.ToArray();
        _search.ImmediateResults = [AiCandidate(media[0]) with { Path = @"E:\old-snapshot\wrong-name.mp4" }];
        _viewModel.SelectedMedia = media[1]; _viewModel.SemanticSearchText = "海"; await SearchAsync();
        Assert.Equal(media[1].MediaFileId, _viewModel.SelectedMedia!.MediaFileId);
        var navigations = 0; _viewModel.AiSearchLibraryRequested += (_, _) => navigations++;
        _viewModel.ShowAiSearchResultInLibraryCommand.Execute(null); await WaitForAsync(() => navigations == 1);
        Assert.Equal(media[0].MediaFileId, _viewModel.SelectedMedia!.MediaFileId); Assert.Equal(media[0].Path, _viewModel.SelectedMedia.Path);
        Assert.Equal(string.Empty, _viewModel.SearchText); Assert.Null(_viewModel.CurrentLibraryScope.LibraryRootId); Assert.Null(_viewModel.OperationTarget);
        _viewModel.SearchText = "no-such-file"; await WaitForAsync(() => !_viewModel.LibraryIsLoading && _viewModel.MediaItems.Count == 0);
        _viewModel.ShowAiSearchResultInLibraryCommand.Execute(null); await WaitForAsync(() => _viewModel.AiSearchLocalFeedback.Length > 0);
        Assert.Equal(1, navigations); Assert.Equal("no-such-file", _viewModel.SearchText);
        Assert.Null(_viewModel.SelectedMedia); Assert.Null(_viewModel.OperationTarget);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void AiSearchRedesignInvalidTimestampIsNotInvented(double timestamp)
        => Assert.Equal("一致時刻を確認できません", MainViewModel.FormatAiSearchTimestamp(timestamp));

    [Fact]
    public void AiSearchRedesignWorkerUnavailableUsesExistingCapabilityWithoutNewProblems()
    {
        var status = new WorkerRuntimeStatus(WorkerAvailability.Unavailable, "RAW_CODE", "RAW_CAPABILITY_MESSAGE", null, null, null, DateTimeOffset.UtcNow);
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.WorkerStatus))!.SetValue(_viewModel, status);
        _viewModel.SemanticSearchText = "海";
        Assert.False(_viewModel.AiSearchCanSubmit); Assert.Contains("現在利用できません", _viewModel.AiSearchCapabilityMessage);
        Assert.DoesNotContain("RAW_CODE", _viewModel.AiSearchCapabilityMessage);
        Assert.Single(_viewModel.GlobalActivity.CurrentConditions, c => c.Kind == CurrentProblemKind.WorkerUnavailable);
    }

    [Fact]
    public async Task AiSearchRedesignKeepsExistingTagFilterAndScopeSemantics()
    {
        _viewModel.CurrentLibraryScope = Scope(_rootA); await _viewModel.ScopeRefreshTask;
        _search.ImmediateResults = [AiCandidate(_viewModel.MediaItems[0]), AiCandidate(_viewModel.MediaItems[1], 0.8)];
        _viewModel.SemanticSearchText = "海"; _viewModel.TagFilterText = "ocean"; _viewModel.TagFilterIncludeSuggested = false;
        await SearchAsync();
        Assert.Equal(AiSearchViewState.NoResults, _viewModel.AiSearchState); Assert.Empty(_viewModel.SemanticResults);
        Assert.Equal("海", _viewModel.SemanticSearchText); Assert.Equal(_rootA.LibraryRootId, _viewModel.CurrentLibraryScope.LibraryRootId);
        Assert.Equal(_rootA.LibraryRootId, Assert.Single(_search.RequestedRoots)); Assert.False(_viewModel.TagFilterIncludeSuggested);
    }

    [Theory]
    [InlineData(1080, 680)]
    [InlineData(1600, 900)]
    public async Task AiSearchRedesignRealWpfBeforeSearchResultsEvidenceAndKeyboard(int width, int height)
    {
        var media = _viewModel.MediaItems.ToArray(); _search.ImmediateResults = [AiCandidate(media[0]), AiCandidate(media[1], 0.8)];
        _viewModel.SemanticSearchText = "海辺の犬";
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            MainWindow? window = null;
            string? framePath = null;
            try
            {
                var relativeFrame = $"ui-search-test/{Guid.NewGuid():N}/matched.png";
                framePath = Path.Combine(AppPaths.Create(AppContext.BaseDirectory).CacheRoot, relativeFrame);
                Directory.CreateDirectory(Path.GetDirectoryName(framePath)!);
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                {
                    drawing.DrawRectangle(Brushes.SteelBlue, null, new Rect(0, 0, 320, 180));
                    drawing.DrawText(new FormattedText("MATCHED FRAME\nUI TEST FIXTURE", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 24, Brushes.White, 1), new Point(30, 55));
                }
                var frame = new RenderTargetBitmap(320, 180, 96, 96, PixelFormats.Pbgra32); frame.Render(visual);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(frame));
                using (var stream = File.Create(framePath)) png.Save(stream);
                _search.ImmediateResults = [AiCandidate(media[0]) with { MatchedFrameRelativePath = relativeFrame }, AiCandidate(media[1], 0.8)];
                window = new MainWindow { DataContext = _viewModel, Width = width, Height = height, Left = -10000, ShowInTaskbar = false, ShowActivated = false };
                window.Show(); var tabs = (TabControl)window.FindName("TaskWorkspaceTabs"); tabs.SelectedIndex = 1; window.UpdateLayout();
                var search = (AiSearchWorkspace)window.FindName("SearchFirstWorkspace");
                Assert.True(search.IsVisible); Assert.False(((Grid)search.FindName("ResultsPanel")).IsVisible);
                Assert.True(((TextBox)search.FindName("QueryInput")).IsVisible); Assert.True(((Button)search.FindName("SearchButton")).IsEnabled);
                Assert.Contains(LibraryVisuals(window).OfType<ComboBox>(), c => c.IsVisible && System.Windows.Automation.AutomationProperties.GetAutomationId(c) == "LibraryScopeSelector");
                SaveAiSearchWindow(window, $"before-{width}x{height}.png");
                tabs.SelectedIndex = 0; Assert.True(window.HandleSearchShortcut(Key.F, ModifierKeys.Control)); Assert.Equal(0, tabs.SelectedIndex);
                Assert.True(window.HandleSearchShortcut(Key.F, ModifierKeys.Control | ModifierKeys.Shift)); Assert.Equal(1, tabs.SelectedIndex);
                Assert.True(window.HandleSearchShortcut(Key.F, ModifierKeys.Control)); Assert.Equal(1, tabs.SelectedIndex);
                Assert.False(window.HandleSearchShortcut(Key.Delete, ModifierKeys.None));
                _viewModel.SemanticSearchCommand.Execute(null); AiPumpUntil(() => !_viewModel.IsSemanticSearchBusy); window.UpdateLayout();
                var table = (DataGrid)search.FindName("ResultsTable"); Assert.True(table.IsVisible); Assert.Equal(2, table.Items.Count); Assert.False(table.CanUserSortColumns);
                SaveAiSearchWindow(window, $"results-{width}x{height}.png");
                Assert.True(table.ActualWidth > (width == 1080 ? 950 : 900)); Assert.True(table.ActualHeight > 100, $"Results height={table.ActualHeight}");
                Assert.DoesNotContain(table.Columns, c => c.Header?.ToString()?.Contains("cosine", StringComparison.OrdinalIgnoreCase) == true);
                if (width == 1080) { ((CheckBox)search.FindName("EvidenceToggle")).IsChecked = true; window.UpdateLayout(); }
                Assert.True(((Border)search.FindName("EvidencePanel")).IsVisible);
                Assert.Equal(width == 1080 ? 100 : 160, ((Image)search.FindName("MatchedFrame")).Height);
                Assert.NotNull(((Image)search.FindName("MatchedFrame")).Source);
                Assert.Contains(LibraryVisuals(search).OfType<Button>(), b => b.IsVisible && ReferenceEquals(b.Command, _viewModel.AiSearchPreviewCommand));
                Assert.Contains(LibraryVisuals(search).OfType<Button>(), b => b.IsVisible && ReferenceEquals(b.Command, _viewModel.ShowAiSearchResultInLibraryCommand));
                Assert.DoesNotContain(LibraryVisuals(search).OfType<Button>(), b => ReferenceEquals(b.Command, _viewModel.HandoffLibraryMediaCommand) || ReferenceEquals(b.Command, _viewModel.ApplyTagFilterCommand));
                Assert.False(_viewModel.IsPreviewOpen); Assert.Null(_viewModel.OperationTarget);
                var deletion = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Delete) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                table.RaiseEvent(deletion); Assert.True(File.Exists(media[0].Path)); Assert.Null(_viewModel.OperationTarget);
                SaveAiSearchWindow(window, $"evidence-{width}x{height}.png");
                if (width == 1080)
                {
                    var back = (Button)search.FindName("BackToResults"); Assert.True(back.IsVisible);
                    back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
                    Assert.True(table.IsVisible); Assert.Equal(media[0].MediaFileId, _viewModel.SelectedSemanticResult!.MediaFileId);
                }
                _viewModel.ShowAiSearchResultInLibraryCommand.Execute(null); AiPumpUntil(() => tabs.SelectedIndex == 0);
                Assert.Equal(media[0].MediaFileId, _viewModel.SelectedMedia!.MediaFileId); Assert.Null(_viewModel.OperationTarget);
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
            finally
            {
                if (window is not null) { window.DataContext = null; window.UpdateLayout(); window.AuthorizeFinalClose(); }
                if (framePath is not null && File.Exists(framePath)) { File.Delete(framePath); Directory.Delete(Path.GetDirectoryName(framePath)!); }
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); try { await done.Task; } finally { thread.Join(); }
    }

    private static void AiPumpUntil(Func<bool> ready)
    {
        var frame = new DispatcherFrame(); var deadline = DateTime.UtcNow.AddSeconds(10);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (ready() || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Assert.True(ready());
    }
    private static void SaveAiSearchWindow(MainWindow window, string name)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var directory = Path.Combine(LibraryRepositoryRoot(), "artifacts", "ui-ai-search"); Directory.CreateDirectory(directory);
        using var output = File.Create(Path.Combine(directory, name)); encoder.Save(output);
    }
}
