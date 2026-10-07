using System.Diagnostics;
using System.Globalization;
using System.Text;
using LocalMediaAI.Core;

namespace SimilarityBench;

/// <summary>
/// Measures finding copies of different length (start or end cut, a scene pulled out, more added) by comparing frames
/// sampled over the whole video (<see cref="TimelineMatching"/>). Videos are generated from several distinct shots so
/// they have cuts the way real videos do. Generated videos only.
/// </summary>
internal static class LengthBench
{
    private const int W = NearDuplicatePreparation.Width;
    private const int H = NearDuplicatePreparation.Height;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Sampling every <paramref name="Interval"/> seconds; with <paramref name="Align"/>, offsets are then measured (TimelineAlignment).</summary>
    private sealed record Method(string Name, double Interval, bool Align);

    // Picking the keyframe before each time was also tried: generated (and phone) videos put keyframes at a fixed
    // spacing, not at cuts, so it found fewer than exact times (27 of 40) and was dropped.
    private static readonly Method[] Methods =
    [
        new("2秒おき", 2, false),
        new("5秒おき", 5, false),
        new("2秒おき＋ずれ補正", 2, true),
        new("5秒おき＋ずれ補正", 5, true),
        new("10秒おき＋ずれ補正", 10, true),
        new("20秒おき＋ずれ補正", 20, true),
    ];

