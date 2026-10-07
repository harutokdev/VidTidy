namespace LocalMediaAI.Infrastructure;

/// <summary>
/// Which files the library picks up. The bundled ffmpeg is a full build that decodes practically every camera, phone,
/// camcorder, disc and web format, so this list decides which files are media, not which codecs work.
/// </summary>
public static class MediaFormats
{
    public static IReadOnlySet<string> VideoExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".qt",          // phones, cameras, Apple
        ".mkv", ".webm",                         // Matroska / web
        ".avi", ".divx",                         // older PC video
        ".wmv", ".asf",                          // Windows Media
        ".flv", ".f4v",                          // Flash video
        ".3gp", ".3g2",                          // older mobile phones
        ".mts", ".m2ts", ".ts",                  // AVCHD camcorders, TV and Blu-ray transport streams
        ".mpg", ".mpeg", ".m2v", ".vob",         // MPEG-1/2, DVD
        ".ogv", ".dv", ".mxf", ".rm", ".rmvb"   // Ogg, DV tape, broadcast cameras, RealMedia
    };

    public static IReadOnlySet<string> ImageExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff"
    };

    /// <summary>Bytes of the file head <see cref="LooksLikeTransportStream"/> needs.</summary>
    public const int TransportStreamProbeBytes = 3 * 192;

    public static bool IsImage(string? extension) => extension is not null && ImageExtensions.Contains(extension);

    public static bool IsMediaExtension(string? extension) =>
        extension is not null && (VideoExtensions.Contains(extension) || ImageExtensions.Contains(extension));

    /// <summary>
    /// ".ts" and ".mts" are also TypeScript source extensions, so such a file counts as video only when its content
    /// is an MPEG transport stream (camcorder .mts files are 192-byte-packet streams and pass).
    /// </summary>
    public static bool NeedsContentCheck(string extension) =>
        string.Equals(extension, ".ts", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".mts", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the head holds three packets of an MPEG transport stream: sync byte 0x47 every 188 bytes, or every
    /// 192 bytes after a 4-byte timestamp (the camcorder / Blu-ray variant).
    /// </summary>
    public static bool LooksLikeTransportStream(ReadOnlySpan<byte> head) =>
        HasSyncBytes(head, offset: 0, packet: 188) || HasSyncBytes(head, offset: 4, packet: 192);

    private static bool HasSyncBytes(ReadOnlySpan<byte> head, int offset, int packet)
    {
        for (var index = 0; index < 3; index++)
        {
            var position = offset + index * packet;
            if (position >= head.Length || head[position] != 0x47) return false;
        }
        return true;
    }
}
