using System.Net;
using System.Text.RegularExpressions;
using LocalMediaAI.Core;

namespace LocalMediaAI.Core.Tests;

/// <summary>
/// Every Japanese display text in the code (L.T / L.F in C#, {l:T '…'} and TFormat= in XAML) has an English entry,
/// and each entry keeps the placeholders of its source. Same rules as tools/i18n/extract.py.
/// </summary>
public sealed partial class LocalizationCatalogTests
{
    private static readonly IReadOnlyDictionary<string, string> English = L.LoadTable(UiLanguage.English);

    [Fact]
    public void EveryDisplayTextHasAnEnglishTranslation()
    {
        var missing = SourceTexts().Where(text => !English.ContainsKey(text)).Order(StringComparer.Ordinal).ToArray();
        Assert.True(missing.Length == 0, $"{missing.Length} texts have no English translation (run tools/i18n/extract.py):\n" + string.Join("\n", missing.Take(40)));
    }

    [Fact]
    public void TranslationsKeepThePlaceholdersOfTheirSource()
    {
        var broken = English.Where(entry => !Placeholders(entry.Key).SequenceEqual(Placeholders(entry.Value))).Select(entry => entry.Key).ToArray();
        Assert.True(broken.Length == 0, "Placeholders differ for:\n" + string.Join("\n", broken.Take(40)));
    }

    [Fact]
    public void TranslatedFormatsCanBeFilledIn()
    {
        var args = Enumerable.Range(0, 10).Select(_ => (object)new AnyFormat()).ToArray();
        foreach (var (key, value) in English.Where(entry => Placeholders(entry.Key).Length > 0))
        {
            var exception = Record.Exception(() => L.Fill(value, args));
            Assert.True(exception is null, $"'{value}' (for '{key}') is not a valid format: {exception?.Message}");
        }
    }

    [Fact]
    public void TranslationsAreNotEmptyOrStillJapanese()
    {
        var bad = English.Where(entry => string.IsNullOrWhiteSpace(entry.Value) != string.IsNullOrWhiteSpace(entry.Key) || JapaneseText().IsMatch(entry.Value))
            .Select(entry => entry.Key).ToArray();
        Assert.True(bad.Length == 0, "Empty or untranslated entries:\n" + string.Join("\n", bad.Take(40)));
    }

    [Fact]
    public void JapaneseIsTheTextAsWritten()
    {
        Assert.Equal(UiLanguage.Japanese, L.Language);
        Assert.Equal("設定", L.T("設定"));
        Assert.Equal("3件", L.F("{0}件", 3));
    }

    /// <summary>An argument that accepts any format specifier (N0, O, yyyy/MM/dd…), so only the format text is checked.</summary>
    private sealed class AnyFormat : IFormattable
    {
        public string ToString(string? format, IFormatProvider? formatProvider) => "x";
    }

    private static string[] Placeholders(string format) =>
        PlaceholderPattern().Matches(format).Select(m => m.Value).Order(StringComparer.Ordinal).ToArray();

    private static HashSet<string> SourceTexts()
    {
        var root = RepositoryRoot();
        var texts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            if (path.EndsWith(".cs", StringComparison.Ordinal))
            {
                foreach (Match m in CSharpCall().Matches(File.ReadAllText(path))) texts.Add(Regex.Unescape(m.Groups[1].Value));
            }
            else if (path.EndsWith(".xaml", StringComparison.Ordinal))
            {
                var xaml = File.ReadAllText(path);
                foreach (Match m in XamlText().Matches(xaml)) texts.Add(WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, @"\\(.)", "$1")));
                foreach (var format in TFormats(xaml)) texts.Add(WebUtility.HtmlDecode(format));
            }
        }
        return texts;
    }

    /// <summary>The value after each TFormat=, up to the brace or comma that closes it ({} escape removed).</summary>
    private static IEnumerable<string> TFormats(string xaml)
    {
        foreach (Match m in Regex.Matches(xaml, "TFormat="))
        {
            var i = m.Index + m.Length;
            if (xaml.AsSpan(i).StartsWith("{}")) i += 2;
            var start = i;
            var depth = 0;
            for (; i < xaml.Length; i++)
            {
                var c = xaml[i];
                if (c == '{') depth++;
                else if (c == '}') { if (depth == 0) break; depth--; }
                else if ((c == ',' || c == '"') && depth == 0) break;
            }
            yield return xaml[start..i];
        }
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "LocalMediaAI.sln"))) return directory.FullName;
        throw new InvalidOperationException("Repository root was not found.");
    }

    [GeneratedRegex("""\bL\.(?:T|F)\(\s*"((?:[^"\\\n]|\\.)*)"(?=\s*[,)])""")]
    private static partial Regex CSharpCall();

    [GeneratedRegex(@"\{l:T '((?:[^'\\]|\\.)*)'\}")]
    private static partial Regex XamlText();

    [GeneratedRegex(@"\{\d+(?:[,:][^{}]*)?\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"[\u3000-\u303f\u3040-\u30ff\u4e00-\u9fff\uff01-\uff60]")]
    private static partial Regex JapaneseText();
}