    public static int Run(string work, string ffmpeg, string ffprobe)
    {
        Directory.CreateDirectory(work);
        var tools = new Tools(ffmpeg, ffprobe);
        Console.WriteLine("generating multi-shot videos…");
        var originals = Enumerable.Range(0, 5).Select(k => tools.Shots(Path.Combine(work, $"L{k}.mp4"), ShotPlan(k, 0, 8))).ToArray();
        // Same opening (first two shots), different rest: episodes of a series.
        var sameOpening = Enumerable.Range(0, 5).Select(k => tools.Shots(Path.Combine(work, $"L{k}_episode.mp4"), [.. ShotPlan(k, 0, 2), .. ShotPlan(k + 50, 2, 6)])).ToArray();

        var pairs = new List<(string Category, string A, string B, bool Expected, TimelineSpan[] ExpectedExtra)>();
        for (var k = 0; k < originals.Length; k++)
        {
            var o = originals[k];
            var d = tools.Duration(o);
            string V(string name, params string[] arguments) => tools.Variant(Path.Combine(work, $"L{k}_{name}.mp4"), arguments);
            pairs.Add(("同じ長さで作り直し", o, V("reencode", "-i", o), true, []));
            pairs.Add(("最初を3秒切った", o, V("trim3", "-ss", "3", "-i", o), true, [new(0, 3)]));
            pairs.Add(("最後を8秒切った", o, V("end8", "-i", o, "-t", (d - 8).ToString("0.###", Inv)), true, [new(d - 8, d)]));
            pairs.Add(("途中の10秒を削った", o, V("cut", "-i", o, "-filter_complex", "[0:v]select='not(between(t,20,30))',setpts=N/FRAME_RATE/TB[v]", "-map", "[v]"), true, [new(20, 30)]));
            var extra = tools.Shots(Path.Combine(work, $"L{k}_tail.mp4"), ShotPlan(k + 80, 0, 2));
            pairs.Add(("後ろに足した", o, tools.Concat(Path.Combine(work, $"L{k}_extended.mp4"), o, extra), true, [new(d, d + tools.Duration(extra))]));
            var copyCut = V("copytrim", "-ss", "10", "-i", o, "-c", "copy");
            pairs.Add(("再圧縮せず最初を切った", o, copyCut, true, [new(0, d - tools.Duration(copyCut))]));
            pairs.Add(("画質を落とし最後を切った", o, V("lowq_end8", "-i", o, "-t", (d - 8).ToString("0.###", Inv), "-crf", "40"), true, [new(d - 8, d)]));
            pairs.Add(("左右に黒い帯＋最初を5秒切った", o, V("pillar_trim5", "-ss", "5", "-i", o, "-vf", "scale=240:180,pad=320:180:40:0"), true, [new(0, 5)]));
            pairs.Add(("別の動画（始まりだけ同じ）", o, sameOpening[k], false, []));
        }
        // Longer videos (about 7 minutes of 60 shots): at 20 s spacing they stand in for an hour-long video sampled 120 times.
        var longOriginals = Enumerable.Range(0, 3).Select(k => tools.Shots(Path.Combine(work, $"M{k}.mp4"), ShotPlan(200 + k, 0, 60))).ToArray();
        for (var k = 0; k < longOriginals.Length; k++)
        {
            var o = longOriginals[k];
            var d = tools.Duration(o);
            string V(string name, params string[] arguments) => tools.Variant(Path.Combine(work, $"M{k}_{name}.mp4"), arguments);
            pairs.Add(("長い動画：最初を3秒切った", o, V("trim3", "-ss", "3", "-i", o), true, [new(0, 3)]));
            pairs.Add(("長い動画：途中の60秒を削った", o, V("cut60", "-i", o, "-filter_complex", "[0:v]select='not(between(t,120,180))',setpts=N/FRAME_RATE/TB[v]", "-map", "[v]"), true, [new(120, 180)]));
            pairs.Add(("長い動画：最後を20秒切った", o, V("end20", "-i", o, "-t", (d - 20).ToString("0.###", Inv)), true, [new(d - 20, d)]));
            pairs.Add(("長い動画：別の動画（始まりだけ同じ）", o, tools.Shots(Path.Combine(work, $"M{k}_episode.mp4"), [.. ShotPlan(200 + k, 0, 4), .. ShotPlan(300 + k, 4, 56)]), false, []));
        }
        for (var i = 0; i < longOriginals.Length; i++)
            for (var j = i + 1; j < longOriginals.Length; j++)
                pairs.Add(("長い動画：別の動画", longOriginals[i], longOriginals[j], false, []));
        for (var i = 0; i < originals.Length; i++)
            for (var j = i + 1; j < originals.Length; j++)
            {
                pairs.Add(("別の動画", originals[i], originals[j], false, []));
                pairs.Add(("別の動画（相手は最初を切ったもの）", originals[i], Path.Combine(work, $"L{j}_trim3.mp4"), false, []));
            }

        var report = new StringBuilder("# 長さが違う動画：測定結果（生成した動画のみ）\n\n");
        report.AppendLine("| 区分 | 組 | 正解 | " + string.Join(" | ", Methods.Select(m => m.Name)) + " |");
        report.AppendLine("|---|---|---|" + string.Concat(Methods.Select(_ => "---|")));
        var results = new List<(string Category, bool Expected, bool[] Found, bool[] SpanOk)>();
        foreach (var (category, a, b, expected, expectedExtra) in pairs)
        {
            var found = new bool[Methods.Length];
            var spanOk = new bool[Methods.Length];
            var cells = new string[Methods.Length];
            for (var m = 0; m < Methods.Length; m++)
            {
                var match = Methods[m].Align
                    ? tools.Align(a, b, Methods[m])
                    : TimelineMatching.Compare(tools.Timeline(a, Methods[m]), tools.Duration(a), tools.Timeline(b, Methods[m]), tools.Duration(b));
                found[m] = match.IsContained;
                spanOk[m] = match.IsContained && SpansAgree(match.ExtraInLonger, expectedExtra, Methods[m].Interval);
                var spans = string.Join(" ", match.ExtraInLonger.Select(s => $"{s.StartSeconds:0}–{s.EndSeconds:0}"));
                cells[m] = $"{(expected == found[m] ? "○" : expected ? "見落とし" : "誤判定")} {match.MatchedFrames}枚 {match.ShorterCoverage:P0}{(spans.Length > 0 ? " 長い所 " + spans : "")}";
            }
            results.Add((category, expected, found, spanOk));
            report.AppendLine(Inv, $"| {category} | {Path.GetFileNameWithoutExtension(a)} / {Path.GetFileNameWithoutExtension(b)} | {(expected ? "含まれる" : "別") + (expectedExtra.Length > 0 ? " " + string.Join(" ", expectedExtra.Select(s => $"{s.StartSeconds:0}–{s.EndSeconds:0}")) : "")} | {string.Join(" | ", cells)} |");
        }
        report.AppendLine("\n## まとめ（正しく判定した組の数／長い所が合っていた組の数）\n");
        report.AppendLine("| 区分 | 組数 | " + string.Join(" | ", Methods.Select(m => m.Name)) + " |");
        report.AppendLine("|---|---:|" + string.Concat(Methods.Select(_ => "---:|")));
        foreach (var g in results.GroupBy(r => r.Category))
            report.AppendLine(Inv, $"| {g.Key} | {g.Count()} | {string.Join(" | ", Methods.Select((_, m) => g.First().Expected ? $"{g.Count(r => r.Found[m])}／{g.Count(r => r.SpanOk[m])}" : $"{g.Count(r => !r.Found[m])}"))} |");
        report.AppendLine();
        for (var m = 0; m < Methods.Length; m++)
        {
            var positives = results.Where(r => r.Expected).ToArray();
            var negatives = results.Where(r => !r.Expected).ToArray();
            report.AppendLine(Inv, $"- {Methods[m].Name}：含まれる {positives.Length}組中 {positives.Count(r => r.Found[m])}組を見つけ（長い所が合っていた {positives.Count(r => r.SpanOk[m])}組）、別の {negatives.Length}組中 {negatives.Count(r => r.Found[m])}組を間違えた。1本あたりの場面の数 平均 {tools.AverageFrames(Methods[m]):0.0}枚{(Methods[m].Align ? $"、組ごとに追加で読んだ場面 平均 {tools.AverageExtraReads(Methods[m]):0.0}枚" : "")}");
        }
        File.WriteAllText(Path.Combine(work, "length-report.md"), report.ToString(), new UTF8Encoding(false));
        Console.WriteLine(report.ToString());
        return 0;
    }

