using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure;

public sealed record RelocationPlan(int FileCount, long TotalBytes, long DestinationFreeBytes, IReadOnlyList<string> Blockers)
{
    public bool CanExecute => Blockers.Count == 0;
}

public sealed record RelocationResult(bool Succeeded, int FilesCopied, long BytesCopied, bool IntegrityOk, long SchemaVersion, string? Failure, bool LeftPartialDestination = false);

/// <summary>
/// Moves a portable install's catalog (data/) and cache (cache/) to the per-user location or to a folder of the
/// user's choosing (then named in data-location.json).
/// Copy into staging folders while the source catalog is held read-only, verify every file by SHA-256,
/// confirm nothing in the source changed, run integrity_check on the copy, then publish with same-volume
/// renames that are undone if any later step fails. The source is never modified. Switching the install
/// over is a separate, reversible step (portable.flag renamed aside, plus data-location.json for a chosen folder).
/// Output is counts and sizes only.
/// </summary>
public sealed class PortableDataRelocation
{
    public const string MarkerFileName = "relocation.json";
    public const string DisabledPortableFlag = "portable.flag.relocated";
    private static readonly string[] Trees = ["data", "cache"];
    private static readonly JsonSerializerOptions MarkerJson = new() { WriteIndented = true };
    private static readonly EnumerationOptions Walk = new() { RecurseSubdirectories = true, AttributesToSkip = 0, ReturnSpecialDirectories = false };
    private readonly string _appRoot;
    private readonly string _destinationRoot;
    private readonly string _productionRoot;

    public PortableDataRelocation(string portableAppRoot, string destinationRoot, string? productionRoot = null)
    {
        _appRoot = Path.GetFullPath(portableAppRoot);
        _destinationRoot = Path.GetFullPath(destinationRoot);
        _productionRoot = Path.GetFullPath(productionRoot ?? TestLabPolicy.ProductionUserDataRoot);
    }

    internal Action<string>? BeforePublishTreeForTests { get; set; }
    internal Action? AfterCopyForTests { get; set; }

    private string PortableFlag => Path.Combine(_appRoot, "portable.flag");
    private string Marker => Path.Combine(_destinationRoot, MarkerFileName);
    private string SourceDatabase => Path.Combine(_appRoot, "data", "library.db");

