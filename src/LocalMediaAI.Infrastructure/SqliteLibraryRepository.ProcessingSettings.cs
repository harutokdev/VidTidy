using System.Text.Json;
using System.Text.Json.Serialization;
using LocalMediaAI.Core.Processing;

namespace LocalMediaAI.Infrastructure;

public sealed partial class SqliteLibraryRepository
{
    private const string ProcessingSettingsKey = "processing.settings";
    private static readonly JsonSerializerOptions ProcessingSettingsJson = new() { Converters = { new JsonStringEnumConverter() } };

    private sealed record ProcessingSettingsDto(ProcessingPace? Pace, bool? OnlyWhenIdle);

    public async Task<ProcessingSettings> GetProcessingSettingsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var value = await ScalarStringAsync(connection, null, "SELECT value FROM app_setting WHERE key=$key;", cancellationToken, ("$key", ProcessingSettingsKey));
        if (string.IsNullOrWhiteSpace(value)) return ProcessingSettings.Defaults;
        try
        {
            var dto = JsonSerializer.Deserialize<ProcessingSettingsDto>(value, ProcessingSettingsJson);
            var defaults = ProcessingSettings.Defaults;
            var pace = dto?.Pace is { } stored && Enum.IsDefined(stored) ? stored : defaults.Pace;
            return new ProcessingSettings(pace, dto?.OnlyWhenIdle ?? defaults.OnlyWhenIdle);
        }
        catch (JsonException ex)
        {
            _log.Write("Warning", "ProcessingSettingsUnreadable", new { }, ex);
            return ProcessingSettings.Defaults;
        }
    }

    public async Task SaveProcessingSettingsAsync(ProcessingSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Enum.IsDefined(settings.Pace)) throw new ArgumentOutOfRangeException(nameof(settings), "Unknown processing pace.");
        var json = JsonSerializer.Serialize(new ProcessingSettingsDto(settings.Pace, settings.OnlyWhenIdle), ProcessingSettingsJson);
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, null, "INSERT INTO app_setting(key,value,updated_at) VALUES($key,$value,$at) ON CONFLICT(key) DO UPDATE SET value=excluded.value,updated_at=excluded.updated_at;", cancellationToken,
            ("$key", ProcessingSettingsKey), ("$value", json), ("$at", Now()));
    }
}
