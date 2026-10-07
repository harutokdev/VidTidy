using System.Globalization;
using LocalMediaAI.Infrastructure;

// DataRelocation — move a portable install's data/ and cache/ to the per-user location or a chosen folder.
// Run it from an ordinary terminal: hosts that virtualize %LOCALAPPDATA% (packaged apps) would redirect the copy.
//   plan     --app <portable install folder> [--destination <folder>]
//   execute  --app <folder> [--destination <folder>] --yes
//   switch   --app <folder> [--destination <folder>]   (disables portable.flag; writes data-location.json for a chosen folder)
//   rollback --app <folder> [--force]                   (restores portable.flag and sets data-location.json aside)

if (args.Length < 3 || args[1] != "--app")
{
    Console.Error.WriteLine("usage: DataRelocation <plan|execute|switch|rollback> --app <folder> [--destination <folder>] [--yes] [--force]");
    return 2;
}

var app = args[2];
// Rollback without --destination checks the folder the install currently points at.
var destination = (args[0] == "rollback" ? DataLocation.TryRead(app) : null) ?? TestLabPolicy.ProductionUserDataRoot;
var destinationIndex = Array.IndexOf(args, "--destination");
if (destinationIndex > 0)
{
    if (destinationIndex + 1 >= args.Length || args[destinationIndex + 1].StartsWith("--", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("--destination needs a folder");
        return 2;
    }
    destination = args[destinationIndex + 1];
}
var relocation = new PortableDataRelocation(app, destination);
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };

try
{
    switch (args[0])
    {
        case "plan":
            return Report(relocation.Plan());
        case "execute":
            if (!args.Contains("--yes")) { Console.Error.WriteLine("execute requires --yes"); return 2; }
            if (Report(relocation.Plan()) != 0) return 1;
            var lastReport = DateTime.UtcNow;
            var result = await relocation.ExecuteAsync(new Progress<(int Files, long Bytes)>(value =>
            {
                if (DateTime.UtcNow - lastReport < TimeSpan.FromSeconds(5)) return;
                lastReport = DateTime.UtcNow;
                Console.WriteLine($"  copied {value.Files} files, {value.Bytes / 1048576.0:F0} MiB");
            }), cancellation.Token);
            Console.WriteLine(result.Succeeded
                ? $"relocated {result.FilesCopied} files, {result.BytesCopied / 1048576.0:F1} MiB to {destination}; integrity ok; schema {result.SchemaVersion}. The source was not changed."
                : "relocation failed: " + result.Failure);
            return result.Succeeded ? 0 : 1;
        case "switch":
            relocation.Switch();
            Console.WriteLine("portable.flag disabled; the app now uses " + Path.GetFullPath(destination));
            return 0;
        case "rollback":
            relocation.Rollback(force: args.Contains("--force"));
            Console.WriteLine("portable.flag restored; the app uses its original portable data again");
            return 0;
        default:
            Console.Error.WriteLine("unknown command: " + args[0]);
            return 2;
    }
}
catch (Exception ex) when (ex is InvalidOperationException or DataLocationException)
{
    Console.Error.WriteLine("refused: " + ex.Message);
    return 1;
}

static int Report(RelocationPlan plan)
{
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"files {plan.FileCount}, {plan.TotalBytes / 1048576.0:F1} MiB; destination free {plan.DestinationFreeBytes / 1073741824.0:F1} GiB"));
    foreach (var blocker in plan.Blockers) Console.WriteLine("BLOCKED: " + blocker);
    return plan.CanExecute ? 0 : 1;
}