    public RelocationPlan Plan()
    {
        var blockers = new List<string>();
        if (!File.Exists(PortableFlag)) blockers.Add("The source install is not in portable mode (portable.flag is missing).");
        if (!File.Exists(SourceDatabase)) blockers.Add("The source catalog data/library.db does not exist.");
        else if (IsInUse(SourceDatabase)) blockers.Add("The source catalog is open. Close VidTidy first.");
        foreach (var tree in Trees)
        {
            var destination = Path.Combine(_destinationRoot, tree);
            if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
                blockers.Add($"The destination {tree} folder already has content; nothing is overwritten.");
        }
        if (Directory.Exists(_destinationRoot) && Directory.EnumerateDirectories(_destinationRoot, "*.relocating-*").Any())
            blockers.Add("An earlier interrupted relocation left staging folders at the destination; remove them first.");
        if (IsSameOrWithin(_appRoot, _destinationRoot) || IsSameOrWithin(_destinationRoot, _appRoot))
            blockers.Add("Source and destination must not contain each other.");
        try { DataLocation.Validate(_destinationRoot); }
        catch (DataLocationException) { blockers.Add("The destination must be a folder, not a drive root."); }
        if (SourceEntries().Any(entry => (entry.Attributes & FileAttributes.ReparsePoint) != 0))
            blockers.Add("The source data or cache contains a junction or link; relocation would not copy it faithfully.");

        var files = SourceFiles().ToList();
        var bytes = files.Sum(file => file.Length);
        long free;
        try { free = new DriveInfo(Path.GetPathRoot(_destinationRoot)!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { free = 0; blockers.Add("The destination drive is not available."); }
        if (free > 0 && free < bytes + bytes / 10 + 1024L * 1024 * 1024) blockers.Add("The destination drive does not have enough free space.");
        return new RelocationPlan(files.Count, bytes, free, blockers);
    }

    public async Task<RelocationResult> ExecuteAsync(IProgress<(int Files, long Bytes)>? progress, CancellationToken cancellationToken)
    {
        var plan = Plan();
        if (!plan.CanExecute) return new RelocationResult(false, 0, 0, false, 0, string.Join(" ", plan.Blockers));
        var suffix = ".relocating-" + Guid.NewGuid().ToString("N")[..8];
        var staging = Trees.ToDictionary(tree => tree, tree => Path.Combine(_destinationRoot, tree + suffix));
        var published = new List<string>();
        var copied = 0;
        long copiedBytes = 0;
        try
        {
            // Holding the catalog with read sharing keeps the app from opening it for writing mid-copy.
            await using var catalogGuard = new FileStream(SourceDatabase, FileMode.Open, FileAccess.Read, FileShare.Read);
            var before = SourceFiles().ToDictionary(file => file.FullName, file => (file.Length, file.LastWriteTimeUtc), StringComparer.OrdinalIgnoreCase);
            Directory.CreateDirectory(_destinationRoot);
            foreach (var tree in Trees)
            {
                var sourceTree = Path.Combine(_appRoot, tree);
                Directory.CreateDirectory(staging[tree]);
                if (!Directory.Exists(sourceTree)) continue;
                foreach (var file in new DirectoryInfo(sourceTree).EnumerateFiles("*", Walk))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await CopyVerifiedAsync(file, Path.Combine(staging[tree], Path.GetRelativePath(sourceTree, file.FullName)), cancellationToken);
                    copied++;
                    copiedBytes += file.Length;
                    progress?.Report((copied, copiedBytes));
                }
            }
            AfterCopyForTests?.Invoke();
            var after = SourceFiles().ToDictionary(file => file.FullName, file => (file.Length, file.LastWriteTimeUtc), StringComparer.OrdinalIgnoreCase);
            if (before.Count != after.Count || before.Any(pair => !after.TryGetValue(pair.Key, out var now) || now != pair.Value))
                throw new InvalidDataException("The source changed while it was being copied.");
            var (integrity, schema) = await CheckCatalogAsync(Path.Combine(staging["data"], "library.db"), cancellationToken);
            if (!integrity) throw new InvalidDataException("The copied catalog failed integrity_check.");
            var sourceDatabase = Describe(SourceDatabase);

            foreach (var tree in Trees)
            {
                BeforePublishTreeForTests?.Invoke(tree);
                var final = Path.Combine(_destinationRoot, tree);
                if (Directory.Exists(final)) Directory.Delete(final, recursive: false); // empty, per Plan()
                await MoveWithRetryAsync(staging[tree], final, cancellationToken);
                published.Add(tree);
            }
            await WriteMarkerAsync(new MarkerDocument(_appRoot, DateTimeOffset.UtcNow, copied, copiedBytes, schema, sourceDatabase), cancellationToken);
            return new RelocationResult(true, copied, copiedBytes, true, schema, null);
        }
        catch (Exception ex)
        {
            SqliteConnection.ClearAllPools();
            var partial = await UnpublishAsync(published, staging);
            foreach (var folder in staging.Values) TryDeleteTree(folder);
            var failure = ex.GetType().Name + ": " + ex.Message;
            return new RelocationResult(false, copied, copiedBytes, false, 0,
                partial.Count == 0 ? failure + " Nothing was published." : failure + " Could not undo: " + string.Join(", ", partial) + " — remove these destination folders by hand before retrying.",
                LeftPartialDestination: partial.Count > 0);
        }
    }

    /// <summary>
    /// Points the install at the relocated data: portable.flag is renamed aside and, unless the data went to the
    /// per-user folder, data-location.json names the destination. Only allowed when the relocation came from this
    /// install and the source catalog has not changed since it was copied.
    /// </summary>
    public void Switch()
    {
        var perUser = IsSame(_destinationRoot, _productionRoot);
        var marker = ReadMarker() ?? throw new InvalidOperationException("No completed relocation was found at the destination.");
        if (!IsSame(marker.SourceAppRoot, _appRoot)) throw new InvalidOperationException("The relocated data came from a different install.");
        if (!File.Exists(Path.Combine(_destinationRoot, "data", "library.db")) || !Directory.Exists(Path.Combine(_destinationRoot, "cache")))
            throw new InvalidOperationException("The relocated catalog or cache is missing at the destination.");
        if (!File.Exists(PortableFlag)) throw new InvalidOperationException("The install is already switched.");
        var current = Describe(SourceDatabase);
        if (current != marker.SourceDatabase)
            throw new InvalidOperationException("The portable catalog changed after it was relocated; relocate again before switching.");
        // The pointer goes in first: portable.flag still wins until it is renamed, so no step leaves the app pointing
        // at the wrong data. A stale pointer would outrank the per-user folder, so that switch sets it aside.
        if (perUser) DataLocation.Disable(_appRoot);
        else DataLocation.Write(_appRoot, _destinationRoot);
        try { File.Move(PortableFlag, Path.Combine(_appRoot, DisabledPortableFlag)); }
        catch
        {
            if (!perUser) DataLocation.Disable(_appRoot);
            throw;
        }
    }

    /// <summary>
    /// Returns the install to its untouched portable data. Refuses when the relocated catalog has been used since the
    /// relocation (that work would be hidden), unless <paramref name="force"/> is set. Relocated data is left in place.
    /// </summary>
    public void Rollback(bool force = false)
    {
        var disabled = Path.Combine(_appRoot, DisabledPortableFlag);
        if (!File.Exists(disabled)) throw new InvalidOperationException("The install was not switched by a relocation.");
        var marker = ReadMarker();
        var relocatedCatalog = new FileInfo(Path.Combine(_destinationRoot, "data", "library.db"));
        if (!force && marker is not null && relocatedCatalog.Exists && relocatedCatalog.LastWriteTimeUtc > marker.RelocatedAt.UtcDateTime.AddMinutes(1))
            throw new InvalidOperationException("The relocated catalog has changed since the relocation; rolling back would hide that work. Use --force to roll back anyway.");
        File.Move(disabled, PortableFlag);
        DataLocation.Disable(_appRoot);
    }

    private sealed record FileDescription(long Length, DateTime LastWriteTimeUtc, string Sha256);

    private sealed record MarkerDocument(string SourceAppRoot, DateTimeOffset RelocatedAt, int Files, long Bytes, long SchemaVersion, FileDescription SourceDatabase);

    private MarkerDocument? ReadMarker()
    {
        if (!File.Exists(Marker)) return null;
        try { return JsonSerializer.Deserialize<MarkerDocument>(File.ReadAllText(Marker), MarkerJson); }
        catch (JsonException) { return null; }
    }

    private async Task WriteMarkerAsync(MarkerDocument marker, CancellationToken cancellationToken)
    {
        var temporary = Marker + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, marker, MarkerJson, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, Marker, overwrite: true);
    }

