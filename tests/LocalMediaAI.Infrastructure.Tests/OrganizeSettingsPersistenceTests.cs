using LocalMediaAI.Core;
using LocalMediaAI.Core.Organize;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class OrganizeSettingsPersistenceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LocalMediaAIOrganizeSettingsTests", Guid.NewGuid().ToString("N"));
    private SqliteLibraryRepository _repository = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "portable.flag"), string.Empty);
        var paths = AppPaths.Create(_root);
        paths.EnsureDirectories();
        _repository = new SqliteLibraryRepository(paths, new NullLogger());
        await _repository.InitializeAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ExtraSettingsRoundTrip()
    {
        var saved = OrganizeSettings.Defaults with { ShowTechnicalDetails = true, VaultPath = @"D:\重複の保管", PriorityConfirmed = true, HideFolderHint = true, ListInFoundOrder = true };
        await _repository.SaveOrganizeSettingsAsync(saved, CancellationToken.None);
        var loaded = await _repository.GetOrganizeSettingsAsync(CancellationToken.None);
        Assert.True(loaded.ShowTechnicalDetails);
        Assert.Equal(@"D:\重複の保管", loaded.VaultPath);
        Assert.True(loaded.PriorityConfirmed);
        Assert.True(loaded.HideFolderHint);
        Assert.True(loaded.ListInFoundOrder);
    }

    [Fact]
    public async Task DefaultsWhenNothingWasSaved()
    {
        var settings = await _repository.GetOrganizeSettingsAsync(CancellationToken.None);
        Assert.Equal(OrganizeSettings.Defaults, settings);
        Assert.Empty(settings.FolderOrder);
        Assert.Equal([KeepCriterion.FolderPriority, KeepCriterion.Date], settings.Criteria);
        Assert.Equal(KeepDateDirection.Oldest, settings.DateDirection);
        Assert.True(settings.CheckChangesOnStartup);
    }

    [Fact]
    public async Task RoundTripsEveryField()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var saved = new OrganizeSettings([b, a], [KeepCriterion.Date, KeepCriterion.FolderPriority], KeepDateDirection.Newest, CheckChangesOnStartup: false);
        await _repository.SaveOrganizeSettingsAsync(saved, CancellationToken.None);

        var loaded = await _repository.GetOrganizeSettingsAsync(CancellationToken.None);
        Assert.Equal(saved.FolderOrder, loaded.FolderOrder);
        Assert.Equal(saved.Criteria, loaded.Criteria);
        Assert.Equal(KeepDateDirection.Newest, loaded.DateDirection);
        Assert.False(loaded.CheckChangesOnStartup);
        Assert.Equal(KeepCriterion.Date, loaded.ToKeepRule().Criteria[0]);
    }

    [Fact]
    public void RanksFollowSavedOrderAndAppendUnknownFoldersInRegistrationOrder()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var settings = OrganizeSettings.Defaults with { FolderOrder = [c, a] };
        var ranks = settings.RanksFor([a, b, c]);
        Assert.Equal(1, ranks[c]);
        Assert.Equal(2, ranks[a]);
        Assert.Equal(3, ranks[b]);
    }

    [Fact]
    public async Task CorruptedValueFallsBackToDefaults()
    {
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={AppPaths.Create(_root).DatabasePath}"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO app_setting(key,value,updated_at) VALUES('organize.settings','{not json','2026-01-01T00:00:00Z');";
            await command.ExecuteNonQueryAsync();
        }
        Assert.Equal(OrganizeSettings.Defaults, await _repository.GetOrganizeSettingsAsync(CancellationToken.None));
    }

    private sealed class NullLogger : IStructuredLogger
    {
        public void Write(string level, string eventName, object fields, Exception? exception = null) { }
    }
}
