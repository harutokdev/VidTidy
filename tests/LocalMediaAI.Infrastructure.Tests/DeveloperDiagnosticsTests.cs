using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using System.Globalization;
using LocalMediaAI.App;
using LocalMediaAI.App.ViewModels;
using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed partial class LibraryScopeFoundationTests
{
    [Fact]
    public void TechnicalSectionsAreGatedWhileEvidenceAndBlockingSafetyRemainGeneral()
    {
        XNamespace local = "clr-namespace:LocalMediaAI.App";
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        foreach (var file in new[] { "LibraryWorkspace.xaml", "AiSearchWorkspace.xaml", "ReviewWorkspace.xaml", "FileOperationWorkspace.xaml" })
        {
            var document = XDocument.Load(Path.Combine(LibraryRepositoryRoot(), "src", "LocalMediaAI.App", file));
            var sections = document.Descendants(presentation + "Expander").Where(element => ((string?)element.Attribute("Header")) is { } header && (header.Contains("技術") || header.Contains("診断"))).ToArray();
            Assert.NotEmpty(sections);
            Assert.All(sections, element => { Assert.Equal("True", (string?)element.Attribute(local + "DeveloperDiagnostics.Section")); Assert.Equal("False", (string?)element.Attribute("IsExpanded")); });
            foreach (var element in document.Descendants().Where(element => element.Attributes().Any(attribute => attribute.Value.Contains("OperationSafetyWarning") || attribute.Value.Contains("OperationPreflightMessage") || attribute.Value.Contains("ReviewJudgmentAvailability") || attribute.Value.Contains("ReviewMeaning") || attribute.Value.Contains("LibraryMediaFacts") || attribute.Value.Contains("AiSearchEvidenceTimestamp"))))
                Assert.DoesNotContain(element.AncestorsAndSelf(), ancestor => (string?)ancestor.Attribute(local + "DeveloperDiagnostics.Section") == "True");
        }
        var converter = new LibraryDisplayConverter();
        Assert.Equal("状態を確認できません", converter.Convert(PresenceState.Unknown, typeof(string), null!, CultureInfo.InvariantCulture));
        Assert.Equal("状態を確認できません", converter.Convert(RootAvailability.Unknown, typeof(string), null!, CultureInfo.InvariantCulture));
        Assert.Equal("完全一致", converter.Convert(DuplicateRelationType.Exact, typeof(string), null!, CultureInfo.InvariantCulture));
        Assert.Equal("類似重複", converter.Convert(DuplicateRelationType.NearDuplicate, typeof(string), null!, CultureInfo.InvariantCulture));
        Assert.Equal("内容の類似", converter.Convert(DuplicateRelationType.ContentSimilar, typeof(string), null!, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task DeveloperModeDefaultsOffAndPersistsWithoutWorkflowMutation()
    {
        await _viewModel.LoadPresentationSettingsAsync();
        Assert.False(_viewModel.IsDeveloperMode);
        Assert.False(await _library.GetDeveloperModeAsync(CancellationToken.None));
        _viewModel.SelectedMedia = _viewModel.MediaItems[0];
        Assert.True(await _viewModel.HandoffMediaToOperationAsync(_viewModel.SelectedMedia.MediaFileId, "fixture"));
        _viewModel.CurrentLibraryScope = Scope(_rootA);
        await _viewModel.ScopeRefreshTask;
        var target = _viewModel.OperationTarget;
        var selected = _viewModel.SelectedMedia;
        var root = _viewModel.SelectedRoot;
        var scope = _viewModel.CurrentLibraryScope;
        var roots = await _library.GetLibraryRootsAsync(CancellationToken.None);
        var retention = await _library.GetRetentionRecordsAsync(CancellationToken.None);
        var activity = _viewModel.GlobalActivity;
        _viewModel.IsDeveloperMode = true;
        _viewModel.IsDeveloperMode = false;
        _viewModel.IsDeveloperMode = true;
        await _viewModel.DeveloperModeSaveTask;
        Assert.Same(target, _viewModel.OperationTarget);
        Assert.Same(selected, _viewModel.SelectedMedia);
        Assert.Same(root, _viewModel.SelectedRoot);
        Assert.Same(scope, _viewModel.CurrentLibraryScope);
        Assert.Equal(activity.CompactSummary, _viewModel.GlobalActivity.CompactSummary);
        Assert.Null(_viewModel.OperationPlanningType);
        Assert.Empty(await _jobs.GetRecentAsync(50, CancellationToken.None));
        Assert.Equal(roots, await _library.GetLibraryRootsAsync(CancellationToken.None));
        Assert.Equal(retention, await _library.GetRetentionRecordsAsync(CancellationToken.None));
        var paths = AppPaths.Create(_fixturePath);
        var restarted = new SqliteLibraryRepository(paths, _log);
        var worker = new WorkerProcessManager(paths, _log);
        var pipeline = new MediaPipeline(restarted, _jobs, new FileSystemMediaScanner(), new FfmpegAdapter(new FfmpegPaths(null, null), paths, _log), _log, worker: worker, paths: paths);
        using var vm = new MainViewModel(restarted, _jobs, pipeline, worker, _search, _log);
        await vm.LoadPresentationSettingsAsync();
        Assert.True(vm.IsDeveloperMode);
        Assert.False(vm.IsJobDiagnosticsExpanded);
        vm.IsDeveloperMode = false;
        await vm.DeveloperModeSaveTask;
        Assert.False(await restarted.GetDeveloperModeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DeveloperModeSaveFailureIsLocalAndDoesNotChangeSafetyOrReadiness()
    {
        var paths = AppPaths.Create(_fixturePath);
        await using var connection = new SqliteConnection($"Data Source={paths.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_presentation_setting BEFORE INSERT ON app_setting WHEN NEW.key='ui.developer_mode' BEGIN SELECT RAISE(ABORT,'fixture failure'); END;";
        await command.ExecuteNonQueryAsync();
        var readiness = _viewModel.Readiness;
        var activity = _viewModel.GlobalActivity;
        var error = _viewModel.ErrorText;
        _viewModel.IsDeveloperMode = true;
        await _viewModel.DeveloperModeSaveTask;
        Assert.True(_viewModel.IsDeveloperMode);
        Assert.Contains("保存できませんでした", _viewModel.DeveloperModeFeedback);
        Assert.DoesNotContain("fixture failure", _viewModel.DeveloperModeFeedback);
        Assert.Equal(readiness, _viewModel.Readiness);
        Assert.Equal(activity.CompactSummary, _viewModel.GlobalActivity.CompactSummary);
        Assert.Equal(error, _viewModel.ErrorText);
        Assert.False(await _library.GetDeveloperModeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task JobEventsLoadOnlyForExpandedDeveloperDiagnostics()
    {
        var job = await _jobs.EnqueueAsync(JobType.Scan, new ScanPayload(_rootA.LibraryRootId, _rootA.RegistrationRevision), 3, null, CancellationToken.None);
        await ReloadAsync();
        _viewModel.SelectedJobIssue = _viewModel.JobIssues.Single(item => item.Job.JobId == job.JobId);
        Assert.Empty(_viewModel.SelectedJobEvents);
        _viewModel.IsJobDiagnosticsExpanded = true;
        Assert.False(_viewModel.IsJobDiagnosticsExpanded);
        _viewModel.IsDeveloperMode = true;
        await _viewModel.DeveloperModeSaveTask;
        Assert.Empty(_viewModel.SelectedJobEvents);
        _viewModel.IsJobDiagnosticsExpanded = true;
        await WaitForAsync(() => _viewModel.SelectedJobEvents.Count > 0);
        _viewModel.IsDeveloperMode = false;
        await _viewModel.DeveloperModeSaveTask;
        Assert.Empty(_viewModel.SelectedJobEvents);
        Assert.False(_viewModel.IsJobDiagnosticsExpanded);
        Assert.Equal(JobState.Pending, (await _jobs.GetRecentAsync(50, CancellationToken.None)).Single(item => item.JobId == job.JobId).State);
    }

    [Theory]
    [InlineData(0, "MediaLibraryWorkspace")]
    [InlineData(1, "SearchFirstWorkspace")]
    [InlineData(2, "EvidenceReviewWorkspace")]
    [InlineData(3, "ProgressiveOperationWorkspace")]
    public async Task DeveloperDiagnosticsRenderCollapsedOffAndOnAt1080(int tab, string workspaceName)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                if (tab == 3)
                {
                    var media = _viewModel.MediaItems[0];
                    var handoff = _viewModel.HandoffMediaToOperationAsync(media.MediaFileId, "fixture");
                    AiPumpUntil(() => handoff.IsCompleted); Assert.True(handoff.GetAwaiter().GetResult());
                    _viewModel.OperationPlanningType = FileOperationType.PermanentDelete;
                }
                window = new MainWindow { DataContext = _viewModel, Width = 1080, Height = 680, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
                window.Show();
                var tabs = (TabControl)window.FindName("TaskWorkspaceTabs");
                // File operation is not a tab any more; it opens in context and hides the tabs.
                if (tab == 3) { window.ShowOperationWorkspaceForRecovery(); AiPumpUntil(() => window.IsOperationWorkspaceOpen); }
                else tabs.SelectedIndex = tab;
                window.UpdateLayout();
                var workspace = (UserControl)window.FindName(workspaceName);
                var diagnostics = LibraryVisuals(workspace).OfType<Expander>().Where(DeveloperDiagnostics.GetSection).ToArray();
                Assert.NotEmpty(diagnostics);
                Assert.All(diagnostics, section => Assert.Equal(Visibility.Collapsed, section.Visibility));
                var legacyHash = ((DataGrid)window.FindName("DuplicateMembersDataGrid")).Columns.Single(column => Equals(column.Header, "Full SHA-256"));
                Assert.Equal(Visibility.Collapsed, legacyHash.Visibility);
                if (tab == 3) Assert.Contains(LibraryVisuals(workspace).OfType<TextBlock>(), text => text.IsVisible && text.Text.Contains("元に戻せません"));
                var scope = _viewModel.CurrentLibraryScope;
                var selection = _viewModel.SelectedMedia;
                var target = _viewModel.OperationTarget;
                if (tab == 0) SaveAiSearchWindow(window, "developer-off-1080x680.png");
                _viewModel.IsDeveloperMode = true;
                AiPumpUntil(() => _viewModel.DeveloperModeSaveTask.IsCompleted);
                window.UpdateLayout();
                Assert.All(diagnostics, section => { Assert.Equal(Visibility.Visible, section.Visibility); Assert.False(section.IsExpanded); Assert.NotEmpty(AutomationProperties.GetName(section)); });
                Assert.Equal(Visibility.Visible, legacyHash.Visibility);
                Assert.True(workspace.IsVisible);
                Assert.True(workspace.ActualWidth > 950);
                Assert.True(workspace.ActualHeight > 200);
                Assert.Equal(tab == 3 ? -1 : tab, tabs.SelectedIndex);
                Assert.Same(scope, _viewModel.CurrentLibraryScope);
                Assert.Same(selection, _viewModel.SelectedMedia);
                Assert.Same(target, _viewModel.OperationTarget);
                if (tab == 3) Assert.Contains(LibraryVisuals(workspace).OfType<TextBlock>(), text => text.IsVisible && text.Text.Contains("元に戻せません"));
                if (tab == 0)
                {
                    var library = (LibraryWorkspace)workspace;
                    var table = (DataGrid)library.FindName("MediaTable");
                    Assert.True(table.ActualWidth > 950);
                    Assert.True(table.ActualHeight > 100);
                    var popup = (Popup)library.FindName("LibrarySettings");
                    popup.IsOpen = true; window.UpdateLayout();
                    var popupDiagnostics = LibraryVisuals(popup.Child).OfType<Expander>().Where(DeveloperDiagnostics.GetSection).ToArray();
                    Assert.NotEmpty(popupDiagnostics);
                    Assert.All(popupDiagnostics, section => Assert.Equal(Visibility.Visible, section.Visibility));
                    popup.IsOpen = false;
                    SaveAiSearchWindow(window, "developer-on-1080x680.png");
                }
                _viewModel.IsDeveloperMode = false;
                AiPumpUntil(() => _viewModel.DeveloperModeSaveTask.IsCompleted);
                window.UpdateLayout();
                Assert.All(diagnostics, section => { Assert.Equal(Visibility.Collapsed, section.Visibility); Assert.False(section.IsExpanded); Assert.False(section.IsVisible); });
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
            finally { if (window is not null) { window.DataContext = null; window.UpdateLayout(); window.AuthorizeFinalClose(); } }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { await done.Task; } finally { thread.Join(); }
    }
}
