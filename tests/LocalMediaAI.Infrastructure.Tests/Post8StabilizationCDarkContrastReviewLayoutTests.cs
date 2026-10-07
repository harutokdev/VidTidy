using System.Xml.Linq;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class Post8StabilizationCDarkContrastReviewLayoutTests
{
    private static readonly string?[] ExpectedReviewModeIds =
    [
        "UnifiedReviewSubTab",
        "DuplicateReviewSubTab",
        "ContentSimilarityReviewSubTab",
        "SuggestedTagReviewSubTab"
    ];

    [Fact]
    public void SharedDarkForegroundCoversNormalTextAndNestedPresentationStyles()
    {
        var document = XDocument.Load(MainWindowPath());
        var resourceStyles = document.Root!
            .Elements().Single(element => element.Name.LocalName == "Window.Resources")
            .Elements().Where(element => element.Name.LocalName == "Style")
            .Concat(XDocument.Load(Path.Combine(RepositoryRoot(), "src", "LocalMediaAI.App", "DarkTheme.xaml")).Root!
                .Elements().Where(element => element.Name.LocalName == "Style"))
            .ToArray();

        AssertSetter(ImplicitStyle(resourceStyles, "TextBlock"), "Foreground", "#E8EDF4");
        AssertSetter(ImplicitStyle(resourceStyles, "Label"), "Foreground", "#E8EDF4");
        AssertSetter(ImplicitStyle(resourceStyles, "AccessText"), "Foreground", "#E8EDF4");
        AssertSetter(ImplicitStyle(resourceStyles, "CheckBox"), "Foreground", "#E8EDF4");
        AssertSetter(ImplicitStyle(resourceStyles, "Expander"), "Foreground", "#E8EDF4");

        var nestedTextBlockStyles = document.Descendants()
            .Where(element => element.Name.LocalName == "TextBlock.Style")
            .SelectMany(element => element.Elements().Where(child => child.Name.LocalName == "Style"))
            .ToArray();
        Assert.NotEmpty(nestedTextBlockStyles);
        Assert.All(nestedTextBlockStyles, style =>
            Assert.Equal("{StaticResource {x:Type TextBlock}}", (string?)style.Attribute("BasedOn")));

        var source = File.ReadAllText(MainWindowPath()) + File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "LocalMediaAI.App", "DarkTheme.xaml"));
        Assert.Contains("TextElement.Foreground=\"{TemplateBinding Foreground}\"", source, StringComparison.Ordinal);

        var typedVisibilityStyles = new[]
        {
            "LibraryWorkspaceTextBlockOnly",
            "AiSearchWorkspaceExpanderOnly",
            "OperationsWorkspaceExpanderOnly",
            "BrowseReviewOrSearchExpanderOnly",
            "ReviewUnifiedExpanderOnly",
            "ReviewDuplicatesExpanderOnly",
            "ReviewContentSimilarityExpanderOnly",
            "ReviewSuggestedTagsExpanderOnly"
        };
        foreach (var key in typedVisibilityStyles)
        {
            var style = resourceStyles.Single(element =>
                (string?)element.Attributes().SingleOrDefault(attribute => attribute.Name.LocalName == "Key") == key);
            Assert.StartsWith("{StaticResource {x:Type ", (string?)style.Attribute("BasedOn"));
        }

        Assert.DoesNotContain(document.Descendants().Where(element => element.Name.LocalName == "Expander"),
            expander => ((string?)expander.Attribute("Style")) is
                "{StaticResource AiSearchWorkspaceOnly}"
                or "{StaticResource OperationsWorkspaceOnly}"
                or "{StaticResource BrowseReviewOrSearchOnly}"
                or "{StaticResource ReviewUnifiedOnly}"
                or "{StaticResource ReviewDuplicatesOnly}"
                or "{StaticResource ReviewContentSimilarityOnly}"
                or "{StaticResource ReviewSuggestedTagsOnly}");
    }

    [Fact]
    public void ReviewModeNavigationAutoSizesEnoughForAllFourExistingModes()
    {
        var document = XDocument.Load(MainWindowPath());
        var tabs = document.Descendants().Single(element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == "ReviewModeTabs");

        Assert.Null(tabs.Attribute("Height"));
        Assert.Equal("140", (string?)tabs.Attribute("MinHeight"));
        Assert.Equal("Top", (string?)tabs.Attribute("VerticalAlignment"));

        var modeIds = tabs.Elements()
            .Where(element => element.Name.LocalName == "TabItem")
            .Select(element => (string?)element.Attribute("AutomationProperties.AutomationId"))
            .ToArray();
        Assert.Equal(ExpectedReviewModeIds, modeIds);
    }

    private static void AssertSetter(XElement style, string property, string value)
        => Assert.Contains(style.Elements(), element => element.Name.LocalName == "Setter"
            && (string?)element.Attribute("Property") == property
            && (string?)element.Attribute("Value") == value);

    private static XElement ImplicitStyle(IEnumerable<XElement> styles, string targetType)
        => styles.Single(element => (string?)element.Attribute("TargetType") == targetType
            && element.Attributes().All(attribute => attribute.Name.LocalName != "Key"));

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
