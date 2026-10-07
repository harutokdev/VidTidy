using System.Diagnostics;
using System.Text.Json;
using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class HdrToneMappingTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LocalMediaAIHdrToneMappingTests", Guid.NewGuid().ToString("N"));
    private AppPaths _paths = null!;
    private FfmpegPaths _tools = null!;
    private OperationCounter _log = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "portable.flag"), string.Empty);
        _paths = AppPaths.Create(_root);
        _paths.EnsureDirectories();
        _tools = ResolveBundledFfmpeg();
        _log = new OperationCounter();
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

    [Theory]
    [InlineData("smpte2084", true)]
    [InlineData("arib-std-b67", true)]
    [InlineData("arib-std-b67\r\n", true)]
    [InlineData("bt709", false)]
    [InlineData("unknown", false)]
    [InlineData(null, false)]
    public void PqAndHlgAreHdr(string? transfer, bool expected) =>
        Assert.Equal(expected, HdrToneMapping.IsHdrTransfer(transfer));

    [Fact]
    public void SdrFrameArgumentsAreUnchanged()
    {
        Assert.Equal(
            ["-hide_banner", "-nostdin", "-y", "-ss", "1.000000", "-copyts", "-i", "in.mp4", "-frames:v", "1", "-an", "-vf", "showinfo,scale='min(1024,iw)':-2", "-q:v", "3", "out.jpg"],
            FfmpegAdapter.BuildFrameExtractionArguments("in.mp4", "1.000000", "out.jpg", null));
        Assert.Contains("[0:v]showinfo,split=2[jpeg-source][dhash-source];[jpeg-source]scale='min(1024,iw)':-2[jpeg];[dhash-source]scale=9:8:flags=area,format=gray[dhash]",
            FfmpegAdapter.BuildFrameExtractionArguments("in.mp4", "1.000000", "out.jpg", "out.raw"));
    }

    [Fact]
    public void HdrFramesAreToneMappedAfterScalingForBothOutputs()
    {
        var visual = FfmpegAdapter.BuildFrameExtractionArguments("in.mp4", "1.000000", "out.jpg", null, hdr: true);
        var near = FfmpegAdapter.BuildFrameExtractionArguments("in.mp4", "1.000000", "out.jpg", "out.raw", hdr: true);

        Assert.Contains("showinfo,scale='min(1024,iw)':-2," + HdrToneMapping.Filter, visual);
        Assert.Contains(near, argument => argument.StartsWith("[0:v]showinfo,scale='min(1024,iw)':-2," + HdrToneMapping.Filter + ",split=2[jpeg][dhash-source]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HdrSourceIsProbedOnceAndItsFramesAreToneMapped()
    {
        var path = await GenerateAsync("hdr.mp4", hdr: true);
        var media = CreateMedia(path);
        var adapter = new FfmpegAdapter(_tools, _paths, _log, TimeSpan.FromSeconds(60));

        var thumbnail = await adapter.GenerateThumbnailAsync(media, "th" + Guid.NewGuid().ToString("N"), CancellationToken.None);
        var first = await adapter.ExtractFrameAsync(media, 0.5, "vf" + Guid.NewGuid().ToString("N"), CancellationToken.None);
        var near = await adapter.ExtractNearDuplicateFrameAsync(media, 2, 0.5, 1.0, "nd" + Guid.NewGuid().ToString("N"), CancellationToken.None);
        var untouched = await ExtractWithoutToneMappingAsync(path, 0.5);

        Assert.Equal(1, _log.Count("ffprobe-color-transfer"));
        Assert.True(thumbnail.ByteSize > 0);
        Assert.Equal(16, near.FingerprintHex.Length);
        Assert.NotEqual(untouched, await File.ReadAllBytesAsync(first.FullPath));
    }

    [Fact]
    public async Task MetadataProbeAnswersTheQuestionWithoutAnotherProbe()
    {
        var path = await GenerateAsync("hdr-probed.mp4", hdr: true);
        var media = CreateMedia(path);
        var adapter = new FfmpegAdapter(_tools, _paths, _log, TimeSpan.FromSeconds(60));

        await adapter.ProbeAsync(media, CancellationToken.None);
        await adapter.GenerateThumbnailAsync(media, "th" + Guid.NewGuid().ToString("N"), CancellationToken.None);

        Assert.Equal(0, _log.Count("ffprobe-color-transfer"));
    }

    [Fact]
    public async Task HdrSourceWithoutColourDetailsFallsBackToTheUntouchedFrame()
    {
        // Tagged PQ but with no primaries or matrix: zscale finds "no path between colorspaces". The untouched frame
        // is still usable, so the file must not fail.
        var path = Path.Combine(_root, "pq-only.mp4");
        await RunAsync(["-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25", "-t", "2",
            "-vf", "setparams=color_trc=smpte2084", "-c:v", "libx265", "-x265-params", "log-level=none:transfer=smpte2084", "-pix_fmt", "yuv420p10le", path]);
        var media = CreateMedia(path);
        var adapter = new FfmpegAdapter(_tools, _paths, _log, TimeSpan.FromSeconds(60));

        var thumbnail = await adapter.GenerateThumbnailAsync(media, "th" + Guid.NewGuid().ToString("N"), CancellationToken.None);
        var frame = await adapter.ExtractFrameAsync(media, 0.5, "vf" + Guid.NewGuid().ToString("N"), CancellationToken.None);
        var near = await adapter.ExtractNearDuplicateFrameAsync(media, 2, 0.5, 1.0, "nd" + Guid.NewGuid().ToString("N"), CancellationToken.None);

        Assert.True(thumbnail.ByteSize > 0);
        Assert.True(frame.ByteSize > 0);
        Assert.Equal(16, near.FingerprintHex.Length);
        Assert.Equal(3, _log.Events("HdrToneMappingFailed"));
    }

    [Fact]
    public async Task SdrSourceFramesAreByteIdenticalToTheUntouchedExtraction()
    {
        var path = await GenerateAsync("sdr.mp4", hdr: false);
        var adapter = new FfmpegAdapter(_tools, _paths, _log, TimeSpan.FromSeconds(60));

        var frame = await adapter.ExtractFrameAsync(CreateMedia(path), 0.5, "vf" + Guid.NewGuid().ToString("N"), CancellationToken.None);

        Assert.Equal(await ExtractWithoutToneMappingAsync(path, 0.5), await File.ReadAllBytesAsync(frame.FullPath));
    }

    private async Task<byte[]> ExtractWithoutToneMappingAsync(string path, double seconds)
    {
        var output = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".jpg");
        await RunAsync(FfmpegAdapter.BuildFrameExtractionArguments(path, seconds.ToString("F6", System.Globalization.CultureInfo.InvariantCulture), output, null));
        return await File.ReadAllBytesAsync(output);
    }

    private async Task<string> GenerateAsync(string name, bool hdr)
    {
        var path = Path.Combine(_root, name);
        List<string> arguments = ["-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25", "-t", "2"];
        arguments.AddRange(hdr
            ? ["-vf", "setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc", "-c:v", "libx265",
                "-x265-params", "log-level=none:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc", "-pix_fmt", "yuv420p10le"]
            : ["-c:v", "libx264", "-pix_fmt", "yuv420p"]);
        arguments.Add(path);
        await RunAsync(arguments);
        return path;
    }

    private async Task RunAsync(IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(_tools.FfmpegPath!) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg did not start.");
        var error = process.StandardError.ReadToEndAsync();
        await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(process.ExitCode == 0, await error);
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

    private sealed class OperationCounter : IStructuredLogger
    {
        private readonly List<string> _finished = [];
        private readonly List<string> _events = [];

        public void Write(string level, string eventName, object fields, Exception? exception = null)
        {
            lock (_events) _events.Add(eventName);
            if (eventName != "MediaProcessFinished") return;
            lock (_finished) _finished.Add(JsonSerializer.Serialize(fields));
        }

        public int Events(string eventName)
        {
            lock (_events) return _events.Count(name => name == eventName);
        }

        public int Count(string operation)
        {
            lock (_finished) return _finished.Count(fields => fields.Contains($"\"Operation\":\"{operation}\"", StringComparison.Ordinal));
        }
    }
}
