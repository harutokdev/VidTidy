using System.Text.Json;

namespace LocalMediaAI.Infrastructure;

/// <summary>Raised when a test-lab instance would touch media or storage outside its declared lab folders.</summary>
public sealed class TestLabViolationException(string message) : InvalidOperationException(message);

/// <summary>
/// Confines a test instance to its lab folders. A `testlab.flag` next to the executable (or an explicit policy
/// from a harness) switches it on: media may only be read from <see cref="MediaRoots"/>, and the catalog,
/// cache and temp must live under <see cref="InstanceRoots"/>, never in the per-user production location.
/// Anything malformed fails closed.
/// </summary>
public sealed class TestLabPolicy
{
    public const string MarkerFileName = "testlab.flag";
    private static readonly JsonSerializerOptions MarkerJson = new() { PropertyNameCaseInsensitive = true };

    public TestLabPolicy(IEnumerable<string> mediaRoots, IEnumerable<string> instanceRoots)
    {
        MediaRoots = NormalizeRoots(mediaRoots, "mediaRoots");
        InstanceRoots = NormalizeRoots(instanceRoots, "instanceRoots");
    }

    public IReadOnlyList<string> MediaRoots { get; }
    public IReadOnlyList<string> InstanceRoots { get; }

    /// <summary>The per-user location the non-portable app uses for its real catalog.</summary>
    public static string ProductionUserDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalMediaAI");

    public static TestLabPolicy? TryLoad(string appRoot)
    {
        var marker = Path.Combine(Path.GetFullPath(appRoot), MarkerFileName);
        if (Directory.Exists(marker)) throw new TestLabViolationException("testlab.flag must be a file.");
        if (!File.Exists(marker)) return null;
        try
        {
            var document = JsonSerializer.Deserialize<MarkerDocument>(File.ReadAllText(marker), MarkerJson);
            if (document?.MediaRoots is null || document.InstanceRoots is null)
                throw new TestLabViolationException("testlab.flag must list mediaRoots and instanceRoots.");
            return new TestLabPolicy(document.MediaRoots, document.InstanceRoots);
        }
        catch (JsonException ex)
        {
            throw new TestLabViolationException("testlab.flag is not valid JSON: " + ex.Message);
        }
    }

    /// <summary>Allows a registered folder or a media file only when it sits inside a media root with no junction on the way.</summary>
    public void EnsureMediaPathAllowed(string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var root in MediaRoots)
        {
            if (!IsSameOrWithin(root, full)) continue;
            if (!IsSame(root, full) && (PathPolicy.HasReparsePointAncestor(root, full) || IsReparsePoint(full)))
                throw new TestLabViolationException("Test-lab media path crosses a junction or symbolic link.");
            return;
        }
        throw new TestLabViolationException("Test-lab mode only reads media inside its declared media folders.");
    }

    public void EnsureInstanceAllowed(AppPaths paths) => EnsureInstanceAllowed(paths, ProductionUserDataRoot);

    public void EnsureInstanceAllowed(AppPaths paths, string productionDataRoot)
    {
        foreach (var storage in new[] { paths.DataRoot, paths.CacheRoot, paths.TempRoot })
            EnsureStoragePathAllowed(storage, productionDataRoot);
    }

    /// <summary>Any folder a test instance writes to (catalog, cache, temp, model cache) must be inside an instance root.</summary>
    public void EnsureStoragePathAllowed(string storage) => EnsureStoragePathAllowed(storage, ProductionUserDataRoot);

    public void EnsureStoragePathAllowed(string storage, string productionDataRoot)
    {
        foreach (var root in InstanceRoots)
            if (IsSameOrWithin(productionDataRoot, root))
                throw new TestLabViolationException("A test-lab instance root may not live inside the production data location.");
        var full = Path.GetFullPath(storage);
        if (IsSameOrWithin(productionDataRoot, full))
            throw new TestLabViolationException("A test-lab instance may not use the production data location.");
        var owner = InstanceRoots.FirstOrDefault(root => IsSameOrWithin(root, full))
            ?? throw new TestLabViolationException("Test-lab catalog, cache and temp must live inside a declared instance folder.");
        if (ExistingPartCrossesReparsePoint(owner, full))
            throw new TestLabViolationException("Test-lab catalog, cache and temp must not sit behind a junction or symbolic link.");
    }

    /// <summary>Checks the already-existing directories from the instance root down to <paramref name="path"/>; folders created later inherit a clean parent.</summary>
    private static bool ExistingPartCrossesReparsePoint(string root, string path)
    {
        var current = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (!string.IsNullOrEmpty(current) && IsSameOrWithin(root, current))
        {
            if (IsReparsePoint(current)) return true;
            if (IsSame(root, current)) return false;
            current = Path.GetDirectoryName(current);
        }
        return false;
    }

    private static List<string> NormalizeRoots(IEnumerable<string> roots, string name)
    {
        var result = new List<string>();
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
                throw new TestLabViolationException($"testlab {name} entries must be absolute paths.");
            var full = Path.GetFullPath(root);
            if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new TestLabViolationException($"testlab {name} entries must be dedicated folders, not a whole drive or share.");
            result.Add(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        if (result.Count == 0) throw new TestLabViolationException($"testlab {name} must not be empty.");
        return result;
    }

    private static bool IsSame(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrWithin(string root, string candidate) => IsSame(root, candidate) || PathPolicy.IsWithinRoot(root, candidate);

    /// <summary>True for an existing file or folder that is a junction or link; unreadable entries count as unsafe.</summary>
    private static bool IsReparsePoint(string path)
    {
        try
        {
            if (Directory.Exists(path)) return PathPolicy.IsReparsePoint(new DirectoryInfo(path));
            return File.Exists(path) && PathPolicy.IsReparsePoint(new FileInfo(path));
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private sealed class MarkerDocument
    {
        public string[]? MediaRoots { get; set; }
        public string[]? InstanceRoots { get; set; }
    }
}
