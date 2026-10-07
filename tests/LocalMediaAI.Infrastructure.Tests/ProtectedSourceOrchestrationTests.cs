using System.Diagnostics;
using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class ProtectedSourceOrchestrationTests : IAsyncLifetime, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LocalMediaAIProtectedSourceTests", Guid.NewGuid().ToString("N"));
    private AppPaths _paths = null!;
    private JsonFileLogger _log = null!;
    private bool _disposed;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "portable.flag"), string.Empty);
        _paths = AppPaths.Create(_root);
        _paths.EnsureDirectories();
        _log = new JsonFileLogger(_paths);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        try
        {
            Dispose();
            SqliteConnection.ClearAllPools();
            for (var attempt = 0; Directory.Exists(_root) && attempt < 8; attempt++)
            {
                try { Directory.Delete(_root, true); }
                catch (IOException) { await Task.Delay(100); }
                catch (UnauthorizedAccessException) { await Task.Delay(100); }
            }
        }
        catch (Exception) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _log?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task LeaseRemainsHeldThroughFuturePublicationBoundaryThenDisposalReleasesIt()
    {
        var fixture = await CreateByteFixtureAsync("lease-lifetime.bin");
        var session = await AcquireAsync(fixture);
        AssertWriteBlocked(fixture.Path);

        session.CompleteSourceStaging();
        Assert.True(session.IsLeaseHeld);
        AssertWriteBlocked(fixture.Path);

        session.EnterDerivedOrPublicationPhase();
        await session.ExecuteDerivedStageAsync("derived-proof", _ => Task.FromResult(true), CancellationToken.None);
        Assert.True(session.IsLeaseHeld);
        AssertWriteBlocked(fixture.Path);

        session.CompletePublicationBoundary(ProcessingPublicationResolution.Published);
        Assert.True(session.IsLeaseHeld);
        AssertWriteBlocked(fixture.Path);

        await session.DisposeAsync();
        Assert.False(session.IsLeaseHeld);
        Assert.Equal(ProcessingSourceSessionPhase.Disposed, session.Phase);
        Assert.True(session.CleanupSucceeded);
        using var writable = new FileStream(fixture.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    }

    [Fact]
    public async Task ConflictingWriterFailsClosedWithoutUnprotectedFallback()
    {
        var fixture = await CreateByteFixtureAsync("writer-conflict.bin");
        using var writer = new FileStream(fixture.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

        var error = await Assert.ThrowsAsync<ProcessingSourceLeaseUnavailableException>(async () =>
            await ProcessingSourceSession.AcquireAsync(_paths, fixture.MediaFileId, fixture.MediaContentId, fixture.Path, _log, CancellationToken.None));

        Assert.Equal("SOURCE_LEASE_CONFLICT", error.Code);
        Assert.Empty(Directory.Exists(Path.Combine(_paths.TempRoot, "processing-source-sessions-v1"))
            ? Directory.EnumerateDirectories(Path.Combine(_paths.TempRoot, "processing-source-sessions-v1"))
            : []);
    }

    [Fact]
    public async Task ExistingCSharpAndFfprobeConsumersShareOneAuthoritativeSession()
    {
        var fixture = await CreateVideoFixtureAsync();
        var media = CreateMediaFile(fixture);
        var session = await AcquireAsync(fixture);
        var observedSessions = new List<Guid>();

        var fullHash = await session.ExecuteSourceStageAsync("full-sha256", async (source, cancellationToken) =>
        {
            observedSessions.Add(source.SessionId);
            source.EnsureMatches(media);
            return await FileHash.Sha256Async(source.SourcePath, cancellationToken);
        }, CancellationToken.None);

        var adapter = new FfmpegAdapter(ResolveBundledFfmpeg(), _paths, _log, TimeSpan.FromSeconds(30));
        var probe = await session.ExecuteSourceStageAsync("ffprobe-metadata", async (source, cancellationToken) =>
        {
            observedSessions.Add(source.SessionId);
            return await adapter.ProbeAsync(source, media, cancellationToken);
        }, CancellationToken.None);

        Assert.Equal(64, fullHash.Length);
        Assert.NotNull(probe.Metadata.DurationSeconds);
        Assert.Equal([session.SessionId, session.SessionId], observedSessions);
        Assert.True(session.IsLeaseHeld);

        session.CompleteSourceStaging();
        session.EnterDerivedOrPublicationPhase();
        session.CompletePublicationBoundary(ProcessingPublicationResolution.Rejected);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task StagedArtifactsRemainSessionOwnedAndUnpublishedUntilSafelyCleaned()
    {
        var fixture = await CreateByteFixtureAsync("staging.bin");
        var session = await AcquireAsync(fixture);
        var stagingDirectory = session.StagingDirectory;
        var artifact = session.CreateUnpublishedArtifact(Path.Combine("metadata", "result.json"));
        await File.WriteAllTextAsync(artifact.FullPath, "{\"synthetic\":true}");

        Assert.Equal(session.SessionId, artifact.SessionId);
        Assert.False(artifact.IsPublished);
        Assert.Single(session.UnpublishedArtifacts);
        Assert.True(PathPolicy.IsWithinRoot(stagingDirectory, artifact.FullPath));
        Assert.False(PathPolicy.IsWithinRoot(_paths.CacheRoot, artifact.FullPath));

        session.CompleteSourceStaging();
        session.EnterDerivedOrPublicationPhase();
        Assert.True(File.Exists(artifact.FullPath));
        Assert.True(session.IsLeaseHeld);
        session.CompletePublicationBoundary(ProcessingPublicationResolution.Rejected);
        await session.DisposeAsync();

        Assert.True(session.CleanupSucceeded);
        Assert.False(Directory.Exists(stagingDirectory));
    }

    [Fact]
    public async Task FailureDisposalReleasesLeaseWithoutPublishingStaging()
    {
        var fixture = await CreateByteFixtureAsync("failure.bin");
        var session = await AcquireAsync(fixture);
        var artifact = session.CreateUnpublishedArtifact("unpublished.tmp");
        await File.WriteAllTextAsync(artifact.FullPath, "synthetic-only");

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteSourceStageAsync<bool>(
            "failing-stage",
            (_, _) => throw new InvalidOperationException("synthetic stage failure"),
            CancellationToken.None));

        await session.DisposeAsync();
        Assert.Null(session.PublicationResolution);
        Assert.False(Directory.Exists(session.StagingDirectory));
        using var writable = new FileStream(fixture.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    }

    [Fact]
    public async Task CleanupFailsClosedWhenSessionTreeContainsAReparsePoint()
    {
        var fixture = await CreateByteFixtureAsync("reparse-cleanup.bin");
        var session = await AcquireAsync(fixture);
        var external = Path.Combine(_root, "external-synthetic");
        Directory.CreateDirectory(external);
        var sentinel = Path.Combine(external, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "must-remain");
        var junction = Path.Combine(session.StagingDirectory, "escape");
        CreateJunction(junction, external);

        session.CompletePublicationBoundary(ProcessingPublicationResolution.Rejected);
        await session.DisposeAsync();

        Assert.False(session.CleanupSucceeded);
        Assert.True(File.Exists(sentinel));
        Assert.True(Directory.Exists(session.StagingDirectory));

        Directory.Delete(junction, recursive: false);
        Directory.Delete(session.StagingDirectory, recursive: true);
    }

    [Fact]
    public async Task RetryMustAcquireANewSessionAndOldBorrowCannotBeReused()
    {
        var fixture = await CreateByteFixtureAsync("retry.bin");
        var first = await AcquireAsync(fixture);
        ProcessingSourceAccess? oldBorrow = null;
        await first.ExecuteSourceStageAsync("capture-borrow", (source, _) =>
        {
            oldBorrow = source;
            return Task.FromResult(true);
        }, CancellationToken.None);
        first.CompletePublicationBoundary(ProcessingPublicationResolution.Rejected);
        await first.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => _ = oldBorrow!.SourcePath);
        var second = await AcquireAsync(fixture);
        Assert.NotEqual(first.SessionId, second.SessionId);
        second.CompletePublicationBoundary(ProcessingPublicationResolution.Rejected);
        await second.DisposeAsync();
    }

    [Fact]
    public async Task V13DurableJobCanRepresentFutureOrchestrationWithoutPersistingAHandle()
    {
        var repository = new SqliteLibraryRepository(_paths, _log);
        await repository.InitializeAsync(CancellationToken.None);
        var jobs = new SqliteJobStore(_paths, _log);
        var payload = new ProtectedSourceOrchestrationPayload(Guid.NewGuid(), Guid.NewGuid());

        var queued = await jobs.EnqueueUniqueAsync(JobType.ProtectedSourceOrchestration, payload, 2, null, CancellationToken.None);
        var acquired = await jobs.TryAcquireNextAsync(CancellationToken.None);

        Assert.NotNull(acquired);
        Assert.Equal(queued.JobId, acquired.JobId);
        Assert.Equal(JobType.ProtectedSourceOrchestration, acquired.JobType);
        Assert.Contains(payload.ExpectedMediaContentId.ToString("D"), acquired.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("handle", acquired.PayloadJson, StringComparison.OrdinalIgnoreCase);
        await jobs.RecoverInterruptedJobsAsync(CancellationToken.None);
        var recovered = Assert.Single(await jobs.GetRecentAsync(10, CancellationToken.None));
        Assert.Equal(JobState.RetryWaiting, recovered.State);
        Assert.Equal(JobReason.AppRestartRecovery, recovered.Reason);
        Assert.True(await repository.IntegrityCheckAsync(CancellationToken.None));
    }

    private async Task<SyntheticFixture> CreateByteFixtureAsync(string name)
    {
        var directory = Path.Combine(_root, "synthetic-media");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        await File.WriteAllBytesAsync(path, Enumerable.Range(0, 32_768).Select(index => (byte)(index % 251)).ToArray());
        return new SyntheticFixture(path, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    }

    private async Task<SyntheticFixture> CreateVideoFixtureAsync()
    {
        var tools = ResolveBundledFfmpeg();
        Assert.NotNull(tools.FfmpegPath);
        var directory = Path.Combine(_root, "synthetic-media");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "generated-color-bars.mp4");
        var start = new ProcessStartInfo(tools.FfmpegPath!)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=10", "-t", "1", "-c:v", "mpeg4", "-q:v", "5", path })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Synthetic FFmpeg fixture process did not start.");
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await stderr);
        Assert.True(File.Exists(path));
        return new SyntheticFixture(path, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    }

    private ValueTask<ProcessingSourceSession> AcquireAsync(SyntheticFixture fixture) =>
        ProcessingSourceSession.AcquireAsync(_paths, fixture.MediaFileId, fixture.MediaContentId, fixture.Path, _log, CancellationToken.None);

    private static MediaFile CreateMediaFile(SyntheticFixture fixture)
    {
        var info = new FileInfo(fixture.Path);
        return new MediaFile(
            fixture.MediaFileId,
            fixture.LibraryRootId,
            fixture.MediaContentId,
            fixture.Path,
            PathPolicy.NormalizeForIdentity(fixture.Path),
            info.Name,
            info.Extension,
            info.Length,
            new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero),
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            null,
            null,
            null,
            null,
            PresenceState.Present,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
    }

    private static void AssertWriteBlocked(string path)
    {
        Assert.Throws<IOException>(() =>
        {
            using var ignored = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        });
    }

    private static FfmpegPaths ResolveBundledFfmpeg()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var runtime = Path.Combine(current.FullName, "runtime", "ffmpeg");
            var ffmpeg = Path.Combine(runtime, "ffmpeg.exe");
            var ffprobe = Path.Combine(runtime, "ffprobe.exe");
            if (File.Exists(ffmpeg) && File.Exists(ffprobe)) return new FfmpegPaths(ffmpeg, ffprobe);
            current = current.Parent;
        }
        return new FfmpegPaths(null, null);
    }

    private static void CreateJunction(string junction, string target)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(junction);
        start.ArgumentList.Add(target);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("mklink did not start.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"mklink /J failed: {output} {error}");
    }

    private sealed record SyntheticFixture(string Path, Guid MediaFileId, Guid MediaContentId, Guid LibraryRootId);
}
