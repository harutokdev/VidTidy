using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace ThroughputLab;

public sealed record MasterSpec(string Name, int Width, int Height, int Fps, string Codec, string BitrateMbps, int Seconds, string Extension);

public sealed record GeneratedClip(string Name, string Kind, string Master, double OffsetSeconds, double DurationSeconds, string Container, bool FastStart, long Bytes);

public sealed record SetProfile(string Name, int Count, double MediumShare, double LargeShare, double UhdShare, double ExactCopyShare, double ReencodeShare);

/// <summary>
/// Builds synthetic libraries that mimic the target distribution (mostly clips under 100 MB, some several-GB files).
/// Masters are encoded once; set files are stream-copied cuts, so a few hundred GB costs minutes, not hours.
/// Content is generated test patterns only.
/// </summary>
public sealed class SyntheticMedia(LabLayout lab, TextWriter log)
{
    public static readonly IReadOnlyList<MasterSpec> Masters =
    [
        new("m720-h264", 1280, 720, 30, "h264", "4.5", 1200, "mp4"),
        new("m1080-h264", 1920, 1080, 30, "h264", "7", 1200, "mp4"),
        new("m1080-hevc", 1920, 1080, 30, "hevc", "4", 600, "mp4"),
        new("m2160-h264", 3840, 2160, 30, "h264", "25", 180, "mp4"),
        new("mvert-h264", 1080, 1920, 30, "h264", "6", 300, "mp4"),
        new("m480-mpeg4", 854, 480, 25, "mpeg4", "2", 600, "avi")
    ];

    public static SetProfile Profile(string name, int? count) => name switch
    {
        "smoke" => new("smoke", count ?? 12, 0.10, 0.0, 0.0, 0.09, 0.09),
        "standard" => new("standard", count ?? 400, 0.11, 0.025, 0.015, 0.02, 0.01),
        "soak" => new("soak", count ?? 1500, 0.11, 0.025, 0.015, 0.02, 0.01),
        _ => throw new ArgumentException("Unknown profile: " + name)
    };

