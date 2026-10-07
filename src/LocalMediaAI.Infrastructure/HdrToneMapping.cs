using System.Collections.Concurrent;
using LocalMediaAI.Core;

namespace LocalMediaAI.Infrastructure;

/// <summary>
/// HDR video (PQ or HLG over BT.2020, as recent phones record) looks faded and tinted when a frame is saved as an
/// ordinary JPEG, which spoils both the thumbnail and what the image model sees. Such sources are tone-mapped to SDR
/// BT.709 after scaling down (the order makes no visible difference and the float conversion runs on fewer pixels).
/// SDR sources keep exactly the filter chain they always had.
/// </summary>
internal static class HdrToneMapping
{
    public const string Filter = "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p";

    public static bool IsHdrTransfer(string? transfer) =>
        transfer?.Trim() is "smpte2084" or "arib-std-b67";

    /// <summary>Appends the tone map to <paramref name="chain"/> for an HDR source; returns it unchanged otherwise.</summary>
    public static string Apply(string chain, bool hdr) => hdr ? chain + "," + Filter : chain;
}

/// <summary>
/// Whether each source is HDR, so the thumbnail and every sampled frame of a file do not probe it again. Keyed by
/// path, size and write time; bounded, since a pass only revisits the files it is working on.
/// </summary>
internal sealed class SourceTransferCache
{
    private const int Capacity = 512;
    private readonly ConcurrentDictionary<string, bool> _hdr = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGet(MediaFile file, out bool hdr) => _hdr.TryGetValue(Key(file), out hdr);

    public void Set(MediaFile file, bool hdr)
    {
        if (_hdr.Count >= Capacity) _hdr.Clear();
        _hdr[Key(file)] = hdr;
    }

    private static string Key(MediaFile file) => $"{file.Path}|{file.SizeBytes}|{file.LastWriteTimeUtc.UtcTicks}";
}
