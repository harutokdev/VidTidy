using LocalMediaAI.Core;
using LocalMediaAI.Core.Processing;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class ProcessingSettingsPersistenceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LocalMediaAIProcessingSettingsTests", Guid.NewGuid().ToString("N"));
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
    public async Task NothingSavedMeansDefaults() =>
        Assert.Equal(ProcessingSettings.Defaults, await _repository.GetProcessingSettingsAsync(CancellationToken.None));

    [Theory]
    [InlineData(ProcessingPace.Fastest, false)]
    [InlineData(ProcessingPace.Quiet, true)]
    public async Task SettingsRoundTrip(ProcessingPace pace, bool onlyWhenIdle)
    {
        await _repository.SaveProcessingSettingsAsync(new ProcessingSettings(pace, onlyWhenIdle), CancellationToken.None);

        Assert.Equal(new ProcessingSettings(pace, onlyWhenIdle), await _repository.GetProcessingSettingsAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"Pace\":\"Turbo\"}")]
    public async Task UnreadableValuesFallBackToDefaults(string stored)
    {
        await _repository.SaveProcessingSettingsAsync(new ProcessingSettings(ProcessingPace.Quiet, true), CancellationToken.None);
        await OverwriteStoredValueAsync(stored);

        Assert.Equal(ProcessingSettings.Defaults, await _repository.GetProcessingSettingsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task UnknownPaceIsRejectedBeforeAnythingIsWritten() =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _repository.SaveProcessingSettingsAsync(new ProcessingSettings((ProcessingPace)99, false), CancellationToken.None));

    private async Task OverwriteStoredValueAsync(string value)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(_root, "data", "library.db") }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE app_setting SET value=$value WHERE key='processing.settings';";
        command.Parameters.AddWithValue("$value", value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private sealed class NullLogger : IStructuredLogger
    {
        public void Write(string level, string eventName, object fields, Exception? exception = null) { }
    }
}
