using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed partial class NearDuplicateFoundationTests
{
    [Theory]
    [InlineData("0.000000,K_\n1.001000,__\n2.002000,K_\n4.004000,K_\n6.006000,K_\n", 2.002)]
    [InlineData("0.000000,K_\n0.033000,__\n", 20.0)]
    [InlineData("", null)]
    public void KeyframeSpacingIsTheMedianGapInTheFirstPackets(string packets, double? expected)
    {
        var spacing = FfmpegAdapter.ParseKeyframeSpacing(packets);
        if (expected is null) Assert.Null(spacing);
        else Assert.Equal(expected.Value, spacing!.Value, 3);
    }

    [Fact]
    public async Task ReadingTheFileOnceGivesTheSameFramesAsReadingThemOneByOne()
    {
        var tools = ResolveBundledFfmpeg();
        _paths.EnsureDirectories();
        var directory = Path.Combine(_root, "timeline-read");
        Directory.CreateDirectory(directory);
        var shots = await ShotsAsync(tools, Path.Combine(directory, "shots.mp4"), 31);
        // Keyframes 10 s apart: frame by frame would read the file more than once, so it is read once from start to end.
        var sparse = Path.Combine(directory, "sparse-keyframes.mp4");
        await RunFfmpegAsync(tools, "-i", shots, "-c:v", "libx264", "-preset", "ultrafast", "-g", "300", "-keyint_min", "300", "-sc_threshold", "0", "-pix_fmt", "yuv420p", sparse);
        var dense = Path.Combine(directory, "dense-keyframes.mp4");
        await RunFfmpegAsync(tools, "-i", shots, "-c:v", "libx264", "-preset", "ultrafast", "-g", "30", "-pix_fmt", "yuv420p", dense);
        var adapter = new FfmpegAdapter(tools, _paths, _log, TimeSpan.FromSeconds(60));
        var times = TimelineSamplingPolicy.Times(32);

        foreach (var (path, expectSequential) in new[] { (sparse, true), (dense, false) })
        {
            var media = await ScannedAsync(path);
            await using var session = await ProcessingSourceSession.AcquireAsync(_paths, media.MediaFileId, media.MediaContentId, media.Path, _log, CancellationToken.None);
            var (timeline, sequential) = await session.ExecuteSourceStageAsync("Test", (source, ct) => adapter.ReadTimelineAsync(source, media, 32, ct), CancellationToken.None);
            var oneByOne = await session.ExecuteSourceStageAsync("Test", (source, ct) => adapter.ReadFramesAtAsync(source, media, times, ct), CancellationToken.None);

            Assert.Equal(expectSequential, sequential);
            Assert.Equal(times, timeline.Select(f => f.Seconds));
            Assert.Equal(times, oneByOne.Select(f => f.Seconds));
            for (var i = 0; i < times.Count; i++)
                Assert.True(timeline[i].Gray.Zip(oneByOne[i].Gray, (x, y) => Math.Abs(x - y)).Average() < 2, $"{Path.GetFileName(path)} at {times[i]} s");
            session.CompletePublicationBoundary(ProcessingPublicationResolution.Rejected);
        }
    }

    private async Task<MediaFile> ScannedAsync(string path)
    {
        var repository = new SqliteLibraryRepository(_paths, _log);
        await repository.InitializeAsync(CancellationToken.None);
        var directory = Path.GetDirectoryName(path)!;
        var root = await repository.AddOrGetLibraryRootAsync(directory, CancellationToken.None);
        await RescanAsync(repository, root);
        var page = await repository.QueryMediaAsync(root.LibraryRootId, null, 0, 100, CancellationToken.None);
        var item = page.Items.Single(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase));
        return (await repository.GetMediaFileAsync(item.MediaFileId, CancellationToken.None))!;
    }
}
