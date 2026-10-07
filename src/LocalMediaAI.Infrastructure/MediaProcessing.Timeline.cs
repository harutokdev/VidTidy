using System.Globalization;
using LocalMediaAI.Core;

namespace LocalMediaAI.Infrastructure;

/// <summary>A grey frame read from a video at a moment (seconds from its start).</summary>
public sealed record TimelineGrayFrame(double Seconds, byte[] Gray);

/// <summary>
/// Frames along a whole video for the timeline comparison (<see cref="TimelineMatching"/>): small grey rasters only, no
/// images are kept. Reads go through the protected source access like every other read of an original.
/// </summary>
public sealed partial class FfmpegAdapter
{
    private const int TimelineWidth = NearDuplicatePreparation.Width;
    private const int TimelineHeight = NearDuplicatePreparation.Height;
    private const int TimelineFramesPerRun = 24;
    private const double KeyframeProbeSeconds = 20;

    /// <summary>
    /// Typical spacing of keyframes, from the first 20 seconds of packets (only that part of the file is read). One
    /// keyframe or none in that stretch counts as 20 seconds or more. Null when it cannot be read.
    /// </summary>
    public async Task<double?> ProbeKeyframeSpacingAsync(ProcessingSourceAccess source, MediaFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.EnsureMatches(file);
        if (_paths.FfprobePath is null) return null;
        EnsureReadOnlyInput(file.Path);
        var result = await RunAsync(_paths.FfprobePath, ["-v", "error", "-select_streams", "v:0", "-read_intervals", "%+" + KeyframeProbeSeconds.ToString(CultureInfo.InvariantCulture),
            "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", file.Path], "ffprobe-keyframe-spacing", cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 ? ParseKeyframeSpacing(result.StandardOutput) : null;
    }

    internal static double? ParseKeyframeSpacing(string packets)
    {
        var keyframes = new List<double>();
        foreach (var line in packets.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',');
            if (parts.Length >= 2 && parts[1].StartsWith('K') && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                keyframes.Add(seconds);
        }
        if (keyframes.Count == 0 && packets.Trim().Length == 0) return null;
        if (keyframes.Count < 2) return KeyframeProbeSeconds;
        var gaps = keyframes.Order().Zip(keyframes.Order().Skip(1), (a, b) => b - a).Where(gap => gap > 0).Order().ToArray();
        return gaps.Length == 0 ? KeyframeProbeSeconds : gaps[gaps.Length / 2];
    }

    /// <summary>
    /// Frames at the given moments, many per FFmpeg run (one input per moment, seeking to each). A run that fails is
    /// retried frame by frame; a moment that cannot be read is left out.
    /// </summary>
    public async Task<IReadOnlyList<TimelineGrayFrame>> ReadFramesAtAsync(ProcessingSourceAccess source, MediaFile file, IReadOnlyList<double> seconds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(seconds);
        source.EnsureMatches(file);
        if (_paths.FfmpegPath is null) throw new MediaToolException(DomainError.FfmpegNotFound);
        EnsureReadOnlyInput(file.Path);
        var hdr = await IsHdrSourceAsync(file, cancellationToken).ConfigureAwait(false);
        var frames = new List<TimelineGrayFrame>();
        foreach (var run in seconds.Where(t => double.IsFinite(t) && t >= 0).Chunk(TimelineFramesPerRun))
        {
            // An HDR tone map that this FFmpeg cannot do is retried without it, as the other frame reads do.
            var read = await ReadRunAsync(file.Path, run, hdr, cancellationToken).ConfigureAwait(false);
            if (read is null && hdr && (read = await ReadRunAsync(file.Path, run, false, cancellationToken).ConfigureAwait(false)) is not null) hdr = false;
            if (read is null)
                foreach (var t in run)
                    if (await ReadRunAsync(file.Path, [t], hdr, cancellationToken).ConfigureAwait(false) is [var single]) frames.Add(single);
                    else _log.Write("Information", "TimelineFrameUnreadable", new { file.MediaFileId, Seconds = t });
            else frames.AddRange(read);
        }
        return frames;
    }

    /// <summary>
    /// Frames at a steady rate from <paramref name="fromSeconds"/> to <paramref name="toSeconds"/>, decoded in one pass
    /// (in runs of at most ten minutes, one after another, so the disk reads forward).
    /// </summary>
    public async Task<IReadOnlyList<TimelineGrayFrame>> ReadStretchAsync(ProcessingSourceAccess source, MediaFile file, double fromSeconds, double toSeconds, double framesPerSecond, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.EnsureMatches(file);
        if (_paths.FfmpegPath is null) throw new MediaToolException(DomainError.FfmpegNotFound);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(framesPerSecond, 0d);
        EnsureReadOnlyInput(file.Path);
        var hdr = await IsHdrSourceAsync(file, cancellationToken).ConfigureAwait(false);
        var frames = new List<TimelineGrayFrame>();
        var step = 1 / framesPerSecond;
        // Each run starts on the frame grid, so runs join without a gap or a repeat. round=up makes output frame k the
        // frame showing at start + k/fps; the default rounding gives the one half a step earlier.
        var perRun = Math.Max(1, (int)Math.Floor(TimelineSamplingPolicy.SequentialChunkSeconds / step));
        for (var start = Math.Max(0, fromSeconds); start < toSeconds; start += perRun * step)
        {
            var length = Math.Min(perRun * step, toSeconds - start);
            var rawPath = TimelineRawPath();
            try
            {
                var result = await RunAsync(_paths.FfmpegPath, ["-hide_banner", "-nostdin", "-loglevel", "error", "-y",
                    "-ss", start.ToString("F3", CultureInfo.InvariantCulture), "-t", length.ToString("F3", CultureInfo.InvariantCulture), "-i", file.Path,
                    "-an", "-sn", "-vf", $"fps=fps={framesPerSecond.ToString(CultureInfo.InvariantCulture)}:round=up," + GrayChain(hdr), "-f", "rawvideo", rawPath],
                    "ffmpeg-timeline-stretch", cancellationToken).ConfigureAwait(false);
                if (result.ExitCode != 0 || !File.Exists(rawPath)) return frames;
                var bytes = await File.ReadAllBytesAsync(rawPath, cancellationToken).ConfigureAwait(false);
                var count = Math.Min(bytes.Length / (TimelineWidth * TimelineHeight), (int)Math.Ceiling(length / step - 1e-9));
                for (var i = 0; i < count; i++)
                    frames.Add(new TimelineGrayFrame(start + i * step, bytes.AsSpan(i * TimelineWidth * TimelineHeight, TimelineWidth * TimelineHeight).ToArray()));
            }
            finally
            {
                DeleteIfExists(rawPath);
            }
        }
        return frames;
    }

    /// <summary>
    /// The video's timeline frames at <see cref="TimelineSamplingPolicy"/>'s moments: frame by frame, or in one forward
    /// pass when frame by frame would read the file more than once. A pass that fails falls back to frame by frame.
    /// </summary>
    public async Task<(IReadOnlyList<TimelineGrayFrame> Frames, bool Sequential)> ReadTimelineAsync(ProcessingSourceAccess source, MediaFile file, double durationSeconds, CancellationToken cancellationToken)
    {
        var times = TimelineSamplingPolicy.Times(durationSeconds);
        if (times.Count == 0) return ([], false);
        var interval = TimelineSamplingPolicy.Interval(durationSeconds);
        if (TimelineSamplingPolicy.ReadSequentially(await ProbeKeyframeSpacingAsync(source, file, cancellationToken).ConfigureAwait(false), interval))
        {
            var stretch = await ReadStretchAsync(source, file, times[0], durationSeconds, 1 / interval, cancellationToken).ConfigureAwait(false);
            if (stretch.Count >= times.Count * 9 / 10) return (stretch.Take(times.Count).ToArray(), true);
            _log.Write("Information", "TimelineSequentialReadFellBack", new { file.MediaFileId, Read = stretch.Count, Expected = times.Count });
        }
        return (await ReadFramesAtAsync(source, file, times, cancellationToken).ConfigureAwait(false), false);
    }

    private async Task<IReadOnlyList<TimelineGrayFrame>?> ReadRunAsync(string path, IReadOnlyList<double> seconds, bool hdr, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-y" };
        foreach (var t in seconds) arguments.AddRange(["-ss", t.ToString("F3", CultureInfo.InvariantCulture), "-i", path]);
        // Stacked into one tall picture: every input gives exactly its first frame, in order.
        var graph = seconds.Count == 1
            ? "[0:v]trim=end_frame=1," + GrayChain(hdr) + "[frames]"
            : string.Concat(seconds.Select((_, i) => $"[{i}:v]trim=end_frame=1,{GrayChain(hdr)},setsar=1[f{i}];")) + string.Concat(seconds.Select((_, i) => $"[f{i}]")) + $"vstack=inputs={seconds.Count}[frames]";
        var rawPath = TimelineRawPath();
        arguments.AddRange(["-filter_complex", graph, "-map", "[frames]", "-frames:v", "1", "-f", "rawvideo", rawPath]);
        try
        {
            var result = await RunAsync(_paths.FfmpegPath!, arguments, "ffmpeg-timeline-frames", cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0 || !File.Exists(rawPath)) return null;
            var bytes = await File.ReadAllBytesAsync(rawPath, cancellationToken).ConfigureAwait(false);
            const int size = TimelineWidth * TimelineHeight;
            if (bytes.Length != size * seconds.Count) return null;
            return seconds.Select((t, i) => new TimelineGrayFrame(t, bytes.AsSpan(i * size, size).ToArray())).ToArray();
        }
        finally
        {
            DeleteIfExists(rawPath);
        }
    }

    private static string GrayChain(bool hdr) => HdrToneMapping.Apply($"scale={TimelineWidth}:{TimelineHeight}:flags=area", hdr) + ",format=gray";

    private string TimelineRawPath()
    {
        Directory.CreateDirectory(_appPaths.TempRoot);
        return Path.Combine(_appPaths.TempRoot, "timeline-" + Guid.NewGuid().ToString("N") + ".raw");
    }
}
