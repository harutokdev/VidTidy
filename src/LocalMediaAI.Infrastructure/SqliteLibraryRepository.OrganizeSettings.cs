using System.Text.Json;
using System.Text.Json.Serialization;
using LocalMediaAI.Core.Organize;

namespace LocalMediaAI.Infrastructure;

public sealed partial class SqliteLibraryRepository
{
    private const string OrganizeSettingsKey = "organize.settings";
    private static readonly JsonSerializerOptions OrganizeSettingsJson = new() { Converters = { new JsonStringEnumConverter() } };

    private sealed record OrganizeSettingsDto(Guid[]? FolderOrder, KeepCriterion[]? Criteria, KeepDateDirection? DateDirection, bool? CheckChangesOnStartup,
        bool? ShowTechnicalDetails = null, string? VaultPath = null, bool? PriorityConfirmed = null, bool? HideFolderHint = null, bool? ListInFoundOrder = null);

    public async Task<OrganizeSettings> GetOrganizeSettingsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var value = await ScalarStringAsync(connection, null, "SELECT value FROM app_setting WHERE key=$key;", cancellationToken, ("$key", OrganizeSettingsKey));
        if (string.IsNullOrWhiteSpace(value)) return OrganizeSettings.Defaults;
        try
        {
            var dto = JsonSerializer.Deserialize<OrganizeSettingsDto>(value, OrganizeSettingsJson);
            if (dto is null) return OrganizeSettings.Defaults;
            var defaults = OrganizeSettings.Defaults;
            var criteria = dto.Criteria is { Length: 2 } c && c.Distinct().Count() == 2 ? c : [.. defaults.Criteria];
            return new OrganizeSettings(dto.FolderOrder ?? [], criteria, dto.DateDirection ?? defaults.DateDirection, dto.CheckChangesOnStartup ?? defaults.CheckChangesOnStartup,
                dto.ShowTechnicalDetails ?? false, string.IsNullOrWhiteSpace(dto.VaultPath) ? null : dto.VaultPath, dto.PriorityConfirmed ?? false,
                dto.HideFolderHint ?? false, dto.ListInFoundOrder ?? false);
        }
        catch (JsonException ex)
        {
            _log.Write("Warning", "OrganizeSettingsUnreadable", new { }, ex);
            return OrganizeSettings.Defaults;
        }
    }

    public async Task SaveOrganizeSettingsAsync(OrganizeSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _ = settings.ToKeepRule(); // validates the criteria before anything is written
        var json = JsonSerializer.Serialize(new OrganizeSettingsDto([.. settings.FolderOrder], [.. settings.Criteria], settings.DateDirection, settings.CheckChangesOnStartup,
            settings.ShowTechnicalDetails, settings.VaultPath, settings.PriorityConfirmed, settings.HideFolderHint, settings.ListInFoundOrder), OrganizeSettingsJson);
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, null, "INSERT INTO app_setting(key,value,updated_at) VALUES($key,$value,$at) ON CONFLICT(key) DO UPDATE SET value=excluded.value,updated_at=excluded.updated_at;", cancellationToken,
            ("$key", OrganizeSettingsKey), ("$value", json), ("$at", Now()));
    }
}
