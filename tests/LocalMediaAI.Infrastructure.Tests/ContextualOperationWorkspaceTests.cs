using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LocalMediaAI.App;
using LocalMediaAI.Core;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed partial class OperationTargetFoundationTests
{
    [Fact]
    public async Task BackRejectsPreparedPlanWithoutApprovalAndCancelsBeforeLeaving()
    {
        var plan = await PrepareAsync(final: true);
        var presentation = _vm.CurrentOperationDryRun;
        _vm.ConfirmOperationPreparationExitAsync = () => Task.FromResult(false);
        Assert.False(await _vm.TryLeaveOperationWorkspaceAsync());
        Assert.Same(presentation, _vm.CurrentOperationDryRun);
        Assert.True(_vm.OperationPlanningFinalConfirmation);
        Assert.Equal(plan, await _library.GetFileOperationPlanAsync(plan.OperationId, CancellationToken.None));

        _vm.ConfirmOperationPreparationExitAsync = () => Task.FromResult(true);
        Assert.True(await _vm.TryLeaveOperationWorkspaceAsync());
        Assert.Equal(FileOperationState.Cancelled, (await _library.GetFileOperationPlanAsync(plan.OperationId, CancellationToken.None))!.State);
        Assert.Null(_vm.CurrentOperationDryRun);
        Assert.False(_vm.OperationPlanningFinalConfirmation);
        Assert.Equal(_a.MediaFileId, _vm.OperationTarget!.MediaFileId);
    }

    [Fact]
    public async Task BackRejectsExecutingTransitionAndPreservesCurrentContext()
    {
        var plan = await PrepareAsync(final: true);
        _vm.ConfirmOperationPreparationExitAsync = async () =>
        {
            await SqlAsync($"UPDATE file_operation_plan SET state='Executing' WHERE operation_id='{plan.OperationId:D}';");
            return true;
        };
        Assert.False(await _vm.TryLeaveOperationWorkspaceAsync());
        Assert.Equal(FileOperationState.Executing, (await _library.GetFileOperationPlanAsync(plan.OperationId, CancellationToken.None))!.State);
        Assert.Equal(plan.OperationId, _vm.CurrentOperationDryRun!.Plan.OperationId);
        Assert.True(_vm.OperationPlanningFinalConfirmation);
    }

    [Fact]
    public async Task BackCancelFailureKeepsPreparedPlanAndTarget()
    {
        var plan = await PrepareAsync(final: true);
        await SqlAsync("CREATE TRIGGER reject_exit_cancel BEFORE UPDATE OF state ON file_operation_plan WHEN NEW.state='Cancelled' BEGIN SELECT RAISE(ABORT,'cancel failed'); END;");
        _vm.ConfirmOperationPreparationExitAsync = () => Task.FromResult(true);
        Assert.False(await _vm.TryLeaveOperationWorkspaceAsync());
        Assert.Equal(plan, await _library.GetFileOperationPlanAsync(plan.OperationId, CancellationToken.None));
        Assert.Equal(_a.MediaFileId, _vm.OperationTarget!.MediaFileId);
        Assert.True(_vm.OperationPlanningFinalConfirmation);
    }

    [Fact]
    public async Task BackCancelsPreparedBatchWithoutClearingExplicitTargetSet()
    {
        await PrepareBatchAsync();
        var batch = _vm.CurrentControlledBatchDryRun!;
        _vm.ConfirmOperationPreparationExitAsync = () => Task.FromResult(true);
        Assert.True(await _vm.TryLeaveOperationWorkspaceAsync());
        Assert.Null(_vm.CurrentControlledBatchDryRun);
        Assert.Equal(2, _vm.ControlledBatchSelection.Count);
        Assert.Equal(BatchOperationState.Cancelled, (await _library.GetBatchOperationAsync(batch.Batch.BatchOperationId, CancellationToken.None))!.State);
        foreach (var item in batch.Items)
            Assert.Equal(FileOperationState.Cancelled, (await _library.GetFileOperationPlanAsync(item.OperationId, CancellationToken.None))!.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task ContextualBackButtonReturnsToHandoffSource(int source)
    {
        Assert.True(await _vm.HandoffMediaToOperationAsync(_a.MediaFileId, source == 2 ? "Review primary explicit handoff" : "Library explicit handoff"));
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                window = new MainWindow { DataContext = _vm, Width = 1080, Height = 680, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
                window.Show(); ((TabControl)window.FindName("TaskWorkspaceTabs")).SelectedIndex = source;
                window.ShowOperationWorkspaceForRecovery(); window.UpdateLayout();
                Assert.True(window.IsOperationWorkspaceOpen);
                ((Button)window.FindName("OperationWorkspaceBackButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpContextual(() => !window.IsOperationWorkspaceOpen);
                Assert.Equal(source, ((TabControl)window.FindName("TaskWorkspaceTabs")).SelectedIndex);
                Assert.Equal(_a.MediaFileId, _vm.OperationTarget!.MediaFileId);
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
            finally { if (window is not null) { window.DataContext = null; window.UpdateLayout(); window.AuthorizeFinalClose(); } }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); try { await done.Task; } finally { thread.Join(); }
    }

    [Fact]
    public async Task ContextualBackButtonWaitsForDurablePreparationCancellation()
    {
        var plan = await PrepareAsync(final: true);
        _vm.ConfirmOperationPreparationExitAsync = () => Task.FromResult(true);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                window = new MainWindow { DataContext = _vm, Width = 1080, Height = 680, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
                window.Show(); window.ShowOperationWorkspaceForRecovery(); window.UpdateLayout();
                ((Button)window.FindName("OperationWorkspaceBackButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpContextual(() => !window.IsOperationWorkspaceOpen);
                Assert.Equal(0, ((TabControl)window.FindName("TaskWorkspaceTabs")).SelectedIndex);
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
            finally { if (window is not null) { window.DataContext = null; window.UpdateLayout(); window.AuthorizeFinalClose(); } }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); try { await done.Task; } finally { thread.Join(); }
        Assert.Equal(FileOperationState.Cancelled, (await _library.GetFileOperationPlanAsync(plan.OperationId, CancellationToken.None))!.State);
    }

    [Theory]
    [InlineData(0, 1080, 680, "contextual-navigation-three-tabs-1080x680.png")]
    [InlineData(1, 1080, 680, "contextual-library-entry-1080x680.png")]
    [InlineData(2, 1080, 680, "contextual-library-back-1080x680.png")]
    [InlineData(3, 1080, 680, "contextual-review-entry-1080x680.png")]
    [InlineData(4, 1080, 680, "contextual-review-back-1080x680.png")]
    [InlineData(2, 1600, 900, "contextual-library-back-1600x900.png")]
    [InlineData(4, 1600, 900, "contextual-review-back-1600x900.png")]
    public async Task ContextualNavigationRendersThreeTabsAndSourceSpecificReturn(int state, int width, int height, string name)
    {
        if (state is 2 or 4)
            Assert.True(await _vm.HandoffMediaToOperationAsync(_a.MediaFileId, state == 4 ? "Review primary explicit handoff" : "Library explicit handoff"));
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                window = new MainWindow { DataContext = _vm, Width = width, Height = height, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
                window.Show(); window.UpdateLayout();
                var tabs = (TabControl)window.FindName("TaskWorkspaceTabs");
                Assert.Equal(3, tabs.Items.Count);
                Assert.DoesNotContain(tabs.Items.Cast<TabItem>(), tab => Equals(tab.Header, "ファイル操作"));
                if (state >= 3) tabs.SelectedIndex = 2;
                if (state is 1 or 3)
                {
                    var handoff = _vm.HandoffMediaToOperationAsync(_a.MediaFileId, state == 3 ? "Review primary explicit handoff" : "Library explicit handoff");
                    PumpContextual(() => handoff.IsCompleted);
                    Assert.True(handoff.GetAwaiter().GetResult());
                }
                else if (state is 2 or 4)
                {
                    window.ShowOperationWorkspaceForRecovery();
                }
                if (state != 0)
                {
                    PumpContextual(() => window.IsOperationWorkspaceOpen);
                    Assert.Equal(-1, tabs.SelectedIndex);
                    Assert.True(((FileOperationWorkspace)window.FindName("ProgressiveOperationWorkspace")).IsVisible);
                    Assert.False(tabs.IsVisible);
                    var back = (Button)window.FindName("OperationWorkspaceBackButton");
                    Assert.Equal(state >= 3 ? "← レビューへ戻る" : "← ライブラリへ戻る", back.Content);
                    Assert.True(back.IsVisible);
                    Assert.Null(_vm.OperationPlanningType);
                }
                window.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var directory = Path.Combine(ProgressRepositoryRoot(), "artifacts", "ui-file-operation"); Directory.CreateDirectory(directory);
                using var output = File.Create(Path.Combine(directory, name)); encoder.Save(output);
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
            finally { if (window is not null) { window.DataContext = null; window.UpdateLayout(); window.AuthorizeFinalClose(); } }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); try { await done.Task; } finally { thread.Join(); }
    }

    [Fact]
    public async Task SnapWidthRoundTripKeepsThreePrimaryTabsAndRestoresThemAfterContextualOperation()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                window = new MainWindow { DataContext = _vm, Width = 1600, Height = 900, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
                window.Show();
                var tabs = (TabControl)window.FindName("TaskWorkspaceTabs");
                var library = (LibraryWorkspace)window.FindName("MediaLibraryWorkspace");
                var sidebarToggle = (CheckBox)library.FindName("SidebarToggle");
                void Check(int width, string artifact)
                {
                    window.Width = width;
                    window.UpdateLayout();
                    Assert.InRange(window.ActualWidth, width - 2, width + 2);
                    Assert.True(tabs.IsVisible);
                    Assert.Equal(3, tabs.Items.Count);
                    Assert.All(tabs.Items.Cast<TabItem>(), tab => Assert.True(tab.IsVisible && tab.ActualWidth > 0, $"{tab.Header} at {width}"));
                    Assert.All(tabs.Items.Cast<TabItem>(), tab => Assert.InRange(tab.TranslatePoint(new Point(tab.ActualWidth, 0), tabs).X, 0, tabs.ActualWidth));
                    Assert.Equal(width >= 1300, sidebarToggle.IsChecked == true);
                    var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    var directory = Path.Combine(ProgressRepositoryRoot(), "artifacts", "ui-snap-regression"); Directory.CreateDirectory(directory);
                    using var output = File.Create(Path.Combine(directory, artifact)); encoder.Save(output);
                }
                Check(1600, "wide-initial.png");
                Check(960, "snap-960.png");
                Check(760, "snap-760.png");
                window.Height = 680;
                Check(1080, "supported-1080.png");
                Assert.InRange(window.ActualHeight, 678, 682);
                window.Height = 900;
                Check(1600, "wide-restored.png");
                _vm.IsDeveloperMode = true;
                Check(760, "developer-narrow.png");
                Check(1600, "developer-wide.png");
                _vm.IsDeveloperMode = false;
                Check(760, "second-narrow.png");
                Check(1600, "second-wide.png");
                foreach (var index in new[] { 1, 2, 0 }) { tabs.SelectedIndex = index; window.UpdateLayout(); Assert.Equal(index, tabs.SelectedIndex); }
                var handoff = _vm.HandoffMediaToOperationAsync(_a.MediaFileId, "Library explicit handoff");
                PumpContextual(() => handoff.IsCompleted);
                Assert.True(handoff.GetAwaiter().GetResult());
                PumpContextual(() => window.IsOperationWorkspaceOpen);
                Assert.False(tabs.IsVisible);
                ((Button)window.FindName("OperationWorkspaceBackButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpContextual(() => !window.IsOperationWorkspaceOpen);
                Check(760, "contextual-return-narrow.png");
                Check(1600, "contextual-return-wide.png");
                window.WindowState = WindowState.Maximized;
                window.UpdateLayout();
                Assert.All(tabs.Items.Cast<TabItem>(), tab => Assert.True(tab.IsVisible && tab.ActualWidth > 0));
                var maximized = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                maximized.Render(window);
                var maximizedEncoder = new PngBitmapEncoder(); maximizedEncoder.Frames.Add(BitmapFrame.Create(maximized));
                using (var output = File.Create(Path.Combine(ProgressRepositoryRoot(), "artifacts", "ui-snap-regression", "maximized-after-narrow.png"))) maximizedEncoder.Save(output);
                window.WindowState = WindowState.Normal;
                Check(1600, "after-maximized.png");
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
            finally { if (window is not null) { window.DataContext = null; window.UpdateLayout(); window.AuthorizeFinalClose(); } }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); try { await done.Task; } finally { thread.Join(); }
    }

    private static void PumpContextual(Func<bool> ready)
    {
        var frame = new DispatcherFrame(); var deadline = DateTime.UtcNow.AddSeconds(10); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (ready() || DateTime.UtcNow > deadline) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Assert.True(ready());
    }
}
