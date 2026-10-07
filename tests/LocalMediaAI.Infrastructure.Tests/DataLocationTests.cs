using LocalMediaAI.Infrastructure;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class DataLocationTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "lmai-location-" + Guid.NewGuid().ToString("N"))).FullName;

    private string AppRoot => Directory.CreateDirectory(Path.Combine(_root, "app")).FullName;
    private string Chosen => Path.Combine(_root, "D-drive", "LocalMediaAI");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void WithoutAPointerTheAppUsesThePerUserFolder()
    {
        var paths = AppPaths.Create(AppRoot);

        Assert.Null(DataLocation.TryRead(AppRoot));
        Assert.Null(paths.DataLocationRoot);
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), paths.DataRoot, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void APointerMovesDataCacheAndTempButNotModelsOrRuntime()
    {
        DataLocation.Write(AppRoot, Chosen);

        var paths = AppPaths.Create(AppRoot);

        Assert.Equal(Path.Combine(Chosen, "data"), paths.DataRoot);
        Assert.Equal(Path.Combine(Chosen, "cache"), paths.CacheRoot);
        Assert.Equal(Path.Combine(Chosen, "temp"), paths.TempRoot);
        Assert.Equal(Path.Combine(AppRoot, "models"), paths.ModelsRoot);
        Assert.Equal(Path.Combine(AppRoot, "runtime"), paths.RuntimeRoot);
        Assert.Empty(Directory.EnumerateFiles(AppRoot, "*.tmp"));
    }

    [Fact]
    public void PortableFlagOutranksAPointer()
    {
        DataLocation.Write(AppRoot, Chosen);
        File.WriteAllText(Path.Combine(AppRoot, "portable.flag"), string.Empty);

        var paths = AppPaths.Create(AppRoot);

        Assert.Equal(Path.Combine(AppRoot, "data"), paths.DataRoot);
        Assert.Null(paths.DataLocationRoot);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{}")]
    [InlineData("{\"root\": \"relative\\\\folder\"}")]
    [InlineData("{\"root\": \"D:\\\\\"}")]
    public void AnUnusablePointerStopsTheAppInsteadOfFallingBack(string content)
    {
        File.WriteAllText(Path.Combine(AppRoot, DataLocation.FileName), content);

        Assert.Throws<DataLocationException>(() => AppPaths.Create(AppRoot));
    }

    [Fact]
    public void AMissingCatalogAtThePointedFolderIsReportedNotRecreated()
    {
        DataLocation.Write(AppRoot, Chosen);
        var paths = AppPaths.Create(AppRoot);

        var missing = Assert.Throws<DataLocationException>(paths.EnsureDataLocationPresent);
        Assert.Contains(Chosen, missing.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Chosen));

        Directory.CreateDirectory(paths.DataRoot);
        File.WriteAllText(paths.DatabasePath, string.Empty);
        paths.EnsureDataLocationPresent();
    }

    [Theory]
    [InlineData(@"D:\LocalMediaAI\", @"D:\LocalMediaAI")]
    [InlineData(@"D:\Data\..\LocalMediaAI", @"D:\LocalMediaAI")]
    public void ValidateNormalisesTheFolder(string input, string expected) =>
        Assert.Equal(expected, DataLocation.Validate(input));
}
