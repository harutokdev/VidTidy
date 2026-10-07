using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalMediaAI.Core;

namespace LocalMediaAI.Infrastructure;

public sealed record AppPaths(
    string AppRoot,
    string DataRoot,
    string CacheRoot,
    string ModelsRoot,
    string RuntimeRoot,
    string TempRoot)
{
    public string DatabasePath => Path.Combine(DataRoot, "library.db");
    public string LogRoot => Path.Combine(DataRoot, "logs");
    public string ThumbnailRoot => Path.Combine(CacheRoot, "thumbnails");
    public string VisualSampleRoot => Path.Combine(CacheRoot, "visual-samples");

    /// <summary>Non-null only for a test-lab instance; confines media reads and storage to the lab folders.</summary>
    public TestLabPolicy? TestLab { get; init; }

    /// <summary>The folder named by data-location.json when the install uses one; null for portable or per-user data.</summary>
    public string? DataLocationRoot { get; init; }

    /// <summary>
    /// A data location only ever comes from a relocation, so its catalog must already be there. Refuse to start
    /// rather than create an empty library when the drive is disconnected or the folder was removed.
    /// </summary>
    public void EnsureDataLocationPresent()
    {
        if (DataLocationRoot is not null && !File.Exists(DatabasePath))
            throw new DataLocationException(L.F("データの保存先（{0}）にライブラリが見つかりません。ドライブが接続されているか、フォルダーが移動・削除されていないか確認してください。", DataLocationRoot));
    }

    public static AppPaths Create(string appRoot, IReadOnlyDictionary<string, string>? overrides = null, TestLabPolicy? testLab = null)
    {
        var paths = CreateUnchecked(appRoot, overrides);
        var policy = testLab ?? TestLabPolicy.TryLoad(paths.AppRoot);
        if (policy is null) return paths;
        policy.EnsureInstanceAllowed(paths);
        return paths with { TestLab = policy };
    }

    private static AppPaths CreateUnchecked(string appRoot, IReadOnlyDictionary<string, string>? overrides)
    {
        var root = Path.GetFullPath(appRoot);
        var portable = File.Exists(Path.Combine(root, "portable.flag"));
        var location = portable ? null : DataLocation.TryRead(root);
        var dataHome = location ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalMediaAI");
        var defaults = portable
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["DataRoot"] = Path.Combine(root, "data"),
                ["CacheRoot"] = Path.Combine(root, "cache"),
                ["ModelsRoot"] = Path.Combine(root, "models"),
                ["RuntimeRoot"] = Path.Combine(root, "runtime"),
                ["TempRoot"] = Path.Combine(root, "temp")
            }
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["DataRoot"] = Path.Combine(dataHome, "data"),
                ["CacheRoot"] = Path.Combine(dataHome, "cache"),
                ["ModelsRoot"] = Path.Combine(root, "models"),
                ["RuntimeRoot"] = Path.Combine(root, "runtime"),
                ["TempRoot"] = Path.Combine(dataHome, "temp")
            };
        string Resolve(string key) => Path.GetFullPath(overrides is not null && overrides.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : defaults[key]);
        return new AppPaths(root, Resolve("DataRoot"), Resolve("CacheRoot"), Resolve("ModelsRoot"), Resolve("RuntimeRoot"), Resolve("TempRoot")) { DataLocationRoot = location };
    }

    public void EnsureDirectories()
    {
        foreach (var path in new[] { DataRoot, CacheRoot, ModelsRoot, RuntimeRoot, TempRoot, LogRoot, ThumbnailRoot, VisualSampleRoot, Path.Combine(DataRoot, "artifacts") })
            Directory.CreateDirectory(path);
    }
}

public static class PathPolicy
{
    public static string NormalizeForIdentity(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }

    public static bool IsWithinRoot(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedCandidate = Path.GetFullPath(candidate);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return normalizedCandidate.StartsWith(normalizedRoot, comparison);
    }

    public static bool IsReparsePoint(FileSystemInfo info) => (info.Attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>
    /// Fails closed when any existing directory from the authorized root through the candidate parent
    /// is a reparse point. A lexical prefix check alone cannot make a junction/symlink escape safe.
    /// </summary>
    public static bool HasReparsePointAncestor(string root, string candidate)
    {
        if (!IsWithinRoot(root, candidate)) return true;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = Path.GetDirectoryName(Path.GetFullPath(candidate));
        while (!string.IsNullOrWhiteSpace(current))
        {
            try
            {
                if (IsReparsePoint(new DirectoryInfo(current))) return true;
            }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
            if (string.Equals(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), normalizedRoot, comparison)) return false;
            var parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, comparison)) return true;
            current = parent;
        }
        return true;
    }
}

public sealed class JsonFileLogger : IStructuredLogger, IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;

    public JsonFileLogger(AppPaths paths)
    {
        paths.EnsureDirectories();
        // Multiple isolated pipeline components can legitimately share an AppPaths root.
        // Give each writer a unique file identity rather than allowing two writers to contend
        // for a date-only JSONL name; readers can still enumerate the log directory safely.
        var path = Path.Combine(paths.LogRoot, $"localmediaai-{DateTime.UtcNow:yyyyMMdd}-{Environment.ProcessId}-{Guid.NewGuid():N}.jsonl");
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
    }

    public void Write(string level, string eventName, object fields, Exception? exception = null)
    {
        var record = new
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = level,
            EventName = eventName,
            Fields = fields,
            Exception = exception is null ? null : new { exception.GetType().FullName, exception.Message, exception.StackTrace }
        };
        lock (_gate) _writer.WriteLine(JsonSerializer.Serialize(record));
    }

    public void Dispose() => _writer.Dispose();
}

public static class FileHash
{
    public static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
