namespace LocalMediaAI.Core;

/// <summary>The part of a frame to fingerprint, in pixels from each edge.</summary>
public readonly record struct FrameCrop(int Left, int Top, int Right, int Bottom)
{
    public static FrameCrop None { get; } = new(0, 0, 0, 0);
    public bool IsNone => Left == 0 && Top == 0 && Right == 0 && Bottom == 0;
}

/// <summary>
/// Prepares a small grey frame before fingerprinting: finds black bars (letterbox, pillarbox) so the same picture with
/// and without bars gives the same fingerprint, and resizes any part of the frame by area averaging. Pure, no I/O.
/// </summary>
public static class FramePreparation
{
    /// <summary>Size of the grey frame taken for preparation: large enough to see bars, small enough to stay cheap.</summary>
    public const int SampleWidth = 96;
    public const int SampleHeight = 54;

    /// <summary>A row or column is part of a bar when no pixel in it is brighter than this.</summary>
    public const int BarBrightness = 24;

    /// <summary>Bars are never taken to cover more than this share of the frame on one side.</summary>
    public const double MaximumBarShare = 0.25;

    /// <summary>
    /// Bars at the edges of one frame. Bars come in pairs of equal width (letterbox top and bottom, pillarbox left and
    /// right), so each pair keeps the narrower side. Darkness reaching a quarter of the frame is a dark picture, not a bar.
    /// </summary>
    public static FrameCrop DetectBlackBorders(ReadOnlySpan<byte> gray, int width, int height)
    {
        Validate(gray, width, height);
        var maxRows = (int)(height * MaximumBarShare);
        var maxColumns = (int)(width * MaximumBarShare);
        var top = 0;
        while (top < maxRows && RowIsDark(gray, width, top)) top++;
        var bottom = 0;
        while (bottom < maxRows && RowIsDark(gray, width, height - 1 - bottom)) bottom++;
        var left = 0;
        while (left < maxColumns && ColumnIsDark(gray, width, height, left)) left++;
        var right = 0;
        while (right < maxColumns && ColumnIsDark(gray, width, height, width - 1 - right)) right++;
        var rows = top >= maxRows || bottom >= maxRows ? 0 : Math.Min(top, bottom);
        var columns = left >= maxColumns || right >= maxColumns ? 0 : Math.Min(left, right);
        return new FrameCrop(columns, rows, columns, rows);
    }

    /// <summary>
    /// Bars that every frame shows. A dark scene can look like a bar in one frame; real bars are in all of them,
    /// so the smallest crop on each side is kept.
    /// </summary>
    public static FrameCrop Common(IEnumerable<FrameCrop> crops)
    {
        ArgumentNullException.ThrowIfNull(crops);
        var list = crops.ToArray();
        return list.Length == 0
            ? FrameCrop.None
            : new FrameCrop(list.Min(c => c.Left), list.Min(c => c.Top), list.Min(c => c.Right), list.Min(c => c.Bottom));
    }

    /// <summary>The middle part of the frame covering <paramref name="share"/> of its width and height.</summary>
    public static FrameCrop Center(int width, int height, double share)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(share, 0d);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(share, 1d);
        var x = (int)Math.Round(width * (1 - share) / 2);
        var y = (int)Math.Round(height * (1 - share) / 2);
        return new FrameCrop(x, y, x, y);
    }

    /// <summary>Area-averages the cropped part of a grey frame down to <paramref name="outWidth"/> × <paramref name="outHeight"/>.</summary>
    /// <remarks>Rounds to whole levels like FFmpeg does: flat areas then compare as equal, which keeps fingerprints of smooth pictures stable.</remarks>
    public static byte[] Resample(ReadOnlySpan<byte> gray, int width, int height, FrameCrop crop, int outWidth, int outHeight)
    {
        Validate(gray, width, height);
        ArgumentOutOfRangeException.ThrowIfLessThan(outWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(outHeight, 1);
        var cropWidth = width - crop.Left - crop.Right;
        var cropHeight = height - crop.Top - crop.Bottom;
        if (crop.Left < 0 || crop.Top < 0 || crop.Right < 0 || crop.Bottom < 0 || cropWidth < 1 || cropHeight < 1)
            throw new ArgumentOutOfRangeException(nameof(crop), "The crop leaves no picture.");
        var result = new byte[outWidth * outHeight];
        var stepX = (double)cropWidth / outWidth;
        var stepY = (double)cropHeight / outHeight;
        for (var oy = 0; oy < outHeight; oy++)
        {
            var y0 = crop.Top + oy * stepY;
            var y1 = y0 + stepY;
            for (var ox = 0; ox < outWidth; ox++)
            {
                var x0 = crop.Left + ox * stepX;
                var x1 = x0 + stepX;
                double sum = 0, area = 0;
                for (var y = (int)y0; y < Math.Min(height, (int)Math.Ceiling(y1)); y++)
                {
                    var wy = Math.Min(y + 1, y1) - Math.Max(y, y0);
                    if (wy <= 0) continue;
                    for (var x = (int)x0; x < Math.Min(width, (int)Math.Ceiling(x1)); x++)
                    {
                        var wx = Math.Min(x + 1, x1) - Math.Max(x, x0);
                        if (wx <= 0) continue;
                        sum += gray[y * width + x] * wx * wy;
                        area += wx * wy;
                    }
                }
                result[oy * outWidth + ox] = (byte)Math.Clamp((int)Math.Round(sum / area), 0, 255);
            }
        }
        return result;
    }

    private static bool RowIsDark(ReadOnlySpan<byte> gray, int width, int row)
    {
        foreach (var value in gray.Slice(row * width, width))
            if (value > BarBrightness) return false;
        return true;
    }

    private static bool ColumnIsDark(ReadOnlySpan<byte> gray, int width, int height, int column)
    {
        for (var y = 0; y < height; y++)
            if (gray[y * width + column] > BarBrightness) return false;
        return true;
    }

    private static void Validate(ReadOnlySpan<byte> gray, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (gray.Length != width * height) throw new ArgumentException($"Expected {width * height} grey bytes.", nameof(gray));
    }
}
