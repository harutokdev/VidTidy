using System.Text.RegularExpressions;
using LocalMediaAI.Core;

namespace LocalMediaAI.Core.Tests;

/// <summary>
/// English counts: a format can choose a word by a number, <c>{0|group|groups}</c>, so "1 group" is not "1 groups".
/// The table is read directly (not through <see cref="L.Use"/>), since the language is app-wide and other tests expect Japanese.
/// </summary>
public sealed partial class LocalizationPluralTests
{
    private static readonly IReadOnlyDictionary<string, string> English = L.LoadTable(UiLanguage.English);

    [Theory]
    [InlineData(1, "There is 1 group")]
    [InlineData(2, "There are 2 groups")]
    [InlineData(0, "There are 0 groups")]
    public void ChoosesTheWordByTheNumber(int count, string expected) =>
        Assert.Equal(expected, L.Fill("There {0|is|are} {0} {0|group|groups}", count));

    [Fact]
    public void ChoicesFollowTheArgumentTheyName()
    {
        Assert.Equal("3 groups · 1 file · 2 GB", L.Fill("{0} {0|group|groups} · {1} {1|file|files} · {2}", 3, 1L, "2 GB"));
        Assert.Equal("Approve 1 clear group", L.Fill("Approve {0} clear {0|group|groups}{0|| together}", 1));
        Assert.Equal("Approve 4 clear groups together", L.Fill("Approve {0} clear {0|group|groups}{0|| together}", 4));
    }

    [Fact]
    public void NumbersOfAnyKindAndNumberTextCount()
    {
        Assert.Equal("1 video", L.Fill("{0} {0|video|videos}", 1.0));
        Assert.Equal("1 video", L.Fill("{0} {0|video|videos}", 1m));
        Assert.Equal("1 video", L.Fill("{0} {0|video|videos}", "1"));
        Assert.Equal("1.5 videos", L.Fill("{0} {0|video|videos}", 1.5));
        Assert.Equal("x videos", L.Fill("{0} {0|video|videos}", "x"));
    }

    [Fact]
    public void FormatsWithoutChoicesAreUnchanged()
    {
        Assert.False(L.HasPluralChoice("{0}件"));
        Assert.True(L.HasPluralChoice("{0} {0|item|items}"));
        Assert.Equal("3件", L.Fill("{0}件", 3));
        Assert.Equal("{literal} 1", L.Fill("{{literal}} {0}", 1));
    }

    [Theory]
    [InlineData("{0}組", 1, "1 group")]
    [InlineData("{0}組", 5, "5 groups")]
    [InlineData("{0}本", 1, "1 video")]
    [InlineData("{0}件", 1, "1 item")]
    [InlineData("よく似ている動画が {0}組 あります", 1, "There is 1 group of very similar videos")]
    [InlineData("よく似ている動画が {0}組 あります", 2, "There are 2 groups of very similar videos")]
    [InlineData("完全に同じファイルが {0}組 あります（{1}）", 1, "There is 1 group of identical files (x)")]
    [InlineData("{0}本 片付ける（{1}）", 1, "Clean up 1 video (x)")]
    [InlineData("整理リストに {0}件（{1}）あります", 1, "1 item in the cleanup list (x)")]
    [InlineData("{0}組を整理リストに入れました", 1, "Added 1 group to the cleanup list")]
    [InlineData("はっきりした {0}組をまとめて承認", 1, "Approve 1 clear group")]
    [InlineData("はっきりした {0}組をまとめて承認", 3, "Approve 3 clear groups together")]
    [InlineData("{0}日前", 1, "1 day ago")]
    public void EnglishCountTextsTakeTheSingularForOne(string japanese, int count, string expected) =>
        Assert.Equal(expected, L.Fill(English[japanese], count, "x"));

    [Fact]
    public void NoEnglishTextPutsANumberBeforeAFixedPluralWord()
    {
        var fixedPlural = English.Where(entry => FixedPlural().IsMatch(entry.Value)).Select(entry => entry.Value).ToArray();
        Assert.True(fixedPlural.Length == 0, "Use {n|one|other} for these:\n" + string.Join("\n", fixedPlural.Take(40)));
    }

    [Fact]
    public void ChoicesNameAnArgumentOfTheirText()
    {
        var bad = English.Where(entry => Choice().Matches(entry.Value).Any(m => !entry.Value.Contains("{" + m.Groups[1].Value + "}") && !entry.Value.Contains("{" + m.Groups[1].Value + ":")))
            .Select(entry => entry.Value).ToArray();
        Assert.True(bad.Length == 0, "Choices for an argument that is not shown:\n" + string.Join("\n", bad));
    }

    [GeneratedRegex(@"\{\d+(?::[^{}]*)?\} (?:[a-z]+ ){0,2}(?:videos|groups|items|files|days|hours|tasks|results|candidates|decisions|events|rows|duplicates)\b")]
    private static partial Regex FixedPlural();

    [GeneratedRegex(@"\{(\d+)\|")]
    private static partial Regex Choice();
}
