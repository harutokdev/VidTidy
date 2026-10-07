using System.Xml.Linq;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class Post8StabilizationADarkLocalizationTests
{
    [Fact]
    public void MainWindowDefinesDarkSurfacesAndVisibleStateContrast()
    {
        var application = XDocument.Load(Path.Combine(RepositoryRoot(), "src", "LocalMediaAI.App", "App.xaml"));
        Assert.Equal("Dark", (string?)application.Root?.Attribute("ThemeMode"));
        Assert.Contains(application.Descendants(), element => element.Name.LocalName == "ResourceDictionary"
            && (string?)element.Attribute("Source") == "DarkTheme.xaml");
        var document = XDocument.Load(MainWindowPath());
        var theme = XDocument.Load(DarkThemePath());
        var styles = theme.Descendants().Concat(document.Descendants()).Where(element => element.Name.LocalName == "Style").ToArray();

        AssertStyle(styles, "DataGrid", "Background", "#1C222B");
        AssertStyle(styles, "DataGridColumnHeader", "Background", "#26313E");
        AssertStyle(styles, "ListBox", "Background", "#1C222B");
        AssertStyle(styles, "TabControl", "Background", "#15191F");
        AssertStyle(styles, "ComboBox", "Background", "#202833");
        Assert.Contains(ImplicitStyle(styles, "DataGridRow").Descendants(), element =>
            element.Name.LocalName == "Trigger" && (string?)element.Attribute("Property") == "IsSelected");
        Assert.Contains(ImplicitStyle(styles, "Button").Descendants(), element =>
            element.Name.LocalName == "Trigger" && (string?)element.Attribute("Property") == "IsEnabled" && (string?)element.Attribute("Value") == "False");
        Assert.Contains(ImplicitStyle(styles, "TabItem").Descendants(), element =>
            element.Name.LocalName == "Trigger" && (string?)element.Attribute("Property") == "IsKeyboardFocused");

        var localItemStyles = document.Descendants().Where(element =>
            element.Name.LocalName == "ListBox.ItemContainerStyle").SelectMany(element => element.Elements());
        Assert.All(localItemStyles, style => Assert.Equal("{StaticResource {x:Type ListBoxItem}}", (string?)style.Attribute("BasedOn")));
    }

    [Fact]
    public void PrimaryWorkspaceAndDailyActionsUseJapanesePresentation()
    {
        var source = LocalizedSource.Read(MainWindowPath());
        foreach (var required in new[]
        {
            // File operation opens in context (not a tab) and the preview opens on selection, so neither has its own header/button.
            "Header=\"ライブラリ\"", "Header=\"AI検索\"", "Header=\"レビュー\"",
            "Text=\"登録フォルダー\"", "Text=\"タグ絞り込み\"", "Header=\"AI検索インデックス\"",
            "Content=\"選択時刻へ移動\"", "Content=\"再生 / 一時停止\"",
            "Content=\"Windowsのごみ箱へ移動\"", "Content=\"完全削除 — 元に戻せません\""
        }) Assert.Contains(required, source, StringComparison.Ordinal);

        foreach (var removed in new[]
        {
            "Header=\"Library\"", "Header=\"AI Search\"", "Header=\"Review\"", "Header=\"Operations\"",
            "Text=\"Library Roots\"", "Text=\"Tag filter\"", "Header=\"Semantic Index\"",
            "Content=\"Open Preview\"", "Content=\"Seek to Selected Timestamp\"", "Content=\"Play / Pause\""
        }) Assert.DoesNotContain(removed, source, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaAndSemanticStatesAreLocalizedWithoutChangingEnumSemantics()
    {
        var source = LocalizedSource.Read(Path.Combine(RepositoryRoot(), "src", "LocalMediaAI.Core", "Domain.cs"));
        Assert.Contains("PresenceState.Present => \"利用可能\"", source, StringComparison.Ordinal);
        Assert.Contains("PresenceState.OfflineRoot => \"登録フォルダーがオフライン\"", source, StringComparison.Ordinal);
        Assert.Contains("SemanticIndexStatus.Indexed => \"解析済み\"", source, StringComparison.Ordinal);
        Assert.Contains("SemanticIndexStatus.Failed => \"解析失敗\"", source, StringComparison.Ordinal);
        Assert.Contains("AI類似度（cosine）", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Confidence", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadinessGuidanceUsesTheSameJapaneseWorkspaceTerms()
    {
        var source = LocalizedSource.Read(Path.Combine(RepositoryRoot(), "src", "LocalMediaAI.App", "ViewModels", "ReadinessPresentation.cs"));
        Assert.Contains("\"AI検索とレビューを利用できます。", source, StringComparison.Ordinal);
        Assert.Contains("\"レビューを開く\"", source, StringComparison.Ordinal);
        Assert.Contains("\"AI検索を開く\"", source, StringComparison.Ordinal);
        Assert.Contains("\"ライブラリを開く\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\"AI Searchを", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Reviewを", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Libraryを", source, StringComparison.Ordinal);
    }

    private static void AssertStyle(IEnumerable<XElement> styles, string targetType, string property, string value)
    {
        var style = ImplicitStyle(styles, targetType);
        Assert.Contains(style.Elements(), element => element.Name.LocalName == "Setter"
            && (string?)element.Attribute("Property") == property
            && (string?)element.Attribute("Value") == value);
    }

    private static XElement ImplicitStyle(IEnumerable<XElement> styles, string targetType)
        => styles.Single(element => element.Parent?.Name.LocalName is "Window.Resources" or "ResourceDictionary"
            && (string?)element.Attribute("TargetType") == targetType
            && element.Attributes().All(attribute => attribute.Name.LocalName != "Key"));

    private static string DarkThemePath() => Path.Combine(RepositoryRoot(), "src", "LocalMediaAI.App", "DarkTheme.xaml");

    private static string MainWindowPath() => Path.Combine(RepositoryRoot(), "src", "LocalMediaAI.App", "MainWindow.xaml");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LocalMediaAI.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not find LocalMediaAI.sln from test base directory.");
    }
}
