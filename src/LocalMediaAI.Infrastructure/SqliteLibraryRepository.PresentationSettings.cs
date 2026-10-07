namespace LocalMediaAI.Infrastructure;
public sealed partial class SqliteLibraryRepository
{
    private const string DeveloperModeKey = "ui.developer_mode";
    public async Task<bool> GetDeveloperModeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var value = await ScalarStringAsync(connection, null, "SELECT value FROM app_setting WHERE key=$key;", cancellationToken, ("$key", DeveloperModeKey));
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }
    public async Task SaveDeveloperModeAsync(bool enabled, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, null, "INSERT INTO app_setting(key,value,updated_at) VALUES($key,$value,$at) ON CONFLICT(key) DO UPDATE SET value=excluded.value,updated_at=excluded.updated_at;", cancellationToken,
            ("$key", DeveloperModeKey), ("$value", enabled ? "true" : "false"), ("$at", Now()));
    }
}
