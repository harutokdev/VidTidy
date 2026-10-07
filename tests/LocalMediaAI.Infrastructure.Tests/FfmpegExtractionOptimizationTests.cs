using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class FfmpegExtractionOptimizationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LocalMediaAIFfmpegOptimizationTests", Guid.NewGuid().ToString("N"));
    private AppPaths _paths = null!;
    private CapturingLogger _log = null!;
    private FfmpegPaths _tools = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "portable.flag"), string.Empty);
        _paths = AppPaths.Create(_root);
        _paths.EnsureDirectories();
        _log = new CapturingLogger();
        _tools = ResolveBundledFfmpeg();
        Assert.NotNull(_tools.FfmpegPath);
        Assert.NotNull(_tools.FfprobePath);
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
    public async Task StoredFrameReadsBackAsFlatOnlyWhenItIsBlackNotWhenItIsAGradient()
    {
        var folder = Path.Combine(_paths.CacheRoot, "near-duplicate");
        Directory.CreateDirectory(folder);
        async Task<byte[]?> Read(string name, string source)
        {
            var path = Path.Combine(folder, name);
            using (var p = Process.Start(new ProcessStartInfo(_tools.FfmpegPath!, ["-loglevel", "error", "-y", "-f", "lavfi", "-i", source, "-frames:v", "1", path]) { UseShellExecute = false })!)
                await p.WaitForExitAsync();
            return await new FfmpegAdapter(_tools, _paths, _log, TimeSpan.FromSeconds(30)).ReadStoredFrameGrayAsync("near-duplicate/" + name, CancellationToken.None);
        }

        var black = await Read("black.jpg", "color=c=black:size=320x180");
        var sky = await Read("sky.jpg", "gradients=size=320x180:c0=0x4060a0:c1=0xd0e0ff:x0=0:y0=0:x1=319:y1=0:nb_colors=2");
        Assert.NotNull(black);
        Assert.NotNull(sky);
        Assert.True(PerceptualFrameFingerprint.IsFlat(black));
        Assert.False(PerceptualFrameFingerprint.IsFlat(sky));
        Assert.Null(await new FfmpegAdapter(_tools, _paths, _log).ReadStoredFrameGrayAsync("near-duplicate/missing.jpg", CancellationToken.None));
    }

    [Fact]
    public async Task ExactSingleShowinfoFrameIsPrimaryAndDoesNotCallFallback()
    {
        var fallbackCalls = 0;
        var timestamp = await FfmpegAdapter.ResolvePresentationTimestampAsync(
            "[Parsed_showinfo_0 @ 0001] n:   0 pts: 123 pts_time:12.3 duration:1",
            10,
            () => { fallbackCalls++; return Task.FromResult<double?>(11.2); });

        Assert.Equal(12.3, timestamp);
        Assert.Equal(0, fallbackCalls);
    }

    [Theory]
    [InlineData("showinfo configuration only", 11.2, 11.2)]
    [InlineData("[Parsed_showinfo_0 @ 0001] n: 0 pts: 1 pts_time:not-a-number", 11.2, 11.2)]
    [InlineData("showinfo configuration only", -1, 10)]
    [InlineData("showinfo configuration only", double.NaN, 10)]
    public async Task MissingOrInvalidShowinfoCallsExactlyOneFallback(string stderr, double fallbackValue, double expected)
    {
        var fallbackCalls = 0;
        var timestamp = await FfmpegAdapter.ResolvePresentationTimestampAsync(
            stderr,
            10,
            () => { fallbackCalls++; return Task.FromResult<double?>(fallbackValue); });

        Assert.Equal(expected, timestamp);
        Assert.Equal(1, fallbackCalls);
    }

    [Fact]
    public async Task MultipleShowinfoFrameRecordsSelectTheUniqueOutputFrameNZeroRatherThanAnArbitraryLine()
    {
        const string stderr = "[Parsed_showinfo_0 @ a] n: 0 pts: 10 pts_time:1.0\n[Parsed_showinfo_0 @ a] n: 1 pts: 20 pts_time:2.0";
        var fallbackCalls = 0;
        var timestamp = await FfmpegAdapter.ResolvePresentationTimestampAsync(
            stderr,
            0.5,
            () => { fallbackCalls++; return Task.FromResult<double?>(1.25); });

        Assert.Equal(1.0, FfmpegAdapter.ParseSingleSelectedFramePresentationTimestamp(stderr));
        Assert.Equal(1.0, timestamp);
        Assert.Equal(0, fallbackCalls);
    }

    [Fact]
    public void NearArgumentsUseOneInputAndSplitOneDecodedFrameIntoExactBranches()
    {
        var arguments = FfmpegAdapter.BuildFrameExtractionArguments("source.mp4", "1.250000", "frame.jpg", "hash.raw");

        Assert.Equal(1, arguments.Count(value => value == "-i"));
        Assert.Equal(1, arguments.Count(value => value == "source.mp4"));
        var graph = arguments[arguments.IndexOf("-filter_complex") + 1];
        Assert.Contains("[0:v]showinfo,split=2[jpeg-source][dhash-source]", graph, StringComparison.Ordinal);
        Assert.Contains("[jpeg-source]scale='min(1024,iw)':-2[jpeg]", graph, StringComparison.Ordinal);
        Assert.Contains("[dhash-source]scale=9:8:flags=area,format=gray[dhash]", graph, StringComparison.Ordinal);
        Assert.DoesNotContain("frame.jpg", graph, StringComparison.Ordinal);
        Assert.Equal(2, arguments.Count(value => value == "-map"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(71)]
    [InlineData(73)]
    public void DHashOutputMustContainExactlySeventyTwoDirectPixelBytes(int length)
    {
        var error = Assert.Throws<MediaToolException>(() => FfmpegAdapter.ValidateDHashBytes(new byte[length]));
        Assert.Equal("FRAME_EXTRACTION_FAILED", error.Error.Code);
    }

    [Fact]
    public void DHashOutputAcceptsExactlySeventyTwoDirectPixelBytes() =>
        Assert.Equal(72, FfmpegAdapter.ValidateDHashBytes(new byte[72]).Length);

    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(true, 0, false)]
    [InlineData(true, 72, false)]
    [InlineData(true, 72, true)]
    [InlineData(true, 71, true)]
    public async Task NearDualOutputValidationRejectsEveryPartialCombination(bool createJpeg, int jpegBytes, bool createDHash)
    {
        var directory = Path.Combine(_root, "partial-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var jpeg = Path.Combine(directory, "frame.jpg");
        var dHash = Path.Combine(directory, "hash.raw");
        if (createJpeg) await File.WriteAllBytesAsync(jpeg, new byte[jpegBytes]);
        if (createDHash) await File.WriteAllBytesAsync(dHash, new byte[createJpeg && jpegBytes > 0 ? 71 : 72]);

        var error = await Assert.ThrowsAsync<MediaToolException>(() => FfmpegAdapter.ValidateNearOutputsAsync(jpeg, dHash, CancellationToken.None));
        Assert.Equal("FRAME_EXTRACTION_FAILED", error.Error.Code);
    }

    [Fact]
    public async Task NearDualOutputValidationAcceptsValidJpegAndExactlySeventyTwoPixels()
    {
        var directory = Path.Combine(_root, "valid-dual-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var jpeg = Path.Combine(directory, "frame.jpg");
        var dHash = Path.Combine(directory, "hash.raw");
        await File.WriteAllBytesAsync(jpeg, [1]);
        await File.WriteAllBytesAsync(dHash, new byte[72]);

        Assert.Equal(72, (await FfmpegAdapter.ValidateNearOutputsAsync(jpeg, dHash, CancellationToken.None)).Length);
    }

    [Theory]
    [InlineData("cfr", "testsrc2=size=160x90:rate=10", 2.0, 1.2)]
    [InlineData("short-end", "testsrc2=size=160x90:rate=10", 0.6, 0.49)]
    [InlineData("nonzero-origin", "testsrc2=size=160x90:rate=10,setpts=PTS+2/TB", 4.0, 2.2)]
    public async Task OneDecodeNearMatchesLegacyDirectSourceDHash(string name, string sourceFilter, double duration, double requestedTimestamp)
    {
        var path = await GenerateFixtureAsync(name, sourceFilter, duration, variableFrameRate: false);
        await AssertNearEquivalentAsync(path, requestedTimestamp);
    }

    [Fact]
    public async Task OneDecodeNearMatchesLegacyDirectSourceDHashForDeterministicVfr()
    {
        var path = await GenerateFixtureAsync(
            "vfr",
            "testsrc2=size=160x90:rate=10,setpts='if(lt(N\\,10)\\,N/(10*TB)\\,(1+(N-10)/5)/TB)'",
            2.8,
            variableFrameRate: true);
        await AssertNearEquivalentAsync(path, 1.45);
    }

    [Fact]
    public async Task VisualExtractionUsesShowinfoWithoutTimestampProbeOnNormalSuccess()
    {
        var path = await GenerateFixtureAsync("visual", "testsrc2=size=160x90:rate=10", 2, variableFrameRate: false);
        var adapter = new FfmpegAdapter(_tools, _paths, _log, TimeSpan.FromSeconds(30));
        var generated = await adapter.ExtractFrameAsync(CreateMedia(path), 1.2, "visual-" + Guid.NewGuid().ToString("N"), CancellationToken.None);

        Assert.True(File.Exists(generated.FullPath));
        Assert.InRange(generated.PresentationTimestampSeconds, 1.19, 1.21);
        Assert.Equal(1, _log.CountOperation("ffmpeg-visual-frame"));
        Assert.Equal(0, _log.CountOperation("ffprobe-visual-frame-timestamp"));
    }

    [Fact]
    public async Task VisualTimestampPastTheLastVideoFrameFallsBackToTheFinalFrames()
    {
        // Audio outlasts video, so the container duration admits a sample time with no video frame left.
        var path = await GenerateAudioTailFixtureAsync("visual-tail", videoSeconds: 2.0, audioSeconds: 2.3);
        var adapter = new FfmpegAdapter(_tools, _paths, _log, TimeSpan.FromSeconds(30));

        var generated = await adapter.ExtractFrameAsync(CreateMedia(path), 2.0, "tail-" + Guid.NewGuid().ToString("N"), CancellationToken.None);

        Assert.True(File.Exists(generated.FullPath));
        Assert.Equal(2.0, generated.RequestedTimestampSeconds);
        Assert.InRange(generated.PresentationTimestampSeconds, 0, 1.99);
        Assert.Equal(2, _log.CountOperation("ffmpeg-visual-frame"));
    }

    [Fact]
    public async Task NearTimestampPastTheLastVideoFrameFallsBackAndStillYieldsBothOutputs()
    {
        var path = await GenerateAudioTailFixtureAsync("near-tail", videoSeconds: 2.0, audioSeconds: 2.3);
        var adapter = new FfmpegAdapter(_tools, _paths, _log, TimeSpan.FromSeconds(30));

        var generated = await adapter.ExtractNearDuplicateFrameAsync(CreateMedia(path), 4, 0.95, 2.1, "near-tail-" + Guid.NewGuid().ToString("N"), CancellationToken.None);

        Assert.True(File.Exists(generated.FullPath));
        Assert.Equal(16, generated.FingerprintHex.Length);
        Assert.InRange(generated.PresentationTimestampSeconds, 0, 1.99);
    }

    [Fact]
    public async Task VeryShortClipFallsBackWhenTheLateSampleIsPastTheLastFrame()
    {
        // A 0.1 s full-range (yuvj420p, as phone cameras record) clip: six frames at 60 fps, last frame at 0.083 s.
        // The 95 % near-duplicate position (0.095 s) has no frame, and for full-range video ffmpeg then exits 0 with
        // an empty output instead of failing.
        var path = Path.Combine(_root, "very-short.mp4");
        var made = await RunAsync(_tools.FfmpegPath!, ["-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=60",
            "-frames:v", "6", "-c:v", "libx264", "-pix_fmt", "yuvj420p", path]);
        Assert.True(made.ExitCode == 0, made.Error);
        var adapter = new FfmpegAdapter(_tools, _paths, _log, TimeSpan.FromSeconds(30));

        var near = await adapter.ExtractNearDuplicateFrameAsync(CreateMedia(path), 4, 0.95, 0.095, "short-near-" + Guid.NewGuid().ToString("N"), CancellationToken.None);
        var visual = await adapter.ExtractFrameAsync(CreateMedia(path), 0.095, "short-visual-" + Guid.NewGuid().ToString("N"), CancellationToken.None);

        Assert.True(File.Exists(near.FullPath));
        Assert.Equal(16, near.FingerprintHex.Length);
        Assert.InRange(near.PresentationTimestampSeconds, 0, 0.09);
        Assert.True(File.Exists(visual.FullPath));
    }

    [Fact]
    public async Task FrameInsideTheVideoNeverUsesTheEndOfStreamFallback()
    {
        var path = await GenerateAudioTailFixtureAsync("inside", videoSeconds: 2.0, audioSeconds: 2.3);
        var adapter = new FfmpegAdapter(_tools, _paths, _log, TimeSpan.FromSeconds(30));

        var generated = await adapter.ExtractFrameAsync(CreateMedia(path), 1.2, "inside-" + Guid.NewGuid().ToString("N"), CancellationToken.None);

        Assert.InRange(generated.PresentationTimestampSeconds, 1.19, 1.21);
        Assert.Equal(1, _log.CountOperation("ffmpeg-visual-frame"));
    }

    [Fact]
    public async Task ExtractionFailureIsNotMaskedAndDoesNotRunTimestampFallback()
    {
        var invalid = Path.Combine(_root, "synthetic-invalid.mp4");
        await File.WriteAllTextAsync(invalid, "synthetic invalid media");
        var adapter = new FfmpegAdapter(_tools, _paths, _log, TimeSpan.FromSeconds(30));

        var error = await Assert.ThrowsAsync<MediaToolException>(() =>
            adapter.ExtractFrameAsync(CreateMedia(invalid), 0.1, "invalid-" + Guid.NewGuid().ToString("N"), CancellationToken.None));

        Assert.Equal("FRAME_EXTRACTION_FAILED", error.Error.Code);
        Assert.Equal(0, _log.CountOperation("ffprobe-visual-frame-timestamp"));
        Assert.Equal(1, _log.CountOperation("ffmpeg-visual-frame"));
    }

    [Fact]
    public async Task LongAudioTailFallsBackToTheEndOfTheVideoStream()
    {
        var path = await GenerateAudioTailFixtureAsync("long-tail", videoSeconds: 2.0, audioSeconds: 8.0);
        var adapter = new FfmpegAdapter(_tools, _paths, _log, TimeSpan.FromSeconds(30));

        var generated = await adapter.ExtractFrameAsync(CreateMedia(path), 7.5, "long-tail-" + Guid.NewGuid().ToString("N"), CancellationToken.None);

        Assert.True(File.Exists(generated.FullPath));
        Assert.Equal(7.5, generated.RequestedTimestampSeconds);
        Assert.InRange(generated.PresentationTimestampSeconds, 0.9, 1.99);
        Assert.Equal(3, _log.CountOperation("ffmpeg-visual-frame"));
        Assert.Equal(1, _log.CountOperation("ffprobe-video-stream-end"));
    }

    [Theory]
    [InlineData("duration=12.500000\n", 12.5)]
    [InlineData("duration=N/A\nTAG:DURATION=00:00:02.000000000\n", 2.0)]
    [InlineData("duration=N/A\n", null)]
    [InlineData("", null)]
    public void VideoStreamEndIsReadFromDurationOrMatroskaTag(string output, double? expected) =>
        Assert.Equal(expected, FfmpegAdapter.ParseVideoStreamEnd(output));

    [Fact]
    public async Task NearSubprocessFailureDoesNotPublishAndCleansContainedTemporaryOutputs()
    {
        var invalid = Path.Combine(_root, "synthetic-invalid-near.mp4");
        await File.WriteAllTextAsync(invalid, "synthetic invalid media");
        var adapter = new FfmpegAdapter(_tools, _paths, _log, TimeSpan.FromSeconds(30));
        var before = Directory.EnumerateDirectories(_paths.TempRoot, "visual-frame-*").ToHashSet(StringComparer.OrdinalIgnoreCase);

        var error = await Assert.ThrowsAsync<MediaToolException>(() => adapter.ExtractNearDuplicateFrameAsync(
            CreateMedia(invalid), 0, 0.5, 0.1, "invalid-near-" + Guid.NewGuid().ToString("N"), CancellationToken.None));

        Assert.Equal("FRAME_EXTRACTION_FAILED", error.Error.Code);
        Assert.Equal(0, _log.CountOperation("ffprobe-visual-frame-timestamp"));
        Assert.Equal(before, Directory.EnumerateDirectories(_paths.TempRoot, "visual-frame-*").ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FailedNearTimestampDoesNotPoisonAnUnrelatedRequestAndLeavesNoTempDirectory()
    {
        var path = await GenerateFixtureAsync("isolation", "testsrc2=size=160x90:rate=10", 1, variableFrameRate: false);
        var adapter = new FfmpegAdapter(_tools, _paths, _log, TimeSpan.FromSeconds(30));
        var before = Directory.EnumerateDirectories(_paths.TempRoot, "visual-frame-*").ToHashSet(StringComparer.OrdinalIgnoreCase);

        var invalid = Path.Combine(_root, "isolation-invalid.mp4");
        await File.WriteAllTextAsync(invalid, "synthetic invalid media");
        await Assert.ThrowsAsync<MediaToolException>(() => adapter.ExtractNearDuplicateFrameAsync(
            CreateMedia(invalid), 0, 0.95, 50, "failed-" + Guid.NewGuid().ToString("N"), CancellationToken.None));
        var valid = await adapter.ExtractNearDuplicateFrameAsync(
            CreateMedia(path), 1, 0.25, 0.2, "valid-" + Guid.NewGuid().ToString("N"), CancellationToken.None);

        Assert.True(File.Exists(valid.FullPath));
        Assert.Equal(before, Directory.EnumerateDirectories(_paths.TempRoot, "visual-frame-*").ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private async Task AssertNearEquivalentAsync(string path, double requestedTimestamp)
    {
        _log.Clear();
        var adapter = new FfmpegAdapter(_tools, _paths, _log, TimeSpan.FromSeconds(30));
        var generated = await adapter.ExtractNearDuplicateFrameAsync(
            CreateMedia(path), 0, 0.5, requestedTimestamp, "near-" + Guid.NewGuid().ToString("N"), CancellationToken.None);
        var legacy = await ExtractLegacyDHashAsync(path, requestedTimestamp);
        var legacyJpeg = await ExtractLegacyJpegAsync(path, requestedTimestamp);

        Assert.Equal(legacy, generated.FingerprintHex);
        Assert.True(File.Exists(generated.FullPath));
        Assert.True(new FileInfo(generated.FullPath).Length > 0);
        Assert.Equal(legacyJpeg.Bytes, await File.ReadAllBytesAsync(generated.FullPath));
        Assert.InRange(Math.Abs(generated.PresentationTimestampSeconds - legacyJpeg.SelectedTimestamp), 0, 0.000_001);
        Assert.Equal(requestedTimestamp, generated.RequestedTimestampSeconds);
        Assert.Equal(1, _log.CountOperation("ffmpeg-near-duplicate-frame"));
        Assert.Equal(0, _log.CountOperation("ffmpeg-near-duplicate-dhash"));
        Assert.Equal(0, _log.CountOperation("ffprobe-visual-frame-timestamp"));
    }

    private async Task<string> ExtractLegacyDHashAsync(string path, double requestedTimestamp)
    {
        var timestamp = requestedTimestamp.ToString("F6", CultureInfo.InvariantCulture);
        var result = await RunAsync(_tools.FfmpegPath!, ["-hide_banner", "-nostdin", "-v", "error", "-ss", timestamp, "-copyts", "-i", path, "-frames:v", "1", "-an", "-vf", "scale=9:8:flags=area,format=gray", "-f", "rawvideo", "-"]);
        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Equal(72, result.Output.Length);
        return PerceptualFrameFingerprint.ComputeDHashHex(result.Output);
    }

    private async Task<LegacyJpegResult> ExtractLegacyJpegAsync(string path, double requestedTimestamp)
    {
        var timestamp = requestedTimestamp.ToString("F6", CultureInfo.InvariantCulture);
        var destination = Path.Combine(_root, "legacy-" + Guid.NewGuid().ToString("N") + ".jpg");
        var result = await RunAsync(_tools.FfmpegPath!, ["-hide_banner", "-nostdin", "-y", "-ss", timestamp, "-copyts", "-i", path, "-frames:v", "1", "-an", "-vf", "showinfo,scale='min(1024,iw)':-2", "-q:v", "3", destination]);
        Assert.True(result.ExitCode == 0, result.Error);
        Assert.True(File.Exists(destination));
        var selectedTimestamp = FfmpegAdapter.ParseSingleSelectedFramePresentationTimestamp(result.Error);
        Assert.NotNull(selectedTimestamp);
        return new LegacyJpegResult(await File.ReadAllBytesAsync(destination), selectedTimestamp.Value);
    }

    private async Task<string> GenerateFixtureAsync(string name, string sourceFilter, double duration, bool variableFrameRate)
    {
        var path = Path.Combine(_root, name + ".mkv");
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i", sourceFilter, "-t", duration.ToString(CultureInfo.InvariantCulture) };
        if (variableFrameRate) { arguments.Add("-fps_mode"); arguments.Add("vfr"); }
        arguments.AddRange(["-c:v", "ffv1", path]);
        var result = await RunAsync(_tools.FfmpegPath!, arguments);
        Assert.True(result.ExitCode == 0, result.Error);
        Assert.True(File.Exists(path));
        return path;
    }

    private async Task<string> GenerateAudioTailFixtureAsync(string name, double videoSeconds, double audioSeconds)
    {
        var path = Path.Combine(_root, name + ".mkv");
        var result = await RunAsync(_tools.FfmpegPath!, ["-hide_banner", "-nostdin", "-y",
            "-f", "lavfi", "-t", videoSeconds.ToString(CultureInfo.InvariantCulture), "-i", "testsrc2=size=160x90:rate=10",
            "-f", "lavfi", "-t", audioSeconds.ToString(CultureInfo.InvariantCulture), "-i", "sine=frequency=440:sample_rate=48000",
            "-c:v", "ffv1", "-c:a", "pcm_s16le", path]);
        Assert.True(result.ExitCode == 0, result.Error);
        return path;
    }

    private static async Task<ProcessBytesResult> RunAsync(string executable, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Synthetic media process did not start.");
        await using var output = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await copy;
        return new ProcessBytesResult(process.ExitCode, output.ToArray(), await error);
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
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var runtime = Path.Combine(current.FullName, "runtime", "ffmpeg");
            var ffmpeg = Path.Combine(runtime, "ffmpeg.exe");
            var ffprobe = Path.Combine(runtime, "ffprobe.exe");
            if (File.Exists(ffmpeg) && File.Exists(ffprobe)) return new FfmpegPaths(ffmpeg, ffprobe);
            current = current.Parent;
        }
        return new FfmpegPaths(null, null);
    }

    private sealed record ProcessBytesResult(int ExitCode, byte[] Output, string Error);
    private sealed record LegacyJpegResult(byte[] Bytes, double SelectedTimestamp);

    private sealed class CapturingLogger : IStructuredLogger
    {
        private readonly List<(string EventName, string Fields)> _events = [];
        public void Write(string level, string eventName, object fields, Exception? exception = null) =>
            _events.Add((eventName, JsonSerializer.Serialize(fields)));
        public int CountOperation(string operation) => _events.Count(value => value.EventName == "MediaProcessFinished" && value.Fields.Contains($"\"Operation\":\"{operation}\"", StringComparison.Ordinal));
        public void Clear() => _events.Clear();
    }
}

internal static class ReadOnlyListTestExtensions
{
    public static int IndexOf<T>(this IReadOnlyList<T> values, T expected) where T : notnull
    {
        for (var index = 0; index < values.Count; index++)
            if (EqualityComparer<T>.Default.Equals(values[index], expected)) return index;
        return -1;
    }
}
