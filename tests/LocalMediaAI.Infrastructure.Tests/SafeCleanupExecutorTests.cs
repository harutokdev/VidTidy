using LocalMediaAI.App.Organize;
using LocalMediaAI.Core;

namespace LocalMediaAI.Infrastructure.Tests;

/// <summary>Runs the organize executor over the real safe-operation gates with a fake recycle bin inside the test folder.</summary>
public sealed class SafeCleanupExecutorTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LocalMediaAICleanupExecutorTests", Guid.NewGuid().ToString("N"));
    private SqliteLibraryRepository _repository = null!;
    private FolderRecycleBin _bin = null!;
    private SafeCleanupExecutor _executor = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "portable.flag"), string.Empty);
        var paths = AppPaths.Create(_root);
        paths.EnsureDirectories();
        var log = new NullLogger();
        _repository = new SqliteLibraryRepository(paths, log);
        await _repository.InitializeAsync(CancellationToken.None);
        _bin = new FolderRecycleBin(Path.Combine(_root, "bin"));
        _executor = new SafeCleanupExecutor(_repository, new SafeFileOperationService(_repository, log, windowsRecycleBin: _bin), log);
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ExactDuplicatesGoToTheRecycleBinAndTheKeptFileStays()
    {
        var bytes = Pattern(90_000, 3);
        await AddFolderAsync("A", ("keep.mp4", bytes), ("copy1.mp4", bytes), ("copy2.mp4", bytes));
        var requests = await RequestsAsync();

        var check = await _executor.CheckAsync(requests, CancellationToken.None);
        Assert.All(check, c => Assert.True(c.IsReady, c.Reason));

        var results = await _executor.RunAsync(requests, null, CancellationToken.None);

        Assert.All(results, r => Assert.True(r.Succeeded, r.Reason));
        Assert.True(File.Exists(requests[0].Keep!.Path));
        Assert.All(requests, r => Assert.False(File.Exists(r.Target.Path)));
        Assert.Equal(2, _bin.Recycled.Count);
        foreach (var r in requests)
            Assert.Equal(PresenceState.Recycled, (await _repository.GetMediaFileAsync(r.Target.MediaFileId, CancellationToken.None))!.PresenceState);

        var history = await new RepositoryOrganizeDataSource(_repository).LoadHistoryAsync(CancellationToken.None);
        var run = Assert.Single(history);
        Assert.Equal(2, run.FileCount);
        Assert.Equal(180_000, run.Bytes);
        var paths = await new RepositoryOrganizeDataSource(_repository).LoadHistoryFilesAsync(run, CancellationToken.None);
        Assert.Equal(requests.Select(r => r.Target.Path).Order(), paths.Order());
    }

    [Fact]
    public async Task ChangedFileIsBlockedAndNothingElseIsHarmed()
    {
        var bytes = Pattern(90_000, 4);
        await AddFolderAsync("A", ("keep.mp4", bytes), ("copy1.mp4", bytes), ("copy2.mp4", bytes));
        var requests = await RequestsAsync();
        var changed = requests[0];
        await File.AppendAllTextAsync(changed.Target.Path, "edited");

        var check = await _executor.CheckAsync(requests, CancellationToken.None);
        Assert.False(check.Single(c => c.Request == changed).IsReady);

        var results = await _executor.RunAsync(requests, null, CancellationToken.None);

        Assert.False(results.Single(r => r.Request == changed).Succeeded);
        Assert.True(File.Exists(changed.Target.Path));
        Assert.True(results.Single(r => r.Request != changed).Succeeded);
        Assert.True(File.Exists(changed.Keep!.Path));
    }

    [Fact]
    public async Task MissingKeptFileBlocksEveryTargetOfThatGroup()
    {
        var bytes = Pattern(90_000, 5);
        await AddFolderAsync("A", ("keep.mp4", bytes), ("copy1.mp4", bytes));
        var requests = await RequestsAsync();
        File.Move(requests[0].Keep!.Path, Path.Combine(_root, "moved-away.mp4"));

        var results = await _executor.RunAsync(requests, null, CancellationToken.None);

        Assert.All(results, r => Assert.False(r.Succeeded));
        Assert.Equal("残すファイルが見つかりません", results[0].Reason);
        Assert.True(File.Exists(requests[0].Target.Path));
        Assert.Empty(_bin.Recycled);
    }

    [Fact]
    public async Task SingleFileWithoutKeptCopyIsRecycledAfterTargetChecks()
    {
        var bytes = Pattern(90_000, 6);
        await AddFolderAsync("A", ("keep.mp4", bytes), ("copy1.mp4", bytes));
        var target = (await RequestsAsync())[0].Target;
        var single = new CleanupRequest(Guid.NewGuid(), LocalMediaAI.Core.Organize.CleanupKind.Single, null, target);

        Assert.True((await _executor.CheckAsync([single], CancellationToken.None))[0].IsReady);
        var result = Assert.Single(await _executor.RunAsync([single], null, CancellationToken.None));

        Assert.True(result.Succeeded, result.Reason);
        Assert.False(File.Exists(target.Path));
    }

    [Fact]
    public async Task FileRestoredFromTheRecycleBinReturnsToItsGroupAfterARescan()
    {
        var bytes = Pattern(90_000, 7);
        await AddFolderAsync("A", ("keep.mp4", bytes), ("copy1.mp4", bytes));
        var requests = await RequestsAsync();
        Assert.All(await _executor.RunAsync(requests, null, CancellationToken.None), r => Assert.True(r.Succeeded, r.Reason));
        Assert.Empty((await new RepositoryOrganizeDataSource(_repository).LoadAsync(CancellationToken.None)).Groups);

        _bin.RestoreAll();
        await RescanAsync();

        var restored = (await _repository.GetMediaFileAsync(requests[0].Target.MediaFileId, CancellationToken.None))!;
        Assert.Equal(PresenceState.Present, restored.PresenceState);
        var group = Assert.Single((await new RepositoryOrganizeDataSource(_repository).LoadAsync(CancellationToken.None)).Groups);
        Assert.Equal(2, group.Files.Count);
    }

    private async Task RescanAsync()
    {
        foreach (var root in await _repository.GetLibraryRootsAsync(CancellationToken.None))
        {
            var scan = await _repository.StartScanAsync(root.LibraryRootId, CancellationToken.None);
            var count = 0;
            await foreach (var scanned in new FileSystemMediaScanner().EnumerateAsync(root, CancellationToken.None))
            {
                await _repository.UpsertScannedFileAsync(scanned, scan, CancellationToken.None);
                count++;
            }
            await _repository.CompleteScanAsync(scan, new ScanCompletion("Completed", count, 0, count, 0, 0, DateTimeOffset.UtcNow), CancellationToken.None);
        }
    }

    private async Task<IReadOnlyList<CleanupRequest>> RequestsAsync()
    {
        await HashAllAndRebuildAsync();
        var snapshot = await new RepositoryOrganizeDataSource(_repository).LoadAsync(CancellationToken.None);
        var group = Assert.Single(snapshot.Groups);
        var keep = group.Files.Single(f => f.FileName == "keep.mp4");
        return CleanupRequest.FromGroups([new GroupRow(group, keep.MediaFileId)]);
    }

    private async Task AddFolderAsync(string name, params (string File, byte[] Bytes)[] files)
    {
        var directory = Path.Combine(_root, "media", name);
        Directory.CreateDirectory(directory);
        foreach (var (file, bytes) in files) await File.WriteAllBytesAsync(Path.Combine(directory, file), bytes);
        var root = await _repository.AddOrGetLibraryRootAsync(directory, CancellationToken.None);
        var scan = await _repository.StartScanAsync(root.LibraryRootId, CancellationToken.None);
        var count = 0;
        await foreach (var scanned in new FileSystemMediaScanner().EnumerateAsync(root, CancellationToken.None))
        {
            await _repository.UpsertScannedFileAsync(scanned, scan, CancellationToken.None);
            count++;
        }
        await _repository.CompleteScanAsync(scan, new ScanCompletion("Completed", count, count, 0, 0, 0, DateTimeOffset.UtcNow), CancellationToken.None);
    }

    private async Task HashAllAndRebuildAsync()
    {
        foreach (var root in await _repository.GetLibraryRootsAsync(CancellationToken.None))
        {
            var page = await _repository.QueryMediaAsync(root.LibraryRootId, null, 0, 100, CancellationToken.None);
            foreach (var item in page.Items)
            {
                var media = (await _repository.GetMediaFileAsync(item.MediaFileId, CancellationToken.None))!;
                var digest = await FileHash.Sha256Async(media.Path, CancellationToken.None);
                Assert.True(await _repository.RecordFullHashEvidenceAsync(new FileHashEvidence(Guid.NewGuid(), media.MediaFileId, media.MediaContentId, "SHA-256", digest, media.SizeBytes, media.LastWriteTimeUtc, "full-sha256-v1", "Verified", null, null, DateTimeOffset.UtcNow), CancellationToken.None));
            }
        }
        await _repository.RebuildExactDuplicateGroupsAsync(CancellationToken.None);
    }

    private static byte[] Pattern(int size, int salt) => Enumerable.Range(0, size).Select(index => (byte)((index * 31 + salt) % 251)).ToArray();

    /// <summary>Moves files into a folder under the test root instead of the real Windows recycle bin.</summary>
    private sealed class FolderRecycleBin(string folder) : IWindowsRecycleBin
    {
        private readonly Dictionary<string, string> _held = [];

        public List<string> Recycled { get; } = [];

        public Task<WindowsRecycleBinResult> RecycleAsync(string fullyQualifiedSourcePath, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(folder);
            var held = Path.Combine(folder, Guid.NewGuid().ToString("N"));
            File.Move(fullyQualifiedSourcePath, held);
            _held[fullyQualifiedSourcePath] = held;
            Recycled.Add(fullyQualifiedSourcePath);
            return Task.FromResult(WindowsRecycleBinResult.Success(aborted: false));
        }

        /// <summary>Like "元に戻す" in Explorer: a rename on the same volume, so the file keeps its identity.</summary>
        public void RestoreAll()
        {
            foreach (var (original, held) in _held) File.Move(held, original);
            _held.Clear();
        }
    }

    private sealed class NullLogger : IStructuredLogger
    {
        public void Write(string level, string eventName, object fields, Exception? exception = null) { }
    }
}
