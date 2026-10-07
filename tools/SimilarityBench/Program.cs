using System.Diagnostics;
using System.Globalization;
using System.Text;
using LocalMediaAI.Core;
using SimilarityBench;

// Usage: SimilarityBench <workDir> <ffmpeg.exe> <ffprobe.exe> [length]
// Generates videos whose answer is known (copies changed in ways people change videos, and different videos that
// look alike), takes the same frames the app takes, and reports how each version of the "よく似ている" rule decides
// each pair. Writes <workDir>\report.md. Uses generated videos only.
if (args.Length < 3) { Console.Error.WriteLine("usage: SimilarityBench <workDir> <ffmpeg.exe> <ffprobe.exe> [length]"); return 2; }
// "length": copies of different length, compared over their whole timeline (LengthBench).
if (args.Length > 3 && args[3] == "length") return LengthBench.Run(Path.GetFullPath(args[0]), args[1], args[2]);
var work = Directory.CreateDirectory(Path.GetFullPath(args[0])).FullName;
var ffmpeg = args[1];
var ffprobe = args[2];
var inv = CultureInfo.InvariantCulture;
const int Seconds = 20;
const int SW = FramePreparation.SampleWidth;
const int SH = FramePreparation.SampleHeight;
const int PW = 256;
const int PH = 144;

string Run(string exe, params string[] arguments)
{
    var info = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
    foreach (var a in arguments) info.ArgumentList.Add(a);
    using var p = Process.Start(info)!;
    var output = p.StandardOutput.ReadToEnd();
    var error = p.StandardError.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(exe)} failed: {error}");
    return output;
}

byte[] Raw(string path, double seconds, string scale)
{
    var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var a in new[] { "-loglevel", "error", "-ss", seconds.ToString("0.###", inv), "-i", path, "-frames:v", "1", "-vf", scale + ":flags=area,format=gray", "-f", "rawvideo", "pipe:1" })
        info.ArgumentList.Add(a);
    using var p = Process.Start(info)!;
    using var buffer = new MemoryStream();
    p.StandardOutput.BaseStream.CopyTo(buffer);
    p.WaitForExit();
    return buffer.ToArray();
}

string Video(string name, string source, string? filter = null)
{
    var path = Path.Combine(work, name + ".mp4");
    if (File.Exists(path)) return path;
    var arguments = new List<string> { "-y", "-loglevel", "error", "-f", "lavfi", "-i", source };
    if (filter is not null) arguments.AddRange(["-vf", filter]);
    arguments.AddRange(["-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-t", Seconds.ToString(inv), path]);
    Run(ffmpeg, [.. arguments]);
    return path;
}

string Variant(string name, string from, string? filter, params string[] seek)
{
    var path = Path.Combine(work, name + ".mp4");
    if (File.Exists(path)) return path;
    var arguments = new List<string> { "-y", "-loglevel", "error" };
    arguments.AddRange(seek);
    arguments.AddRange(["-i", from]);
    if (filter is not null) arguments.AddRange(["-vf", filter]);
    arguments.AddRange(["-c:v", "libx264", "-preset", "ultrafast", "-crf", name.Contains("lowq", StringComparison.Ordinal) ? "40" : "23", "-pix_fmt", "yuv420p", path]);
    Run(ffmpeg, [.. arguments]);
    return path;
}

