using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LocalMediaAI.App;
using LocalMediaAI.App.ViewModels;
using LocalMediaAI.Core;

namespace LocalMediaAI.Infrastructure.Tests;
public sealed partial class LibraryScopeFoundationTests
{
    [Fact]
    public async Task LibraryRedesignManagementSelectionIsSeparateFromScopeAndHandoff()
    {
        var media = _viewModel.MediaItems.First(); _viewModel.SelectedMedia = media;
        Assert.Null(_viewModel.OperationTarget);
        Assert.True(await _viewModel.HandoffMediaToOperationAsync(media.MediaFileId, "Library Detail"));
        var target = _viewModel.OperationTarget;
        _viewModel.SelectedRoot = _rootB;
        Assert.Null(_viewModel.CurrentLibraryScope.LibraryRootId);
        _viewModel.ViewManagedFolderCommand.Execute(null); await _viewModel.ScopeRefreshTask;
        Assert.Equal(_rootB.LibraryRootId, _viewModel.CurrentLibraryScope.LibraryRootId);
        Assert.Same(target, _viewModel.OperationTarget); Assert.Null(_viewModel.OperationPlanningType);
        _viewModel.CurrentLibraryScope = LibraryScopePresentation.All; await _viewModel.ScopeRefreshTask;
        Assert.Equal(_rootB.LibraryRootId, _viewModel.SelectedRoot!.LibraryRootId);
        Assert.Equal(3, _viewModel.MediaItems.Count);
    }
    [Fact]
    public async Task LibraryRedesignSearchAndClearFiltersKeepScopeAndTarget()
    {
        _viewModel.CurrentLibraryScope = Scope(_rootA); await _viewModel.ScopeRefreshTask;
        var selected = _viewModel.MediaItems.First(); await _viewModel.HandoffMediaToOperationAsync(selected.MediaFileId, "Library");
        var scope = _viewModel.CurrentLibraryScope; var target = _viewModel.OperationTarget;
        _viewModel.SearchText = "not-a-real-file";
        await WaitForAsync(() => !_viewModel.LibraryIsLoading && _viewModel.MediaItems.Count == 0);
        Assert.Equal(LibraryEmptyState.FilteredEmpty, _viewModel.LibraryContentState); Assert.Null(_viewModel.SelectedMedia);
        _viewModel.LibraryClearFiltersCommand.Execute(null);
        await WaitForAsync(() => !_viewModel.LibraryIsLoading && _viewModel.MediaItems.Count == 2);
        Assert.Equal(scope, _viewModel.CurrentLibraryScope); Assert.Same(target, _viewModel.OperationTarget);
    }
    [Fact]
    public async Task LibraryRedesignRetentionUsesExistingSaveWithoutCleanupOrRootMutation()
    {
        var roots = await _library.GetLibraryRootsAsync(CancellationToken.None);
        var records = await _library.GetRetentionRecordsAsync(CancellationToken.None);
        Assert.Equal(RetentionSettings.Defaults, await _library.GetRetentionSettingsAsync(CancellationToken.None));
        _viewModel.FileRetentionText = "10"; _viewModel.FolderRetentionText = "60"; _viewModel.DriveRetentionText = "無制限";
        _viewModel.SaveRetentionSettingsCommand.Execute(null); await WaitForAsync(() => _viewModel.LibrarySettingsFeedback.StartsWith('✓'));
        Assert.Equal(new RetentionSettings(10, 60, null), await _library.GetRetentionSettingsAsync(CancellationToken.None));
        Assert.Equal(roots, await _library.GetLibraryRootsAsync(CancellationToken.None)); Assert.Equal(records, await _library.GetRetentionRecordsAsync(CancellationToken.None));
    }
    [Fact]
    public async Task LibraryRedesignPreviewUsesSelectedMediaWithoutHandoffAndPreservesSelectionOnClose()
    {
        var media = _viewModel.MediaItems.First(); _viewModel.SelectedMedia = media;
        _viewModel.LibraryPreviewCommand.Execute(null);
        await WaitForAsync(() => !_viewModel.IsPreviewBusy && _viewModel.IsPreviewOpen);
        Assert.Equal(media.Path, _viewModel.PreviewMediaPath); Assert.Null(_viewModel.OperationTarget);
        _viewModel.ClosePreviewCommand.Execute(null);
        await WaitForAsync(() => !_viewModel.IsPreviewOpen);
        Assert.Equal(media.MediaFileId, _viewModel.SelectedMedia!.MediaFileId); Assert.Null(_viewModel.OperationTarget);
    }
    [Fact]
    public void LibraryRedesignEmptyClassificationNeverInfersUnknownAsCompletedOrMissing()
    {
        LibraryEmptyState Classify(bool loading = false, bool failed = false, int count = 0, bool filtered = false, bool processing = false, IReadOnlyList<RootScanCurrentness>? scans = null, IReadOnlyList<LibraryRoot>? roots = null) => MainViewModel.ClassifyLibraryEmpty(loading, failed, roots ?? [_rootA], null, count, filtered, processing ? [LibraryScanJob(_rootA)] : [], scans ?? []);
        Assert.Equal(LibraryEmptyState.NoFolders, Classify(roots: [])); Assert.Equal(LibraryEmptyState.Loading, Classify(loading: true));
        Assert.Equal(LibraryEmptyState.Error, Classify(failed: true)); Assert.Equal(LibraryEmptyState.Processing, Classify(processing: true));
        Assert.Equal(LibraryEmptyState.FilteredEmpty, Classify(filtered: true)); Assert.Equal(LibraryEmptyState.HasMedia, Classify(count: 1));
        Assert.Equal(LibraryEmptyState.UnverifiedEmpty, Classify());
        var completed = new RootScanCurrentness(_rootA.LibraryRootId, _rootA.RegistrationRevision, "Completed", 0);
        Assert.Equal(LibraryEmptyState.ScannedEmpty, Classify(scans: [completed], roots: [_rootA with { Availability = RootAvailability.Online }]));
        Assert.Equal(LibraryEmptyState.UnverifiedEmpty, Classify(scans: [completed with { RegistrationRevision = completed.RegistrationRevision - 1 }]));
        Assert.Equal(LibraryEmptyState.UnverifiedEmpty, Classify(scans: [completed], roots: [_rootA with { Availability = RootAvailability.Unknown }]));
        var converter = new LibraryDisplayConverter();
        Assert.Equal("状態を確認できません", converter.Convert(PresenceState.Unknown, typeof(string), "", System.Globalization.CultureInfo.InvariantCulture));
    }
    private static Job LibraryScanJob(LibraryRoot root, JobState state = JobState.Running) => new(
        Guid.NewGuid(), JobType.Scan, state, "Scanning", JobReason.None,
        System.Text.Json.JsonSerializer.Serialize(new ScanPayload(root.LibraryRootId, root.RegistrationRevision)),
        1, 2, null, null, null, null, new JobProgress(0, 0, "items", true), null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public void LibraryRedesignProcessingEmptyUsesCurrentScopeAndRegistrationNotGlobalActivity()
    {
        var a = _rootA with { Availability = RootAvailability.Online };
        var b = _rootB with { Availability = RootAvailability.Online };
        var completed = new RootScanCurrentness(a.LibraryRootId, a.RegistrationRevision, "Completed", 0);
        LibraryEmptyState Classify(Guid? scope, IReadOnlyList<Job> jobs, bool filtered = false, IReadOnlyList<RootScanCurrentness>? scans = null, IReadOnlyList<LibraryRoot>? roots = null)
            => MainViewModel.ClassifyLibraryEmpty(false, false, roots ?? [a, b], scope, 0, filtered, jobs, scans ?? []);

        Assert.Equal(LibraryEmptyState.UnverifiedEmpty, Classify(a.LibraryRootId, [LibraryScanJob(b)]));
        Assert.Equal(LibraryEmptyState.ScannedEmpty, Classify(a.LibraryRootId, [LibraryScanJob(b)], scans: [completed]));
        Assert.Equal(LibraryEmptyState.Processing, Classify(a.LibraryRootId, [LibraryScanJob(a)]));
        Assert.Equal(LibraryEmptyState.ScannedEmpty, Classify(a.LibraryRootId, [], scans: [completed]));
        Assert.Equal(LibraryEmptyState.Processing, Classify(null, [LibraryScanJob(b)]));
        Assert.Equal(LibraryEmptyState.ScannedEmpty, Classify(null, [], scans: [completed, new RootScanCurrentness(b.LibraryRootId, b.RegistrationRevision, "Completed", 0)]));
        Assert.Equal(LibraryEmptyState.FilteredEmpty, Classify(a.LibraryRootId, [LibraryScanJob(a)], filtered: true));
        Assert.Equal(LibraryEmptyState.FilteredEmpty, Classify(null, [LibraryScanJob(b)], filtered: true));
        Assert.Equal(LibraryEmptyState.UnverifiedEmpty, Classify(a.LibraryRootId, [LibraryScanJob(a with { RegistrationRevision = a.RegistrationRevision - 1 })]));
        Assert.Equal(LibraryEmptyState.UnverifiedEmpty, Classify(null, [LibraryScanJob(b)], roots: [a, b with { RegistrationState = RootRegistrationState.Unregistered }]));
        Assert.Equal(LibraryEmptyState.UnverifiedEmpty, Classify(a.LibraryRootId, [LibraryScanJob(a) with { JobType = JobType.Metadata }]));
        Assert.Equal(LibraryEmptyState.UnverifiedEmpty, Classify(null, [LibraryScanJob(a with { LibraryRootId = Guid.NewGuid() })]));
        foreach (var state in new[] { JobState.Pending, JobState.RetryWaiting, JobState.Waiting, JobState.Paused, JobState.Failed, JobState.Partial, JobState.Completed })
            Assert.Equal(LibraryEmptyState.UnverifiedEmpty, Classify(a.LibraryRootId, [LibraryScanJob(a, state)]));
        foreach (var json in new[] { "{", "null", "[]", "{}", $"{{\"LibraryRootId\":\"{a.LibraryRootId}\"}}", "{\"RegistrationRevision\":\"unknown\"}" })
            Assert.Equal(LibraryEmptyState.UnverifiedEmpty, Classify(a.LibraryRootId, [LibraryScanJob(a) with { PayloadJson = json }]));
    }
    [Theory]
    [InlineData(1080, 680)]
    [InlineData(1600, 900)]
    public async Task LibraryRedesignRealWpfMediaTableScopeDetailSettingsAndKeyboard(int width, int height)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow { DataContext = _viewModel, Width = width, Height = height, Left = -10000, ShowInTaskbar = false, ShowActivated = false };
                window.Show(); window.UpdateLayout();
                var library = (LibraryWorkspace)window.FindName("MediaLibraryWorkspace"); Assert.True(library.IsVisible);
                var table = (DataGrid)library.FindName("MediaTable"); Assert.Equal(3, table.Items.Count); Assert.True(table.ActualWidth > (width == 1080 ? 950 : 800));
                Assert.True(((ComboBox)window.FindName("LibraryScopeSelector") ?? LibraryVisuals(window).OfType<ComboBox>().First(c => System.Windows.Automation.AutomationProperties.GetAutomationId(c) == "LibraryScopeSelector")).IsVisible);
                Assert.DoesNotContain(table.Columns, c => c.Header?.ToString()?.Contains("インデックス", StringComparison.Ordinal) == true);
                Assert.True(table.ActualHeight > 150, $"Table height={table.ActualHeight}");
                SaveLibraryWindow(window, $"library-{width}x{height}.png");
                _viewModel.SelectedMedia = _viewModel.MediaItems.First();
                ((CheckBox)library.FindName("DetailToggle")).IsChecked = true; window.UpdateLayout();
                var detail = (Border)library.FindName("DetailPanel"); Assert.True(detail.IsVisible);
                Assert.Null(_viewModel.OperationTarget);
                Assert.Contains(LibraryVisuals(detail).OfType<Button>(), b => b.IsVisible && b.Content?.ToString() == "ファイルを整理");
                var source = PresentationSource.FromVisual(window)!;
                var deletion = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Delete) { RoutedEvent = Keyboard.PreviewKeyDownEvent }; table.RaiseEvent(deletion);
                Assert.Null(_viewModel.OperationTarget); Assert.True(File.Exists(_viewModel.SelectedMedia.Path));
                SaveLibraryWindow(window, $"library-detail-{width}x{height}.png");
                if (width == 1080)
                {
                    var back = (Button)library.FindName("BackToTable"); Assert.True(back.IsVisible);
                    back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
                    Assert.True(table.IsVisible); Assert.True(table.ActualHeight > 150); Assert.NotNull(_viewModel.SelectedMedia);
                }
                var settings = (System.Windows.Controls.Primitives.Popup)library.FindName("LibrarySettings");
                settings.IsOpen = true; settings.Child.UpdateLayout();
                var fields = LibraryVisuals(settings.Child).OfType<TextBox>().ToArray();
                Assert.Equal(3, fields.Length); Assert.Equal(_viewModel.FileRetentionText, fields[0].Text);
                Assert.Equal(_viewModel.FolderRetentionText, fields[1].Text); Assert.Equal(_viewModel.DriveRetentionText, fields[2].Text);
                Assert.Contains(LibraryVisuals(settings.Child).OfType<Button>(), b => ReferenceEquals(b.Command, _viewModel.SaveRetentionSettingsCommand));
                var escape = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                window.RaiseEvent(escape); Assert.True(escape.Handled); Assert.False(settings.IsOpen);
                Assert.False(((System.Windows.Controls.Primitives.ToggleButton)library.FindName("SettingsToggle")).IsChecked);
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
            finally { if (window is not null) { window.DataContext = null; window.UpdateLayout(); window.AuthorizeFinalClose(); } }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); try { await done.Task; } finally { thread.Join(); }
    }
    private static IEnumerable<DependencyObject> LibraryVisuals(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var item in LibraryVisuals(child)) yield return item; }
    }
    private static void SaveLibraryWindow(MainWindow window, string name)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var directory = Path.Combine(LibraryRepositoryRoot(), "artifacts", "ui-library"); Directory.CreateDirectory(directory);
        using var output = File.Create(Path.Combine(directory, name)); encoder.Save(output);
    }
    private static string LibraryRepositoryRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory); while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "src", "LocalMediaAI.App"))) d = d.Parent; return d!.FullName;
    }
}
