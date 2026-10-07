using System.Text.Json;

namespace LocalMediaAI.Infrastructure;

/// <summary>
/// data-location.json in the install folder points the app at a data folder the user chose, such as a second SSD.
/// It holds data/, cache/ and temp/ exactly like the per-user folder. portable.flag still wins; with neither file the
/// per-user folder is used. A pointer that cannot be read stops the app instead of silently falling back, which
/// would open an empty library.
/// </summary>
public static class DataLocation
{
    public const string FileName = "data-location.json";
    public const string DisabledFileName = "data-location.json.disabled";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    /// <summary>The configured folder, or null when the install has no pointer.</summary>
    public static string? TryRead(string appRoot)
    {
        var file = Path.Combine(Path.GetFullPath(appRoot), FileName);
        if (!File.Exists(file)) return null;
        Document? document;
        try { document = JsonSerializer.Deserialize<Document>(File.ReadAllText(file), Json); }
        catch (JsonException ex) { throw new DataLocationException(L.F("データの保存先の設定（{0}）を読み取れません。", FileName), ex); }
        catch (IOException ex) { throw new DataLocationException(L.F("データの保存先の設定（{0}）を読み取れません。", FileName), ex); }
        return Validate(document?.Root);
    }

    /// <summary>A full path that is not a bare drive root; returned normalised.</summary>
    public static string Validate(string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new DataLocationException(L.T("データの保存先には、ドライブ名から始まるフォルダーを指定してください。"));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (string.Equals(full, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? string.Empty), StringComparison.OrdinalIgnoreCase))
            throw new DataLocationException(L.T("データの保存先にドライブそのもの（例: D:\\）は指定できません。フォルダーを指定してください。"));
        return full;
    }

    /// <summary>Writes the pointer atomically so a crash never leaves a half-written file the app would refuse.</summary>
    public static void Write(string appRoot, string root)
    {
        var validated = Validate(root);
        var file = Path.Combine(Path.GetFullPath(appRoot), FileName);
        var temporary = file + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, new Document(validated), Json);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, file, overwrite: true);
    }

    /// <summary>Renames the pointer aside (kept for reference); no-op when there is none.</summary>
    public static void Disable(string appRoot)
    {
        var file = Path.Combine(Path.GetFullPath(appRoot), FileName);
        if (File.Exists(file)) File.Move(file, Path.Combine(Path.GetFullPath(appRoot), DisabledFileName), overwrite: true);
    }

    private sealed record Document(string? Root);
}

/// <summary>The configured data location is unusable; the message is shown to the user as is.</summary>
public sealed class DataLocationException : Exception
{
    public DataLocationException(string message) : base(message) { }
    public DataLocationException(string message, Exception inner) : base(message, inner) { }
}
