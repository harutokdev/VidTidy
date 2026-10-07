using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class UiLanguageSettingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LocalMediaAIUiLanguageTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void NoChoiceIsRecordedUntilTheUserPicksOne()
    {
        Assert.Null(UiLanguageSetting.Read(_root));
    }

    [Theory]
    [InlineData(UiLanguage.English)]
    [InlineData(UiLanguage.Japanese)]
    public void TheChosenLanguageIsReadBack(UiLanguage language)
    {
        UiLanguageSetting.Write(_root, language);
        Assert.Equal(language, UiLanguageSetting.Read(_root));
        UiLanguageSetting.Write(_root, language == UiLanguage.English ? UiLanguage.Japanese : UiLanguage.English);
        Assert.NotEqual(language, UiLanguageSetting.Read(_root));
    }

    [Fact]
    public void AnUnreadableValueCountsAsNoChoice()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, UiLanguageSetting.FileName), "klingon");
        Assert.Null(UiLanguageSetting.Read(_root));
    }

    [Fact]
    public void LanguageCodesRoundTrip()
    {
        foreach (var language in Enum.GetValues<UiLanguage>())
            Assert.Equal(language, L.Parse(L.Code(language)));
        Assert.Null(L.Parse(null));
    }
}