    private async Task<List<string>> UnpublishAsync(List<string> published, Dictionary<string, string> staging)
    {
        var stuck = new List<string>();
        foreach (var tree in published)
        {
            var final = Path.Combine(_destinationRoot, tree);
            try { await MoveWithRetryAsync(final, staging[tree], CancellationToken.None); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { stuck.Add(final); }
        }
        TryDeleteFile(Marker + ".tmp");
        return stuck;
    }

    /// <summary>Directory renames fail while a scanner (Defender, indexer) holds a just-written file; ride that out briefly.</summary>
    private static async Task MoveWithRetryAsync(string from, string to, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { Directory.Move(from, to); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 8 && !Directory.Exists(to))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken);
            }
        }
    }

    private IEnumerable<FileSystemInfo> SourceEntries() => Trees
        .Select(tree => Path.Combine(_appRoot, tree))
        .Where(Directory.Exists)
        .SelectMany(folder => new DirectoryInfo(folder).EnumerateFileSystemInfos("*", Walk));

    private IEnumerable<FileInfo> SourceFiles() => SourceEntries().OfType<FileInfo>();

    private static async Task CopyVerifiedAsync(FileInfo source, string target, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        byte[] sourceHash;
        await using (var input = new FileStream(source.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
        await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024))
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[1024 * 1024];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
            sourceHash = hash.GetHashAndReset();
        }
        byte[] targetHash;
        await using (var verify = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
            targetHash = await SHA256.HashDataAsync(verify, cancellationToken);
        if (!sourceHash.AsSpan().SequenceEqual(targetHash)) throw new InvalidDataException("A copied file did not match its source.");
        File.SetLastWriteTimeUtc(target, source.LastWriteTimeUtc);
    }

    private static FileDescription Describe(string path)
    {
        var info = new FileInfo(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, FileOptions.SequentialScan);
        return new FileDescription(info.Length, info.LastWriteTimeUtc, Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static async Task<(bool Integrity, long Schema)> CheckCatalogAsync(string database, CancellationToken cancellationToken)
    {
        if (!File.Exists(database)) return (false, 0);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var integrity = connection.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check;";
        var ok = string.Equals(Convert.ToString(await integrity.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture), "ok", StringComparison.OrdinalIgnoreCase);
        await using var schema = connection.CreateCommand();
        schema.CommandText = "SELECT COALESCE(MAX(version),0) FROM schema_migration;";
        var version = Convert.ToInt64(await schema.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        return (ok, version);
    }

    private static bool IsInUse(string database)
    {
        try
        {
            using var probe = new FileStream(database, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static bool IsSame(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrWithin(string root, string candidate) => IsSame(root, candidate) || PathPolicy.IsWithinRoot(root, candidate);

    private static void TryDeleteTree(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteFile(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
