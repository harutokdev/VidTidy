using LocalMediaAI.Infrastructure;

namespace ThroughputLab;

/// <summary>
/// The only folders this tool touches. Media is read from the lab's media folders; catalogs, caches and
/// measurements live in the lab's instance folders on the HDD or on the SSD.
/// </summary>
public sealed class LabLayout
{
    public LabLayout(string labRoot, string repoRoot)
    {
        LabRoot = Path.GetFullPath(labRoot);
        if (!string.Equals(Path.GetFileName(LabRoot.TrimEnd(Path.DirectorySeparatorChar)), "LocalMediaAI-TestLab", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("--lab must be a folder named LocalMediaAI-TestLab.", nameof(labRoot));
        RepoRoot = Path.GetFullPath(repoRoot);
        // Not under %LOCALAPPDATA%: packaged hosts (such as the Claude desktop app) silently redirect AppData writes.
        SsdLabRoot = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "LocalMediaAI-TestLab");
        Policy = new TestLabPolicy([SyntheticRoot, RealRoot, RealDerivedRoot], [HddInstances, SsdLabRoot]);
    }

    public string LabRoot { get; }
    public string RepoRoot { get; }
    public string SsdLabRoot { get; }
    public TestLabPolicy Policy { get; }

    public string SyntheticRoot => Path.Combine(LabRoot, "media-synthetic");
    public string RealRoot => Path.Combine(LabRoot, "media-real");
    /// <summary>Other-format copies Claude makes from the user's clips; kept apart so the user's folder stays as placed.</summary>
    public string RealDerivedRoot => Path.Combine(LabRoot, "media-real-derived");
    public string MastersRoot => Path.Combine(SyntheticRoot, "masters");
    public string SetsRoot => Path.Combine(SyntheticRoot, "sets");
    public string HddInstances => Path.Combine(LabRoot, "instances");
    public string SsdInstances => Path.Combine(SsdLabRoot, "instances");
    public string Measurements => Path.Combine(LabRoot, "measurements");
    /// <summary>One shared copy of the model bundle on the SSD side of the lab, never the per-user production cache.</summary>
    public string AiResourceCacheRoot => Path.Combine(SsdLabRoot, "ai-resource-cache");
    public string EvictionFile => Path.Combine(HddInstances, "cache-eviction.bin");
    public string RuntimeRoot => Path.Combine(RepoRoot, "runtime");
    public string ModelsRoot => Path.Combine(RepoRoot, "models");
    public string Ffmpeg => Path.Combine(RuntimeRoot, "ffmpeg", "ffmpeg.exe");
    public string Ffprobe => Path.Combine(RuntimeRoot, "ffmpeg", "ffprobe.exe");

    public string SetFolder(string setName)
    {
        if (string.IsNullOrWhiteSpace(setName) || setName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || setName.Contains(".."))
            throw new ArgumentException("Set names are plain folder names.", nameof(setName));
        return Path.Combine(SetsRoot, setName);
    }

    /// <summary>Resolves a media folder for a run: a synthetic set by name, "real" for the user-supplied folder, or "real-derived" for its format copies.</summary>
    public string MediaFolder(string setName)
    {
        var folder = setName switch { "real" => RealRoot, "real-derived" => RealDerivedRoot, _ => SetFolder(setName) };
        Policy.EnsureMediaPathAllowed(folder);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Test media folder does not exist: " + folder);
        return folder;
    }

    public void EnsureCreated()
    {
        foreach (var folder in new[] { SyntheticRoot, MastersRoot, SetsRoot, HddInstances, SsdInstances, Measurements })
            Directory.CreateDirectory(folder);
    }
}
