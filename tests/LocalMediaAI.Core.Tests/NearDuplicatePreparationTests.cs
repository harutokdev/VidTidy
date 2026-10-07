namespace LocalMediaAI.Core.Tests;

public sealed class NearDuplicatePreparationTests
{
    private const int W = NearDuplicatePreparation.Width;
    private const int H = NearDuplicatePreparation.Height;

    private static byte[] Frame(Func<double, double, double> picture)
    {
        var frame = new byte[W * H];
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
                frame[y * W + x] = (byte)Math.Clamp(Math.Round(picture(x + 0.5, y + 0.5)), 0, 255);
        return frame;
    }

    /// <summary>A picture with structure at the scale a 9x8 fingerprint sees, shifted per sample so frames differ.</summary>
    private static Func<double, double, double> Scene(int sample) =>
        (x, y) => 128 + 90 * Math.Sin((x + sample * 11) / 9.0) * Math.Cos((y - sample * 5) / 6.0);

    /// <summary>The same picture shrunk into the middle rows, with black bars above and below.</summary>
    private static Func<double, double, double> Letterboxed(Func<double, double, double> picture, int bar) =>
        (x, y) => y < bar || y >= H - bar ? 0 : picture(x, (y - bar) * H / (H - 2.0 * bar));

    /// <summary>The middle part of the picture blown up to fill the frame, as when a copy was zoomed or its edges cut.</summary>
    private static Func<double, double, double> Zoomed(Func<double, double, double> picture, double share) =>
        (x, y) => picture(W / 2.0 + (x - W / 2.0) * share, H / 2.0 + (y - H / 2.0) * share);

    private static IReadOnlyList<PreparedNearDuplicateFrame> Prepare(Func<int, Func<double, double, double>> scene) =>
        NearDuplicatePreparation.Prepare(Enumerable.Range(0, 5).Select(i => (i, Frame(scene(i)))).ToArray());

    [Fact]
    public void ACopyWithBlackBarsComparesAsTheSamePicture()
    {
        var plain = Prepare(Scene);
        var barred = Prepare(i => Letterboxed(Scene(i), 6));
        var (distances, flat) = NearDuplicatePreparation.Compare(plain, barred);
        Assert.All(distances, d => Assert.True(d.HammingDistance <= 8, $"sample {d.SampleIndex}: {d.HammingDistance}"));
        Assert.Empty(flat);
        Assert.True(NearDuplicateVerificationProfile.HighPrecisionV1.Verify(20, 20, distances, flat).IsVerified);
    }

    [Fact]
    public void AZoomedCopyIsMatchedThroughTheMiddleOfTheOriginal()
    {
        var plain = Prepare(Scene);
        var zoomed = Prepare(i => Zoomed(Scene(i), 0.8));
        var wholeOnly = plain.Zip(zoomed, (a, b) => PerceptualFrameFingerprint.HammingDistance(a.WholeHex, b.WholeHex)).Sum();
        var (distances, _) = NearDuplicatePreparation.Compare(plain, zoomed);
        Assert.True(distances.Sum(d => d.HammingDistance) < wholeOnly);
        Assert.Equal(distances, NearDuplicatePreparation.Compare(zoomed, plain).Distances);
    }

    [Fact]
    public void BlackFramesAreFlatAndOnlyCountAsFlatWhenBothAre()
    {
        var black = Prepare(_ => (_, _) => 16);
        var plain = Prepare(Scene);
        Assert.All(black, frame => Assert.True(frame.IsFlat));
        Assert.All(plain, frame => Assert.False(frame.IsFlat));
        Assert.Equal([0, 1, 2, 3, 4], NearDuplicatePreparation.Compare(black, black).FlatSamples);
        Assert.Empty(NearDuplicatePreparation.Compare(black, plain).FlatSamples);
    }

    [Fact]
    public void BarsAreOnlyThoseEveryFrameShows()
    {
        var frames = Enumerable.Range(0, 5).Select(i => Frame(i == 2 ? Scene(i) : Letterboxed(Scene(i), 6))).ToArray();
        Assert.True(NearDuplicatePreparation.Bars(frames).IsNone);
        Assert.Equal(new FrameCrop(0, 6, 0, 6), NearDuplicatePreparation.Bars(frames.Select((f, i) => i == 2 ? Frame(Letterboxed(Scene(i), 6)) : f).ToArray()));
    }

    [Fact]
    public void FramesOfTheWrongSizeAreRefused() =>
        Assert.Throws<ArgumentException>(() => NearDuplicatePreparation.Prepare([(0, new byte[72])]));
}
