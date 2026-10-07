using LocalMediaAI.App.Organize;
using LocalMediaAI.Core;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class GroupRowLengthTests
{
    private static OrganizeFile File(string name, double duration) =>
        new(Guid.NewGuid(), Guid.NewGuid(), name, $@"D:\videos\{name}", 1_000_000, DateTimeOffset.UnixEpoch, 1920, 1080, 8_000_000, duration, "h264");

    private static GroupRow Row(OrganizeFile a, OrganizeFile b, LengthDifference? length) =>
        new(new OrganizeGroupData(Guid.NewGuid(), GroupCategory.Similar, 0.95, [a, b], length), a.MediaFileId);

    [Fact]
    public void TheWarningSaysWhichFileIsLongerAndWhereItHasMore()
    {
        var shorter = File("cut.mp4", 3000);
        var longer = File("full.mp4", 3700);
        var row = Row(shorter, longer, new LengthDifference(longer.MediaFileId, [new TimelineSpan(0, 3), new TimelineSpan(1200, 1800), new TimelineSpan(3660, 3700)]));

        Assert.Equal("B の方が長く、最初の 0:03 と 20:00〜30:00 と 最後の 0:40 が A にはありません。", row.LengthText);
        Assert.Equal(row.LengthText, row.Warnings[0]);
        Assert.DoesNotContain(row.Warnings, w => w.Contains("一部にしか無い場面", StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutATimelineMatchTheGeneralLengthWarningStays()
    {
        var row = Row(File("a.mp4", 60), File("b.mp4", 90), null);
        Assert.Null(row.LengthText);
        Assert.Contains(row.Warnings, w => w.Contains("一部にしか無い場面", StringComparison.Ordinal));
    }
}
