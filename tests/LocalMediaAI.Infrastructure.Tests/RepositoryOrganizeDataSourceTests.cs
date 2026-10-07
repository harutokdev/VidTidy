using LocalMediaAI.App.Organize;
using LocalMediaAI.Core;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class RepositoryOrganizeDataSourceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LocalMediaAIOrganizeSourceTests", Guid.NewGuid().ToString("N"));
    private SqliteLibraryRepository _repository = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "portable.flag"), string.Empty);
        var paths = AppPaths.Create(_root);
        paths.EnsureDirectories();
        _repository = new SqliteLibraryRepository(paths, new NullLogger());
        await _repository.InitializeAsync(CancellationToken.None);
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
    public async Task ExactDuplicatesAcrossFoldersBecomeOneGroupWithBothFolders()
    {
        var bytes = Pattern(150_000, 17);
        var folderA = await AddFolderAsync("整理済み", ("a.mp4", bytes), ("unique.mp4", Pattern(150_000, 99)));
        var folderB = await AddFolderAsync("ダウンロード", ("a (1).mp4", bytes));
        await HashAllAndRebuildAsync();

        var snapshot = await new RepositoryOrganizeDataSource(_repository).LoadAsync(CancellationToken.None);

        Assert.Equal(2, snapshot.Folders.Count);
        Assert.All(snapshot.Folders, f => Assert.True(f.IsOnline));
        var group = Assert.Single(snapshot.Groups);
        Assert.Equal(GroupCategory.Exact, group.Category);
        Assert.Equal(new[] { folderA.LibraryRootId, folderB.LibraryRootId }.ToHashSet(), group.Files.Select(f => f.RootId).ToHashSet());
        Assert.All(group.Files, f => Assert.Equal(150_000, f.SizeBytes));
    }

    [Fact]
    public async Task NotDuplicateJudgmentHidesTheGroupUntilReset()
    {
        var bytes = Pattern(120_000, 5);
        await AddFolderAsync("A", ("x.mp4", bytes), ("x copy.mp4", bytes));
        await HashAllAndRebuildAsync();
        var source = new RepositoryOrganizeDataSource(_repository);
        var group = Assert.Single((await source.LoadAsync(CancellationToken.None)).Groups);

        await source.RecordNotDuplicateAsync(group.GroupId, CancellationToken.None);
        Assert.Empty((await source.LoadAsync(CancellationToken.None)).Groups);

        await source.ResetJudgmentAsync(group.GroupId, CancellationToken.None);
        Assert.Single((await source.LoadAsync(CancellationToken.None)).Groups);
    }

    [Fact]
    public void SimilarGroupMadeOnlyOfExactCopiesDisappears()
    {
        var root = Guid.NewGuid();
        OrganizeFile F(string name) => new(Guid.NewGuid(), root, name, $"C:\\v\\{name}", 1, DateTimeOffset.UnixEpoch, null, null, null, null, null);
        var a = F("a.mp4");
        var aCopy = F("a copy.mp4");
        var b = F("b.mp4");
        var exact = Guid.NewGuid();
        var map = new Dictionary<Guid, Guid> { [a.MediaFileId] = exact, [aCopy.MediaFileId] = exact };

        Assert.Single(RepositoryOrganizeDataSource.WithoutExactCopies([a, aCopy], map));
        var mixed = RepositoryOrganizeDataSource.WithoutExactCopies([a, aCopy, b], map);
        Assert.Equal(2, mixed.Count);
        Assert.Contains(b, mixed);
        Assert.Single(mixed, f => f == a || f == aCopy);
    }

    private async Task<LibraryRoot> AddFolderAsync(string name, params (string File, byte[] Bytes)[] files)
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
        return root;
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

    private sealed class NullLogger : IStructuredLogger
    {
        public void Write(string level, string eventName, object fields, Exception? exception = null) { }
    }
}