    public async Task EnsureMastersAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(lab.MastersRoot);
        for (var index = 0; index < Masters.Count; index++)
        {
            var master = Masters[index];
            var path = MasterPath(master);
            if (File.Exists(path) && new FileInfo(path).Length > 0) continue;
            var temporary = path + ".partial." + master.Extension;
            var stopwatch = Stopwatch.StartNew();
            log.WriteLine($"master {master.Name}: encoding {master.Seconds}s {master.Width}x{master.Height} {master.Codec}");
            try
            {
                await EncodeMasterAsync(master, index, temporary, gpu: master.Codec != "mpeg4", cancellationToken);
            }
            catch (InvalidOperationException) when (master.Codec != "mpeg4")
            {
                log.WriteLine($"master {master.Name}: GPU encoder unavailable, falling back to CPU");
                await EncodeMasterAsync(master, index, temporary, gpu: false, cancellationToken);
            }
            File.Move(temporary, path, overwrite: true);
            log.WriteLine($"master {master.Name}: {new FileInfo(path).Length / 1048576} MiB in {stopwatch.Elapsed.TotalSeconds:F0}s");
        }
    }

    public async Task<IReadOnlyList<GeneratedClip>> GenerateSetAsync(string setName, SetProfile profile, int seed, CancellationToken cancellationToken)
    {
        await EnsureMastersAsync(cancellationToken);
        var folder = lab.SetFolder(setName);
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            throw new InvalidOperationException("Set folder already exists and is not empty: " + folder);
        Directory.CreateDirectory(folder);
        var random = new Random(seed);
        var clips = new List<GeneratedClip>();
        var copies = (int)Math.Round(profile.Count * profile.ExactCopyShare);
        var reencodes = (int)Math.Round(profile.Count * profile.ReencodeShare);
        var originals = profile.Count - copies - reencodes;
        var stopwatch = Stopwatch.StartNew();
        for (var index = 0; index < originals; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plan = PlanOriginal(random, profile);
            var name = $"clip-{index + 1:D4}.{plan.Container}";
            var destination = Path.Combine(folder, name);
            await CutAsync(plan.Master, plan.Offset, plan.Duration, plan.FastStart, destination, cancellationToken);
            clips.Add(new GeneratedClip(name, "original", plan.Master.Name, plan.Offset, plan.Duration, plan.Container, plan.FastStart, new FileInfo(destination).Length));
            if ((index + 1) % 25 == 0) log.WriteLine($"set {setName}: {index + 1}/{originals} originals ({stopwatch.Elapsed.TotalSeconds:F0}s)");
        }
        var smallOriginals = clips.Where(clip => clip.Bytes < 100L * 1024 * 1024).ToList();
        for (var index = 0; index < copies && smallOriginals.Count > 0; index++)
        {
            var source = smallOriginals[random.Next(smallOriginals.Count)];
            var name = $"copy-{index + 1:D4}{Path.GetExtension(source.Name)}";
            File.Copy(Path.Combine(folder, source.Name), Path.Combine(folder, name));
            clips.Add(source with { Name = name, Kind = "exact-copy", Bytes = new FileInfo(Path.Combine(folder, name)).Length });
        }
        for (var index = 0; index < reencodes && smallOriginals.Count > 0; index++)
        {
            var source = smallOriginals[random.Next(smallOriginals.Count)];
            var name = $"reencode-{index + 1:D4}.mp4";
            await ReencodeAsync(Path.Combine(folder, source.Name), Path.Combine(folder, name), cancellationToken);
            clips.Add(source with { Name = name, Kind = "reencode", Container = "mp4", FastStart = true, Bytes = new FileInfo(Path.Combine(folder, name)).Length });
        }
        var manifest = new
        {
            set = setName,
            profile,
            seed,
            generatedAt = DateTimeOffset.Now,
            totalBytes = clips.Sum(clip => clip.Bytes),
            sizeClasses = new
            {
                under100MiB = clips.Count(clip => clip.Bytes < 100L * 1024 * 1024),
                from100MiBTo1GiB = clips.Count(clip => clip.Bytes >= 100L * 1024 * 1024 && clip.Bytes < 1024L * 1024 * 1024),
                atLeast1GiB = clips.Count(clip => clip.Bytes >= 1024L * 1024 * 1024)
            },
            clips
        };
        await File.WriteAllTextAsync(Path.Combine(lab.SetsRoot, setName + ".manifest.json"), JsonSerializer.Serialize(manifest, Json.Indented), cancellationToken);
        log.WriteLine($"set {setName}: {clips.Count} files, {clips.Sum(clip => clip.Bytes) / 1073741824.0:F1} GiB in {stopwatch.Elapsed.TotalMinutes:F1} min");
        return clips;
    }

    private static (MasterSpec Master, double Offset, double Duration, string Container, bool FastStart) PlanOriginal(Random random, SetProfile profile)
    {
        var roll = random.NextDouble();
        MasterSpec master;
        double duration;
        if (roll < profile.LargeShare)
        {
            master = Find("m1080-h264");
            duration = 1800 + random.Next(1200);
        }
        else if (roll < profile.LargeShare + profile.UhdShare)
        {
            master = Find("m2160-h264");
            duration = 60 + random.Next(120);
        }
        else if (roll < profile.LargeShare + profile.UhdShare + profile.MediumShare)
        {
            master = Pick(random, ("m1080-h264", 0.45), ("m1080-hevc", 0.30), ("m720-h264", 0.25));
            duration = 300 + random.Next(600);
        }
        else
        {
            master = Pick(random, ("m720-h264", 0.35), ("m1080-h264", 0.35), ("mvert-h264", 0.15), ("m1080-hevc", 0.10), ("m480-mpeg4", 0.05));
            duration = 20 + random.Next(160);
        }
        var offset = duration < master.Seconds ? random.Next(0, (int)(master.Seconds - duration)) : 0;
        var container = master.Extension == "avi" ? "avi" : PickName(random, ("mp4", 0.75), ("mkv", 0.15), ("mov", 0.10));
        return (master, offset, duration, container, container is "mp4" or "mov" && random.NextDouble() < 0.5);
    }

    private static MasterSpec Pick(Random random, params (string Name, double Weight)[] choices) => Find(PickName(random, choices));

    private static string PickName(Random random, params (string Name, double Weight)[] choices)
    {
        var roll = random.NextDouble() * choices.Sum(choice => choice.Weight);
        foreach (var choice in choices)
        {
            if (roll < choice.Weight) return choice.Name;
            roll -= choice.Weight;
        }
        return choices[^1].Name;
    }

    private static MasterSpec Find(string name) => Masters.First(master => master.Name == name);

    private string MasterPath(MasterSpec master) => Path.Combine(lab.MastersRoot, master.Name + "." + master.Extension);

    private Task EncodeMasterAsync(MasterSpec master, int index, string destination, bool gpu, CancellationToken cancellationToken)
    {
        var bitrate = master.BitrateMbps + "M";
        var arguments = new List<string>
        {
            "-hide_banner", "-nostdin", "-y",
            "-f", "lavfi", "-i", $"testsrc2=size={master.Width}x{master.Height}:rate={master.Fps},hue=h=t*{7 + index * 5}+{index * 60},noise=alls=10:allf=t",
            "-f", "lavfi", "-i", $"sine=frequency={330 + index * 70}:sample_rate=48000",
            "-t", master.Seconds.ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p"
        };
        arguments.AddRange(master.Codec switch
        {
            "mpeg4" => ["-c:v", "mpeg4", "-b:v", bitrate, "-g", "250"],
            "hevc" when gpu => ["-c:v", "hevc_nvenc", "-preset", "p2", "-rc", "cbr", "-b:v", bitrate, "-g", "120", "-tag:v", "hvc1"],
            "hevc" => ["-c:v", "libx265", "-preset", "ultrafast", "-b:v", bitrate, "-x265-params", "keyint=120", "-tag:v", "hvc1"],
            _ when gpu => ["-c:v", "h264_nvenc", "-preset", "p2", "-rc", "cbr", "-b:v", bitrate, "-g", index % 2 == 0 ? "60" : "250"],
            _ => ["-c:v", "libx264", "-preset", "ultrafast", "-b:v", bitrate, "-g", index % 2 == 0 ? "60" : "250"]
        });
        arguments.AddRange(master.Codec == "mpeg4" ? ["-c:a", "libmp3lame", "-b:a", "128k"] : ["-c:a", "aac", "-b:a", "128k"]);
        arguments.Add(destination);
        return RunFfmpegAsync(arguments, cancellationToken);
    }

    private Task CutAsync(MasterSpec master, double offset, double duration, bool fastStart, string destination, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-y" };
        if (duration > master.Seconds) arguments.AddRange(["-stream_loop", "-1"]);
        arguments.AddRange(["-ss", offset.ToString(CultureInfo.InvariantCulture), "-i", MasterPath(master),
            "-t", duration.ToString(CultureInfo.InvariantCulture), "-map", "0", "-c", "copy"]);
        if (fastStart) arguments.AddRange(["-movflags", "+faststart"]);
        arguments.Add(destination);
        return RunFfmpegAsync(arguments, cancellationToken);
    }

    private Task ReencodeAsync(string source, string destination, CancellationToken cancellationToken) => RunFfmpegAsync(
        ["-hide_banner", "-nostdin", "-y", "-i", source, "-vf", "scale=-2:540", "-c:v", "h264_nvenc", "-preset", "p2", "-b:v", "2M",
            "-c:a", "aac", "-b:a", "96k", "-movflags", "+faststart", destination], cancellationToken);

    private async Task RunFfmpegAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(lab.Ffmpeg) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg did not start.");
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var error = await stderr;
        _ = await stdout;
        if (process.ExitCode != 0)
        {
            var output = arguments[^1];
            if (File.Exists(output)) File.Delete(output);
            throw new InvalidOperationException("ffmpeg failed: " + error[Math.Max(0, error.Length - 800)..]);
        }
    }
}
