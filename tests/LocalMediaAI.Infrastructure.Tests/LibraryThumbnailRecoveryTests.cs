using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LocalMediaAI.App;
using LocalMediaAI.App.ViewModels;
using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class LibraryThumbnailRecoveryTests : IAsyncLifetime, IDisposable
{
    private readonly string _fixture = Path.Combine(Path.GetTempPath(), "LocalMediaAIThumbnailRecovery", Guid.NewGuid().ToString("N"));
    private AppPaths _paths = null!;
    private JsonFileLogger _log = null!;
    private SqliteLibraryRepository _library = null!;
    private SqliteJobStore _jobs = null!;
    private string _ffmpeg = null!;
    private string _ffprobe = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_fixture);
        File.WriteAllText(Path.Combine(_fixture, "portable.flag"), string.Empty);
        _paths = AppPaths.Create(_fixture);
        _log = new JsonFileLogger(_paths);
        _library = new SqliteLibraryRepository(_paths, _log);
        _jobs = new SqliteJobStore(_paths, _log);
        await _library.InitializeAsync(CancellationToken.None);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src", "LocalMediaAI.App"))) root = root.Parent;
        _ffmpeg = Path.Combine(root!.FullName, "runtime", "ffmpeg", "ffmpeg.exe");
        _ffprobe = Path.Combine(root.FullName, "runtime", "ffmpeg", "ffprobe.exe");
    }

    [Fact]
    public async Task ImageVideoAndMissingArtifactUseCurrentContentAndExistingBoundedThumbnailJobs()
    {
        var mediaDirectory = Path.Combine(_fixture, "media");
        Directory.CreateDirectory(mediaDirectory);
        var image = Path.Combine(mediaDirectory, "still.png");
        var video = Path.Combine(mediaDirectory, "short.mp4");
        await GenerateAsync("red", image, false);
        await GenerateAsync("blue", video, true);
        var root = await _library.AddOrGetLibraryRootAsync(mediaDirectory, CancellationToken.None);
        var scanner = new FileSystemMediaScanner();
        var first = await ScanAsync(scanner, root);
        Assert.Equal(2, first.Count);
        Assert.Contains(first, media => media.Extension == ".png");
        Assert.Contains(first, media => media.Extension == ".mp4");

        var adapter = new FfmpegAdapter(new FfmpegPaths(_ffmpeg, _ffprobe), _paths, _log);
        using var pipeline = new MediaPipeline(_library, _jobs, scanner, adapter, _log);
        var ready = new System.Collections.Concurrent.ConcurrentBag<ThumbnailReadyEventArgs>();
        pipeline.ThumbnailReady += (_, item) => ready.Add(item);
        Assert.Equal(2, await pipeline.EnqueueMissingThumbnailJobsAsync(CancellationToken.None));
        await pipeline.RunUntilIdleAsync(CancellationToken.None);
        Assert.Equal(2, ready.Count);
        Assert.All(first, media => Assert.Contains(ready, item => item.MediaFileId == media.MediaFileId && item.MediaContentId == media.MediaContentId));
        var visible = await _library.QueryMediaAsync(root.LibraryRootId, null, 0, 10, CancellationToken.None);
        Assert.All(visible.Items, item =>
        {
            Assert.NotNull(item.ThumbnailRelativePath);
            Assert.True(File.Exists(Path.Combine(_paths.CacheRoot, item.ThumbnailRelativePath!)));
        });
        await CaptureLibraryAsync(visible.Items, "mixed-image-video-library.png");
        var videoRow = Assert.Single(visible.Items, item => item.FileName == "short.mp4");
        var videoArtifact = Path.Combine(_paths.CacheRoot, videoRow.ThumbnailRelativePath!);
        var validVideoArtifact = await File.ReadAllBytesAsync(videoArtifact);
        await File.WriteAllBytesAsync(videoArtifact, [0, 1, 2, 3]); // isolated rebuildable cache fixture
        await CaptureLibraryAsync(visible.Items, "individual-decode-failure.png", expectedLoaded: 1, expectDecodeFailure: true);
        await File.WriteAllBytesAsync(videoArtifact, validVideoArtifact);

        var imageRow = Assert.Single(visible.Items, item => item.FileName == "still.png");
        var oldArtifact = Path.Combine(_paths.CacheRoot, imageRow.ThumbnailRelativePath!);
        File.Delete(oldArtifact); // isolated rebuildable cache fixture only
        Assert.Equal(1, await pipeline.EnqueueMissingThumbnailJobsAsync(CancellationToken.None));
        await pipeline.RunUntilIdleAsync(CancellationToken.None);
        Assert.True(File.Exists(oldArtifact));
        Assert.Equal(3, ready.Count);

        await GenerateAsync("green", image, false);
        var current = await ScanAsync(scanner, root);
        Assert.NotEqual(first.Single(media => media.Extension == ".png").MediaContentId,
            current.Single(media => media.Extension == ".png").MediaContentId);
        Assert.Equal(1, await pipeline.EnqueueMissingThumbnailJobsAsync(CancellationToken.None));
        await pipeline.RunUntilIdleAsync(CancellationToken.None);
        var changed = Assert.Single((await _library.QueryMediaAsync(root.LibraryRootId, "still", 0, 10, CancellationToken.None)).Items);
        Assert.NotEqual(imageRow.ThumbnailRelativePath, changed.ThumbnailRelativePath);
        Assert.Contains(ready, item => item.MediaContentId == current.Single(media => media.Extension == ".png").MediaContentId && item.RelativePath == changed.ThumbnailRelativePath);

        var operations = new SafeFileOperationService(_library, _log);
        var videoMedia = (await _library.GetMediaFileAsync(first.Single(media => media.Extension == ".mp4").MediaFileId, CancellationToken.None))!;
        var destinationDirectory = Path.Combine(mediaDirectory, "organized");
        Directory.CreateDirectory(destinationDirectory);
        var destination = Path.Combine(destinationDirectory, "short.mp4");
        var move = await operations.CreatePlanAsync(videoMedia.MediaFileId, FileOperationType.Move, FileOperationMode.ProductionDryRun, destination, null, "Isolated generated fixture", SourceValidationStrength.StrongFullHash, CancellationToken.None);
        Assert.Equal(FileOperationState.ConfirmationRequired, (await operations.RunDryRunAsync(move.OperationId, CancellationToken.None)).Plan.State);
        await operations.AcknowledgeProductionDryRunAsync(move.OperationId, CancellationToken.None);
        await operations.ConfirmProductionSameVolumeExecutionAsync(move.OperationId, CancellationToken.None);
        Assert.Equal(FileOperationState.Completed, (await operations.ExecuteProductionSameVolumeAsync(move.OperationId, CancellationToken.None)).State);
        var afterMove = await _library.QueryMediaAsync(root.LibraryRootId, null, 0, 10, CancellationToken.None);
        Assert.Contains(afterMove.Items, item => item.Path == destination && item.MediaFileId == videoMedia.MediaFileId);
        Assert.DoesNotContain(afterMove.Items, item => item.Path == video);
        await CaptureLibraryAsync(afterMove.Items, "after-move.png");

        var recycle = await operations.CreatePlanAsync(videoMedia.MediaFileId, FileOperationType.Recycle, FileOperationMode.ProductionDryRun, null, null, "Isolated generated fixture", SourceValidationStrength.StrongFullHash, CancellationToken.None);
        Assert.Equal(FileOperationState.ConfirmationRequired, (await operations.RunDryRunAsync(recycle.OperationId, CancellationToken.None)).Plan.State);
        await operations.AcknowledgeProductionDryRunAsync(recycle.OperationId, CancellationToken.None);
        await operations.ConfirmProductionRecycleExecutionAsync(recycle.OperationId, CancellationToken.None);
        Assert.Equal(FileOperationState.Recycled, (await operations.ExecuteProductionRecycleAsync(recycle.OperationId, CancellationToken.None)).State);
        var afterRecycle = await _library.QueryMediaAsync(root.LibraryRootId, null, 0, 10, CancellationToken.None);
        Assert.DoesNotContain(afterRecycle.Items, item => item.MediaFileId == videoMedia.MediaFileId);
        Assert.Single(afterRecycle.Items);
        Assert.NotNull(afterRecycle.Items[0].ThumbnailRelativePath);
        Assert.True(File.Exists(Path.Combine(_paths.CacheRoot, afterRecycle.Items[0].ThumbnailRelativePath!)));
        await CaptureLibraryAsync(afterRecycle.Items, "after-recycle.png");
    }

    private async Task CaptureLibraryAsync(IReadOnlyList<MediaListItem> items, string fileName, int? expectedLoaded = null, bool expectDecodeFailure = false)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            MainViewModel? viewModel = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var worker = new WorkerProcessManager(_paths, _log);
                var adapter = new FfmpegAdapter(new FfmpegPaths(_ffmpeg, _ffprobe), _paths, _log);
                var pipeline = new MediaPipeline(_library, _jobs, new FileSystemMediaScanner(), adapter, _log, worker: worker, paths: _paths);
                viewModel = new MainViewModel(_library, _jobs, pipeline, worker, new SemanticSearchService(_library, worker, _paths, _log), _log);
                window = new MainWindow { DataContext = viewModel, Width = 1600, Height = 900, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
                window.Show();
                viewModel.RefreshCommand.Execute(null);
                var table = (DataGrid)((LibraryWorkspace)window.FindName("MediaLibraryWorkspace")).FindName("MediaTable");
                var deadline = DateTime.UtcNow.AddSeconds(8);
                var scrolled = false;
                var targetLoaded = expectedLoaded ?? items.Count;
                while (DateTime.UtcNow < deadline)
                {
                    window.UpdateLayout();
                    if (!scrolled && table.Items.Count > 0) { table.ScrollIntoView(table.Items[0]); table.UpdateLayout(); scrolled = true; }
                    var loaded = Descendants<Image>(table).Count(image => image.Source is not null);
                    var failures = Descendants<Image>(table).Count(image => LibraryThumbnailLoader.GetLoadError(image) is not null);
                    if (loaded >= targetLoaded && (!expectDecodeFailure || failures > 0) && viewModel.MediaItems.Count == items.Count && viewModel.TotalMediaCount == items.Count) break;
                    var frame = new DispatcherFrame();
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                    timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                    timer.Start(); Dispatcher.PushFrame(frame);
                }
                var rendered = Descendants<Image>(table).Count(image => image.Source is not null);
                var images = Descendants<Image>(table).ToArray();
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var output = Path.Combine(RepositoryRoot(), "artifacts", "task-three-fixes-runtime", fileName);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                using var file = File.Create(output); encoder.Save(file);
                Assert.True(rendered >= targetLoaded && (!expectDecodeFailure || images.Any(image => LibraryThumbnailLoader.GetLoadError(image) is not null)), $"Current real thumbnails should render in WPF: rendered={rendered}, images={images.Length}, loaded={string.Join(',', images.Select(image => image.IsLoaded))}, statuses={string.Join(',', images.Select(LibraryThumbnailLoader.GetLoadStatus))}, errors={string.Join(',', images.Select(LibraryThumbnailLoader.GetLoadError))}, roots={string.Join(',', images.Select(LibraryThumbnailLoader.GetCacheRoot))}, attached={string.Join(',', images.Select(LibraryThumbnailLoader.GetRelativePath))}, tableItems={table.Items.Count}, tableHeight={table.ActualHeight}, rows={viewModel.MediaItems.Count}, total={viewModel.TotalMediaCount}, expected={items.Count}, paths={string.Join(',', viewModel.MediaItems.Select(item => item.ThumbnailRelativePath))}.");
                Assert.Equal(items.Count, viewModel.TotalMediaCount);
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
            finally
            {
                if (window is not null) { window.DataContext = null; window.AuthorizeFinalClose(); }
                viewModel?.Dispose();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { await done.Task; } finally { thread.Join(); }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T item) yield return item;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src", "LocalMediaAI.App"))) root = root.Parent;
        return root!.FullName;
    }

    private async Task<IReadOnlyList<MediaFile>> ScanAsync(FileSystemMediaScanner scanner, LibraryRoot root)
    {
        var scan = await _library.StartScanAsync(root.LibraryRootId, CancellationToken.None);
        var media = new List<MediaFile>();
        await foreach (var file in scanner.EnumerateAsync(root, CancellationToken.None))
            media.Add((await _library.UpsertScannedFileAsync(file, scan, CancellationToken.None)).MediaFile);
        await _library.CompleteScanAsync(scan, new ScanCompletion("Completed", media.Count, media.Count, 0, 0, 0, DateTimeOffset.UtcNow), CancellationToken.None);
        return media;
    }

    private async Task GenerateAsync(string color, string destination, bool video)
    {
        var info = new ProcessStartInfo(_ffmpeg) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"color=c={color}:s=320x180" }) info.ArgumentList.Add(argument);
        if (video) foreach (var argument in new[] { "-t", "0.5", "-c:v", "mpeg4" }) info.ArgumentList.Add(argument);
        else foreach (var argument in new[] { "-frames:v", "1" }) info.ArgumentList.Add(argument);
        info.ArgumentList.Add(destination);
        using var process = Process.Start(info)!;
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(CancellationToken.None);
        Assert.True(process.ExitCode == 0, await errors);
    }

    public async Task DisposeAsync()
    {
        Dispose();
        SqliteConnection.ClearAllPools();
        for (var attempt = 0; Directory.Exists(_fixture) && attempt < 8; attempt++)
        {
            try { Directory.Delete(_fixture, true); }
            catch (IOException) { await Task.Delay(100); }
        }
    }
    public void Dispose() { _log?.Dispose(); GC.SuppressFinalize(this); }
}
