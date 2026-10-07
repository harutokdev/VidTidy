using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalMediaAI.Core;

namespace LocalMediaAI.Infrastructure;

public sealed record AiResourceCacheResolution(
    string ModelsRoot,
    bool CacheHit,
    bool Populated,
    string Status,
    string? EntryName);

/// <summary>
/// Materializes the immutable, versioned semantic-model bundle under the current user's
/// app-owned fast cache. The bundled model remains authoritative and is never modified.
/// </summary>
public static partial class AiResourceCache
{
    private const int LayoutVersion = 1;
    private const string Owner = "Local Media AI";
    private const string Purpose = "immutable-ai-resource-cache";
    private const string ApprovedAdapterId = "llm-jp/llm-jp-clip-vit-base-patch16-openclip-adapter-v1";
    private const string ApprovedAdapterRevision = "phase32-openclip-local-adapter-v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<AiResourceCacheResolution> ResolveModelsRootAsync(
        AppPaths paths,
        SemanticModelProfile profile,
        IStructuredLogger log,
        CancellationToken cancellationToken,
        string? cacheRootOverride = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(log);
        var sourceModelsRoot = Path.GetFullPath(paths.ModelsRoot);
        if (paths.TestLab is not null)
        {
            // A test-lab instance never writes to (or prunes) the per-user production model cache.
            cacheRootOverride ??= Path.Combine(paths.CacheRoot, "ai-resource-cache");
            paths.TestLab.EnsureStoragePathAllowed(cacheRootOverride);
        }
        try
        {
            var descriptor = await LoadDescriptorAsync(sourceModelsRoot, profile, cancellationToken);
            var cacheRoot = ResolveCacheRoot(cacheRootOverride);
            EnsureOwnedCacheRoot(cacheRoot);
            var entryName = $"v{LayoutVersion}-{descriptor.IdentityHash[..24]}";
            var entryRoot = ContainedPath(cacheRoot, entryName);
            var cachedModelsRoot = ContainedPath(entryRoot, "models");

            if (ValidateCompletedEntry(cacheRoot, entryRoot, entryName, descriptor))
            {
                CleanupRecognizedOldEntries(cacheRoot, entryName);
                log.Write("Information", "AiResourceCacheHit", new { entryName, LayoutVersion });
                return new AiResourceCacheResolution(cachedModelsRoot, true, false, "CacheHit", entryName);
            }

            if (Directory.Exists(entryRoot))
            {
                log.Write("Warning", "AiResourceCacheFallback", new { Reason = "InvalidOrIncompleteVersionEntry", entryName });
                return new AiResourceCacheResolution(sourceModelsRoot, false, false, "SourceFallbackInvalidEntry", entryName);
            }

            var lockPath = ContainedPath(cacheRoot, "population.lock");
            await using var populationLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous);
            if (ValidateCompletedEntry(cacheRoot, entryRoot, entryName, descriptor))
                return new AiResourceCacheResolution(cachedModelsRoot, true, false, "CacheHitAfterWait", entryName);

            var temporaryRoot = ContainedPath(cacheRoot, $".populate-{entryName}-{Guid.NewGuid():N}");
            try
            {
                var temporaryModelsRoot = ContainedPath(temporaryRoot, "models");
                Directory.CreateDirectory(temporaryModelsRoot);
                var inventory = new List<CacheFileRecord>();
                foreach (var bundle in descriptor.Bundles)
                    await CopyBundleAsync(sourceModelsRoot, temporaryModelsRoot, bundle, inventory, cancellationToken);

                var marker = new CacheCompletionMarker(Owner, Purpose, LayoutVersion, entryName, descriptor.IdentityHash, descriptor.Identity, inventory, DateTimeOffset.UtcNow);
                await WriteJsonDurablyAsync(ContainedPath(temporaryRoot, "complete.json"), marker, cancellationToken);
                Directory.Move(temporaryRoot, entryRoot);
            }
            catch
            {
                DeleteExactTemporaryEntry(cacheRoot, temporaryRoot);
                throw;
            }

