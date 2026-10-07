using LocalMediaAI.App.Organize;
using LocalMediaAI.Core;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed partial class EnglishUiTests
{
    [Fact]
    public void TheLongerCopyNoteReadsAsOneEnglishSentence()
    {
        static OrganizeFile File(string name, double duration) =>
            new(Guid.NewGuid(), Guid.NewGuid(), name, $@"D:\videos\{name}", 1_000_000, DateTimeOffset.UnixEpoch, 1920, 1080, 8_000_000, duration, "h264");
        var shorter = File("cut.mp4", 3000);
        var longer = File("full.mp4", 3700);
        var row = new GroupRow(new OrganizeGroupData(Guid.NewGuid(), GroupCategory.Similar, 0.95, [shorter, longer],
            new LengthDifference(longer.MediaFileId, [new TimelineSpan(0, 3), new TimelineSpan(1200, 1800), new TimelineSpan(3660, 3700)])), shorter.MediaFileId);

        Assert.Equal("B is longer. Not in A: the first 0:03, 20:00–30:00, the last 0:40.", row.LengthText);
    }
}
