using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalMediaAI.Core;

/// <summary>Display languages of the app. Japanese is the source language: its text is written in the code.</summary>
public enum UiLanguage { Japanese, English }

/// <summary>
/// Translates display text. The Japanese text in the code is the key; other languages look it up in an embedded
/// table (<c>Localization/en.json</c>). Text with no entry is shown as written, so a missing translation never hides
/// anything. The language is chosen once at start-up (a change takes effect after a restart).
/// </summary>
public static partial class L
{
    private static IReadOnlyDictionary<string, string> _table = new Dictionary<string, string>();

    public static UiLanguage Language { get; private set; } = UiLanguage.Japanese;

    /// <summary>
    /// Uses <paramref name="language"/> for text from now on. Numbers and dates keep the user's Windows regional format
    /// (CurrentCulture is left alone); only the UI culture follows the display language.
    /// </summary>
    public static void Use(UiLanguage language)
    {
        Language = language;
        _table = language == UiLanguage.Japanese ? new Dictionary<string, string>() : LoadTable(language);
        var culture = CultureInfo.GetCultureInfo(language == UiLanguage.English ? "en-US" : "ja-JP");
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    /// <summary>The text in the current language.</summary>
    public static string T(string japanese) =>
        _table.TryGetValue(japanese, out var translated) ? translated : japanese;

    /// <summary>A composite format (<c>{0}</c>, <c>{1:N0}</c>…) translated, then filled in with the current culture.</summary>
    public static string F(string japaneseFormat, params object?[] args) => Fill(T(japaneseFormat), args);

    /// <summary>
    /// Fills in an already translated format. Besides the usual placeholders it takes word choices by a number,
    /// <c>{0|group|groups}</c>: the first word when argument 0 is exactly 1, the second otherwise. English needs no
    /// other forms; Japanese text has no choices and is filled in as is.
    /// </summary>
    public static string Fill(string translatedFormat, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, ChooseWords(translatedFormat, args), args);

    /// <summary>Whether <paramref name="format"/> chooses words by a number (so it cannot be used as a plain StringFormat).</summary>
    public static bool HasPluralChoice(string format) => format.Contains('|') && PluralChoice().IsMatch(format);

    private static string ChooseWords(string format, object?[] args) =>
        !HasPluralChoice(format) ? format : PluralChoice().Replace(format, m =>
        {
            var index = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var word = index < args.Length && IsOne(args[index]) ? m.Groups[2].Value : m.Groups[3].Value;
            return word.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal);
        });

    private static bool IsOne(object? value) => value switch
    {
        string text => text.Trim() == "1",
        byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal =>
            Convert.ToDouble(value, CultureInfo.InvariantCulture) == 1d,
        _ => false
    };

    [GeneratedRegex(@"\{(\d+)\|([^|{}]*)\|([^|{}]*)\}")]
    private static partial Regex PluralChoice();

    /// <summary>The embedded table for <paramref name="language"/>: Japanese text to translated text.</summary>
    public static IReadOnlyDictionary<string, string> LoadTable(UiLanguage language)
    {
        var name = language switch { UiLanguage.English => "en", _ => throw new ArgumentOutOfRangeException(nameof(language)) };
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"LocalMediaAI.Core.Localization.{name}.json")
            ?? throw new InvalidOperationException($"Translation table '{name}' is not embedded.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new Dictionary<string, string>();
    }

    /// <summary>The setting value stored for a language ("ja" / "en").</summary>
    public static string Code(UiLanguage language) => language == UiLanguage.English ? "en" : "ja";

    public static UiLanguage? Parse(string? code) => code?.Trim().ToLowerInvariant() switch
    {
        "ja" => UiLanguage.Japanese,
        "en" => UiLanguage.English,
        _ => null
    };
}