            if (!ValidateCompletedEntry(cacheRoot, entryRoot, entryName, descriptor))
                throw new InvalidDataException("Published AI resource cache entry failed validation.");
            CleanupRecognizedOldEntries(cacheRoot, entryName);
            log.Write("Information", "AiResourceCachePopulated", new { entryName, LayoutVersion, FileCount = descriptor.Files.Count, descriptor.TotalBytes });
            return new AiResourceCacheResolution(cachedModelsRoot, false, true, "Populated", entryName);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.Write("Warning", "AiResourceCacheFallback", new { Reason = ex.GetType().Name }, ex);
            return new AiResourceCacheResolution(sourceModelsRoot, false, false, "SourceFallback", null);
        }
    }

    public static string DefaultCacheRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LocalMediaAI",
        "ai-resource-cache");

    private static string ResolveCacheRoot(string? cacheRootOverride)
    {
        var localAppData = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        var cacheRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(cacheRootOverride) ? DefaultCacheRoot : cacheRootOverride);
        if (string.IsNullOrWhiteSpace(cacheRootOverride) && !PathPolicy.IsWithinRoot(localAppData, cacheRoot))
            throw new InvalidOperationException("AI resource cache root is outside LocalApplicationData.");
        return cacheRoot;
    }

    private static void EnsureOwnedCacheRoot(string cacheRoot)
    {
        var existed = Directory.Exists(cacheRoot);
        if (!existed) Directory.CreateDirectory(cacheRoot);
        if (PathPolicy.IsReparsePoint(new DirectoryInfo(cacheRoot)))
            throw new InvalidOperationException("AI resource cache root cannot be a reparse point.");
        var markerPath = ContainedPath(cacheRoot, "cache-root.json");
        if (File.Exists(markerPath))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(markerPath));
            var root = document.RootElement;
            if (root.GetProperty("owner").GetString() != Owner ||
                root.GetProperty("purpose").GetString() != Purpose ||
                root.GetProperty("layout_version").GetInt32() != LayoutVersion)
                throw new InvalidDataException("AI resource cache root identity is not recognized.");
            return;
        }
        if (existed && Directory.EnumerateFileSystemEntries(cacheRoot).Any())
            throw new InvalidDataException("An unrecognized non-empty AI resource cache root was found.");
        var temporaryMarker = ContainedPath(cacheRoot, $".cache-root-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temporaryMarker, JsonSerializer.Serialize(new { owner = Owner, purpose = Purpose, layout_version = LayoutVersion }, JsonOptions), new UTF8Encoding(false));
        File.Move(temporaryMarker, markerPath);
    }

    private static async Task<ResourceDescriptor> LoadDescriptorAsync(string sourceModelsRoot, SemanticModelProfile profile, CancellationToken cancellationToken)
    {
        var model = await LoadBundleAsync(sourceModelsRoot, profile.ModelDirectoryName, BundleKind.Model, profile, cancellationToken);
        if (string.IsNullOrWhiteSpace(profile.AdapterDirectoryName))
            throw new InvalidDataException("The approved semantic model requires its versioned local adapter bundle.");
        var adapter = await LoadBundleAsync(sourceModelsRoot, profile.AdapterDirectoryName, BundleKind.Adapter, profile, cancellationToken);
        var identity = string.Join("\n",
            $"layout={LayoutVersion}",
            $"profile={profile.ProfileId}",
            $"model={profile.ModelId}",
            $"revision={profile.ModelRevision}",
            $"processor={profile.ProcessorVersion}",
            $"model-directory={profile.ModelDirectoryName}",
            $"adapter-directory={profile.AdapterDirectoryName}",
            $"model-manifest={model.ManifestIdentity}",
            $"adapter-manifest={adapter.ManifestIdentity}");
        var identityHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        var files = model.Files.Concat(adapter.Files).ToArray();
        return new ResourceDescriptor(identity, identityHash, new[] { model, adapter }, files, files.Sum(item => item.Bytes));
    }

    private static async Task<ResourceBundle> LoadBundleAsync(string sourceModelsRoot, string directoryName, BundleKind kind, SemanticModelProfile profile, CancellationToken cancellationToken)
    {
        var bundleRoot = ContainedPath(sourceModelsRoot, directoryName);
        if (!Directory.Exists(bundleRoot) || PathPolicy.IsReparsePoint(new DirectoryInfo(bundleRoot)))
            throw new DirectoryNotFoundException("The authoritative AI resource bundle is unavailable.");
        var provenancePath = ContainedPath(bundleRoot, "provenance.json");
        if (!File.Exists(provenancePath)) throw new FileNotFoundException("AI resource provenance is unavailable.");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(provenancePath, cancellationToken));
        var root = document.RootElement;
        if (!root.TryGetProperty("runtime_network_allowed", out var network) || network.GetBoolean())
            throw new InvalidDataException("AI resource provenance does not prohibit runtime network access.");
        string manifestIdentity;
        if (kind == BundleKind.Model)
        {
            if (root.GetProperty("model_id").GetString() != profile.ModelId || root.GetProperty("revision").GetString() != profile.ModelRevision)
                throw new InvalidDataException("AI model provenance does not match the approved profile.");
            manifestIdentity = root.GetProperty("artifact_manifest_sha256").GetString() ?? throw new InvalidDataException("AI model manifest identity is missing.");
        }
        else
        {
            if (!root.TryGetProperty("pretrained_hf", out var pretrainedHf) || pretrainedHf.GetBoolean())
                throw new InvalidDataException("AI adapter provenance permits an unsupported remote pretrained source.");
            var adapterId = root.GetProperty("adapter_id").GetString() ?? throw new InvalidDataException("AI adapter identity is missing.");
            var adapterRevision = root.GetProperty("adapter_revision").GetString() ?? throw new InvalidDataException("AI adapter revision is missing.");
            if (adapterId != ApprovedAdapterId || adapterRevision != ApprovedAdapterRevision)
                throw new InvalidDataException("AI adapter provenance does not match the approved profile.");
            manifestIdentity = $"{adapterId}@{adapterRevision}";
        }

        var files = new List<SourceFileRecord>();
        foreach (var item in root.GetProperty("files").EnumerateArray())
        {
            var relativePath = NormalizeManifestPath(item.GetProperty("path").GetString());
            var bytes = item.GetProperty("bytes").GetInt64();
            var sha256 = item.GetProperty("sha256").GetString()?.ToLowerInvariant() ?? throw new InvalidDataException("AI resource hash is missing.");
            files.Add(new SourceFileRecord(directoryName, relativePath, bytes, sha256));
        }
        var provenanceHash = await FileHash.Sha256Async(provenancePath, cancellationToken);
        files.Add(new SourceFileRecord(directoryName, "provenance.json", new FileInfo(provenancePath).Length, provenanceHash));
        var canonicalManifest = string.Join("\n", files.OrderBy(item => item.RelativePath, StringComparer.Ordinal).Select(item => $"{item.RelativePath}|{item.Bytes}|{item.Sha256}"));
        var canonicalHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalManifest))).ToLowerInvariant();
        return new ResourceBundle(directoryName, $"{manifestIdentity}:{canonicalHash}", files);
    }

    private static async Task CopyBundleAsync(string sourceModelsRoot, string destinationModelsRoot, ResourceBundle bundle, List<CacheFileRecord> inventory, CancellationToken cancellationToken)
    {
        var sourceBundleRoot = ContainedPath(sourceModelsRoot, bundle.DirectoryName);
        var destinationBundleRoot = ContainedPath(destinationModelsRoot, bundle.DirectoryName);
        Directory.CreateDirectory(destinationBundleRoot);
        foreach (var file in bundle.Files)
        {
            var source = ContainedPath(sourceBundleRoot, file.RelativePath);
            if (!File.Exists(source) || PathPolicy.HasReparsePointAncestor(sourceBundleRoot, source))
                throw new InvalidDataException("AI resource source contains an unavailable or reparse-point file.");
            var destination = ContainedPath(destinationBundleRoot, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var (bytes, sha256) = await CopyAndHashAsync(source, destination, cancellationToken);
            if (bytes != file.Bytes || !string.Equals(sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("AI resource copy did not match its authoritative manifest.");
            inventory.Add(new CacheFileRecord(Path.Combine(bundle.DirectoryName, file.RelativePath).Replace('\\', '/'), bytes, sha256));
        }
    }

    private static async Task<(long Bytes, string Sha256)> CopyAndHashAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
        }
        await output.FlushAsync(cancellationToken);
        output.Flush(true);
        return (total, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static bool ValidateCompletedEntry(string cacheRoot, string entryRoot, string entryName, ResourceDescriptor descriptor)
    {
        try
        {
            if (!Directory.Exists(entryRoot) || !PathPolicy.IsWithinRoot(cacheRoot, entryRoot) || HasReparsePointInTree(entryRoot)) return false;
            var markerPath = ContainedPath(entryRoot, "complete.json");
            if (!File.Exists(markerPath)) return false;
            var marker = JsonSerializer.Deserialize<CacheCompletionMarker>(File.ReadAllText(markerPath), JsonOptions);
            if (marker is null || marker.Owner != Owner || marker.Purpose != Purpose || marker.LayoutVersion != LayoutVersion ||
                marker.EntryName != entryName || marker.IdentityHash != descriptor.IdentityHash || marker.Identity != descriptor.Identity ||
                marker.Files.Count != descriptor.Files.Count) return false;
            foreach (var file in marker.Files)
            {
                var full = ContainedPath(ContainedPath(entryRoot, "models"), file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full) || new FileInfo(full).Length != file.Bytes || PathPolicy.HasReparsePointAncestor(entryRoot, full)) return false;
            }
            return true;
        }
        catch (Exception) when (!System.Diagnostics.Debugger.IsAttached)
        {
            return false;
        }
    }

    private static void CleanupRecognizedOldEntries(string cacheRoot, string currentEntryName)
    {
        foreach (var directory in Directory.EnumerateDirectories(cacheRoot))
        {
            var name = Path.GetFileName(directory);
            if (name == currentEntryName || !VersionEntryName().IsMatch(name)) continue;
            try
            {
                if (!PathPolicy.IsWithinRoot(cacheRoot, directory) || HasReparsePointInTree(directory)) continue;
                var markerPath = ContainedPath(directory, "complete.json");
                if (!File.Exists(markerPath)) continue;
                var marker = JsonSerializer.Deserialize<CacheCompletionMarker>(File.ReadAllText(markerPath), JsonOptions);
                if (marker is null || marker.Owner != Owner || marker.Purpose != Purpose || marker.LayoutVersion != LayoutVersion || marker.EntryName != name) continue;
                Directory.Delete(directory, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (JsonException) { }
        }
    }

    private static void DeleteExactTemporaryEntry(string cacheRoot, string temporaryRoot)
    {
        try
        {
            var name = Path.GetFileName(temporaryRoot);
            if (!name.StartsWith(".populate-v", StringComparison.Ordinal) || !PathPolicy.IsWithinRoot(cacheRoot, temporaryRoot) || HasReparsePointInTree(temporaryRoot)) return;
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task WriteJsonDurablyAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(true);
    }

    private static bool HasReparsePointInTree(string root)
    {
        var rootInfo = new DirectoryInfo(root);
        if (PathPolicy.IsReparsePoint(rootInfo)) return true;
        return rootInfo.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
            .Any(PathPolicy.IsReparsePoint);
    }

    private static string NormalizeManifestPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) throw new InvalidDataException("AI resource manifest path is invalid.");
        var normalized = path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (normalized.Split(Path.DirectorySeparatorChar).Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("AI resource manifest path is invalid.");
        return normalized;
    }

    private static string ContainedPath(string root, string relative)
    {
        var combined = Path.GetFullPath(Path.Combine(root, relative));
        if (!PathPolicy.IsWithinRoot(root, combined)) throw new InvalidDataException("AI resource cache path escaped its authorized root.");
        return combined;
    }

    [GeneratedRegex("^v1-[0-9a-f]{24}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionEntryName();

    private enum BundleKind { Model, Adapter }
    private sealed record SourceFileRecord(string BundleDirectory, string RelativePath, long Bytes, string Sha256);
    private sealed record ResourceBundle(string DirectoryName, string ManifestIdentity, IReadOnlyList<SourceFileRecord> Files);
    private sealed record ResourceDescriptor(string Identity, string IdentityHash, IReadOnlyList<ResourceBundle> Bundles, IReadOnlyList<SourceFileRecord> Files, long TotalBytes);
    private sealed record CacheFileRecord(string RelativePath, long Bytes, string Sha256);
    private sealed record CacheCompletionMarker(string Owner, string Purpose, int LayoutVersion, string EntryName, string IdentityHash, string Identity, IReadOnlyList<CacheFileRecord> Files, DateTimeOffset CompletedAtUtc);
}
