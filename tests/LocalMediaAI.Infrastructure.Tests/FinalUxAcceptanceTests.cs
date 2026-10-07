using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LocalMediaAI.App;
using LocalMediaAI.App.ViewModels;
using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed partial class LibraryScopeFoundationTests
{
    private static readonly string[] AcceptanceWorkspaceNames = ["MediaLibraryWorkspace", "SearchFirstWorkspace", "EvidenceReviewWorkspace", "ProgressiveOperationWorkspace"];
    private static readonly bool[] AcceptanceDeveloperModes = [false, true];
    [Fact]
    public async Task FinalAcceptanceRuntimeAutomationNamesAndDeveloperVisibilityPreserveContext()
    {
        var media = _viewModel.MediaItems[0];
        _viewModel.SelectedMedia = media;
        _search.ImmediateResults = [AiCandidate(media)];
        _viewModel.SemanticSearchText = "海";
        await SearchAsync();
        var review = new UnifiedReviewRow(new UnifiedReviewItem("acceptance-review", UnifiedReviewType.ContentSimilar, "edge", Guid.NewGuid(), media.MediaFileId, Guid.NewGuid(), null, Guid.NewGuid(),
            media.FileName, "contents", ReviewEvidenceState.Current, "Present", "Preferred", "keep", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, "test", "embedding", "test", "0.9", "test", null, null, 120, 1, "test"));
        _viewModel.UnifiedReviewItems.Add(review);
        _viewModel.SelectedUnifiedReview = review;
        await _viewModel.ReviewEvidenceLoadTask;
        // This fabricated relation has no persisted evidence members to resolve its display label.
        review.GeneralMediaLabel = media.FileName;
        Assert.True(await _viewModel.HandoffMediaToOperationAsync(media.MediaFileId, "Acceptance fixture"));
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                window = new MainWindow { DataContext = _viewModel, Width = 1080, Height = 680, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
                window.Show();
                var tabs = (TabControl)window.FindName("TaskWorkspaceTabs");
                foreach (var tab in tabs.Items.OfType<TabItem>()) Assert.False(string.IsNullOrWhiteSpace(AcceptancePeer(tab).GetName()));
                var scope = LibraryVisuals(window).OfType<ComboBox>().Single(c => AutomationProperties.GetAutomationId(c) == "LibraryScopeSelector");
                Assert.Contains("対象範囲", AcceptancePeer(scope).GetName());
                var original = (_viewModel.CurrentLibraryScope, _viewModel.SelectedMediaFileId, _viewModel.SelectedSemanticResult, _viewModel.SelectedUnifiedReview, _viewModel.OperationTarget, _viewModel.OperationPlanningFinalConfirmation);
                var workspaceNames = AcceptanceWorkspaceNames;
                // Workspaces 0-2 are tabs; file operation (3) opens in context, hides the tabs and keeps Ctrl+F for its own search.
                void Show(int index)
                {
                    if (index == 3) { window.ShowOperationWorkspaceForRecovery(); AiPumpUntil(() => window.IsOperationWorkspaceOpen); }
                    else tabs.SelectedIndex = index;
                    window.UpdateLayout();
                }
                for (var index = 0; index < workspaceNames.Length; index++)
                {
                    Show(index);
                    var workspace = (UserControl)window.FindName(workspaceNames[index]);
                    Assert.True(window.HandleSearchShortcut(Key.F, ModifierKeys.Control));
                    window.UpdateLayout();
                    Assert.Equal(index == 3 ? -1 : index, tabs.SelectedIndex);
                    Assert.Same(workspace.FindName(index switch { 0 => "MediaSearch", 1 => "QueryInput", 2 => "CandidateFilter", _ => "TargetSearch" }), FocusManager.GetFocusedElement(window));
                    if (index == 3) ((Expander)workspace.FindName("TargetSelector")).IsExpanded = false;
                    if (index < 3)
                    {
                        Assert.True(window.HandleSearchShortcut(Key.F, ModifierKeys.Control | ModifierKeys.Shift));
                        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame);
                        Assert.Equal(1, tabs.SelectedIndex);
                        Assert.Same(((UserControl)window.FindName("SearchFirstWorkspace")).FindName("QueryInput"), FocusManager.GetFocusedElement(window));
                        Show(index);
                    }
                    else Assert.False(window.HandleSearchShortcut(Key.F, ModifierKeys.Control | ModifierKeys.Shift));
                    if (index < 2)
                    {
                        var grid = (DataGrid)workspace.FindName(index == 0 ? "MediaTable" : "ResultsTable");
                        grid.ScrollIntoView(grid.SelectedItem); grid.UpdateLayout();
                        var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(grid.SelectedItem);
                        Assert.Contains(media.FileName, AcceptancePeer(row).GetName());
                        Assert.False(string.IsNullOrWhiteSpace(AcceptancePeer(row).GetHelpText()));
                    }
                    else if (index == 2)
                    {
                        var queue = (ListBox)workspace.FindName("Queue"); queue.UpdateLayout();
                        var row = (ListBoxItem)queue.ItemContainerGenerator.ContainerFromItem(review);
                        Assert.Contains(media.FileName, AcceptancePeer(row).GetName());
                        Assert.Contains("判断", AcceptancePeer(row).GetHelpText());
                    }
                    else Assert.Contains("未選択", AcceptancePeer((ComboBox)workspace.FindName("SingleOperationSelector")).GetName());
                    foreach (var developer in AcceptanceDeveloperModes)
                    {
                        _viewModel.IsDeveloperMode = developer;
                        AiPumpUntil(() => _viewModel.DeveloperModeSaveTask.IsCompleted);
                        window.UpdateLayout();
                        foreach (var diagnostic in LibraryVisuals(workspace).OfType<Expander>().Where(DeveloperDiagnostics.GetSection))
                        {
                            Assert.Equal(developer ? Visibility.Visible : Visibility.Collapsed, diagnostic.Visibility);
                            Assert.False(diagnostic.IsExpanded);
                            if (!developer) Assert.True(AcceptancePeer(diagnostic).IsOffscreen());
                            else Assert.False(string.IsNullOrWhiteSpace(AcceptancePeer(diagnostic).GetName()));
                        }
                        var liveStatus = LibraryVisuals(workspace).OfType<TextBlock>().First(text => text.IsVisible && !string.IsNullOrWhiteSpace(text.Text) && AutomationProperties.GetLiveSetting(text) == AutomationLiveSetting.Polite);
                        Assert.Equal(liveStatus.Text, AcceptancePeer(liveStatus).GetName());
                        if (developer)
                        {
                            if (index == 0) ((CheckBox)workspace.FindName("DetailToggle")).IsChecked = true;
                            if (index == 1) ((CheckBox)workspace.FindName("EvidenceToggle")).IsChecked = true;
                            window.UpdateLayout();
                            var diagnostic = LibraryVisuals(workspace).OfType<Expander>().First(section => DeveloperDiagnostics.GetSection(section) && section.IsVisible);
                            diagnostic.IsExpanded = true; window.UpdateLayout(); diagnostic.BringIntoView(); window.UpdateLayout();
                            Assert.True(diagnostic.ActualWidth > 100); Assert.True(workspace.ActualHeight > 180);
                            SaveAiSearchWindow(window, $"final-expanded-diagnostics-{index}-1080x680.png");
                            diagnostic.IsExpanded = false;
                            if (index == 0) ((CheckBox)workspace.FindName("DetailToggle")).IsChecked = false;
                            if (index == 1) ((CheckBox)workspace.FindName("EvidenceToggle")).IsChecked = false;
                        }
                        Assert.Equal(original, (_viewModel.CurrentLibraryScope, _viewModel.SelectedMediaFileId, _viewModel.SelectedSemanticResult, _viewModel.SelectedUnifiedReview, _viewModel.OperationTarget, _viewModel.OperationPlanningFinalConfirmation));
                        Assert.Equal("Preferred", review.HumanDecision);
                    }
                }
                var dpi = VisualTreeHelper.GetDpi(window);
                var directory = Path.Combine(LibraryRepositoryRoot(), "artifacts", "ui-final-acceptance"); Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "runtime-automation.txt"), $"Runtime automation peers: PASS. WPF DpiScale={dpi.DpiScaleX},{dpi.DpiScaleY}. Window=1080x680 DIP. Physical keyboard, monitor DPI transitions and screen reader: NOT TESTED.");
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
            finally { if (window is not null) { window.DataContext = null; window.UpdateLayout(); window.AuthorizeFinalClose(); } }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); try { await done.Task; } finally { thread.Join(); }
    }

    private static AutomationPeer AcceptancePeer(UIElement element) => UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element)!;

    [Fact]
    public async Task FinalAcceptanceTwentyThousandFixtureMediaRevealAndTargetSelectorStayBounded()
    {
        const int count = 20000;
        var paths = AppPaths.Create(_fixturePath);
        await using (var connection = new SqliteConnection($"Data Source={paths.DatabasePath}"))
        {
            await connection.OpenAsync();
            var columns = new List<string>();
            await using (var schema = connection.CreateCommand())
            {
                schema.CommandText = "PRAGMA table_info(media_file)";
                await using var reader = await schema.ExecuteReaderAsync();
                while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
            }
            var fileName = "'large-' || printf('%05d',n) || '.mp4'";
            var values = columns.Select(column => column switch
            {
                "media_file_id" => "'00000000-0000-4000-8000-' || printf('%012d',n)",
                "file_name" => fileName,
                "path" or "normalized_path" => "$directory || '\\' || " + fileName,
                _ => "base.\"" + column + "\""
            });
            await using var seed = connection.CreateCommand();
            seed.CommandText = $"WITH RECURSIVE numbers(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM numbers WHERE n<$count) INSERT INTO media_file({string.Join(',', columns.Select(c => "\"" + c + "\""))}) SELECT {string.Join(',', values)} FROM numbers CROSS JOIN media_file base WHERE base.media_file_id=$base";
            seed.Parameters.AddWithValue("$count", count); seed.Parameters.AddWithValue("$base", _viewModel.MediaItems[0].MediaFileId.ToString("D")); seed.Parameters.AddWithValue("$directory", _rootA.ConfiguredPath);
            Assert.Equal(count, await seed.ExecuteNonQueryAsync());
        }
        _viewModel.CurrentLibraryScope = Scope(_rootA); await _viewModel.ScopeRefreshTask;
        var targetId = Guid.Parse($"00000000-0000-4000-8000-{count:D12}");
        _search.ImmediateResults = [new SemanticSearchCandidate(targetId, "stale.mp4", @"X:\old\stale.mp4", 0, 0.8, Guid.NewGuid(), Guid.NewGuid())];
        _viewModel.SemanticSearchText = "海"; await SearchAsync();
        var navigation = false; _viewModel.AiSearchLibraryRequested += (_, _) => navigation = true;
        var stopwatch = Stopwatch.StartNew();
        _viewModel.ShowAiSearchResultInLibraryCommand.Execute(null); await WaitForAsync(() => navigation); stopwatch.Stop();
        Assert.Equal(targetId, _viewModel.SelectedMediaFileId); Assert.Equal(200, _viewModel.CurrentPage);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Reveal took {stopwatch.Elapsed}");
        _viewModel.OperationTargetSearch = "large-";
        _viewModel.FindOperationTargetCommand.Execute(null); await WaitForAsync(() => _viewModel.FindOperationTargetCommand.CanExecute(null));
        Assert.Equal(30, _viewModel.OperationTargetCandidates.Count); Assert.Null(_viewModel.OperationTarget);
        var directory = Path.Combine(LibraryRepositoryRoot(), "artifacts", "ui-final-acceptance"); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "large-fixture.txt"), $"Isolated fixture: {count + 2} media in current root. AI Search -> Library page 201 reveal: {stopwatch.Elapsed.TotalMilliseconds:F0} ms. Target selector: 30 items. Synthetic records, not a production-library benchmark.");
    }
}

