using LocalMediaAI.Infrastructure;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class PhysicalDiskTests
{
    [Fact]
    public void LocalPathResolvesToADiskWhoseCountersCanBeReadWithoutElevation()
    {
        var disk = PhysicalDisk.TryGetDiskNumber(Path.GetTempPath());

        Assert.NotNull(disk);
        var snapshot = PhysicalDisk.TryRead(disk!.Value);
        Assert.NotNull(snapshot);
        Assert.Equal(disk.Value, snapshot!.DiskNumber);
        Assert.True(snapshot.QueryTime > DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public void FlushedWriteShowsUpInTheDiskDelta()
    {
        var file = Path.Combine(Path.GetTempPath(), "lmai-diskperf-" + Guid.NewGuid().ToString("N"));
        var disk = PhysicalDisk.TryGetDiskNumber(file)!.Value;
        var before = PhysicalDisk.TryRead(disk)!;
        try
        {
            using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(new byte[64 * 1024]);
                stream.Flush(flushToDisk: true);
            }
            var activity = PhysicalDisk.TryRead(disk)!.Since(before);

            Assert.True(activity.WriteCount >= 1);
            Assert.True(activity.BytesWritten >= 64 * 1024);
            Assert.InRange(activity.BusyPercent, 0, 100);
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData(@"\\server\share\clip.mp4")]
    [InlineData("")]
    public void UnresolvablePathsReturnNull(string path) => Assert.Null(PhysicalDisk.TryGetDiskNumber(path));

    [Fact]
    public void MissingDiskReturnsNull() => Assert.Null(PhysicalDisk.TryRead(250));

    [Fact]
    public void ActivityRejectsSnapshotsFromDifferentDisks()
    {
        var a = new DiskPerformanceSnapshot(0, 0, 0, 0, 0, TimeSpan.Zero, DateTimeOffset.UtcNow);
        var b = a with { DiskNumber = 1 };

        Assert.Throws<ArgumentException>(() => b.Since(a));
    }
}
