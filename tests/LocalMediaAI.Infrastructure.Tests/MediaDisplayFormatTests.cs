using System.Globalization;
using LocalMediaAI.App;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class MediaDisplayFormatTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1,023 B")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(116_413L, "114 KB")]
    [InlineData(5_242_880L, "5.0 MB")]
    [InlineData(1_610_612_736L, "1.5 GB")]
    [InlineData(2_199_023_255_552L, "2.0 TB")]
    public void FileSizeUsesReadableBinaryUnits(long bytes, string expected)
        => Assert.Equal(expected, MediaDisplayFormat.FileSize(bytes));

    [Theory]
    [InlineData(0d, "0:00")]
    [InlineData(3.2d, "0:03")]
    [InlineData(59.6d, "1:00")]
    [InlineData(754d, "12:34")]
    [InlineData(3_723d, "1:02:03")]
    public void DurationUsesClockNotation(double seconds, string expected)
        => Assert.Equal(expected, MediaDisplayFormat.Duration(seconds));

    [Fact]
    public void ConvertersFallBackToEmptyForMissingValues()
    {
        Assert.Equal(string.Empty, new FileSizeDisplayConverter().Convert(null!, typeof(string), null!, CultureInfo.InvariantCulture));
        Assert.Equal(string.Empty, new DurationDisplayConverter().Convert(null!, typeof(string), null!, CultureInfo.InvariantCulture));
        Assert.Equal(string.Empty, MediaDisplayFormat.Duration(double.NaN));
        Assert.Equal("1.5 KB", new FileSizeDisplayConverter().Convert(1536L, typeof(string), null!, CultureInfo.InvariantCulture));
        Assert.Equal("0:03", new DurationDisplayConverter().Convert(3.0d, typeof(string), null!, CultureInfo.InvariantCulture));
    }
}
