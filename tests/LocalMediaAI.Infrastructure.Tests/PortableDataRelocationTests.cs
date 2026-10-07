using System.Security.Cryptography;
using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class PortableDataRelocationTests
{
    [Fact]
    public async Task RelocationCopiesDataAndCacheVerbatimAndLeavesTheSourceUntouched()
    {
        using var fixture = await RelocationFixture.CreateAsync();
        var sourceBefore = fixture.SourceHashes();
        var relocation = fixture.Relocation();

        var plan = relocation.Plan();
        var result = await relocation.ExecuteAsync(null, CancellationToken.None);

        Assert.True(plan.CanExecute, string.Join(" ", plan.Blockers));
        Assert.True(result.Succeeded, result.Failure);
        Assert.True(result.IntegrityOk);
        Assert.Equal(plan.FileCount, result.FilesCopied);
        Assert.Equal(plan.TotalBytes, result.BytesCopied);
        Assert.Equal(sourceBefore, fixture.SourceHashes());
        Assert.True(File.Exists(Path.Combine(fixture.Destination, "cache", "thumbnails", "ab", "abcdef.jpg")));
        Assert.True(File.Exists(Path.Combine(fixture.Destination, PortableDataRelocation.MarkerFileName)));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Destination, "*.relocating-*"));
        Assert.True(File.Exists(Path.Combine(fixture.AppRoot, "portable.flag")));

        var relocated = new SqliteLibraryRepository(fixture.RelocatedPaths(), new NullLogger());
        await relocated.InitializeAsync(CancellationToken.None);
        Assert.Single(await relocated.GetLibraryRootsAsync(CancellationToken.None));
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task ExistingDestinationContentBlocksAndIsNeverOverwritten()
    {
        using var fixture = await RelocationFixture.CreateAsync();
        var existing = Path.Combine(fixture.Destination, "data", "library.db");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        await File.WriteAllTextAsync(existing, "existing");
        var relocation = fixture.Relocation();

        var result = await relocation.ExecuteAsync(null, CancellationToken.None);

        Assert.False(relocation.Plan().CanExecute);
        Assert.False(result.Succeeded);
        Assert.Equal("existing", await File.ReadAllTextAsync(existing));
    }

    [Fact]
    public async Task OpenCatalogBlocksRelocation()
    {
        using var fixture = await RelocationFixture.CreateAsync();
        await using var holder = new FileStream(Path.Combine(fixture.AppRoot, "data", "library.db"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        var plan = fixture.Relocation().Plan();

        Assert.Contains(plan.Blockers, blocker => blocker.Contains("open", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NonPortableInstallIsBlocked()
    {
        using var fixture = await RelocationFixture.CreateAsync();
        File.Delete(Path.Combine(fixture.AppRoot, "portable.flag"));

        Assert.False(fixture.Relocation().Plan().CanExecute);
    }

    [Fact]
    public async Task UnreadableSourceFileAbortsWithoutPublishingAnything()
    {
        using var fixture = await RelocationFixture.CreateAsync();
        var locked = Path.Combine(fixture.AppRoot, "cache", "thumbnails", "ab", "abcdef.jpg");
        await using var holder = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = await fixture.Relocation().ExecuteAsync(null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(Directory.Exists(Path.Combine(fixture.Destination, "data")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Destination, "cache")));
        Assert.Empty(Directory.Exists(fixture.Destination) ? Directory.EnumerateDirectories(fixture.Destination, "*.relocating-*") : []);
        Assert.False(File.Exists(Path.Combine(fixture.Destination, PortableDataRelocation.MarkerFileName)));
    }

    [Fact]
    public async Task SwitchDisablesPortableFlagOnlyAfterARelocationAndRollbackRestoresIt()
    {
        using var fixture = await RelocationFixture.CreateAsync();
        var relocation = fixture.Relocation();
        Assert.Throws<InvalidOperationException>(relocation.Switch);

        Assert.True((await relocation.ExecuteAsync(null, CancellationToken.None)).Succeeded);
        relocation.Switch();

        Assert.False(File.Exists(Path.Combine(fixture.AppRoot, "portable.flag")));
        Assert.True(File.Exists(Path.Combine(fixture.AppRoot, PortableDataRelocation.DisabledPortableFlag)));
        relocation.Rollback();
        Assert.True(File.Exists(Path.Combine(fixture.AppRoot, "portable.flag")));
        Assert.True(Directory.Exists(Path.Combine(fixture.Destination, "data")));
    }

    [Fact]
    public async Task SwitchToAChosenFolderPointsTheAppThereAndRollbackUndoesIt()
    {
        using var fixture = await RelocationFixture.CreateAsync();
        var chosen = Path.Combine(fixture.Base, "second-ssd", "LocalMediaAI");
        var relocation = new PortableDataRelocation(fixture.AppRoot, chosen, fixture.Destination);
        Assert.True((await relocation.ExecuteAsync(null, CancellationToken.None)).Succeeded);

        relocation.Switch();
        var switched = AppPaths.Create(fixture.AppRoot);

        Assert.Equal(Path.Combine(chosen, "data"), switched.DataRoot);
        Assert.Equal(Path.Combine(chosen, "cache"), switched.CacheRoot);
        Assert.Equal(Path.Combine(chosen, "temp"), switched.TempRoot);
        Assert.Equal(chosen, switched.DataLocationRoot);
        switched.EnsureDataLocationPresent();

        relocation.Rollback();
        var restored = AppPaths.Create(fixture.AppRoot);

        Assert.Equal(Path.Combine(fixture.AppRoot, "data"), restored.DataRoot);
        Assert.Null(restored.DataLocationRoot);
        Assert.False(File.Exists(Path.Combine(fixture.AppRoot, DataLocation.FileName)));
        Assert.True(File.Exists(Path.Combine(fixture.AppRoot, DataLocation.DisabledFileName)));
    }

    [Fact]
    public async Task SwitchingToThePerUserFolderSetsAStalePointerAside()
    {
        using var fixture = await RelocationFixture.CreateAsync();
        DataLocation.Write(fixture.AppRoot, Path.Combine(fixture.Base, "old-choice"));
        var relocation = fixture.Relocation();
        Assert.True((await relocation.ExecuteAsync(null, CancellationToken.None)).Succeeded);

        relocation.Switch();

        Assert.False(File.Exists(Path.Combine(fixture.AppRoot, DataLocation.FileName)));
        Assert.False(File.Exists(Path.Combine(fixture.AppRoot, "portable.flag")));
    }

    [Fact]
    public async Task DriveRootDestinationIsBlockedBeforeAnythingIsCopied()
    {
        using var fixture = await RelocationFixture.CreateAsync();
        var driveRoot = Path.GetPathRoot(fixture.Base)!;

        var plan = new PortableDataRelocation(fixture.AppRoot, driveRoot, fixture.Destination).Plan();

        Assert.Contains(plan.Blockers, blocker => blocker.Contains("drive root", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SwitchRefusesDataRelocatedFromAnotherInstall()
    {
        using var fixture = await RelocationFixture.CreateAsync();
        Assert.True((await fixture.Relocation().ExecuteAsync(null, CancellationToken.None)).Succeeded);
        var other = Directory.CreateDirectory(Path.Combine(fixture.Base, "other-app")).FullName;
        await File.WriteAllTextAsync(Path.Combine(other, "portable.flag"), "");

        Assert.Throws<InvalidOperationException>(() => new PortableDataRelocation(other, fixture.Destination, fixture.Destination).Switch());
        Assert.True(File.Exists(Path.Combine(other, "portable.flag")));
    }

    [Fact]
    public async Task SwitchRefusesWhenThePortableCatalogChangedAfterRelocation()
    {
        using var fixture = await RelocationFixture.CreateAsync();
        var relocation = fixture.Relocation();
        Assert.True((await relocation.ExecuteAsync(null, CancellationToken.None)).Succeeded);
        await File.AppendAllTextAsync(Path.Combine(fixture.AppRoot, "data", "library.db"), "later write");

        Assert.Throws<InvalidOperationException>(relocation.Switch);
        Assert.True(File.Exists(Path.Combine(fixture.AppRoot, "portable.flag")));
    }

    [Fact]
    public async Task RollbackRefusesToHideWorkDoneInThePerUserCatalogUnlessForced()
    {
        using var fixture = await RelocationFixture.CreateAsync();
        var relocation = fixture.Relocation();
        Assert.True((await relocation.ExecuteAsync(null, CancellationToken.None)).Succeeded);
        relocation.Switch();
        File.SetLastWriteTimeUtc(Path.Combine(fixture.Destination, "data", "library.db"), DateTime.UtcNow.AddHours(2));

        Assert.Throws<InvalidOperationException>(() => relocation.Rollback());
        Assert.False(File.Exists(Path.Combine(fixture.AppRoot, "portable.flag")));
        relocation.Rollback(force: true);
        Assert.True(File.Exists(Path.Combine(fixture.AppRoot, "portable.flag")));
    }

    [Fact]
    public async Task FailureWhilePublishingTheSecondTreeUndoesTheFirstAndPublishesNothing()
    {
        using var fixture = await RelocationFixture.CreateAsync();
        var relocation = fixture.Relocation();
        relocation.BeforePublishTreeForTests = tree => { if (tree == "cache") throw new IOException("simulated scanner lock"); };

        var result = await relocation.ExecuteAsync(null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(result.LeftPartialDestination);
        Assert.Contains("Nothing was published", result.Failure);
        Assert.False(Directory.Exists(Path.Combine(fixture.Destination, "data")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Destination, "cache")));
        Assert.False(File.Exists(Path.Combine(fixture.Destination, PortableDataRelocation.MarkerFileName)));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Destination, "*.relocating-*"));
        Assert.True(fixture.Relocation().Plan().CanExecute);
    }

    [Fact]
    public async Task SourceChangingDuringTheCopyAbortsBeforePublishing()
    {
        using var fixture = await RelocationFixture.CreateAsync();
        var relocation = fixture.Relocation();
        relocation.AfterCopyForTests = () => File.AppendAllText(Path.Combine(fixture.AppRoot, "cache", "thumbnails", "ab", "abcdef.jpg"), "x");

        var result = await relocation.ExecuteAsync(null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("changed", result.Failure);
        Assert.False(Directory.Exists(Path.Combine(fixture.Destination, "data")));
    }

    [Fact]
    public async Task CancellationLeavesNoStagingBehind()
    {
        using var fixture = await RelocationFixture.CreateAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var result = await fixture.Relocation().ExecuteAsync(null, cancelled.Token);

        Assert.False(result.Succeeded);
        Assert.Empty(Directory.Exists(fixture.Destination) ? Directory.EnumerateDirectories(fixture.Destination) : []);
    }

    [Fact]
    public async Task JunctionInsideTheSourceBlocksRelocation()
    {
        using var fixture = await RelocationFixture.CreateAsync();
        var outside = Directory.CreateDirectory(Path.Combine(fixture.Base, "outside")).FullName;
        using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", Path.Combine(fixture.AppRoot, "cache", "linked"), outside]) { CreateNoWindow = true, UseShellExecute = false })!)
            await process.WaitForExitAsync();

        Assert.Contains(fixture.Relocation().Plan().Blockers, blocker => blocker.Contains("junction", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class NullLogger : IStructuredLogger
    {
        public void Write(string level, string eventName, object fields, Exception? exception = null) { }
    }

    private sealed class RelocationFixture : IDisposable
    {
        private RelocationFixture(string @base)
        {
            Base = @base;
            AppRoot = Directory.CreateDirectory(Path.Combine(@base, "app")).FullName;
            Destination = Path.Combine(@base, "LocalAppData", "LocalMediaAI");
        }

        public string Base { get; }
        public string AppRoot { get; }
        public string Destination { get; }

        public static async Task<RelocationFixture> CreateAsync()
        {
            var fixture = new RelocationFixture(Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "lmai-relocate-" + Guid.NewGuid().ToString("N"))).FullName);
            await File.WriteAllTextAsync(Path.Combine(fixture.AppRoot, "portable.flag"), "");
            var paths = AppPaths.Create(fixture.AppRoot);
            var repository = new SqliteLibraryRepository(paths, new NullLogger());
            await repository.InitializeAsync(CancellationToken.None);
            var media = Directory.CreateDirectory(Path.Combine(fixture.Base, "media")).FullName;
            await repository.AddOrGetLibraryRootAsync(media, CancellationToken.None);
            var thumbnail = Path.Combine(paths.CacheRoot, "thumbnails", "ab", "abcdef.jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(thumbnail)!);
            await File.WriteAllBytesAsync(thumbnail, RandomNumberGenerator.GetBytes(5000));
            SqliteConnection.ClearAllPools();
            return fixture;
        }

        public PortableDataRelocation Relocation() => new(AppRoot, Destination, Destination);

        public AppPaths RelocatedPaths() => new(AppRoot, Path.Combine(Destination, "data"), Path.Combine(Destination, "cache"),
            Path.Combine(AppRoot, "models"), Path.Combine(AppRoot, "runtime"), Path.Combine(Destination, "temp"));

        public Dictionary<string, string> SourceHashes() => new[] { "data", "cache" }
            .Select(tree => Path.Combine(AppRoot, tree))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Base, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
