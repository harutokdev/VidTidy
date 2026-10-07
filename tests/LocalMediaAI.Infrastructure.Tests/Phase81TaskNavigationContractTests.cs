using System.Xml.Linq;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class Phase81TaskNavigationContractTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void MainWindowExposesThreePrimaryWorkspacesAndContextualOperationWorkspace()
    {
        var document = LoadMainWindow();
        var automationIds = document.Descendants()
            .Select(element => (string?)element.Attribute("AutomationProperties.AutomationId"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        Assert.Equal(1, automationIds.Count(id => id == "TaskWorkspaceTabs"));
        Assert.Contains("LibraryWorkspaceTab", automationIds);
        Assert.Contains("AiSearchWorkspaceTab", automationIds);
        Assert.Contains("ReviewWorkspaceTab", automationIds);
        Assert.DoesNotContain("OperationsWorkspaceTab", automationIds);
        Assert.Contains("OperationWorkspaceBackButton", automationIds);
        Assert.Contains("ReviewModeTabs", automationIds);
        Assert.Contains("UnifiedReviewSubTab", automationIds);
        Assert.Contains("DuplicateReviewSubTab", automationIds);
        Assert.Contains("ContentSimilarityReviewSubTab", automationIds);
        Assert.Contains("SuggestedTagReviewSubTab", automationIds);
    }

    [Fact]
    public void ExistingFeatureControlsRemainSingleBoundInstancesWithNearbyContextDetail()
    {
        var document = LoadMainWindow();
        var names = document.Descendants()
            .Select(element => (string?)element.Attribute(Xaml + "Name"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        var source = File.ReadAllText(MainWindowPath());

        Assert.Equal(1, names.Count(name => name == "SemanticSearchInput"));
        Assert.Equal(1, names.Count(name => name == "SemanticResultsDataGrid"));
        Assert.Equal(1, names.Count(name => name == "TagReviewDataGrid"));
        Assert.Equal(1, names.Count(name => name == "PreviewMediaElement"));
        Assert.Contains("AutomationProperties.AutomationId=\"LibraryMediaDataGrid\"", source, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"SuggestedTagContextDetail\"", source, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"SelectedTagReviewName\"", source, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource OperationsWorkspaceExpanderOnly}\"", source, StringComparison.Ordinal);
        Assert.Contains("Full SHA-256 evidence", source, StringComparison.Ordinal);
        Assert.Contains("non-transitive pair edge", source, StringComparison.Ordinal);
        Assert.Contains("完全削除の一括操作: 対象外 / 利用不可", source, StringComparison.Ordinal);

        var contextPanel = document.Descendants().Single(element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == "ContextDetailPanel");
        Assert.Null(contextPanel.Attribute("Grid.Column"));

        var contextStyle = document.Descendants().Single(element =>
            (string?)element.Attribute(Xaml + "Key") == "ContextWorkspacePanel");
        var operationsTrigger = contextStyle.Descendants().Single(element =>
            element.Name.LocalName == "DataTrigger" && (string?)element.Attribute("Value") == "3");
        Assert.Contains(operationsTrigger.Descendants(), element =>
            element.Name.LocalName == "Setter"
            && (string?)element.Attribute("Property") == "Grid.Column"
            && (string?)element.Attribute("Value") == "0");
        Assert.Contains(operationsTrigger.Descendants(), element =>
            element.Name.LocalName == "Setter"
            && (string?)element.Attribute("Property") == "Grid.ColumnSpan"
            && (string?)element.Attribute("Value") == "3");
    }

    private static XDocument LoadMainWindow() => XDocument.Load(MainWindowPath());

    private static string MainWindowPath() => Path.Combine(FindRepositoryRoot(), "src", "LocalMediaAI.App", "MainWindow.xaml");

    private static string FindRepositoryRoot()
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