    /// <summary>Each expected stretch has a found one whose ends are within the sampling interval, and nothing else is reported.</summary>
    private static bool SpansAgree(IReadOnlyList<TimelineSpan> found, IReadOnlyList<TimelineSpan> expected, double interval)
    {
        var tolerance = interval + 1;
        return expected.All(e => found.Any(f => Math.Abs(f.StartSeconds - e.StartSeconds) <= tolerance && Math.Abs(f.EndSeconds - e.EndSeconds) <= tolerance))
            && found.Count == expected.Count;
    }

    /// <summary>Shots <paramref name="first"/>.. of video <paramref name="k"/>: generator, its settings and length.</summary>
    private static IReadOnlyList<(string Source, double Seconds)> ShotPlan(int k, int first, int count)
    {
        string[] kinds =
        [
            "life=size=320x180:rate=30:mold=10:ratio=0.2:seed={0}",
            "cellauto=size=320x180:rate=30:rule={1}:random_fill_ratio=0.5:seed={0}",
            "gradients=size=320x180:rate=30:speed=0.02:seed={0}",
            "mandelbrot=size=320x180:rate=30:start_x={2}:start_y={3}:start_scale=0.4",
        ];
        double[] lengths = [5, 8, 6, 9, 7, 5, 10, 6];
        return Enumerable.Range(first, count).Select(s =>
        {
            var seed = 1000 + k * 37 + s * 11;
            var source = string.Format(Inv, kinds[(s + k) % kinds.Length], seed, new[] { 30, 90, 110, 150 }[seed % 4], -0.75 + (seed % 7) * 0.05, 0.1 + (seed % 5) * 0.03);
            return (source, lengths[(s + k) % lengths.Length]);
        }).ToArray();
    }

