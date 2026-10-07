using System.Diagnostics;
using System.Text.Json;
using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.IntegrationTests;

public sealed class IntegrationTests : IAsyncLifetime, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LocalMediaAIIntegration", Guid.NewGuid().ToString("N"));
    private AppPaths _paths = null!;
    private JsonFileLogger _log = null!;
    private bool _fixtureDisposed;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "portable.flag"), string.Empty);
        _paths = AppPaths.Create(_root); _log = new JsonFileLogger(_paths);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        // Cleanup of pooled SQLite files is best-effort and must not mask a recovery assertion.
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
        if (_fixtureDisposed) return;
        _fixtureDisposed = true;
        _log?.Dispose();
        GC.SuppressFinalize(this);
    }

    private static async Task RunFfmpegAsync(string executable, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start fixture generator.");
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
    }

    [Fact]
    public async Task CrashRecoveryRequeuesRunningScanWithoutChangingOriginal()
    {
        var mediaRoot = Path.Combine(_root, "original-media"); Directory.CreateDirectory(mediaRoot);
        var original = Path.Combine(mediaRoot, "fixture.mp4"); await File.WriteAllBytesAsync(original, Enumerable.Repeat((byte)42, 200_000).ToArray());
        var beforeHash = await FileHash.Sha256Async(original, CancellationToken.None);
        var beforeLength = new FileInfo(original).Length;
        var beforeMtime = File.GetLastWriteTimeUtc(original);
        var repository = new SqliteLibraryRepository(_paths, _log); await repository.InitializeAsync(CancellationToken.None);
        var root = await repository.AddOrGetLibraryRootAsync(mediaRoot, CancellationToken.None);
        var jobs = new SqliteJobStore(_paths, _log);
        var pending = await jobs.EnqueueAsync(JobType.Scan, new ScanPayload(root.LibraryRootId), 2, null, CancellationToken.None);
        Assert.NotNull(await jobs.TryAcquireNextAsync(CancellationToken.None)); // fault-injection point: simulated process termination while Running.
        await jobs.RecoverInterruptedJobsAsync(CancellationToken.None);
        var recovery = (await jobs.GetRecentAsync(10, CancellationToken.None)).Single(x => x.JobId == pending.JobId);
        Assert.Equal(JobState.RetryWaiting, recovery.State);
        Assert.Equal(JobReason.AppRestartRecovery, recovery.Reason);
        Assert.True(await repository.IntegrityCheckAsync(CancellationToken.None));
        Assert.Equal(beforeHash, await FileHash.Sha256Async(original, CancellationToken.None));
        Assert.Equal(beforeLength, new FileInfo(original).Length);
        Assert.Equal(beforeMtime, File.GetLastWriteTimeUtc(original));
    }

    [Fact]
    public async Task FullVerticalSliceWithFfmpegPreservesOriginalAndPublishesThumbnail()
    {
        var runtime = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "runtime", "ffmpeg"));
        var ffmpeg = Path.Combine(runtime, "ffmpeg.exe");
        var ffprobe = Path.Combine(runtime, "ffprobe.exe");
        Assert.True(File.Exists(ffmpeg), $"FFmpeg runtime was not found: {ffmpeg}");
        Assert.True(File.Exists(ffprobe), $"FFprobe runtime was not found: {ffprobe}");
        var libraryDirectory = Path.Combine(_root, "fixture library_日本語"); Directory.CreateDirectory(libraryDirectory);
        var fixture = Path.Combine(libraryDirectory, "動画 テスト_日本語_01-!.mp4");
        await RunFfmpegAsync(ffmpeg, ["-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc=size=64x48:rate=10", "-t", "2", "-pix_fmt", "yuv420p", fixture]);
        var originalHash = await FileHash.Sha256Async(fixture, CancellationToken.None);
        var originalLength = new FileInfo(fixture).Length;
        var originalMtime = File.GetLastWriteTimeUtc(fixture);

        var repository = new SqliteLibraryRepository(_paths, _log); await repository.InitializeAsync(CancellationToken.None);
        var jobs = new SqliteJobStore(_paths, _log);
        var root = await repository.AddOrGetLibraryRootAsync(libraryDirectory, CancellationToken.None);
        var pipeline = new MediaPipeline(repository, jobs, new FileSystemMediaScanner(), new FfmpegAdapter(new FfmpegPaths(ffmpeg, ffprobe), _paths, _log), _log);
        await pipeline.EnqueueScanAsync(root.LibraryRootId, CancellationToken.None);
        await pipeline.RunUntilIdleAsync(CancellationToken.None);

        var item = Assert.Single((await repository.QueryMediaAsync(root.LibraryRootId, null, 0, 10, CancellationToken.None)).Items);
        var jobDiagnostics = string.Join(" | ", (await jobs.GetRecentAsync(20, CancellationToken.None)).Select(job => $"{job.JobType}:{job.State}:{job.LastErrorCode}:{job.LastErrorMessage}"));
        Assert.True(item.Metadata is not null, jobDiagnostics);
        Assert.Equal(64, item.Metadata!.Width);
        Assert.Equal(48, item.Metadata.Height);
        Assert.NotNull(item.ThumbnailRelativePath);
        Assert.True(File.Exists(Path.Combine(_paths.CacheRoot, item.ThumbnailRelativePath!.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Equal(originalHash, await FileHash.Sha256Async(fixture, CancellationToken.None));
        Assert.Equal(originalLength, new FileInfo(fixture).Length);
        Assert.Equal(originalMtime, File.GetLastWriteTimeUtc(fixture));
        Assert.All(await jobs.GetRecentAsync(20, CancellationToken.None), job => Assert.True(job.State is JobState.Completed or JobState.Partial ||
            (job.State == JobState.Cancelled && job.Phase == "SupersededByProtectedOrchestration")));
        Assert.True(await repository.IntegrityCheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task NearDuplicateTemporalFingerprintAcceptsAlternateEncodingAndRejectsDifferentVideo()
    {
        var runtime = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "runtime", "ffmpeg"));
        var ffmpeg = Path.Combine(runtime, "ffmpeg.exe");
        var ffprobe = Path.Combine(runtime, "ffprobe.exe");
        Assert.True(File.Exists(ffmpeg), $"FFmpeg runtime was not found: {ffmpeg}");
        Assert.True(File.Exists(ffprobe), $"FFprobe runtime was not found: {ffprobe}");
        var libraryDirectory = Path.Combine(_root, "near-duplicate-acceptance");
        Directory.CreateDirectory(libraryDirectory);
        var source = Path.Combine(libraryDirectory, "source.mp4");
        var alternate = Path.Combine(libraryDirectory, "alternate-resolution-bitrate-container.mkv");
        var different = Path.Combine(libraryDirectory, "different-same-duration.mp4");
        await RunFfmpegAsync(ffmpeg, ["-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100", "-t", "8", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", source]);
        await RunFfmpegAsync(ffmpeg, ["-hide_banner", "-nostdin", "-y", "-i", source, "-vf", "scale=256:144", "-c:v", "mpeg4", "-q:v", "7", "-c:a", "libopus", "-b:a", "96k", alternate]);
        await RunFfmpegAsync(ffmpeg, ["-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i", "smptebars=size=320x180:rate=24", "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=44100", "-t", "8", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", different]);
        var before = new[] { source, alternate, different }.Select(path => new { Path = path, Hash = FileHash.Sha256Async(path, CancellationToken.None).GetAwaiter().GetResult(), Length = new FileInfo(path).Length, Mtime = File.GetLastWriteTimeUtc(path) }).ToArray();

        var repository = new SqliteLibraryRepository(_paths, _log);
        await repository.InitializeAsync(CancellationToken.None);
        var jobs = new SqliteJobStore(_paths, _log);
        var root = await repository.AddOrGetLibraryRootAsync(libraryDirectory, CancellationToken.None);
        using var pipeline = new MediaPipeline(repository, jobs, new FileSystemMediaScanner(), new FfmpegAdapter(new FfmpegPaths(ffmpeg, ffprobe), _paths, _log), _log, resourceAdmission: new AlwaysAdmitResourcePolicy());
        await pipeline.EnqueueScanAsync(root.LibraryRootId, CancellationToken.None);
        await pipeline.RunUntilIdleAsync(CancellationToken.None);

        var groups = (await repository.QueryDuplicateGroupsByRelationAsync(root.LibraryRootId, DuplicateRelationType.NearDuplicate, true, 0, 100, CancellationToken.None)).Items;
        Assert.NotEmpty(groups);
        var allMembers = new List<NearDuplicateComparisonMember>();
        foreach (var group in groups) allMembers.AddRange(await repository.GetNearDuplicateComparisonMembersAsync(group.DuplicateGroupId, CancellationToken.None));
        Assert.Contains(allMembers, member => member.Media.FileName == Path.GetFileName(source));
        Assert.Contains(allMembers, member => member.Media.FileName == Path.GetFileName(alternate));
        Assert.DoesNotContain(allMembers, member => member.Media.FileName == Path.GetFileName(different));
        foreach (var group in groups)
        {
            var evidenceRows = await repository.GetNearDuplicateEvidenceForGroupAsync(group.DuplicateGroupId, CancellationToken.None);
            Assert.NotEmpty(evidenceRows);
            Assert.All(evidenceRows, evidence =>
            {
                Assert.Equal("Verified", evidence.Status);
                Assert.True(evidence.MatchedSampleCount >= 4);
                Assert.True(evidence.LongestConsecutiveMatchCount >= 3);
            });
        }
        foreach (var item in before)
        {
            Assert.Equal(item.Hash, await FileHash.Sha256Async(item.Path, CancellationToken.None));
            Assert.Equal(item.Length, new FileInfo(item.Path).Length);
            Assert.Equal(item.Mtime, File.GetLastWriteTimeUtc(item.Path));
        }
        Assert.DoesNotContain(await jobs.GetRecentAsync(100, CancellationToken.None), job => job.JobType == JobType.NearDuplicateFingerprint && job.State == JobState.Failed);
        Assert.True(await repository.IntegrityCheckAsync(CancellationToken.None));
    }

    private sealed class AlwaysAdmitResourcePolicy : IResourceAdmissionPolicy
    {
        public AdmissionDecision Evaluate(ResourceCostEstimate estimate, ResourceSnapshot snapshot) => new(AdmissionDecisionKind.Admit, JobReason.None, "TEST_RESOURCE_ADMIT", "Deterministic integration admission.");
    }

    [Fact]
    public async Task MissingFfmpegFailsJobWithoutChangingSource()
    {
        var libraryDirectory = Path.Combine(_root, "missing-ffmpeg-library");
        Directory.CreateDirectory(libraryDirectory);
        var fixture = Path.Combine(libraryDirectory, "unprocessed.mp4");
        await File.WriteAllBytesAsync(fixture, Enumerable.Repeat((byte)23, 80_000).ToArray());
        var originalHash = await FileHash.Sha256Async(fixture, CancellationToken.None);
        var originalLength = new FileInfo(fixture).Length;
        var originalMtime = File.GetLastWriteTimeUtc(fixture);

        var repository = new SqliteLibraryRepository(_paths, _log); await repository.InitializeAsync(CancellationToken.None);
        var jobs = new SqliteJobStore(_paths, _log);
        var root = await repository.AddOrGetLibraryRootAsync(libraryDirectory, CancellationToken.None);
        var pipeline = new MediaPipeline(repository, jobs, new FileSystemMediaScanner(), new FfmpegAdapter(new FfmpegPaths(null, null), _paths, _log), _log);
        await pipeline.EnqueueScanAsync(root.LibraryRootId, CancellationToken.None);
        await pipeline.RunUntilIdleAsync(CancellationToken.None);

        Assert.Contains(await jobs.GetRecentAsync(20, CancellationToken.None), job => job.JobType == JobType.ProtectedSourceOrchestration && job.State == JobState.Failed && job.LastErrorCode == "FFMPEG_NOT_FOUND");
        Assert.Equal(originalHash, await FileHash.Sha256Async(fixture, CancellationToken.None));
        Assert.Equal(originalLength, new FileInfo(fixture).Length);
        Assert.Equal(originalMtime, File.GetLastWriteTimeUtc(fixture));
        Assert.True(await repository.IntegrityCheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task InvalidMediaIsRecordedWithoutCrashingPipeline()
    {
        var runtime = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "runtime", "ffmpeg"));
        var ffmpeg = Path.Combine(runtime, "ffmpeg.exe");
        var ffprobe = Path.Combine(runtime, "ffprobe.exe");
        Assert.True(File.Exists(ffmpeg), $"FFmpeg runtime was not found: {ffmpeg}");
        Assert.True(File.Exists(ffprobe), $"FFprobe runtime was not found: {ffprobe}");
        var libraryDirectory = Path.Combine(_root, "invalid-media-library");
        Directory.CreateDirectory(libraryDirectory);
        var fixture = Path.Combine(libraryDirectory, "corrupt.mp4");
        await File.WriteAllBytesAsync(fixture, Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray());
        var originalHash = await FileHash.Sha256Async(fixture, CancellationToken.None);

        var repository = new SqliteLibraryRepository(_paths, _log); await repository.InitializeAsync(CancellationToken.None);
        var jobs = new SqliteJobStore(_paths, _log);
        var root = await repository.AddOrGetLibraryRootAsync(libraryDirectory, CancellationToken.None);
        var pipeline = new MediaPipeline(repository, jobs, new FileSystemMediaScanner(), new FfmpegAdapter(new FfmpegPaths(ffmpeg, ffprobe), _paths, _log), _log);
        await pipeline.EnqueueScanAsync(root.LibraryRootId, CancellationToken.None);
        await pipeline.RunUntilIdleAsync(CancellationToken.None);

        Assert.Contains(await jobs.GetRecentAsync(20, CancellationToken.None), job => job.JobType == JobType.ProtectedSourceOrchestration && job.State == JobState.Failed && job.LastErrorCode == "FFPROBE_FAILED");
        Assert.Equal(originalHash, await FileHash.Sha256Async(fixture, CancellationToken.None));
        Assert.True(await repository.IntegrityCheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FfprobeTimeoutIsClassifiedAndPreservesOriginal()
    {
        var runtime = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "runtime", "ffmpeg"));
        var ffmpeg = Path.Combine(runtime, "ffmpeg.exe");
        var ffprobe = Path.Combine(runtime, "ffprobe.exe");
        Assert.True(File.Exists(ffmpeg), $"FFmpeg runtime was not found: {ffmpeg}");
        Assert.True(File.Exists(ffprobe), $"FFprobe runtime was not found: {ffprobe}");
        var libraryDirectory = Path.Combine(_root, "timeout-library");
        Directory.CreateDirectory(libraryDirectory);
        var fixture = Path.Combine(libraryDirectory, "timeout.mp4");
        await RunFfmpegAsync(ffmpeg, ["-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc=size=640x360:rate=60", "-t", "4", "-pix_fmt", "yuv420p", fixture]);
        var originalHash = await FileHash.Sha256Async(fixture, CancellationToken.None);
        var originalLength = new FileInfo(fixture).Length;
        var now = DateTimeOffset.UtcNow;
        var media = new MediaFile(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), fixture, PathPolicy.NormalizeForIdentity(fixture), Path.GetFileName(fixture), ".mp4", originalLength, now, now, null, null, null, null, PresenceState.Present, now, now);
        var uniqueProbeName = $"lmai-timeout-ffprobe-{Guid.NewGuid():N}";
        var ownedFfprobe = Path.Combine(_root, uniqueProbeName + ".exe");
        File.Copy(ffprobe, ownedFfprobe);
        var timeoutLog = new CapturingLogger();
        var adapter = new FfmpegAdapter(new FfmpegPaths(ffmpeg, ownedFfprobe), _paths, timeoutLog, TimeSpan.Zero);

        var error = await Assert.ThrowsAsync<MediaToolException>(() => adapter.ProbeAsync(media, CancellationToken.None));

        Assert.Equal("PROCESS_TIMEOUT", error.Error.Code);
        var cleanup = Assert.Single(timeoutLog.Events, item => item.EventName == "MediaProcessTimeoutCleanupCompleted");
        using (var fields = JsonDocument.Parse(cleanup.Fields))
        {
            // With an immediate timeout the owned process may finish between WaitAsync
            // timing out and the cleanup branch checking HasExited. A tree-kill request
            // is required only while it is still running; owned exit and stream
            // observation are the invariant cleanup result in both cases.
            Assert.True(fields.RootElement.GetProperty("KillEntireProcessTreeRequested").ValueKind is JsonValueKind.True or JsonValueKind.False);
            Assert.True(fields.RootElement.GetProperty("OwnedProcessExited").GetBoolean());
            Assert.True(fields.RootElement.GetProperty("StandardOutputTaskObserved").GetBoolean());
            Assert.True(fields.RootElement.GetProperty("StandardErrorTaskObserved").GetBoolean());
        }
        var residualOwnedProcesses = Process.GetProcessesByName(uniqueProbeName);
        try { Assert.Empty(residualOwnedProcesses); }
        finally { foreach (var process in residualOwnedProcesses) process.Dispose(); }
        Assert.Equal(originalHash, await FileHash.Sha256Async(fixture, CancellationToken.None));
        Assert.Equal(originalLength, new FileInfo(fixture).Length);
    }

    [Fact]
    public async Task AccessDeniedMediaIsClassifiedWithoutChangingSource()
    {
        if (!OperatingSystem.IsWindows()) return;
        var libraryDirectory = Path.Combine(_root, "access-denied-library");
        Directory.CreateDirectory(libraryDirectory);
        var fixture = Path.Combine(libraryDirectory, "locked.mp4");
        await File.WriteAllBytesAsync(fixture, Enumerable.Repeat((byte)7, 16_384).ToArray());
        var originalHash = await FileHash.Sha256Async(fixture, CancellationToken.None);
        var originalLength = new FileInfo(fixture).Length;
        var identity = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        var restoreRequired = false;
        string? errorCode = null;
        try
        {
            await RunCommandAsync("icacls.exe", [fixture, "/inheritance:r", "/deny", $"{identity}:(R)"]);
            restoreRequired = true;
            var now = DateTimeOffset.UtcNow;
            var media = new MediaFile(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), fixture, PathPolicy.NormalizeForIdentity(fixture), Path.GetFileName(fixture), ".mp4", originalLength, now, now, null, null, null, null, PresenceState.Present, now, now);
            var adapter = new FfmpegAdapter(new FfmpegPaths(null, Path.Combine(Environment.SystemDirectory, "cmd.exe")), _paths, _log);

            var error = await Assert.ThrowsAsync<MediaToolException>(() => adapter.ProbeAsync(media, CancellationToken.None));
            errorCode = error.Error.Code;
        }
        finally
        {
            if (restoreRequired)
            {
                await RunCommandAsync("icacls.exe", [fixture, "/remove:d", identity]);
                await RunCommandAsync("icacls.exe", [fixture, "/inheritance:e"]);
            }
        }

        Assert.Equal("MEDIA_ACCESS_DENIED", errorCode);
        Assert.NotEqual("MEDIA_NOT_FOUND", errorCode);
        Assert.Equal(originalHash, await FileHash.Sha256Async(fixture, CancellationToken.None));
        Assert.Equal(originalLength, new FileInfo(fixture).Length);
    }

    [Fact]
    public void IncompleteThumbnailTempCleanupPreservesPublishedAndOtherStageFiles()
    {
        var incomplete = Path.Combine(_paths.TempRoot, "thumbnail-interrupted");
        Directory.CreateDirectory(incomplete);
        File.WriteAllText(Path.Combine(incomplete, "partial.jpg"), "not-published");
        var otherStage = Path.Combine(_paths.TempRoot, "metadata-interrupted");
        Directory.CreateDirectory(otherStage);
        File.WriteAllText(Path.Combine(otherStage, "partial.json"), "keep");
        var published = Path.Combine(_paths.CacheRoot, "thumbnails", "published.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(published)!);
        File.WriteAllText(published, "cache-source-of-truth");

        var adapter = new FfmpegAdapter(new FfmpegPaths(null, null), _paths, _log);
        Assert.Equal(1, adapter.CleanupIncompleteThumbnailArtifacts());
        Assert.False(Directory.Exists(incomplete));
        Assert.True(Directory.Exists(otherStage));
        Assert.True(File.Exists(published));
        Assert.Equal("cache-source-of-truth", File.ReadAllText(published));
    }

    private static async Task RunCommandAsync(string executable, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {executable}.");
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
    }

    [Fact]
    public async Task Paging10kRowsReturnsOnlyRequestedPage()
    {
        var repository = new SqliteLibraryRepository(_paths, _log); await repository.InitializeAsync(CancellationToken.None);
        var mediaRoot = Path.Combine(_root, "many"); Directory.CreateDirectory(mediaRoot);
        var root = await repository.AddOrGetLibraryRootAsync(mediaRoot, CancellationToken.None);
        var scanId = await repository.StartScanAsync(root.LibraryRootId, CancellationToken.None);
        var timestamp = DateTimeOffset.UtcNow;
        for (var index = 0; index < 10_000; index++)
        {
            var name = $"row-{index:D5}.mp4";
            var path = Path.Combine(mediaRoot, name);
            await repository.UpsertScannedFileAsync(new ScannedMediaFile(root.LibraryRootId, path, PathPolicy.NormalizeForIdentity(path), name, ".mp4", index + 1, timestamp, timestamp, null, null, $"fingerprint-{index}"), scanId, CancellationToken.None);
        }
        await repository.CompleteScanAsync(scanId, new ScanCompletion("Completed", 10_000, 10_000, 0, 0, 0, DateTimeOffset.UtcNow), CancellationToken.None);
        var started = Stopwatch.StartNew();
        var page = await repository.QueryMediaAsync(root.LibraryRootId, "row-099", 0, 100, CancellationToken.None);
        started.Stop();
        Console.WriteLine($"PERFORMANCE_PAGING_10K_SEARCH_MS={started.Elapsed.TotalMilliseconds:F3}");
        Assert.True(page.TotalCount >= 100);
        Assert.True(page.Items.Count <= 100);
        Assert.DoesNotContain(page.Items, item => item.FileName == "row-00000.mp4");
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10));
    }

    private sealed class CapturingLogger : IStructuredLogger
    {
        public List<(string EventName, string Fields)> Events { get; } = [];
        public void Write(string level, string eventName, object fields, Exception? exception = null) =>
            Events.Add((eventName, JsonSerializer.Serialize(fields)));
    }
}


public sealed class VisualSamplingIntegrationTests
{
    [Fact]
    public async Task VisualSamplingUsesFixedTenSecondPolicyPublishesFramesAndPreservesOriginal()
    {
        var root = Path.Combine(Path.GetTempPath(), "LocalMediaAIVisualSampling", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "portable.flag"), string.Empty);
        var paths = AppPaths.Create(root);
        using var log = new JsonFileLogger(paths);
        try
        {
            var runtime = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "runtime", "ffmpeg"));
            var ffmpeg = Path.Combine(runtime, "ffmpeg.exe");
            var ffprobe = Path.Combine(runtime, "ffprobe.exe");
            Assert.True(File.Exists(ffmpeg), $"FFmpeg runtime was not found: {ffmpeg}");
            Assert.True(File.Exists(ffprobe), $"FFprobe runtime was not found: {ffprobe}");

            var libraryDirectory = Path.Combine(root, "visual-sampling-library");
            Directory.CreateDirectory(libraryDirectory);
            var source = Path.Combine(libraryDirectory, "twenty-two-seconds.mp4");
            await CreateFixtureAsync(ffmpeg, source);
            var beforeHash = await FileHash.Sha256Async(source, CancellationToken.None);
            var beforeLength = new FileInfo(source).Length;
            var beforeMtime = File.GetLastWriteTimeUtc(source);

            var repository = new SqliteLibraryRepository(paths, log);
            await repository.InitializeAsync(CancellationToken.None);
            var jobs = new SqliteJobStore(paths, log);
            var libraryRoot = await repository.AddOrGetLibraryRootAsync(libraryDirectory, CancellationToken.None);
            using var pipeline = new MediaPipeline(repository, jobs, new FileSystemMediaScanner(), new FfmpegAdapter(new FfmpegPaths(ffmpeg, ffprobe), paths, log), log);
            await pipeline.EnqueueScanAsync(libraryRoot.LibraryRootId, CancellationToken.None);
            await pipeline.RunUntilIdleAsync(CancellationToken.None);

            var media = Assert.Single((await repository.QueryMediaAsync(libraryRoot.LibraryRootId, null, 0, 10, CancellationToken.None)).Items);
            var samples = await repository.GetVisualSamplesAsync(media.MediaFileId, "quick-identity-v1", VisualSamplingPolicy.FixedIntervalV1.Version, CancellationToken.None);
            // The scanner-derived quick fingerprint is deterministic but intentionally not assumed as a literal fixture value.
            if (samples.Count == 0)
            {
                var file = await repository.GetMediaFileAsync(media.MediaFileId, CancellationToken.None);
                Assert.NotNull(file);
                samples = await repository.GetVisualSamplesAsync(media.MediaFileId, file.QuickFingerprint!, VisualSamplingPolicy.FixedIntervalV1.Version, CancellationToken.None);
            }
            Assert.Equal(3, samples.Count);
            Assert.Equal([0d, 10d, 20d], samples.Select(sample => sample.RequestedTimestampSeconds).ToArray());
            Assert.All(samples, sample =>
            {
                Assert.Equal(VisualSampleStatus.Completed, sample.Status);
                Assert.NotNull(sample.FrameRelativePath);
                Assert.True(File.Exists(Path.Combine(paths.CacheRoot, sample.FrameRelativePath!.Replace('/', Path.DirectorySeparatorChar))));
                Assert.NotNull(sample.FrameSha256);
                Assert.True(sample.FrameByteSize > 0);
                Assert.InRange(sample.PresentationTimestampSeconds, Math.Max(0d, sample.RequestedTimestampSeconds - 1d), sample.RequestedTimestampSeconds + 1d);
            });
            Assert.Contains(await jobs.GetRecentAsync(20, CancellationToken.None), job => job.JobType == JobType.ProtectedSourceOrchestration && job.State == JobState.Completed && job.Phase == "GuardedPublished");
            Assert.Equal(beforeHash, await FileHash.Sha256Async(source, CancellationToken.None));
            Assert.Equal(beforeLength, new FileInfo(source).Length);
            Assert.Equal(beforeMtime, File.GetLastWriteTimeUtc(source));
            Assert.True(await repository.IntegrityCheckAsync(CancellationToken.None));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static async Task CreateFixtureAsync(string ffmpeg, string destination)
    {
        var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc=size=64x48:rate=10", "-t", "22", "-pix_fmt", "yuv420p", destination }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start fixture generator.");
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
    }
}


