using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class AiResourceCacheTests
{
    [Fact]
    public async Task PopulationPublishesValidatedEntryAndNextResolveIsAHit()
    {
        using var fixture = new CacheFixture();
        var sourceBefore = fixture.SourceHashes();

        var populated = await AiResourceCache.ResolveModelsRootAsync(fixture.Paths, fixture.Profile, fixture.Log, CancellationToken.None, fixture.CacheRoot);
        var hit = await AiResourceCache.ResolveModelsRootAsync(fixture.Paths, fixture.Profile, fixture.Log, CancellationToken.None, fixture.CacheRoot);

        Assert.True(populated.Populated);
        Assert.False(populated.CacheHit);
        Assert.True(hit.CacheHit);
        Assert.False(hit.Populated);
        Assert.Equal(populated.ModelsRoot, hit.ModelsRoot);
        Assert.NotEqual(fixture.Paths.ModelsRoot, hit.ModelsRoot);
        Assert.True(File.Exists(Path.Combine(hit.ModelsRoot, fixture.Profile.ModelDirectoryName, "open_clip_model.safetensors")));
        Assert.Equal(sourceBefore, fixture.SourceHashes());
    }

    [Fact]
    public async Task IncompleteVersionEntryIsNeverUsedAndFallsBackToAuthoritativeSource()
    {
        using var fixture = new CacheFixture();
        var populated = await AiResourceCache.ResolveModelsRootAsync(fixture.Paths, fixture.Profile, fixture.Log, CancellationToken.None, fixture.CacheRoot);
        File.Delete(Path.Combine(Path.GetDirectoryName(populated.ModelsRoot)!, "complete.json"));

        var result = await AiResourceCache.ResolveModelsRootAsync(fixture.Paths, fixture.Profile, fixture.Log, CancellationToken.None, fixture.CacheRoot);

        Assert.False(result.CacheHit);
        Assert.False(result.Populated);
        Assert.Equal("SourceFallbackInvalidEntry", result.Status);
        Assert.Equal(fixture.Paths.ModelsRoot, result.ModelsRoot);
    }

    [Fact]
    public async Task ChangedManifestIdentityPublishesNewVersionAndSafelyRemovesRecognizedOldVersion()
    {
        using var fixture = new CacheFixture();
        var first = await AiResourceCache.ResolveModelsRootAsync(fixture.Paths, fixture.Profile, fixture.Log, CancellationToken.None, fixture.CacheRoot);
        var firstEntry = Path.GetDirectoryName(first.ModelsRoot)!;
        fixture.ReplaceModelFile("model-v2");

        var second = await AiResourceCache.ResolveModelsRootAsync(fixture.Paths, fixture.Profile, fixture.Log, CancellationToken.None, fixture.CacheRoot);

        Assert.True(second.Populated);
        Assert.NotEqual(first.ModelsRoot, second.ModelsRoot);
        Assert.False(Directory.Exists(firstEntry));
        Assert.True(Directory.Exists(Path.GetDirectoryName(second.ModelsRoot)));
    }

    [Fact]
    public async Task MissingSourceAndUnrecognizedCacheRootFailClosedWithoutDeletingForeignData()
    {
        using var fixture = new CacheFixture(createCacheRootWithForeignFile: true);
        var foreign = Path.Combine(fixture.CacheRoot, "foreign.txt");
        Directory.Delete(Path.Combine(fixture.Paths.ModelsRoot, fixture.Profile.ModelDirectoryName), true);

        var result = await AiResourceCache.ResolveModelsRootAsync(fixture.Paths, fixture.Profile, fixture.Log, CancellationToken.None, fixture.CacheRoot);

        Assert.Equal(fixture.Paths.ModelsRoot, result.ModelsRoot);
        Assert.False(result.CacheHit);
        Assert.False(result.Populated);
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public async Task ManifestHashMismatchNeverPublishesAValidCacheEntry()
    {
        using var fixture = new CacheFixture();
        File.WriteAllText(Path.Combine(fixture.Paths.ModelsRoot, fixture.Profile.ModelDirectoryName, "open_clip_model.safetensors"), "tampered", Encoding.UTF8);

        var result = await AiResourceCache.ResolveModelsRootAsync(fixture.Paths, fixture.Profile, fixture.Log, CancellationToken.None, fixture.CacheRoot);

        Assert.Equal(fixture.Paths.ModelsRoot, result.ModelsRoot);
        Assert.False(result.CacheHit);
        Assert.False(result.Populated);
        Assert.Empty(Directory.Exists(fixture.CacheRoot)
            ? Directory.EnumerateFiles(fixture.CacheRoot, "complete.json", SearchOption.AllDirectories)
            : Array.Empty<string>());
    }

    private sealed class CacheFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "LocalMediaAI-AiResourceCacheTests", Guid.NewGuid().ToString("N"));
        public CacheFixture(bool createCacheRootWithForeignFile = false)
        {
            Profile = SemanticModelRegistry.ResolveDevelopmentCandidate();
            var appRoot = Path.Combine(_root, "app");
            var modelsRoot = Path.Combine(appRoot, "models");
            CacheRoot = Path.Combine(_root, "app-owned-cache");
            Paths = AppPaths.Create(appRoot, new Dictionary<string, string>
            {
                ["DataRoot"] = Path.Combine(appRoot, "data"),
                ["CacheRoot"] = Path.Combine(appRoot, "artifacts"),
                ["ModelsRoot"] = modelsRoot,
                ["RuntimeRoot"] = Path.Combine(appRoot, "runtime"),
                ["TempRoot"] = Path.Combine(appRoot, "temp")
            });
            CreateBundles("model-v1");
            if (createCacheRootWithForeignFile)
            {
                Directory.CreateDirectory(CacheRoot);
                File.WriteAllText(Path.Combine(CacheRoot, "foreign.txt"), "preserve", Encoding.UTF8);
            }
        }

        public SemanticModelProfile Profile { get; }
        public AppPaths Paths { get; }
        public string CacheRoot { get; }
        public TestLogger Log { get; } = new();

        public Dictionary<string, string> SourceHashes() => Directory.EnumerateFiles(Paths.ModelsRoot, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToDictionary(path => Path.GetRelativePath(Paths.ModelsRoot, path), Hash, StringComparer.Ordinal);

        public void ReplaceModelFile(string content)
        {
            var modelRoot = Path.Combine(Paths.ModelsRoot, Profile.ModelDirectoryName);
            File.WriteAllText(Path.Combine(modelRoot, "open_clip_model.safetensors"), content, Encoding.UTF8);
            WriteModelProvenance(modelRoot);
        }

        private void CreateBundles(string modelContent)
        {
            var modelRoot = Path.Combine(Paths.ModelsRoot, Profile.ModelDirectoryName);
            Directory.CreateDirectory(modelRoot);
            File.WriteAllText(Path.Combine(modelRoot, "open_clip_config.json"), "{}", Encoding.UTF8);
            File.WriteAllText(Path.Combine(modelRoot, "open_clip_model.safetensors"), modelContent, Encoding.UTF8);
            File.WriteAllText(Path.Combine(modelRoot, "tokenizer.json"), "{}", Encoding.UTF8);
            File.WriteAllText(Path.Combine(modelRoot, "tokenizer_config.json"), "{}", Encoding.UTF8);
            WriteModelProvenance(modelRoot);

            var adapterRoot = Path.Combine(Paths.ModelsRoot, Profile.AdapterDirectoryName!);
            Directory.CreateDirectory(adapterRoot);
            File.WriteAllText(Path.Combine(adapterRoot, "llm-jp-clip-vit-base-patch16.json"), "{}", Encoding.UTF8);
            File.WriteAllText(Path.Combine(adapterRoot, "preprocess_config.json"), "{}", Encoding.UTF8);
            WriteProvenance(adapterRoot, new Dictionary<string, object?>
            {
                ["schema_version"] = 1,
                ["adapter_id"] = "llm-jp/llm-jp-clip-vit-base-patch16-openclip-adapter-v1",
                ["adapter_revision"] = "phase32-openclip-local-adapter-v1",
                ["runtime_network_allowed"] = false,
                ["pretrained_hf"] = false,
                ["files"] = Manifest(adapterRoot)
            });
        }

        private void WriteModelProvenance(string modelRoot)
        {
            var files = Manifest(modelRoot);
            var manifestText = string.Join("\n", files.Select(item => $"{item.Path}|{item.Bytes}|{item.Sha256}"));
            WriteProvenance(modelRoot, new Dictionary<string, object?>
            {
                ["schema_version"] = 1,
                ["model_id"] = Profile.ModelId,
                ["revision"] = Profile.ModelRevision,
                ["runtime_network_allowed"] = false,
                ["artifact_manifest_sha256"] = HashText(manifestText),
                ["files"] = files
            });
        }

        private static TestManifestRecord[] Manifest(string directory) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("provenance.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new TestManifestRecord(Path.GetRelativePath(directory, path).Replace('\\', '/'), new FileInfo(path).Length, Hash(path)))
            .ToArray();

        private static void WriteProvenance(string directory, object value) =>
            File.WriteAllText(Path.Combine(directory, "provenance.json"), JsonSerializer.Serialize(value), Encoding.UTF8);

        private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        private static string HashText(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private sealed record TestManifestRecord(
            [property: JsonPropertyName("path")] string Path,
            [property: JsonPropertyName("bytes")] long Bytes,
            [property: JsonPropertyName("sha256")] string Sha256);
    }

    public sealed class TestLogger : IStructuredLogger
    {
        public void Write(string level, string eventName, object fields, Exception? exception = null) { }
    }
}
