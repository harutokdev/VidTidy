using System.Text.RegularExpressions;

namespace LocalMediaAI.Infrastructure.Tests;

/// <summary>
/// Source files read by text-checking tests, with the translation wrappers taken off: <c>{l:T '日本語'}</c> and
/// <c>L.T("日本語")</c> read as the Japanese text they show, so those tests keep checking the wording.
/// </summary>
internal static partial class LocalizedSource
{
    public static string Read(string path) => Plain(File.ReadAllText(path));

    public static string Plain(string source)
    {
        source = XamlText().Replace(source, m => Regex.Replace(m.Groups[1].Value, @"\\(.)", "$1"));
        return CSharpText().Replace(source, m => m.Groups[1].Value);
    }

    [GeneratedRegex(@"\{l:T '((?:[^'\\]|\\.)*)'\}")]
    private static partial Regex XamlText();

    [GeneratedRegex("""\bL\.T\(("(?:[^"\\\n]|\\.)*")\)""")]
    private static partial Regex CSharpText();
}
