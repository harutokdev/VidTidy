using System.Diagnostics;
using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed partial class NearDuplicateFoundationTests
{
    private const string PreparationVersion = NearDuplicatePreparation.Version;

    [Fact]
    public async Task PreparationIsPendingUntilSavedAndStaleOnceAFrameIsTakenAgain()
    {
        var fixture = await CreateFixtureAsync(("a.mp4", Pattern(120_000, 11)), ("b.mp4", Pattern(120_000, 12)));
        await PersistCurrentFramesAsync(fixture.Repository, fixture.Media[0], FiveHashes(0));
        await PersistCurrentFramesAsync(fixture.Repository, fixture.Media[1], FiveHashes(0));
        Assert.Equal(2, (await PendingAsync(fixture.Repository)).Count);

        var first = Assert.Single(await PendingAsync(fixture.Repository, fixture.Media[0].MediaFileId));
        Assert.True(await SavePreparedAsync(fixture.Repository, fixture.Media[0], first.FramesVersion, Prepared(0x10)));
        Assert.Equal(fixture.Media[1].MediaFileId, Assert.Single(await PendingAsync(fixture.Repository)).MediaFileId);
        var current = await CurrentPreparedAsync(fixture.Repository, fixture.Media[0]);
        Assert.Equal(Prepared(0x10).Select(frame => frame.WholeHex), current.Select(frame => frame.WholeHex));

        await Task.Delay(5);
        await PersistCurrentFramesAsync(fixture.Repository, fixture.Media[0], FiveHashes(1));
        Assert.Empty(await CurrentPreparedAsync(fixture.Repository, fixture.Media[0]));
        Assert.Contains(await PendingAsync(fixture.Repository), item => item.MediaFileId == fixture.Media[0].MediaFileId);
        Assert.False(await SavePreparedAsync(fixture.Repository, fixture.Media[0], first.FramesVersion, Prepared(0x10)));
    }

    [Fact]
    public async Task AVideoWhoseStoredFramesCannotBeReadIsNotRetriedAndHasNoPreparedFrames()
    {
        var fixture = await CreateFixtureAsync(("a.mp4", Pattern(120_000, 13)));
        await PersistCurrentFramesAsync(fixture.Repository, fixture.Media[0], FiveHashes(0));
        var pending = Assert.Single(await PendingAsync(fixture.Repository));

        Assert.True(await fixture.Repository.SaveNearDuplicatePreparationAsync(fixture.Media[0].MediaFileId, fixture.Media[0].MediaContentId, NearDuplicateSamplingPolicy.Version, PerceptualFrameFingerprint.Algorithm, PreparationVersion, pending.FramesVersion, FrameCrop.None, [], CancellationToken.None));

        Assert.Empty(await PendingAsync(fixture.Repository));
        Assert.Empty(await CurrentPreparedAsync(fixture.Repository, fixture.Media[0]));
    }

    [Fact]
    public async Task PreparedCandidatesPairTheMiddleOfOneVideoWithTheWholeOfAnother()
    {
        var fixture = await CreateFixtureAsync(("a.mp4", Pattern(120_000, 14)), ("b.mp4", Pattern(120_000, 15)), ("c.mp4", Pattern(120_000, 16)));
        // Plain fingerprints share no band, so only the prepared ones can pair them.
        await PersistCurrentFramesAsync(fixture.Repository, fixture.Media[0], FiveHashes(0));
        await PersistCurrentFramesAsync(fixture.Repository, fixture.Media[1], Enumerable.Range(0, 5).Select(i => $"123456789abcdef{i}").ToArray());
        await PersistCurrentFramesAsync(fixture.Repository, fixture.Media[2], Enumerable.Range(0, 5).Select(i => $"fedcba987654321{i}").ToArray());
        Assert.Empty(await fixture.Repository.GetNearDuplicateCandidatesAsync(null, NearDuplicateSamplingPolicy.Version, PerceptualFrameFingerprint.Algorithm, CancellationToken.None));

        var zoomedOut = Prepared(0x20);
        // b's whole picture is a's middle 80%; c's middle matches a's middle only, which is not a pairing.
        var zoomedIn = zoomedOut.Select(frame => frame with { WholeHex = frame.Center80Hex, Center90Hex = Hex(0x30, frame.SampleIndex), Center80Hex = Hex(0x31, frame.SampleIndex) }).ToArray();
        var middleOnly = zoomedOut.Select(frame => frame with { WholeHex = Hex(0x40, frame.SampleIndex), Center80Hex = Hex(0x42, frame.SampleIndex) }).ToArray();
        foreach (var (media, frames) in new[] { (fixture.Media[0], zoomedOut), (fixture.Media[1], zoomedIn), (fixture.Media[2], middleOnly) })
            Assert.True(await SavePreparedAsync(fixture.Repository, media, Assert.Single(await PendingAsync(fixture.Repository, media.MediaFileId)).FramesVersion, frames));

        var candidate = Assert.Single(await fixture.Repository.GetPreparedNearDuplicateCandidatesAsync(null, NearDuplicateSamplingPolicy.Version, PerceptualFrameFingerprint.Algorithm, PreparationVersion, CancellationToken.None));
        Assert.Equal(new[] { fixture.Media[0].MediaFileId, fixture.Media[1].MediaFileId }.Order(), new[] { candidate.MediaFileAId, candidate.MediaFileBId }.Order());
        var (distances, _) = NearDuplicatePreparation.Compare(await CurrentPreparedAsync(fixture.Repository, fixture.Media[0]), await CurrentPreparedAsync(fixture.Repository, fixture.Media[1]));
        Assert.All(distances, item => Assert.Equal(0, item.HammingDistance));
    }

    [Fact]
    public async Task APillarboxedCopyIsFoundFromStoredFramesWithoutReadingTheVideosAgain()
    {
        var tools = ResolveBundledFfmpeg();
        Assert.NotNull(tools.FfmpegPath);
        var directory = Path.Combine(_root, "generated");
        Directory.CreateDirectory(directory);
        var original = Path.Combine(directory, "original.mp4");
        var pillarbox = Path.Combine(directory, "pillarbox.mp4");
        await RunFfmpegAsync(tools, "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=15", "-t", "8", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", original);
        await RunFfmpegAsync(tools, "-i", original, "-vf", "scale=240:180,pad=320:180:40:0", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", pillarbox);
        var before = new[] { original, pillarbox }.Select(path => (path, File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path))).ToArray();

        var repository = new SqliteLibraryRepository(_paths, _log);
        await repository.InitializeAsync(CancellationToken.None);
        var jobs = new SqliteJobStore(_paths, _log);
        var root = await repository.AddOrGetLibraryRootAsync(directory, CancellationToken.None);
        using var pipeline = new MediaPipeline(repository, jobs, new FileSystemMediaScanner(), new FfmpegAdapter(tools, _paths, _log, TimeSpan.FromSeconds(60)), _log,
            resourceAdmission: new AdmitEverything(), paths: _paths);
        await pipeline.EnqueueScanAsync(root.LibraryRootId, CancellationToken.None);
        await pipeline.RunUntilIdleAsync(CancellationToken.None);

        var group = Assert.Single((await repository.QueryDuplicateGroupsByRelationAsync(root.LibraryRootId, DuplicateRelationType.NearDuplicate, true, 0, 10, CancellationToken.None)).Items);
        Assert.Equal(2, group.CurrentMemberCount);
        // The fingerprints of the frames as stored, bars included, do not make the pair similar on their own.
        var media = await Task.WhenAll((await repository.QueryMediaAsync(root.LibraryRootId, null, 0, 10, CancellationToken.None)).Items
            .Select(async item => (await repository.GetMediaFileAsync(item.MediaFileId, CancellationToken.None))!));
        var plain = await Task.WhenAll(media.Select(item => repository.GetCurrentNearDuplicateFramesAsync(item.MediaFileId, item.MediaContentId, NearDuplicateSamplingPolicy.Version, PerceptualFrameFingerprint.Algorithm, CancellationToken.None)));
        var plainDistances = plain[0].Zip(plain[1], (a, b) => (a.SampleIndex, PerceptualFrameFingerprint.HammingDistance(a.FingerprintHex!, b.FingerprintHex!))).ToArray();
        Assert.False(NearDuplicateVerificationProfile.HighPrecisionV1.Verify(8, 8, plainDistances).IsVerified);
        Assert.Equal(2L, await ScalarAsync("SELECT COUNT(1) FROM near_duplicate_preparation WHERE status='Completed';"));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(1) FROM near_duplicate_preparation WHERE crop_left>0 AND crop_left=crop_right AND crop_top=0;"));
        foreach (var (path, bytes, written) in before)
        {
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        }
    }

    [Fact]
    public async Task ARescanPreparesVideosFingerprintedEarlierWithoutQueueingWorkOnTheVideos()
    {
        var tools = ResolveBundledFfmpeg();
        var directory = Path.Combine(_root, "catch-up");
        Directory.CreateDirectory(directory);
        await RunFfmpegAsync(tools, "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=15", "-t", "6", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", Path.Combine(directory, "clip.mp4"));
        var repository = new SqliteLibraryRepository(_paths, _log);
        await repository.InitializeAsync(CancellationToken.None);
        var jobs = new SqliteJobStore(_paths, _log);
        var root = await repository.AddOrGetLibraryRootAsync(directory, CancellationToken.None);
        using var pipeline = new MediaPipeline(repository, jobs, new FileSystemMediaScanner(), new FfmpegAdapter(tools, _paths, _log, TimeSpan.FromSeconds(60)), _log,
            resourceAdmission: new AdmitEverything(), paths: _paths);
        await pipeline.EnqueueScanAsync(root.LibraryRootId, CancellationToken.None);
        await pipeline.RunUntilIdleAsync(CancellationToken.None);
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(1) FROM near_duplicate_preparation WHERE status='Completed';"));

        // As a catalog fingerprinted by the build before black bars were cut off.
        await ScalarAsync("DELETE FROM near_duplicate_prepared_lsh_bucket; DELETE FROM near_duplicate_prepared_frame; DELETE FROM near_duplicate_preparation; SELECT 0;");
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(1) FROM near_duplicate_preparation;"));
        var jobsBefore = await ScalarAsync("SELECT COUNT(1) FROM job;");
        await pipeline.EnqueueScanAsync(root.LibraryRootId, CancellationToken.None);
        await pipeline.RunUntilIdleAsync(CancellationToken.None);

        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(1) FROM near_duplicate_preparation WHERE status='Completed';"));
        Assert.Equal(jobsBefore + 1, await ScalarAsync("SELECT COUNT(1) FROM job;"));
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(1) FROM job WHERE job_type IN ('NearDuplicateFingerprint','ProtectedSourceOrchestration') AND created_at > (SELECT MAX(created_at) FROM job WHERE job_type='Scan') ;"));
    }

    [Fact]
    public async Task StoredFramesAreReadBackTogetherAndNotAtAllWhenOneIsMissing()
    {
        var tools = ResolveBundledFfmpeg();
        _paths.EnsureDirectories();
        var folder = Path.Combine(_paths.CacheRoot, "near-duplicate");
        Directory.CreateDirectory(folder);
        await RunFfmpegAsync(tools, "-f", "lavfi", "-i", "color=c=black:size=320x180", "-frames:v", "1", Path.Combine(folder, "black.jpg"));
        await RunFfmpegAsync(tools, "-f", "lavfi", "-i", "color=c=white:size=640x480", "-frames:v", "1", Path.Combine(folder, "white.jpg"));
        var adapter = new FfmpegAdapter(tools, _paths, _log, TimeSpan.FromSeconds(30));

        // Five frames, as a video has: each must come back as it reads alone, in order.
        string[] names = ["near-duplicate/black.jpg", "near-duplicate/white.jpg", "near-duplicate/white.jpg", "near-duplicate/black.jpg", "near-duplicate/white.jpg"];
        var frames = await adapter.ReadStoredFramesGrayAsync(names, NearDuplicatePreparation.Width, NearDuplicatePreparation.Height, CancellationToken.None);

        Assert.NotNull(frames);
        Assert.Equal(5, frames.Count);
        Assert.All(frames, frame => Assert.Equal(NearDuplicatePreparation.InputByteCount, frame.Length));
        for (var index = 0; index < names.Length; index++)
            Assert.Equal((await adapter.ReadStoredFramesGrayAsync([names[index]], NearDuplicatePreparation.Width, NearDuplicatePreparation.Height, CancellationToken.None))![0], frames[index]);
        Assert.True(frames[0].Max() <= FramePreparation.BarBrightness);
        Assert.True(frames[1].Min() > 200);
        Assert.Null(await adapter.ReadStoredFramesGrayAsync(["near-duplicate/black.jpg", "near-duplicate/missing.jpg"], NearDuplicatePreparation.Width, NearDuplicatePreparation.Height, CancellationToken.None));
        Assert.Null(await adapter.ReadStoredFramesGrayAsync(["near-duplicate/black.jpg", null], NearDuplicatePreparation.Width, NearDuplicatePreparation.Height, CancellationToken.None));
    }

    /// <summary>A fingerprint none of whose four bands is shared with another seed or sample.</summary>
    private static string Hex(int seed, int sample) => string.Concat(Enumerable.Range(0, 4).Select(band => $"{seed:x2}{sample:x1}{band:x1}"));

    private static PreparedNearDuplicateFrame[] Prepared(int seed) =>
        Enumerable.Range(0, 5).Select(i => new PreparedNearDuplicateFrame(i, Hex(seed, i), Hex(seed + 1, i), Hex(seed + 2, i), false)).ToArray();

    private static Task<IReadOnlyList<NearDuplicatePreparationPending>> PendingAsync(SqliteLibraryRepository repository, Guid? mediaFileId = null) =>
        repository.GetPendingNearDuplicatePreparationsAsync(null, mediaFileId, NearDuplicateSamplingPolicy.Version, PerceptualFrameFingerprint.Algorithm, PreparationVersion, -1, CancellationToken.None);

    private static Task<IReadOnlyList<PreparedNearDuplicateFrame>> CurrentPreparedAsync(SqliteLibraryRepository repository, MediaFile media) =>
        repository.GetCurrentPreparedNearDuplicateFramesAsync(media.MediaFileId, media.MediaContentId, NearDuplicateSamplingPolicy.Version, PerceptualFrameFingerprint.Algorithm, PreparationVersion, CancellationToken.None);

    private static async Task<bool> SavePreparedAsync(SqliteLibraryRepository repository, MediaFile media, string framesVersion, IReadOnlyList<PreparedNearDuplicateFrame> prepared)
    {
        var frames = await repository.GetCurrentNearDuplicateFramesAsync(media.MediaFileId, media.MediaContentId, NearDuplicateSamplingPolicy.Version, PerceptualFrameFingerprint.Algorithm, CancellationToken.None);
        return await repository.SaveNearDuplicatePreparationAsync(media.MediaFileId, media.MediaContentId, NearDuplicateSamplingPolicy.Version, PerceptualFrameFingerprint.Algorithm, PreparationVersion, framesVersion, FrameCrop.None,
            frames.Zip(prepared, (frame, value) => (frame.NearDuplicateFrameId, value)).ToArray(), CancellationToken.None);
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _paths.DatabasePath }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task RunFfmpegAsync(FfmpegPaths tools, params string[] arguments)
    {
        var start = new ProcessStartInfo(tools.FfmpegPath!) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "-hide_banner", "-nostdin", "-y", "-loglevel", "error" }.Concat(arguments)) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Synthetic FFmpeg process did not start.");
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
    }

    private sealed class AdmitEverything : IResourceAdmissionPolicy
    {
        public AdmissionDecision Evaluate(ResourceCostEstimate estimate, ResourceSnapshot snapshot) =>
            new(AdmissionDecisionKind.Admit, JobReason.None, "TEST_RESOURCE_ADMIT", "Generated videos only.");
    }

    private static FfmpegPaths ResolveBundledFfmpeg()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var runtime = Path.Combine(current.FullName, "runtime", "ffmpeg");
            if (File.Exists(Path.Combine(runtime, "ffmpeg.exe")) && File.Exists(Path.Combine(runtime, "ffprobe.exe")))
                return new FfmpegPaths(Path.Combine(runtime, "ffmpeg.exe"), Path.Combine(runtime, "ffprobe.exe"));
        }
        return new FfmpegPaths(null, null);
    }
}