public sealed class WorkerEmbeddingIntegrationTests
{
    [Fact]
    public async Task PipelinePersistsOfflineCudaVisualEmbeddingsWithoutChangingOriginal()
    {
        var root = Path.Combine(Path.GetTempPath(), "LocalMediaAIWorkerEmbedding", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "portable.flag"), string.Empty);
        var candidateRoot = Environment.GetEnvironmentVariable("LOCAL_MEDIA_AI_COMPLETE_CANDIDATE_ROOT");
        var resourceRoot = string.IsNullOrWhiteSpace(candidateRoot)
            ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."))
            : Path.GetFullPath(candidateRoot);
        var runtime = Path.Combine(resourceRoot, "runtime");
        var ffmpegRoot = Path.Combine(runtime, "ffmpeg");
        var ffmpeg = Path.Combine(ffmpegRoot, "ffmpeg.exe");
        var ffprobe = Path.Combine(ffmpegRoot, "ffprobe.exe");
        var python = Path.Combine(runtime, "python-openclip", "python.exe");
        var workerScript = Path.Combine(runtime, "worker", "local_media_ai_worker.py");
        var modelsRoot = Path.Combine(resourceRoot, "models");
        Assert.True(File.Exists(ffmpeg));
        Assert.True(File.Exists(ffprobe));
        Assert.True(File.Exists(python));
        Assert.True(File.Exists(workerScript));
        Assert.True(Directory.Exists(Path.Combine(modelsRoot, "llm-jp-clip-vit-base-patch16")));
        Assert.True(Directory.Exists(Path.Combine(modelsRoot, "llm-jp-clip-vit-base-patch16-openclip-adapter")));
        if (!string.IsNullOrWhiteSpace(candidateRoot))
        {
            Assert.True(File.Exists(Path.Combine(resourceRoot, "portable.flag")));
            var candidatePaths = AppPaths.Create(resourceRoot);
            var resolvedWorker = WorkerProcessOptions.Resolve(candidatePaths, SemanticModelRegistry.ResolveDevelopmentCandidate());
            Assert.Equal(python, resolvedWorker.PythonExecutablePath);
            Assert.Equal(workerScript, resolvedWorker.WorkerScriptPath);
            Assert.Equal(modelsRoot, candidatePaths.ModelsRoot);
        }

