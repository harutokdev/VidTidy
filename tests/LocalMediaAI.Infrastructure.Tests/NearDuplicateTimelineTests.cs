using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed partial class NearDuplicateFoundationTests
{
    /// <summary>A generated video of four different shots, eight seconds each, like a video with cuts.</summary>
    private static async Task<string> ShotsAsync(FfmpegPaths tools, string path, int seed)
    {
        string[] shots =
        [
            $"life=size=320x180:rate=30:mold=10:ratio=0.2:seed={seed}",
            // gradients picks random colours and points even with a seed, so they are given to keep the videos the same every run.
            $"gradients=size=320x180:rate=30:speed=0.02:nb_colors=3:c0=0x{seed * 7919 % 0xFFFFFF:x6}:c1=0x{seed * 104729 % 0xFFFFFF:x6}:c2=0x{seed * 1299709 % 0xFFFFFF:x6}:x0=0:y0=0:x1=319:y1=179",
            $"mandelbrot=size=320x180:rate=30:start_x={-0.75 + seed % 7 * 0.05:0.00}:start_y={0.1 + seed % 5 * 0.03:0.00}:start_scale=0.4",
            $"cellauto=size=320x180:rate=30:rule=110:random_fill_ratio=0.5:seed={seed + 2}",
        ];
        var arguments = new List<string>();
        foreach (var shot in shots) arguments.AddRange(["-f", "lavfi", "-t", "8", "-i", shot.Replace(',', '.')]);
        var graph = string.Concat(shots.Select((_, i) => $"[{i}:v]setsar=1,fps=30,format=yuv420p[s{i}];")) + string.Concat(shots.Select((_, i) => $"[s{i}]")) + $"concat=n={shots.Length}:v=1:a=0[v]";
        arguments.AddRange(["-filter_complex", graph, "-map", "[v]", "-c:v", "libx264", "-preset", "ultrafast", path]);
        await RunFfmpegAsync(tools, [.. arguments]);
        return path;
    }

    [Fact]
    public async Task ACopyWithItsStartCutOffIsGroupedAndTheLongerStartIsKept()
    {
        var tools = ResolveBundledFfmpeg();
        var directory = Path.Combine(_root, "timeline");
        Directory.CreateDirectory(directory);
        var original = await ShotsAsync(tools, Path.Combine(directory, "original.mp4"), 11);
        var trimmed = Path.Combine(directory, "trimmed.mp4");
        await RunFfmpegAsync(tools, "-ss", "3", "-i", original, "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", trimmed);
        await ShotsAsync(tools, Path.Combine(directory, "unrelated.mp4"), 57);
        var before = Directory.GetFiles(directory).Select(path => (path, File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path))).ToArray();

        var repository = new SqliteLibraryRepository(_paths, _log);
        await repository.InitializeAsync(CancellationToken.None);
        var jobs = new SqliteJobStore(_paths, _log);
        var root = await repository.AddOrGetLibraryRootAsync(directory, CancellationToken.None);
        using var pipeline = new MediaPipeline(repository, jobs, new FileSystemMediaScanner(), new FfmpegAdapter(tools, _paths, _log, TimeSpan.FromSeconds(60)), _log,
            resourceAdmission: new AdmitEverything(), paths: _paths);
        await pipeline.EnqueueScanAsync(root.LibraryRootId, CancellationToken.None);
        await pipeline.RunUntilIdleAsync(CancellationToken.None);

        Assert.Equal(3L, await ScalarAsync("SELECT COUNT(1) FROM near_duplicate_timeline WHERE frame_count > 0;"));
        var group = Assert.Single((await repository.QueryDuplicateGroupsByRelationAsync(root.LibraryRootId, DuplicateRelationType.NearDuplicate, true, 0, 10, CancellationToken.None)).Items);
        var members = await repository.GetNearDuplicateComparisonMembersAsync(group.DuplicateGroupId, CancellationToken.None);
        Assert.Equal(["original.mp4", "trimmed.mp4"], members.Select(m => m.Media.FileName).Order());
        var evidence = members[0].Evidence;
        Assert.Equal(TimelineAlignment.ThresholdVersion, evidence.ThresholdVersion);
        var detail = await repository.GetTimelineEvidenceDetailAsync(evidence.NearDuplicateEvidenceId, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal(members.Single(m => m.Media.FileName == "trimmed.mp4").MediaFileId, detail.ShorterMediaFileId);
        var extra = Assert.Single(detail.ExtraInLonger);
        Assert.InRange(extra.StartSeconds, 0, 0.5);
        Assert.InRange(extra.EndSeconds, 2.5, 3.5);

        // The organize screen says which is longer and where.
        var shown = Assert.Single((await new LocalMediaAI.App.Organize.RepositoryOrganizeDataSource(repository).LoadAsync(CancellationToken.None)).Groups);
        Assert.NotNull(shown.Length);
        Assert.Equal(members.Single(m => m.Media.FileName == "original.mp4").MediaFileId, shown.Length.LongerFileId);
        var row = new LocalMediaAI.App.Organize.GroupRow(shown, shown.Files[0].MediaFileId);
        Assert.Contains("最初の 0:03", row.LengthText, StringComparison.Ordinal);
        foreach (var (path, bytes, written) in before)
        {
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        }
    }

    [Fact]
    public async Task VideosFingerprintedBeforeTimelinesGetOneOnTheNextScan()
    {
        var tools = ResolveBundledFfmpeg();
        var directory = Path.Combine(_root, "timeline-catch-up");
        Directory.CreateDirectory(directory);
        await ShotsAsync(tools, Path.Combine(directory, "clip.mp4"), 23);
        var repository = new SqliteLibraryRepository(_paths, _log);
        await repository.InitializeAsync(CancellationToken.None);
        var jobs = new SqliteJobStore(_paths, _log);
        var root = await repository.AddOrGetLibraryRootAsync(directory, CancellationToken.None);
        using var pipeline = new MediaPipeline(repository, jobs, new FileSystemMediaScanner(), new FfmpegAdapter(tools, _paths, _log, TimeSpan.FromSeconds(60)), _log,
            resourceAdmission: new AdmitEverything(), paths: _paths);
        await pipeline.EnqueueScanAsync(root.LibraryRootId, CancellationToken.None);
        await pipeline.RunUntilIdleAsync(CancellationToken.None);
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(1) FROM near_duplicate_timeline;"));

        // As a catalog made by the build before timelines: frames, no timeline.
        await ScalarAsync("DELETE FROM near_duplicate_timeline_bucket; DELETE FROM near_duplicate_timeline_frame; DELETE FROM near_duplicate_timeline; SELECT 0;");
        await pipeline.EnqueueScanAsync(root.LibraryRootId, CancellationToken.None);
        await pipeline.RunUntilIdleAsync(CancellationToken.None);

        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(1) FROM near_duplicate_timeline WHERE frame_count >= 6;"));
        Assert.Equal(5L, await ScalarAsync("SELECT COUNT(1) FROM near_duplicate_frame WHERE status='Completed';"));
    }
}
