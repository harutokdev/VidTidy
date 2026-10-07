using System.Text.Json;
using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class TestLabPolicyTests
{
    [Fact]
    public void MediaInsideAMediaRootIsAllowed()
    {
        using var lab = new LabFixture();
        var folder = Directory.CreateDirectory(Path.Combine(lab.MediaRoot, "sets", "small")).FullName;

        lab.Policy.EnsureMediaPathAllowed(folder);
        lab.Policy.EnsureMediaPathAllowed(lab.MediaRoot);
        lab.Policy.EnsureMediaPathAllowed(Path.Combine(folder, "clip.mp4"));
    }

    [Fact]
    public void MediaOutsideEveryMediaRootIsRejected()
    {
        using var lab = new LabFixture();
        var personal = Directory.CreateDirectory(Path.Combine(lab.Base, "personal-videos")).FullName;

        Assert.Throws<TestLabViolationException>(() => lab.Policy.EnsureMediaPathAllowed(personal));
        Assert.Throws<TestLabViolationException>(() => lab.Policy.EnsureMediaPathAllowed(Path.Combine(personal, "clip.mp4")));
    }

    [Fact]
    public void SiblingWithTheSamePrefixIsNotInsideTheMediaRoot()
    {
        using var lab = new LabFixture();
        var sibling = Directory.CreateDirectory(lab.MediaRoot + "-other").FullName;

        Assert.Throws<TestLabViolationException>(() => lab.Policy.EnsureMediaPathAllowed(sibling));
    }

    [Fact]
    public void ParentTraversalCannotEscapeTheMediaRoot()
    {
        using var lab = new LabFixture();

        Assert.Throws<TestLabViolationException>(() => lab.Policy.EnsureMediaPathAllowed(Path.Combine(lab.MediaRoot, "..", "escape")));
    }

    [Fact]
    public async Task JunctionInsideTheMediaRootIsRejected()
    {
        using var lab = new LabFixture();
        var outside = Directory.CreateDirectory(Path.Combine(lab.Base, "outside")).FullName;
        var link = Path.Combine(lab.MediaRoot, "link");
        await CreateDirectoryJunctionAsync(link, outside);

        Assert.Throws<TestLabViolationException>(() => lab.Policy.EnsureMediaPathAllowed(link));
        Assert.Throws<TestLabViolationException>(() => lab.Policy.EnsureMediaPathAllowed(Path.Combine(link, "clip.mp4")));
    }

    [Fact]
    public void InstanceInsideAnInstanceRootIsAllowed()
    {
        using var lab = new LabFixture();
        var paths = lab.InstancePaths(Path.Combine(lab.InstanceRoot, "run-1"));

        lab.Policy.EnsureInstanceAllowed(paths, lab.ProductionRoot);
    }

    [Fact]
    public void InstanceWithAnyStorageRootOutsideTheLabIsRejected()
    {
        using var lab = new LabFixture();
        var paths = lab.InstancePaths(Path.Combine(lab.InstanceRoot, "run-1")) with { CacheRoot = Path.Combine(lab.Base, "elsewhere", "cache") };

        Assert.Throws<TestLabViolationException>(() => lab.Policy.EnsureInstanceAllowed(paths, lab.ProductionRoot));
    }

    [Fact]
    public void InstanceRootInsideProductionDataIsRejected()
    {
        using var lab = new LabFixture();
        var policy = new TestLabPolicy([lab.MediaRoot], [Path.Combine(lab.ProductionRoot, "lab")]);
        var paths = lab.InstancePaths(Path.Combine(lab.ProductionRoot, "lab", "run-1"));

        Assert.Throws<TestLabViolationException>(() => policy.EnsureInstanceAllowed(paths, lab.ProductionRoot));
    }

    [Fact]
    public void MarkerIsParsedAndMissingMarkerMeansNoTestLab()
    {
        using var lab = new LabFixture();
        var appRoot = Directory.CreateDirectory(Path.Combine(lab.Base, "app")).FullName;
        Assert.Null(TestLabPolicy.TryLoad(appRoot));

        File.WriteAllText(Path.Combine(appRoot, TestLabPolicy.MarkerFileName),
            JsonSerializer.Serialize(new { mediaRoots = new[] { lab.MediaRoot }, instanceRoots = new[] { lab.InstanceRoot } }));
        var loaded = TestLabPolicy.TryLoad(appRoot);

        Assert.NotNull(loaded);
        Assert.Equal([Path.GetFullPath(lab.MediaRoot)], loaded!.MediaRoots);
        Assert.Equal([Path.GetFullPath(lab.InstanceRoot)], loaded.InstanceRoots);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"mediaRoots\":[],\"instanceRoots\":[\"C:\\\\lab\"]}")]
    [InlineData("{\"mediaRoots\":[\"relative\\\\path\"],\"instanceRoots\":[\"C:\\\\lab\"]}")]
    [InlineData("{\"mediaRoots\":[\"C:\\\\media\"]}")]
    public void MalformedMarkerFailsClosed(string content)
    {
        using var lab = new LabFixture();
        var appRoot = Directory.CreateDirectory(Path.Combine(lab.Base, "app")).FullName;
        File.WriteAllText(Path.Combine(appRoot, TestLabPolicy.MarkerFileName), content);

        Assert.Throws<TestLabViolationException>(() => TestLabPolicy.TryLoad(appRoot));
    }

    [Fact]
    public void AppPathsInTestLabModeRefuseDefaultsOutsideTheLab()
    {
        using var lab = new LabFixture();
        var appRoot = Directory.CreateDirectory(Path.Combine(lab.Base, "app")).FullName;
        File.WriteAllText(Path.Combine(appRoot, TestLabPolicy.MarkerFileName),
            JsonSerializer.Serialize(new { mediaRoots = new[] { lab.MediaRoot }, instanceRoots = new[] { lab.InstanceRoot } }));

        // Without portable.flag the defaults point at the per-user production location.
        Assert.Throws<TestLabViolationException>(() => AppPaths.Create(appRoot));
    }

    [Fact]
    public void AppPathsInTestLabModeCarryThePolicyWhenStorageIsInsideTheLab()
    {
        using var lab = new LabFixture();
        var appRoot = Directory.CreateDirectory(Path.Combine(lab.InstanceRoot, "app")).FullName;
        File.WriteAllText(Path.Combine(appRoot, "portable.flag"), "");
        File.WriteAllText(Path.Combine(appRoot, TestLabPolicy.MarkerFileName),
            JsonSerializer.Serialize(new { mediaRoots = new[] { lab.MediaRoot }, instanceRoots = new[] { lab.InstanceRoot } }));

        var paths = AppPaths.Create(appRoot);

        Assert.NotNull(paths.TestLab);
        Assert.Null(AppPaths.Create(Directory.CreateDirectory(Path.Combine(lab.Base, "plain")).FullName).TestLab);
    }

    [Fact]
    public async Task RepositoryRefusesToRegisterAFolderOutsideTheLab()
    {
        using var lab = new LabFixture();
        var paths = lab.InstancePaths(Path.Combine(lab.InstanceRoot, "run-1")) with { TestLab = lab.Policy };
        var repository = new SqliteLibraryRepository(paths, new NullLogger());
        await repository.InitializeAsync(CancellationToken.None);
        var personal = Directory.CreateDirectory(Path.Combine(lab.Base, "personal-videos")).FullName;
        var allowed = Directory.CreateDirectory(Path.Combine(lab.MediaRoot, "set")).FullName;

        await Assert.ThrowsAsync<TestLabViolationException>(() => repository.AddOrGetLibraryRootAsync(personal, CancellationToken.None));
        await Assert.ThrowsAsync<TestLabViolationException>(() => repository.ReregisterRootAsync(personal, CancellationToken.None));
        var root = await repository.AddOrGetLibraryRootAsync(allowed, CancellationToken.None);

        Assert.Single(await repository.GetLibraryRootsAsync(CancellationToken.None));
        Assert.Equal(Path.GetFullPath(allowed), Path.GetFullPath(root.ConfiguredPath));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task PipelineRefusesToScanAnOutsideFolderAlreadyInTheCatalog()
    {
        using var lab = new LabFixture();
        var plainPaths = lab.InstancePaths(Path.Combine(lab.InstanceRoot, "run-1"));
        var log = new NullLogger();
        var repository = new SqliteLibraryRepository(plainPaths, log);
        await repository.InitializeAsync(CancellationToken.None);
        var personal = Directory.CreateDirectory(Path.Combine(lab.Base, "personal-videos")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(personal, "clip.mp4"), [1, 2, 3, 4]);
        // Registered before the instance was put into test-lab mode.
        var root = await repository.AddOrGetLibraryRootAsync(personal, CancellationToken.None);

        var labPaths = plainPaths with { TestLab = lab.Policy };
        var jobs = new SqliteJobStore(labPaths, log);
        using var pipeline = new MediaPipeline(repository, jobs, new FileSystemMediaScanner(), new FfmpegAdapter(new FfmpegPaths(null, null), labPaths, log), log, paths: labPaths);
        await pipeline.EnqueueScanAsync(root.LibraryRootId, CancellationToken.None);
        await pipeline.RunUntilIdleAsync(CancellationToken.None);

        var scan = Assert.Single(await jobs.GetRecentAsync(10, CancellationToken.None), job => job.JobType == JobType.Scan);
        Assert.Equal(JobState.Cancelled, scan.State);
        Assert.Empty((await repository.QueryMediaAsync(root.LibraryRootId, null, 0, 10, CancellationToken.None)).Items);
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"E:\")]
    [InlineData(@"\\server\share")]
    public void WholeDrivesAndSharesCannotBeLabRoots(string root)
    {
        Assert.Throws<TestLabViolationException>(() => new TestLabPolicy([root], [Path.Combine(Path.GetTempPath(), "lab")]));
        Assert.Throws<TestLabViolationException>(() => new TestLabPolicy([Path.Combine(Path.GetTempPath(), "lab")], [root]));
    }

    [Fact]
    public void MarkerThatIsAFolderFailsClosed()
    {
        using var lab = new LabFixture();
        var appRoot = Directory.CreateDirectory(Path.Combine(lab.Base, "app")).FullName;
        Directory.CreateDirectory(Path.Combine(appRoot, TestLabPolicy.MarkerFileName));

        Assert.Throws<TestLabViolationException>(() => TestLabPolicy.TryLoad(appRoot));
    }

    [Fact]
    public async Task StorageBehindAJunctionInsideTheInstanceRootIsRejected()
    {
        using var lab = new LabFixture();
        var outside = Directory.CreateDirectory(Path.Combine(lab.Base, "outside")).FullName;
        var instance = Directory.CreateDirectory(Path.Combine(lab.InstanceRoot, "run-1")).FullName;
        await CreateDirectoryJunctionAsync(Path.Combine(instance, "data"), outside);

        Assert.Throws<TestLabViolationException>(() => lab.Policy.EnsureInstanceAllowed(lab.InstancePaths(instance), lab.ProductionRoot));
    }

    [Fact]
    public async Task ModelCacheOfATestInstanceStaysInsideTheLab()
    {
        using var lab = new LabFixture();
        var paths = lab.InstancePaths(Path.Combine(lab.InstanceRoot, "run-1")) with { TestLab = lab.Policy };
        var outsideCache = Path.Combine(lab.Base, "LocalAppData", "LocalMediaAI", "ai-resource-cache");

        await Assert.ThrowsAsync<TestLabViolationException>(() => AiResourceCache.ResolveModelsRootAsync(paths,
            SemanticModelRegistry.ResolveDevelopmentCandidate(), new NullLogger(), CancellationToken.None, Path.Combine(lab.Base, "elsewhere")));
        Assert.False(Directory.Exists(outsideCache));
    }

    [Fact]
    public async Task FfmpegAdapterRefusesAnOutsideOriginalBeforeStartingAnyProcess()
    {
        using var lab = new LabFixture();
        var paths = lab.InstancePaths(Path.Combine(lab.InstanceRoot, "run-1")) with { TestLab = lab.Policy };
        paths.EnsureDirectories();
        var personal = Directory.CreateDirectory(Path.Combine(lab.Base, "personal-videos")).FullName;
        var clip = Path.Combine(personal, "clip.mp4");
        await File.WriteAllBytesAsync(clip, [1, 2, 3, 4]);
        var info = new FileInfo(clip);
        var now = DateTimeOffset.UtcNow;
        var media = new MediaFile(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), clip, PathPolicy.NormalizeForIdentity(clip), info.Name, info.Extension,
            info.Length, now, now, null, null, null, null, PresenceState.Present, now, now);
        var adapter = new FfmpegAdapter(new FfmpegPaths(@"C:\missing\ffmpeg.exe", @"C:\missing\ffprobe.exe"), paths, new NullLogger());

        var error = await Assert.ThrowsAsync<MediaToolException>(() => adapter.ExtractFrameAsync(media, 0, "k" + Guid.NewGuid().ToString("N"), CancellationToken.None));

        Assert.Equal("TESTLAB_MEDIA_REJECTED", error.Error.Code);
    }

    private static async Task CreateDirectoryJunctionAsync(string linkPath, string targetPath)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in new[] { "/c", "mklink", "/J", linkPath, targetPath }) process.StartInfo.ArgumentList.Add(argument);
        Assert.True(process.Start());
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, "mklink /J failed: " + await process.StandardError.ReadToEndAsync());
    }

    private sealed class NullLogger : IStructuredLogger
    {
        public void Write(string level, string eventName, object fields, Exception? exception = null) { }
    }

    private sealed class LabFixture : IDisposable
    {
        public LabFixture()
        {
            Base = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "lmai-testlab-" + Guid.NewGuid().ToString("N"))).FullName;
            MediaRoot = Directory.CreateDirectory(Path.Combine(Base, "LocalMediaAI-TestLab", "media-synthetic")).FullName;
            InstanceRoot = Directory.CreateDirectory(Path.Combine(Base, "LocalMediaAI-TestLab", "instances")).FullName;
            ProductionRoot = Directory.CreateDirectory(Path.Combine(Base, "LocalAppData", "LocalMediaAI")).FullName;
            Policy = new TestLabPolicy([MediaRoot], [InstanceRoot]);
        }

        public string Base { get; }
        public string MediaRoot { get; }
        public string InstanceRoot { get; }
        public string ProductionRoot { get; }
        public TestLabPolicy Policy { get; }

        public AppPaths InstancePaths(string instance) => new(instance, Path.Combine(instance, "data"), Path.Combine(instance, "cache"),
            Path.Combine(instance, "models"), Path.Combine(instance, "runtime"), Path.Combine(instance, "temp"));

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(Base, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
