namespace LocalMediaAI.Core;

/// <summary>
/// Fingerprints of one sampled frame after black bars were cut off: of the whole remaining picture and of its middle
/// 90% and 80% (for copies that were zoomed or had their edges cut).
/// </summary>
public sealed record PreparedNearDuplicateFrame(int SampleIndex, string WholeHex, string Center90Hex, string Center80Hex, bool IsFlat);

/// <summary>
/// Turns the five sampled frames of one video into <see cref="PreparedNearDuplicateFrame"/>s. Bars are those every frame
/// shows (see <see cref="FramePreparation.Common"/>). Pure, no I/O: the caller supplies 96x54 grey frames.
/// </summary>
public static class NearDuplicatePreparation
{
    public const string Version = "bars-center-v1";
    public const int Width = FramePreparation.SampleWidth;
    public const int Height = FramePreparation.SampleHeight;
    public const int InputByteCount = Width * Height;

    public static FrameCrop Bars(IReadOnlyList<byte[]> grayFrames)
    {
        ArgumentNullException.ThrowIfNull(grayFrames);
        return FramePreparation.Common(grayFrames.Select(frame => FramePreparation.DetectBlackBorders(frame, Width, Height)));
    }

    public static IReadOnlyList<PreparedNearDuplicateFrame> Prepare(IReadOnlyList<(int SampleIndex, byte[] Gray)> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        return Prepare(frames, Bars(Validated(frames).Select(frame => frame.Gray).ToArray()));
    }

    /// <summary>Prepares frames with bars already known for their video, such as extra frames read for one comparison.</summary>
    public static IReadOnlyList<PreparedNearDuplicateFrame> Prepare(IReadOnlyList<(int SampleIndex, byte[] Gray)> frames, FrameCrop bars)
    {
        ArgumentNullException.ThrowIfNull(frames);
        return Validated(frames).Select(frame =>
        {
            var whole = FramePreparation.Resample(frame.Gray, Width, Height, bars, PerceptualFrameFingerprint.Width, PerceptualFrameFingerprint.Height);
            var wholeHex = PerceptualFrameFingerprint.ComputeDHashHex(whole);
            return new PreparedNearDuplicateFrame(frame.SampleIndex, wholeHex,
                Hash(frame.Gray, Inside(bars, 0.9)), Hash(frame.Gray, Inside(bars, 0.8)),
                PerceptualFrameFingerprint.HasFewEdges(wholeHex) && PerceptualFrameFingerprint.IsFlat(whole));
        }).ToArray();
    }

    /// <summary>
    /// How far apart two frames are: the whole pictures, or the middle of either one against the whole of the other,
    /// whichever is closest.
    /// </summary>
    public static int Distance(PreparedNearDuplicateFrame a, PreparedNearDuplicateFrame b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var best = PerceptualFrameFingerprint.HammingDistance(a.WholeHex, b.WholeHex);
        foreach (var (inner, whole) in new[] { (a.Center90Hex, b.WholeHex), (a.Center80Hex, b.WholeHex), (b.Center90Hex, a.WholeHex), (b.Center80Hex, a.WholeHex) })
            best = Math.Min(best, PerceptualFrameFingerprint.HammingDistance(inner, whole));
        return best;
    }

    /// <summary>Per-sample distances and the samples both videos show as flat, ready for <see cref="NearDuplicateVerificationProfile.Verify"/>.</summary>
    public static (IReadOnlyList<(int SampleIndex, int HammingDistance)> Distances, IReadOnlyList<int> FlatSamples) Compare(
        IReadOnlyList<PreparedNearDuplicateFrame> a, IReadOnlyList<PreparedNearDuplicateFrame> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var pairs = a.Join(b, left => left.SampleIndex, right => right.SampleIndex, (left, right) => (left, right)).OrderBy(pair => pair.left.SampleIndex).ToArray();
        return (pairs.Select(pair => (pair.left.SampleIndex, Distance(pair.left, pair.right))).ToArray(),
            pairs.Where(pair => pair.left.IsFlat && pair.right.IsFlat).Select(pair => pair.left.SampleIndex).ToArray());
    }

    private static IReadOnlyList<(int SampleIndex, byte[] Gray)> Validated(IReadOnlyList<(int SampleIndex, byte[] Gray)> frames) =>
        frames.Any(frame => frame.Gray is null || frame.Gray.Length != InputByteCount)
            ? throw new ArgumentException($"Each frame must be {Width}x{Height} grey bytes.", nameof(frames))
            : frames;

    private static string Hash(byte[] gray, FrameCrop crop) =>
        PerceptualFrameFingerprint.ComputeDHashHex(FramePreparation.Resample(gray, Width, Height, crop, PerceptualFrameFingerprint.Width, PerceptualFrameFingerprint.Height));

    private static FrameCrop Inside(FrameCrop bars, double share)
    {
        var center = FramePreparation.Center(Width - bars.Left - bars.Right, Height - bars.Top - bars.Bottom, share);
        return new FrameCrop(bars.Left + center.Left, bars.Top + center.Top, bars.Right + center.Right, bars.Bottom + center.Bottom);
    }
}
