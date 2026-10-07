using LocalMediaAI.Core;

namespace LocalMediaAI.Infrastructure;

/// <summary>
/// The display language the user chose, kept as "ja" or "en" in a small file in the data folder. It is a file rather
/// than a catalog setting so it can be read before the catalog opens (the first window already needs the language).
/// </summary>
public static class UiLanguageSetting
{
    public const string FileName = "ui-language.txt";

    /// <summary>The chosen language, or null when none was chosen yet (or the file cannot be read).</summary>
    public static UiLanguage? Read(string dataRoot)
    {
        try
        {
            var path = Path.Combine(dataRoot, FileName);
            return File.Exists(path) ? L.Parse(File.ReadAllText(path)) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Records the choice; written to a temporary file first so a crash never leaves half a value.</summary>
    public static void Write(string dataRoot, UiLanguage language)
    {
        Directory.CreateDirectory(dataRoot);
        var path = Path.Combine(dataRoot, FileName);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, L.Code(language));
        File.Move(temporary, path, overwrite: true);
    }
}
