namespace LocalMediaAI.Core.Tests;

public sealed class FramePreparationTests
{
    private const int W = 20;
    private const int H = 20;

    private static byte[] Frame(Func<int, int, byte> pixel)
    {
        var frame = new byte[W * H];
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
                frame[y * W + x] = pixel(x, y);
        return frame;
    }

    private static byte Picture(int x, int y) => (byte)(60 + (x * 7 + y * 13) % 150);

    [Fact]
    public void LetterboxBarsAreFound()
    {
        var letterbox = Frame((x, y) => y < 2 || y >= H - 2 ? (byte)12 : Picture(x, y));
        Assert.Equal(new FrameCrop(0, 2, 0, 2), FramePreparation.DetectBlackBorders(letterbox, W, H));
    }

    [Fact]
    public void APictureWithoutBarsIsNotCropped() =>
        Assert.True(FramePreparation.DetectBlackBorders(Frame(Picture), W, H).IsNone);

    [Fact]
    public void ADarkPictureIsNotTakenForBars()
    {
        Assert.True(FramePreparation.DetectBlackBorders(new byte[W * H], W, H).IsNone);
        var darkTop = Frame((x, y) => y < 3 ? (byte)5 : Picture(x, y));   // dark sky above, nothing below
        Assert.True(FramePreparation.DetectBlackBorders(darkTop, W, H).IsNone);
    }

    [Fact]
    public void PillarboxBarsAreFound()
    {
        var pillarbox = Frame((x, y) => x < 3 || x >= W - 3 ? (byte)0 : Picture(x, y));
        Assert.Equal(new FrameCrop(3, 0, 3, 0), FramePreparation.DetectBlackBorders(pillarbox, W, H));
    }

    [Fact]
    public void OnlyBarsInEveryFrameAreKept()
    {
        var bars = new FrameCrop(0, 2, 0, 2);
        var darkSky = new FrameCrop(0, 4, 0, 2);
        Assert.Equal(bars, FramePreparation.Common([bars, darkSky, bars]));
        Assert.True(FramePreparation.Common([]).IsNone);
    }

    [Fact]
    public void ResamplingTheCroppedPictureMatchesTheUncroppedOne()
    {
        var plain = Frame(Picture);
        var letterbox = Frame((x, y) => y < 2 || y >= H - 2 ? (byte)0 : Picture(x, (y - 2) * H / (H - 4)));
        var a = FramePreparation.Resample(plain, W, H, FrameCrop.None, 9, 8);
        var b = FramePreparation.Resample(letterbox, W, H, FramePreparation.DetectBlackBorders(letterbox, W, H), 9, 8);
        Assert.True(PerceptualFrameFingerprint.HammingDistance(PerceptualFrameFingerprint.ComputeDHashHex(a), PerceptualFrameFingerprint.ComputeDHashHex(b)) <= 12);
    }

    [Fact]
    public void ResampleAveragesAreas()
    {
        var halves = Frame((x, _) => x < W / 2 ? (byte)0 : (byte)200);
        Assert.Equal([0, 200], FramePreparation.Resample(halves, W, H, FrameCrop.None, 2, 1).Select(b => (int)b));
        Assert.Equal(new FrameCrop(2, 2, 2, 2), FramePreparation.Center(W, H, 0.8));
    }
}
