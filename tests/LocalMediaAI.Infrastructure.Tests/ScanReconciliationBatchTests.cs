using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class ScanReconciliationBatchTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LocalMediaAIScanBatchTests", Guid.NewGuid().ToString("N"));
    private AppPaths _paths = null!;
    private JsonFileLogger _log = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "portable.flag"), string.Empty);
        _paths = AppPaths.Create(_root);
        _log = new JsonFileLogger(_paths);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _log.Dispose();
        SqliteConnection.ClearAllPools();
        for (var attempt = 0; Directory.Exists(_root) && attempt < 8; attempt++)
        {
            try { Directory.Delete(_root, true); }
            catch (IOException) { await Task.Delay(100); }
            catch (UnauthorizedAccessException) { await Task.Delay(100); }
        }
    }

    [Fact]
    public async Task ReconciliationBatchPreservesRenameMissingAndSourceVersionContracts()
    {
        var mediaRoot = Path.Combine(_root, "media");
        Directory.CreateDirectory(mediaRoot);
        await File.WriteAllBytesAsync(Path.Combine(mediaRoot, "a.mp4"), Pattern(200_000, 1));
        await File.WriteAllBytesAsync(Path.Combine(mediaRoot, "b.mp4"), Pattern(200_000, 2));
        await File.WriteAllBytesAsync(Path.Combine(mediaRoot, "c.mp4"), Pattern(200_000, 3));
        var repository = new SqliteLibraryRepository(_paths, _log);
        await repository.InitializeAsync(CancellationToken.None);
        var root = await repository.AddOrGetLibraryRootAsync(mediaRoot, CancellationToken.None);
        Assert.Empty(await repository.ReconcileScannedFilesAsync([], Guid.NewGuid(), CancellationToken.None));

        await ScanWithBatchAsync(repository, root);
        var initial = await SnapshotAsync(repository, root.LibraryRootId);
        File.Move(Path.Combine(mediaRoot, "a.mp4"), Path.Combine(mediaRoot, "renamed.mp4"));
        File.Delete(Path.Combine(mediaRoot, "b.mp4"));
        await File.WriteAllBytesAsync(Path.Combine(mediaRoot, "c.mp4"), Pattern(210_000, 9));
        File.SetLastWriteTimeUtc(Path.Combine(mediaRoot, "c.mp4"), DateTime.UtcNow.AddSeconds(2));

        await ScanWithBatchAsync(repository, root);
        var current = await SnapshotAsync(repository, root.LibraryRootId);
        var initialA = initial.Single(value => value.FileName == "a.mp4");
        var initialB = initial.Single(value => value.FileName == "b.mp4");
        var initialC = initial.Single(value => value.FileName == "c.mp4");
        var renamed = current.Single(value => value.FileName == "renamed.mp4");
        var missing = current.Single(value => value.MediaFileId == initialB.MediaFileId);
        var changed = current.Single(value => value.FileName == "c.mp4");

        Assert.Equal(initialA.MediaFileId, renamed.MediaFileId);
        Assert.Equal(initialA.MediaContentId, renamed.MediaContentId);
        Assert.Equal(PresenceState.Present, renamed.PresenceState);
        Assert.Equal(PresenceState.Missing, missing.PresenceState);
        Assert.Equal(initialC.MediaFileId, changed.MediaFileId);
        Assert.NotEqual(initialC.MediaContentId, changed.MediaContentId);
        Assert.Equal(PresenceState.Present, changed.PresenceState);
    }

    private static async Task ScanWithBatchAsync(SqliteLibraryRepository repository, LibraryRoot root)
    {
        var scanner = new FileSystemMediaScanner();
        var scan = await repository.StartScanAsync(root.LibraryRootId, CancellationToken.None);
        var files = new List<ScannedMediaFile>();
        await foreach (var file in scanner.EnumerateAsync(root, CancellationToken.None)) files.Add(file);
        var results = await repository.ReconcileScannedFilesAsync(files, scan, CancellationToken.None);
        await repository.CompleteScanAsync(scan, new ScanCompletion("Completed", files.Count, results.Count(result => result.IsNew), results.Count(result => !result.IsNew), 0, 0, DateTimeOffset.UtcNow), CancellationToken.None);
        await repository.MarkAbsentFilesAfterCompletedScanAsync(root.LibraryRootId, scan, CancellationToken.None);
    }

    // Reads every catalog row of the root, including Missing ones that the current-media Library list leaves out.
    private async Task<IReadOnlyList<MediaFile>> SnapshotAsync(SqliteLibraryRepository repository, Guid rootId)
    {
        var ids = new List<Guid>();
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _paths.DatabasePath }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT media_file_id FROM media_file WHERE library_root_id=$root;";
            command.Parameters.AddWithValue("$root", rootId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) ids.Add(Guid.Parse(reader.GetString(0)));
        }
        var values = new List<MediaFile>();
        foreach (var id in ids) values.Add((await repository.GetMediaFileAsync(id, CancellationToken.None))!);
        return values;
    }

    private static byte[] Pattern(int size, int salt) => Enumerable.Range(0, size).Select(index => (byte)((index * 31 + salt) % 251)).ToArray();
}