double Duration(string path) => double.Parse(Run(ffprobe, "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path).Trim(), inv);

Console.WriteLine("generating videos…");
var originals = new Dictionary<string, string>
{
    ["mandelbrot"] = Video("o_mandelbrot", "mandelbrot=size=640x360:rate=30"),
    ["testsrc2"] = Video("o_testsrc2", "testsrc2=size=640x360:rate=30"),
    ["life"] = Video("o_life", "life=size=640x360:rate=30:mold=10:ratio=0.1:death_color=#C83232:life_color=#00ff00"),
    ["cellauto"] = Video("o_cellauto", "cellauto=size=640x360:rate=30:rule=110"),
    ["gradients"] = Video("o_gradients", "gradients=size=640x360:rate=30:speed=0.03:seed=7"),
};
// Different videos that are mostly black: a small bright object moving differently in each.
var dark = new Dictionary<string, string>
{
    ["dark-a"] = Video("d_a", "color=c=black:size=640x360:rate=30", "drawbox=x='mod(t*40,600)':y=100:w=20:h=20:color=white:t=fill"),
    ["dark-b"] = Video("d_b", "color=c=black:size=640x360:rate=30", "drawbox=x=300:y='mod(t*25,330)':w=16:h=16:color=yellow:t=fill"),
    ["dark-c"] = Video("d_c", "color=c=0x080808:size=640x360:rate=30", "drawbox=x='600-mod(t*30,600)':y=250:w=24:h=12:color=red:t=fill"),
};
// Copies changed the way people change videos. trim-start-long is longer than the 3% the rule allows.
var changes = new (string Name, string? Filter, string[] Seek, string Kind)[]
{
    ("reencode-lowq", null, [], "作り直し（画質を落とした）"),
    ("smaller", "scale=320:180", [], "小さくした"),
    ("larger", "scale=1280:720", [], "大きくした"),
    ("brighter", "eq=brightness=0.08", [], "明るさを変えた"),
    ("letterbox", "scale=640:300,pad=640:360:0:30", [], "黒い帯を付けた"),
    ("pillarbox", "scale=480:360,pad=640:360:80:0", [], "左右に黒い帯を付けた"),
    ("cropped", "crop=576:324,scale=640:360", [], "周りを切った"),
    ("trim-start", null, ["-ss", "0.3"], "最初を少し切った（3%以内）"),
    ("trim-start-long", null, ["-ss", "3"], "最初を3秒切った（15%）"),
};

// Per video: the 9x8 frames the app takes today, and the 96x54 frames the new preparation starts from.
var direct = new Dictionary<string, byte[][]>();
var large = new Dictionary<string, byte[][]>();
var pdqFrames = new Dictionary<string, byte[][]>();
var durations = new Dictionary<string, double>();
void Measure(string key, string path)
{
    var duration = Duration(path);
    var stamps = NearDuplicateSamplingPolicy.CreateTimestamps(duration);
    direct[key] = stamps.Select(t => Raw(path, t.RequestedTimestampSeconds, "scale=9:8")).ToArray();
    large[key] = stamps.Select(t => Raw(path, t.RequestedTimestampSeconds, $"scale={SW}:{SH}")).ToArray();
    pdqFrames[key] = stamps.Select(t => Raw(path, t.RequestedTimestampSeconds, $"scale={PW}:{PH}")).ToArray();
    durations[key] = duration;
}
foreach (var (key, path) in originals.Concat(dark)) Measure(key, path);
foreach (var (key, path) in originals)
    foreach (var change in changes)
        Measure($"{key}~{change.Name}", Variant($"v_{key}_{change.Name}", path, change.Filter, change.Seek));

string Hash(byte[] nine) => PerceptualFrameFingerprint.ComputeDHashHex(nine);
string HashExact(string key, int i, FrameCrop crop) => Hash(FramePreparation.Resample(large[key][i], SW, SH, crop, 9, 8));
FrameCrop Bars(string key) => FramePreparation.Common(large[key].Select(f => FramePreparation.DetectBlackBorders(f, SW, SH)));
byte[] Prepared(string key, int i, FrameCrop crop) => FramePreparation.Resample(large[key][i], SW, SH, crop, 9, 8);
FrameCrop Inside(FrameCrop bars, double share)
{
    var w = SW - bars.Left - bars.Right;
    var h = SH - bars.Top - bars.Bottom;
    var c = FramePreparation.Center(w, h, share);
    return new FrameCrop(bars.Left + c.Left, bars.Top + c.Top, bars.Right + c.Right, bars.Bottom + c.Bottom);
}

// PDQ: bars found on the larger frame are cut off, then the 256-bit hash. Frame limit is the reference's image match
// limit (31 of 256 bits); mean and largest scale the dHash rule's 8/6/12 of 64 by the same ratio.
FrameCrop PdqBars(string key) => FramePreparation.Common(pdqFrames[key].Select(f => FramePreparation.DetectBlackBorders(f, PW, PH)));
FrameCrop PdqInside(FrameCrop bars, double share)
{
    var c = FramePreparation.Center(PW - bars.Left - bars.Right, PH - bars.Top - bars.Bottom, share);
    return new FrameCrop(bars.Left + c.Left, bars.Top + c.Top, bars.Right + c.Right, bars.Bottom + c.Bottom);
}
var pdqCache = new Dictionary<(string, int, FrameCrop), PdqFingerprint>();
PdqFingerprint Pdq(string key, int i, FrameCrop crop)
{
    if (pdqCache.TryGetValue((key, i, crop), out var cached)) return cached;
    var w = PW - crop.Left - crop.Right;
    var h = PH - crop.Top - crop.Bottom;
    return pdqCache[(key, i, crop)] = PdqHasher.Compute(PdqHasher.LumaFromGray(pdqFrames[key][i], PW, PH, crop), w, h);
}
var pdqProfile = new NearDuplicateVerificationProfile("pdq-bench", 5, 4, 3, MaximumFrameDistance: PdqHasher.MatchDistance,
    MaximumMeanDistance: 23, MaximumObservedDistance: 46, MaximumDurationDifferenceRatio: 0.03d);

// pHash: the 8x8 lowest frequencies of a 32x32 DCT (DC left out), one bit per coefficient above their median.
// Coarser than PDQ's 16x16, so it should tolerate small shifts in time and framing the way dHash does.
var cosTable = new double[8, 32];
for (var u = 0; u < 8; u++) for (var x = 0; x < 32; x++) cosTable[u, x] = Math.Cos((2 * x + 1) * u * Math.PI / 64);
var pHashCache = new Dictionary<(string, int, FrameCrop), string>();
string PHash(string key, int i, FrameCrop crop)
{
    if (pHashCache.TryGetValue((key, i, crop), out var cached)) return cached;
    var small = FramePreparation.Resample(pdqFrames[key][i], PW, PH, crop, 32, 32);
    var coefficients = new double[64];
    for (var v = 0; v < 8; v++)
        for (var u = 0; u < 8; u++)
        {
            double sum = 0;
            for (var y = 0; y < 32; y++)
                for (var x = 0; x < 32; x++)
                    sum += small[y * 32 + x] * cosTable[u, x] * cosTable[v, y];
            coefficients[v * 8 + u] = sum;
        }
    var ac = coefficients.Skip(1).Order().ToArray();
    var median = (ac[30] + ac[31]) / 2;
    ulong bits = 0;
    for (var k = 1; k < 64; k++) if (coefficients[k] > median) bits |= 1UL << k;
    return pHashCache[(key, i, crop)] = bits.ToString("x16", inv);
}

var profile = NearDuplicateVerificationProfile.HighPrecisionV1;
var versions = new[] { "今", "平ら除外", "+黒い帯", "+黒い帯+中央", "PDQ+黒い帯", "PDQ+黒い帯+中央", "pHash+黒い帯", "pHash+黒い帯+中央", "dHashかpHash", "アプリ（今か帯+中央）" };
var preparedCache = new Dictionary<string, IReadOnlyList<PreparedNearDuplicateFrame>>();
IReadOnlyList<PreparedNearDuplicateFrame> PreparedFrames(string key) =>
    preparedCache.TryGetValue(key, out var cached) ? cached : preparedCache[key] = NearDuplicatePreparation.Prepare(large[key].Select((frame, i) => (i, frame)).ToArray());

(bool[] Verdicts, string Note) Decide(string a, string b)
{
    var n = direct[a].Length;
    var verdicts = new bool[versions.Length];
    string Distances((int, int)[] d) => string.Join(",", d.Select(x => x.Item2));
    int[] Flat(Func<int, byte[]> fa, Func<int, byte[]> fb) => Enumerable.Range(0, n)
        .Where(i => PerceptualFrameFingerprint.HasFewEdges(Hash(fa(i))) && PerceptualFrameFingerprint.HasFewEdges(Hash(fb(i))) && PerceptualFrameFingerprint.IsFlat(fa(i)) && PerceptualFrameFingerprint.IsFlat(fb(i))).ToArray();

    // 今 and 平ら除外: 9x8 straight from FFmpeg.
    var d0 = Enumerable.Range(0, n).Select(i => (i, PerceptualFrameFingerprint.HammingDistance(Hash(direct[a][i]), Hash(direct[b][i])))).ToArray();
    verdicts[0] = profile.Verify(durations[a], durations[b], d0).IsVerified;
    verdicts[1] = profile.Verify(durations[a], durations[b], d0, Flat(i => direct[a][i], i => direct[b][i])).IsVerified;

    // +黒い帯: bars found in all five frames are cut off before the 9x8 fingerprint.
    var barsA = Bars(a);
    var barsB = Bars(b);
    var d2 = Enumerable.Range(0, n).Select(i => (i, PerceptualFrameFingerprint.HammingDistance(HashExact(a, i, barsA), HashExact(b, i, barsB)))).ToArray();
    var flat2 = Flat(i => Prepared(a, i, barsA), i => Prepared(b, i, barsB));
    verdicts[2] = profile.Verify(durations[a], durations[b], d2, flat2).IsVerified;

    // +中央: a frame also matches when the middle 90% or 80% of one matches the whole of the other (zoomed, cut edges).
    var d3 = Enumerable.Range(0, n).Select(i =>
    {
        var best = d2[i].Item2;
        foreach (var share in new[] { 0.9, 0.8 })
        {
            best = Math.Min(best, PerceptualFrameFingerprint.HammingDistance(HashExact(a, i, Inside(barsA, share)), HashExact(b, i, barsB)));
            best = Math.Min(best, PerceptualFrameFingerprint.HammingDistance(HashExact(a, i, barsA), HashExact(b, i, Inside(barsB, share))));
        }
        return (i, best);
    }).ToArray();
    verdicts[3] = profile.Verify(durations[a], durations[b], d3, flat2).IsVerified;
    // What the app does: the earlier rule, or else the same comparison through NearDuplicatePreparation (must agree with d3).
    var (appDistances, appFlat) = NearDuplicatePreparation.Compare(PreparedFrames(a), PreparedFrames(b));
    if (!appDistances.Select(x => x.HammingDistance).SequenceEqual(d3.Select(x => x.Item2)) || !appFlat.SequenceEqual(flat2))
        throw new InvalidOperationException($"NearDuplicatePreparation differs from the bench for {a} / {b}");
    verdicts[9] = verdicts[1] || profile.Verify(durations[a], durations[b], appDistances, appFlat).IsVerified;

    // PDQ: a frame both videos show with too little detail (quality below the reference's 50) counts neither way.
    var pBarsA = PdqBars(a);
    var pBarsB = PdqBars(b);
    var p1 = Enumerable.Range(0, n).Select(i => (i, PdqHasher.HammingDistance(Pdq(a, i, pBarsA).Hex, Pdq(b, i, pBarsB).Hex))).ToArray();
    var plain = Enumerable.Range(0, n).Where(i => Pdq(a, i, pBarsA).Quality < PdqHasher.LowQualityBelow && Pdq(b, i, pBarsB).Quality < PdqHasher.LowQualityBelow).ToArray();
    verdicts[4] = pdqProfile.Verify(durations[a], durations[b], p1, plain).IsVerified;
    var p2 = Enumerable.Range(0, n).Select(i =>
    {
        var best = p1[i].Item2;
        foreach (var share in new[] { 0.9, 0.8 })
        {
            best = Math.Min(best, PdqHasher.HammingDistance(Pdq(a, i, PdqInside(pBarsA, share)).Hex, Pdq(b, i, pBarsB).Hex));
            best = Math.Min(best, PdqHasher.HammingDistance(Pdq(a, i, pBarsA).Hex, Pdq(b, i, PdqInside(pBarsB, share)).Hex));
        }
        return (i, best);
    }).ToArray();
    verdicts[5] = pdqProfile.Verify(durations[a], durations[b], p2, plain).IsVerified;
    // pHash on the same bar-free picture; flat frames are set aside with the same 9x8 check as dHash.
    var h1 = Enumerable.Range(0, n).Select(i => (i, PerceptualFrameFingerprint.HammingDistance(PHash(a, i, pBarsA), PHash(b, i, pBarsB)))).ToArray();
    verdicts[6] = profile.Verify(durations[a], durations[b], h1, flat2).IsVerified;
    var h2 = Enumerable.Range(0, n).Select(i =>
    {
        var best = h1[i].Item2;
        foreach (var share in new[] { 0.9, 0.8 })
        {
            best = Math.Min(best, PerceptualFrameFingerprint.HammingDistance(PHash(a, i, PdqInside(pBarsA, share)), PHash(b, i, pBarsB)));
            best = Math.Min(best, PerceptualFrameFingerprint.HammingDistance(PHash(a, i, pBarsA), PHash(b, i, PdqInside(pBarsB, share))));
        }
        return (i, best);
    }).ToArray();
    verdicts[7] = profile.Verify(durations[a], durations[b], h2, flat2).IsVerified;
    verdicts[8] = verdicts[3] || verdicts[7];
    var qualities = string.Join(",", Enumerable.Range(0, n).Select(i => Math.Min(Pdq(a, i, pBarsA).Quality, Pdq(b, i, pBarsB).Quality)));
    return (verdicts, $"今 {Distances(d0)}／帯 {Distances(d2)}／中央 {Distances(d3)}／PDQ {Distances(p1)}／PDQ中央 {Distances(p2)}／質 {qualities}／pHash {Distances(h1)}／pHash中央 {Distances(h2)}");
}

var rows = new List<(string Category, string Pair, bool Expected, bool[] Verdicts, string Note)>();
foreach (var key in originals.Keys)
    foreach (var change in changes)
    {
        var (v, note) = Decide(key, $"{key}~{change.Name}");
        rows.Add((change.Kind, $"{key} / {change.Name}", true, v, note));
    }
var keys = originals.Keys.ToArray();
for (var i = 0; i < keys.Length; i++)
    for (var j = i + 1; j < keys.Length; j++)
    {
        var (v, note) = Decide(keys[i], keys[j]);
        rows.Add(("別の動画", $"{keys[i]} / {keys[j]}", false, v, note));
    }
// Different videos against changed copies of other videos: more chances for a wrong match.
foreach (var a in keys)
    foreach (var b in keys.Where(k => k != a))
        foreach (var change in new[] { "letterbox", "pillarbox", "cropped", "smaller" })
        {
            var (v, note) = Decide(a, $"{b}~{change}");
            rows.Add(("別の動画（相手は変えたもの）", $"{a} / {b}~{change}", false, v, note));
        }
var darkKeys = dark.Keys.ToArray();
for (var i = 0; i < darkKeys.Length; i++)
    for (var j = i + 1; j < darkKeys.Length; j++)
    {
        var (v, note) = Decide(darkKeys[i], darkKeys[j]);
        rows.Add(("別の動画（ほとんど黒い）", $"{darkKeys[i]} / {darkKeys[j]}", false, v, note));
    }

string Mark(bool expected, bool actual) => expected == actual ? "○" : expected ? "見落とし" : "誤判定";
var report = new StringBuilder();
report.AppendLine("# よく似ている動画の判定：測定結果（生成した動画のみ）").AppendLine();
report.AppendLine("| 区分 | 組 | 正解 | " + string.Join(" | ", versions) + " | 距離 |").AppendLine("|---|---|---|" + string.Concat(versions.Select(_ => "---|")) + "---|");
foreach (var r in rows)
    report.AppendLine(inv, $"| {r.Category} | {r.Pair} | {(r.Expected ? "似ている" : "別")} | {string.Join(" | ", r.Verdicts.Select(v => Mark(r.Expected, v)))} | {r.Note} |");
report.AppendLine().AppendLine("## まとめ（正しく判定した組の数）").AppendLine();
report.AppendLine("| 区分 | 組数 | " + string.Join(" | ", versions) + " |").AppendLine("|---|---:|" + string.Concat(versions.Select(_ => "---:|")));
foreach (var g in rows.GroupBy(r => r.Category))
    report.AppendLine(inv, $"| {g.Key} | {g.Count()} | {string.Join(" | ", Enumerable.Range(0, versions.Length).Select(v => g.Count(r => r.Expected == r.Verdicts[v])))} |");
var positives = rows.Where(r => r.Expected).ToArray();
var negatives = rows.Where(r => !r.Expected).ToArray();
report.AppendLine();
for (var v = 0; v < versions.Length; v++)
    report.AppendLine(inv, $"- {versions[v]}：似ている {positives.Length}組中 {positives.Count(r => r.Verdicts[v])}組を見つけ、別の {negatives.Length}組中 {negatives.Count(r => r.Verdicts[v])}組を間違えて「似ている」とした");
File.WriteAllText(Path.Combine(work, "report.md"), report.ToString(), new UTF8Encoding(false));
Console.WriteLine(report.ToString());
return 0;