public sealed partial class OperationTargetFoundationTests
{
    [Fact]
    public async Task FinalAcceptancePermanentDeleteKeepsNonDestructiveFocusAndKeyGuards()
    {
        Assert.True(await _vm.HandoffMediaToOperationAsync(_a.MediaFileId, "Acceptance fixture"));
        _vm.OperationPlanningType = FileOperationType.PermanentDelete; await _vm.OperationInputChangeTask;
        await CommandAsync(_vm.CreateOperationDryRunCommand);
        _vm.OperationProgressAcknowledgement = true; await CommandAsync(_vm.AcknowledgeOperationDryRunCommand);
        _vm.OperationPlanningFinalConfirmation = true; await CommandAsync(_vm.ConfirmOperationFinalExecutionCommand);
        var operationId = _vm.CurrentOperationDryRun!.Plan.OperationId;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                window = new MainWindow { DataContext = _vm, Width = 1080, Height = 680, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
                window.ShowOperationWorkspaceForRecovery();
                window.Show(); window.UpdateLayout(); CrossFocusDrain();
                var operation = (FileOperationWorkspace)window.FindName("ProgressiveOperationWorkspace");
                var destructive = (Button)operation.FindName("PermanentExecute");
                var confirmation = (TextBlock)operation.FindName("FinalConfirmationFocusTarget");
                Assert.False(destructive.IsDefault); Assert.Same(confirmation, FocusManager.GetFocusedElement(window));
                Assert.Contains("元に戻せません", UIElementAutomationPeer.CreatePeerForElement(destructive)!.GetName());
                Assert.True(destructive.IsEnabled); Assert.True(confirmation.IsVisible);
                Assert.True(window.HandleSearchShortcut(Key.F, ModifierKeys.Control)); // Existing target search remains available.
                Assert.Same(confirmation, FocusManager.GetFocusedElement(window));
                var source = PresentationSource.FromVisual(window)!;
                foreach (var key in new[] { Key.Enter, Key.Delete })
                {
                    var input = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    destructive.RaiseEvent(input); Assert.True(input.Handled);
                    Assert.True(File.Exists(_a.Path)); Assert.Equal(operationId, _vm.CurrentOperationDryRun!.Plan.OperationId);
                }
                // Deliberate Space activation is not intercepted. Do not synthesize KeyUp or execute deletion.
                var space = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                destructive.RaiseEvent(space); Assert.False(space.Handled);
                Assert.True(File.Exists(_a.Path)); Assert.NotEqual(FileOperationState.Executing, _vm.CurrentOperationDryRun!.Plan.State);
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
            finally { if (window is not null) { window.DataContext = null; window.UpdateLayout(); window.AuthorizeFinalClose(); } }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); try { await done.Task; } finally { thread.Join(); }
    }
}
