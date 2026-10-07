using System.Globalization;
using LocalMediaAI.Infrastructure;
using ThroughputLab;

// ThroughputLab — analysis throughput / HDD load measurement on test-lab media only.
//   masters  --lab <root>
//   generate --lab <root> --set <name> --profile smoke|standard|soak [--count N] [--seed N]
//   run      --lab <root> --set <name|real> --storage hdd|ssd --label <label> [--pace fastest|standard|quiet] [--no-evict] [--keep-instance] [--timeout-minutes N]
//   soak     --lab <root> --set <name> [--idle-minutes 10] [--work-minutes 60] [--rest-minutes 30]
//   disks    --lab <root>

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: ThroughputLab <masters|generate|run|soak|disks> --lab <root> ...");
    return 2;
}

var options = ParseOptions(args.Skip(1).ToArray());
var lab = new LabLayout(Required(options, "lab"), FindRepoRoot());
lab.EnsureCreated();
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
var log = Console.Out;

switch (args[0])
{
    case "masters":
        await new SyntheticMedia(lab, log).EnsureMastersAsync(cancellation.Token);
        return 0;
    case "generate":
        var profile = SyntheticMedia.Profile(Required(options, "profile"), OptionalInt(options, "count"));
        await new SyntheticMedia(lab, log).GenerateSetAsync(Required(options, "set"), profile, OptionalInt(options, "seed") ?? 1, cancellation.Token);
        return 0;
    case "run":
        await new ThroughputRun(lab, log).ExecuteAsync(new RunOptions(
            Required(options, "set"), Required(options, "storage"), Required(options, "label"),
            Evict: !options.ContainsKey("no-evict"), KeepInstance: options.ContainsKey("keep-instance"),
            Timeout: TimeSpan.FromMinutes(OptionalInt(options, "timeout-minutes") ?? 360),
            Pace: options.TryGetValue("pace", out var pace) ? Enum.Parse<LocalMediaAI.Core.Processing.ProcessingPace>(pace, ignoreCase: true) : LocalMediaAI.Core.Processing.ProcessingPace.Fastest), cancellation.Token);
        return 0;
    case "soak":
        await new SoakProtocol(lab, log).ExecuteAsync(new SoakOptions(Required(options, "set"),
            TimeSpan.FromMinutes(OptionalInt(options, "idle-minutes") ?? 10),
            TimeSpan.FromMinutes(OptionalInt(options, "work-minutes") ?? 60),
            TimeSpan.FromMinutes(OptionalInt(options, "rest-minutes") ?? 30)), cancellation.Token);
        return 0;
    case "disks":
        foreach (var (name, path) in new[] { ("lab media", lab.SyntheticRoot), ("hdd instances", lab.HddInstances), ("ssd instances", lab.SsdInstances) })
            log.WriteLine($"{name}: disk {PhysicalDisk.TryGetDiskNumber(path)?.ToString(CultureInfo.InvariantCulture) ?? "?"}");
        return 0;
    default:
        Console.Error.WriteLine("unknown command: " + args[0]);
        return 2;
}

static Dictionary<string, string> ParseOptions(string[] arguments)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < arguments.Length; index++)
    {
        if (!arguments[index].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("unexpected argument: " + arguments[index]);
        var key = arguments[index][2..];
        var hasValue = index + 1 < arguments.Length && !arguments[index + 1].StartsWith("--", StringComparison.Ordinal);
        result[key] = hasValue ? arguments[++index] : "true";
    }
    return result;
}

static string Required(Dictionary<string, string> options, string key) =>
    options.TryGetValue(key, out var value) ? value : throw new ArgumentException($"--{key} is required");

static int? OptionalInt(Dictionary<string, string> options, string key) =>
    options.TryGetValue(key, out var value) ? int.Parse(value, CultureInfo.InvariantCulture) : null;

static string FindRepoRoot()
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "LocalMediaAI.sln"))) return directory.FullName;
    throw new InvalidOperationException("Run ThroughputLab from inside the repository.");
}