    private sealed class Tools(string ffmpeg, string ffprobe)
    {
        private readonly Dictionary<string, double> _durations = new();
        private readonly Dictionary<(string, string), IReadOnlyList<TimelineFrame>> _timelines = new();
        private readonly Dictionary<string, List<int>> _frameCounts = new();
        private readonly Dictionary<string, List<int>> _extraReads = new();
        private readonly Dictionary<string, FrameCrop> _bars = new();

        public string Shots(string path, IReadOnlyList<(string Source, double Seconds)> shots)
        {
            if (File.Exists(path)) return path;
            var arguments = new List<string> { "-y", "-loglevel", "error" };
            foreach (var (source, seconds) in shots) arguments.AddRange(["-f", "lavfi", "-t", seconds.ToString(Inv), "-i", source]);
            var graph = string.Concat(shots.Select((_, i) => $"[{i}:v]setsar=1,fps=30,format=yuv420p[s{i}];")) + string.Concat(shots.Select((_, i) => $"[s{i}]")) + $"concat=n={shots.Count}:v=1:a=0[v]";
            arguments.AddRange(["-filter_complex", graph, "-map", "[v]", "-c:v", "libx264", "-preset", "ultrafast", path]);
            Run(ffmpeg, arguments);
            return path;
        }

        public string Variant(string path, params string[] arguments)
        {
            if (File.Exists(path)) return path;
            var copy = arguments.Contains("copy");
            var all = new List<string> { "-y", "-loglevel", "error" };
            all.AddRange(arguments);
            if (!copy)
            {
                if (!arguments.Contains("-crf")) all.AddRange(["-crf", "23"]);
                all.AddRange(["-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p"]);
            }
            all.Add(path);
            Run(ffmpeg, all);
            return path;
        }

