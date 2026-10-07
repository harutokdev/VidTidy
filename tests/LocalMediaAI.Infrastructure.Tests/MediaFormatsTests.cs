using System.Diagnostics;
using System.Text;
using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class MediaFormatsTests : IAsyncLifetime
{
    private const string Source = "testsrc2=size=160x96:rate=25";

    // Extension, extra ffmpeg output arguments, source filter override. Every video extension the scanner accepts is
    // produced here with a codec typical for it and then run through probe, thumbnail and frame extraction.
    public static TheoryData<string, string[], string?> Formats { get; } = new()
    {
        { ".mp4", ["-c:v", "libx264", "-pix_fmt", "yuv420p"], null },
        { ".m4v", ["-c:v", "libx264", "-pix_fmt", "yuv420p"], null },
        { ".mov", ["-c:v", "libx264", "-pix_fmt", "yuv420p"], null },
        { ".qt", ["-c:v", "libx264", "-pix_fmt", "yuv420p", "-f", "mov"], null },
        { ".mkv", ["-c:v", "libx264", "-pix_fmt", "yuv420p"], null },
        { ".webm", ["-c:v", "libvpx-vp9", "-b:v", "0", "-crf", "45", "-deadline", "realtime"], null },
        { ".avi", ["-c:v", "mpeg4"], null },
        { ".divx", ["-c:v", "mpeg4", "-f", "avi"], null },
        { ".wmv", ["-c:v", "wmv2"], null },
        { ".asf", ["-c:v", "wmv2", "-f", "asf"], null },
        { ".flv", ["-c:v", "flv"], null },
        { ".f4v", ["-c:v", "libx264", "-pix_fmt", "yuv420p", "-f", "f4v"], null },
        { ".3gp", ["-c:v", "libx264", "-pix_fmt", "yuv420p"], null },
        { ".3g2", ["-c:v", "libx264", "-pix_fmt", "yuv420p", "-f", "3g2"], null },
        { ".mts", ["-c:v", "libx264", "-pix_fmt", "yuv420p", "-f", "mpegts", "-mpegts_m2ts_mode", "1"], null },
        { ".m2ts", ["-c:v", "libx264", "-pix_fmt", "yuv420p", "-f", "mpegts", "-mpegts_m2ts_mode", "1"], null },
        { ".ts", ["-c:v", "libx264", "-pix_fmt", "yuv420p", "-f", "mpegts"], null },
        { ".mpg", ["-c:v", "mpeg1video"], null },
        { ".mpeg", ["-c:v", "mpeg2video", "-f", "mpeg"], null },
        { ".m2v", ["-c:v", "mpeg2video", "-f", "mpeg2video"], null },
        { ".vob", ["-c:v", "mpeg2video", "-f", "vob"], null },
        { ".ogv", ["-c:v", "libtheora"], null },
        { ".dv", ["-c:v", "dvvideo", "-pix_fmt", "yuv411p", "-f", "dv"], "testsrc2=size=720x480:rate=30000/1001" },
        { ".mxf", ["-c:v", "mpeg2video", "-f", "mxf"], null },
        { ".rm", ["-c:v", "rv10", "-f", "rm"], null },
        { ".rmvb", ["-c:v", "rv20", "-f", "rm"], null },
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "LocalMediaAIMediaFormatsTests", Guid.NewGuid().ToString("N"));
    private AppPaths _paths = null!;
    private FfmpegPaths _tools = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "portable.flag"), string.Empty);
        _paths = AppPaths.Create(_root);
        _paths.EnsureDirectories();
        _tools = ResolveBundledFfmpeg();
        Assert.NotNull(_tools.FfmpegPath);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return Task.CompletedTask;
    }

    [Fact]
    public void EveryVideoExtensionIsCoveredByTheDecodeTest()
    {
        var tested = Formats.Select(row => (string)row[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.All(MediaFormats.VideoExtensions, extension => Assert.Contains(extension, tested));
    }

    [Theory]
    [InlineData(".MP4", true)]
    [InlineData(".Mts", true)]
    [InlineData(".wmv", true)]
    [InlineData(".JPG", true)]
    [InlineData(".txt", false)]
    [InlineData(".srt", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void MediaExtensionsIgnoreCase(string? extension, bool expected) =>
        Assert.Equal(expected, MediaFormats.IsMediaExtension(extension));

    [Theory]
    [InlineData(188, 0, true)]
    [InlineData(192, 4, true)]
    [InlineData(188, 1, false)]
    [InlineData(100, 0, false)]
    public void TransportStreamIsRecognisedBySyncBytes(int packet, int offset, bool expected)
    {
        var head = new byte[MediaFormats.TransportStreamProbeBytes];
        for (var index = 0; index < 3; index++) head[offset + index * packet] = 0x47;

        Assert.Equal(expected, MediaFormats.LooksLikeTransportStream(head));
    }

    [Fact]
    public void TypeScriptSourceIsNotATransportStream()
    {
        var source = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("export const value: number = 1;\n", 40)));

        Assert.False(MediaFormats.LooksLikeTransportStream(source));
        Assert.False(MediaFormats.LooksLikeTransportStream([]));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task EachFormatIsProbedThumbnailedAndSampled(string extension, string[] output, string? sourceOverride)
    {
        var path = await GenerateAsync("clip" + extension, output, sourceOverride);
        var media = CreateMedia(path);
        var adapter = new FfmpegAdapter(_tools, _paths, new NullLogger(), TimeSpan.FromSeconds(60));

        var probe = await adapter.ProbeAsync(media, CancellationToken.None);
        var thumbnail = await adapter.GenerateThumbnailAsync(media, "th" + Guid.NewGuid().ToString("N"), CancellationToken.None);
        var frame = await adapter.ExtractFrameAsync(media, 1.0, "vf" + Guid.NewGuid().ToString("N"), CancellationToken.None);

        Assert.True(probe.Metadata.Width > 0, "no video width for " + extension);
        Assert.True(probe.Metadata.Height > 0, "no video height for " + extension);
        Assert.True(File.Exists(thumbnail.FullPath) && thumbnail.ByteSize > 0, "no thumbnail for " + extension);
        Assert.True(File.Exists(frame.FullPath) && frame.ByteSize > 0, "no visual frame for " + extension);
    }

    [Fact]
    public async Task DamagedStreamFramesComeFromTheNextIntactKeyframe()
    {
        // The bundled build's libtheora output decodes with many errors in ffmpeg's own decoder, so ffmpeg gives up on
        // the decode error rate: a ready-made damaged stream. Only its keyframes decode cleanly. (If a later ffmpeg
        // fixes the encoder this still passes, through the normal path.)
        var path = await GenerateAsync("damaged.ogv", ["-c:v", "libtheora", "-q:v", "6"], "testsrc2=size=320x180:rate=30", seconds: 12);
        var media = CreateMedia(path);
        var adapter = new FfmpegAdapter(_tools, _paths, new NullLogger(), TimeSpan.FromSeconds(60));

        var thumbnail = await adapter.GenerateThumbnailAsync(media, "th" + Guid.NewGuid().ToString("N"), CancellationToken.None);
        var visual = await adapter.ExtractFrameAsync(media, 5.3, "vf" + Guid.NewGuid().ToString("N"), CancellationToken.None);
        var near = await adapter.ExtractNearDuplicateFrameAsync(media, 0, 0.5, 5.3, "nd" + Guid.NewGuid().ToString("N"), CancellationToken.None);

        Assert.True(thumbnail.ByteSize > 0);
        Assert.InRange(visual.PresentationTimestampSeconds, 5.3, 8.0);
        Assert.InRange(near.PresentationTimestampSeconds, 5.3, 8.0);
        Assert.False(string.IsNullOrEmpty(near.FingerprintHex));
    }

    [Fact]
    public void KeyframesOnlyIsAnInputOption()
    {
        var arguments = FfmpegAdapter.BuildFrameExtractionArguments("in.ogv", "5.000000", "out.jpg", null, keyframesOnly: true).ToList();

        Assert.True(arguments.IndexOf("-skip_frame") < arguments.IndexOf("-i"));
        Assert.Equal("nokey", arguments[arguments.IndexOf("-skip_frame") + 1]);
        Assert.DoesNotContain("-skip_frame", FfmpegAdapter.BuildFrameExtractionArguments("in.mp4", "5.000000", "out.jpg", null));
    }

    [Fact]
    public async Task ScannerPicksUpEveryFormatButSkipsTypeScriptAndOtherFiles()
    {
        var folder = Path.Combine(_root, "library");
        Directory.CreateDirectory(folder);
        await GenerateAsync(Path.Combine("library", "camcorder.mts"), ["-c:v", "libx264", "-pix_fmt", "yuv420p", "-f", "mpegts", "-mpegts_m2ts_mode", "1"], null);
        await GenerateAsync(Path.Combine("library", "recording.ts"), ["-c:v", "libx264", "-pix_fmt", "yuv420p", "-f", "mpegts"], null);
        await GenerateAsync(Path.Combine("library", "old.wmv"), ["-c:v", "wmv2"], null);
        await File.WriteAllTextAsync(Path.Combine(folder, "app.ts"), "export const value: number = 1;\n");
        await File.WriteAllTextAsync(Path.Combine(folder, "module.mts"), "export default function main(): void {}\n");
        await File.WriteAllTextAsync(Path.Combine(folder, "notes.txt"), "not media");
        await File.WriteAllTextAsync(Path.Combine(folder, "clip.srt"), "1\n00:00:00,000 --> 00:00:01,000\nhi\n");
        var root = new LibraryRoot(Guid.NewGuid(), folder, PathPolicy.NormalizeForIdentity(folder), "library", null, RootAvailability.Online, DateTimeOffset.UtcNow, null);

        var names = new List<string>();
        await foreach (var file in new FileSystemMediaScanner().EnumerateAsync(root, CancellationToken.None)) names.Add(file.FileName);

        Assert.Equal(["camcorder.mts", "old.wmv", "recording.ts"], names.Order(StringComparer.Ordinal));
    }

    private async Task<string> GenerateAsync(string relativePath, string[] output, string? sourceOverride, int seconds = 2)
    {
        var path = Path.Combine(_root, relativePath);
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i", sourceOverride ?? Source, "-t", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        arguments.AddRange(output);
        arguments.Add(path);
        var start = new ProcessStartInfo(_tools.FfmpegPath!) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg did not start.");
        var error = process.StandardError.ReadToEndAsync();
        await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(process.ExitCode == 0, relativePath + ": " + await error);
        return path;
    }

    private static MediaFile CreateMedia(string path)
    {
        var info = new FileInfo(path);
        var now = DateTimeOffset.UtcNow;
        return new MediaFile(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), path, PathPolicy.NormalizeForIdentity(path), info.Name, info.Extension,
            info.Length, new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero), new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            null, null, null, null, PresenceState.Present, now, now);
    }

    private static FfmpegPaths ResolveBundledFfmpeg()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var runtime = Path.Combine(current.FullName, "runtime", "ffmpeg");
            var ffmpeg = Path.Combine(runtime, "ffmpeg.exe");
            var ffprobe = Path.Combine(runtime, "ffprobe.exe");
            if (File.Exists(ffmpeg) && File.Exists(ffprobe)) return new FfmpegPaths(ffmpeg, ffprobe);
        }
        return new FfmpegPaths(null, null);
    }

    private sealed class NullLogger : IStructuredLogger
    {
        public void Write(string level, string eventName, object fields, Exception? exception = null) { }
    }
}