        var paths = AppPaths.Create(root, new Dictionary<string, string> { ["ModelsRoot"] = modelsRoot });
        using var log = new JsonFileLogger(paths);
        WorkerProcessManager? worker = null;
        try
        {
            var mediaDirectory = Path.Combine(root, "worker-embedding-library");
            Directory.CreateDirectory(mediaDirectory);
            var source = Path.Combine(mediaDirectory, "worker-embedding.mp4");
            await CreateFixtureAsync(ffmpeg, source);
            var beforeHash = await FileHash.Sha256Async(source, CancellationToken.None);
            var beforeLength = new FileInfo(source).Length;
            var beforeMtime = File.GetLastWriteTimeUtc(source);

            var repository = new SqliteLibraryRepository(paths, log);
            await repository.InitializeAsync(CancellationToken.None);
            var jobs = new SqliteJobStore(paths, log);
            worker = new WorkerProcessManager(paths, log, new WorkerProcessOptions(python, workerScript, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(5)));
            var libraryRoot = await repository.AddOrGetLibraryRootAsync(mediaDirectory, CancellationToken.None);
            using var pipeline = new MediaPipeline(repository, jobs, new FileSystemMediaScanner(), new FfmpegAdapter(new FfmpegPaths(ffmpeg, ffprobe), paths, log), log, worker: worker, paths: paths);
            await pipeline.EnqueueScanAsync(libraryRoot.LibraryRootId, CancellationToken.None);
            await pipeline.RunUntilIdleAsync(CancellationToken.None);

            var embeddings = await repository.GetCompletedVisualEmbeddingsAsync(CancellationToken.None);
            var diagnosticJobs = await jobs.GetRecentAsync(30, CancellationToken.None);
            Assert.True(embeddings.Count == 3, $"Expected 3 LLM-jp embeddings but found {embeddings.Count}. Jobs: {string.Join("; ", diagnosticJobs.Select(job => $"{job.JobType}/{job.State}/{job.Phase}/{job.LastErrorCode}/{job.LastErrorMessage}"))}. Worker stdout: {string.Join(" | ", worker!.CapturedStandardOutput)}. Worker stderr: {string.Join(" | ", worker.CapturedStandardError)}");
            Assert.All(embeddings, item =>
            {
                Assert.Equal(512, item.Embedding.Dimension);
                Assert.True(item.Embedding.Normalized);
                Assert.NotNull(item.Embedding.VectorFloat32);
                Assert.Equal(512 * sizeof(float), item.Embedding.VectorFloat32!.Length);
                Assert.Equal("llm-jp/llm-jp-clip-vit-base-patch16", item.Embedding.ModelName);
                Assert.Equal("e3b65fb4a871bacde924b3613dae0eccb7835e95", item.Embedding.ModelRevision);
                Assert.Equal("llm-jp-openclip-2.31-local-adapter-v1", item.Embedding.ProcessorVersion);
            });
            var semanticSearch = new SemanticSearchService(repository, worker, paths, log);
            var englishResults = await semanticSearch.SearchAsync("test pattern", 10, CancellationToken.None);
            var japaneseResults = await semanticSearch.SearchAsync("テストパターン", 10, CancellationToken.None);
            Assert.Equal(3, englishResults.Count);
            Assert.Equal(3, japaneseResults.Count);
            Assert.All(englishResults.Concat(japaneseResults), result => Assert.InRange(result.Score, -1d, 1d));
            Assert.All(await jobs.GetRecentAsync(30, CancellationToken.None), job => Assert.True(job.State is JobState.Completed or JobState.Partial ||
                (job.State == JobState.Cancelled && job.Phase == "SupersededByProtectedOrchestration")));
            Assert.Equal(beforeHash, await FileHash.Sha256Async(source, CancellationToken.None));
            Assert.Equal(beforeLength, new FileInfo(source).Length);
            Assert.Equal(beforeMtime, File.GetLastWriteTimeUtc(source));
            Assert.True(await repository.IntegrityCheckAsync(CancellationToken.None));
        }
        finally
        {
            if (worker is not null) await worker.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static async Task CreateFixtureAsync(string ffmpeg, string destination)
    {
        var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc=size=64x48:rate=10", "-t", "22", "-pix_fmt", "yuv420p", destination }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start fixture generator.");
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
    }
}
