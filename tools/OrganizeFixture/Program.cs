using System.Diagnostics;
using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;

// Usage: OrganizeFixture <workDir> <ffmpeg.exe>
// Creates <workDir>\media (three folders of generated test videos with exact and similar duplicates)
// and <workDir>\app\data (a portable library already scanned and hashed). Copy the app build into <workDir>\app to run it.
if (args.Length < 2) { Console.Error.WriteLine("usage: OrganizeFixture <workDir> <ffmpeg.exe>"); return 2; }
var work = Path.GetFullPath(args[0]);
var ffmpeg = args[1];
var media = Path.Combine(work, "media");
var appRoot = Path.Combine(work, "app");
if (Directory.Exists(media) || Directory.Exists(Path.Combine(appRoot, "data"))) { Console.Error.WriteLine("work dir already has a fixture"); return 3; }
string Folder(string name) => Directory.CreateDirectory(Path.Combine(media, name)).FullName;
var sorted = Folder("整理済み");
var travel = Directory.CreateDirectory(Path.Combine(sorted, "旅行")).FullName;
var downloads = Folder("ダウンロード");
var backup = Folder("バックアップ2023");

void Make(string path, string source, string size, int seconds, int tone)
{
    var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardError = true };
    foreach (var a in new[] { "-y", "-loglevel", "error", "-f", "lavfi", "-i", $"{source}=size={size}:rate=30", "-f", "lavfi", "-i", $"sine=frequency={tone}:duration={seconds}", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-t", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), path })
        info.ArgumentList.Add(a);
    using var p = Process.Start(info)!;
    p.WaitForExit();
    if (p.ExitCode != 0) throw new InvalidOperationException(p.StandardError.ReadToEnd());
}
void Copy(string from, string to) => File.Copy(from, to);

Make(Path.Combine(sorted, "花火大会.mp4"), "mandelbrot", "640x360", 4, 440);
Copy(Path.Combine(sorted, "花火大会.mp4"), Path.Combine(sorted, "花火大会 - コピー.mp4"));
Make(Path.Combine(travel, "夏の旅行_沖縄.mp4"), "testsrc", "640x360", 3, 520);
Copy(Path.Combine(travel, "夏の旅行_沖縄.mp4"), Path.Combine(downloads, "夏の旅行_沖縄 (1).mp4"));
Copy(Path.Combine(travel, "夏の旅行_沖縄.mp4"), Path.Combine(backup, "夏の旅行_沖縄 - コピー.mp4"));
Make(Path.Combine(downloads, "運動会2023 (1).mp4"), "testsrc2", "1280x720", 6, 600);
Copy(Path.Combine(downloads, "運動会2023 (1).mp4"), Path.Combine(sorted, "運動会2023.mp4"));
Make(Path.Combine(downloads, "結婚式ダイジェスト.mp4"), "smptebars", "640x360", 3, 700);
Copy(Path.Combine(downloads, "結婚式ダイジェスト.mp4"), Path.Combine(backup, "結婚式ダイジェスト.mp4"));
// A similar pair: same picture, different resolution.
Make(Path.Combine(sorted, "卒業式.mp4"), "rgbtestsrc", "1280x720", 8, 800);
Make(Path.Combine(backup, "卒業式_縮小.mp4"), "rgbtestsrc", "640x360", 8, 800);

Directory.CreateDirectory(appRoot);
File.WriteAllText(Path.Combine(appRoot, "portable.flag"), string.Empty);
var paths = AppPaths.Create(appRoot);
paths.EnsureDirectories();
var log = new JsonFileLogger(paths);
var repo = new SqliteLibraryRepository(paths, log);
await repo.InitializeAsync(CancellationToken.None);
foreach (var dir in new[] { sorted, downloads, backup })
{
    var root = await repo.AddOrGetLibraryRootAsync(dir, CancellationToken.None);
    var scan = await repo.StartScanAsync(root.LibraryRootId, CancellationToken.None);
    var count = 0;
    await foreach (var scanned in new FileSystemMediaScanner().EnumerateAsync(root, CancellationToken.None))
    {
        await repo.UpsertScannedFileAsync(scanned, scan, CancellationToken.None);
        count++;
    }
    await repo.CompleteScanAsync(scan, new ScanCompletion("Completed", count, count, 0, 0, 0, DateTimeOffset.UtcNow), CancellationToken.None);
    var page = await repo.QueryMediaAsync(root.LibraryRootId, null, 0, 500, CancellationToken.None);
    foreach (var item in page.Items)
    {
        var file = (await repo.GetMediaFileAsync(item.MediaFileId, CancellationToken.None))!;
        var digest = await FileHash.Sha256Async(file.Path, CancellationToken.None);
        await repo.RecordFullHashEvidenceAsync(new FileHashEvidence(Guid.NewGuid(), file.MediaFileId, file.MediaContentId, "SHA-256", digest, file.SizeBytes, file.LastWriteTimeUtc, "full-sha256-v1", "Verified", null, null, DateTimeOffset.UtcNow), CancellationToken.None);
    }
    Console.WriteLine($"{Path.GetFileName(dir)}: {count} files");
}
await repo.RebuildExactDuplicateGroupsAsync(CancellationToken.None);
Console.WriteLine($"fixture ready: {work}");
return 0;