        public string Concat(string path, string first, string second)
        {
            if (File.Exists(path)) return path;
            Run(ffmpeg, ["-y", "-loglevel", "error", "-i", first, "-i", second, "-filter_complex", "[0:v][1:v]concat=n=2:v=1:a=0[v]", "-map", "[v]", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", path]);
            return path;
        }

        public double Duration(string path)
        {
            if (_durations.TryGetValue(path, out var known)) return known;
            return _durations[path] = double.Parse(Run(ffprobe, ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path]).Trim(), Inv);
        }

        public IReadOnlyList<TimelineFrame> Timeline(string path, Method method)
        {
            if (_timelines.TryGetValue((path, method.Name), out var known)) return known;
            var duration = Duration(path);
            var times = new List<double>();
            for (var t = method.Interval / 2; t < duration; t += method.Interval) times.Add(t);
            var gray = Frames(path, times);
            var prepared = NearDuplicatePreparation.Prepare(gray.Select((frame, i) => (i, frame)).ToArray());
            _bars[path] = NearDuplicatePreparation.Bars(gray);
            var timeline = prepared.Select((frame, i) => new TimelineFrame(times[i], frame)).ToArray();
            if (!_frameCounts.TryGetValue(method.Name, out var counts)) _frameCounts[method.Name] = counts = [];
            counts.Add(timeline.Length);
            return _timelines[(path, method.Name)] = timeline;
        }

        /// <summary>The rough line-up, then offsets measured on stretches of both videos and every frame compared at its matching moment.</summary>
        public TimelineMatch Align(string a, string b, Method method)
        {
            var extra = 0;
            TimelineVideo Video(string path)
            {
                var bars = _bars[path];
                IReadOnlyList<PreparedNearDuplicateFrame> Prepared(IReadOnlyList<byte[]> gray) => NearDuplicatePreparation.Prepare(gray.Select((frame, i) => (i, frame)).ToArray(), bars);
                TimelineWindowReader window = (from, to, fps, _) =>
                {
                    var gray = Window(path, from, to, fps);
                    extra += gray.Count;
                    var prepared = Prepared(gray);
                    return Task.FromResult<IReadOnlyList<TimelineFrame>>(prepared.Select((frame, i) => new TimelineFrame(from + i / fps, frame)).ToArray());
                };
                TimelineFrameReader at = (times, _) =>
                {
                    extra += times.Count;
                    return Task.FromResult<IReadOnlyList<PreparedNearDuplicateFrame?>>(Prepared(Frames(path, times)).ToArray());
                };
                return new TimelineVideo(Timeline(path, method), Duration(path), window, at);
            }
            var match = TimelineAlignment.AlignAsync(Video(a), Video(b), method.Interval, CancellationToken.None).GetAwaiter().GetResult();
            if (!_extraReads.TryGetValue(method.Name, out var reads)) _extraReads[method.Name] = reads = [];
            reads.Add(extra);
            return match;
        }

        /// <summary>Frames from one stretch at a steady rate, decoded in one pass.</summary>
        private IReadOnlyList<byte[]> Window(string path, double from, double to, double fps)
        {
            var bytes = RunBytes(ffmpeg, ["-loglevel", "error", "-ss", from.ToString("0.###", Inv), "-t", (to - from).ToString("0.###", Inv), "-i", path,
                "-vf", $"fps=fps={fps.ToString(Inv)}:round=up,scale={W}:{H}:flags=area,format=gray", "-f", "rawvideo", "pipe:1"]);
            return Enumerable.Range(0, bytes.Length / (W * H)).Select(i => bytes.AsSpan(i * W * H, W * H).ToArray()).ToArray();
        }

        public double AverageExtraReads(Method method) => _extraReads.TryGetValue(method.Name, out var reads) && reads.Count > 0 ? reads.Average() : 0;

        public double AverageFrames(Method method) => _frameCounts.TryGetValue(method.Name, out var counts) && counts.Count > 0 ? counts.Average() : 0;

        /// <summary>Frames at exact times, many per FFmpeg run, stacked into one picture.</summary>
        private byte[][] Frames(string path, IReadOnlyList<double> times)
        {
            var result = new List<byte[]>();
            foreach (var batch in times.Chunk(24))
            {
                var arguments = new List<string> { "-loglevel", "error" };
                // The last frame starts a little before the container's end; asking past it returns nothing.
                foreach (var t in batch) arguments.AddRange(["-ss", Math.Clamp(t, 0, Duration(path) - 0.15).ToString("0.###", Inv), "-i", path]);
                var graph = batch.Length == 1
                    ? $"[0:v]scale={W}:{H}:flags=area,format=gray[o]"
                    : string.Concat(batch.Select((_, i) => $"[{i}:v]trim=end_frame=1,scale={W}:{H}:flags=area,format=gray,setsar=1[f{i}];")) + string.Concat(batch.Select((_, i) => $"[f{i}]")) + $"vstack=inputs={batch.Length}[o]";
                arguments.AddRange(["-filter_complex", graph, "-map", "[o]", "-frames:v", "1", "-f", "rawvideo", "pipe:1"]);
                var bytes = RunBytes(ffmpeg, arguments);
                if (bytes.Length != W * H * batch.Length) throw new InvalidOperationException($"{Path.GetFileName(path)}: expected {batch.Length} frames, got {bytes.Length} bytes");
                result.AddRange(Enumerable.Range(0, batch.Length).Select(i => bytes.AsSpan(i * W * H, W * H).ToArray()));
            }
            return [.. result];
        }

        private static string Run(string exe, IEnumerable<string> arguments) => Encoding.UTF8.GetString(RunBytes(exe, arguments));

        private static byte[] RunBytes(string exe, IEnumerable<string> arguments)
        {
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in arguments) info.ArgumentList.Add(a);
            using var p = Process.Start(info)!;
            using var output = new MemoryStream();
            var error = p.StandardError.ReadToEndAsync();
            p.StandardOutput.BaseStream.CopyTo(output);
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(exe)} failed: {error.Result}");
            return output.ToArray();
        }
    }
}
