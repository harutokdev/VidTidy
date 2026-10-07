using System.Xml.Linq;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class Phase86CAccessibilityPresentationTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void RootRowsExposeConciseIdentityAndOfflineStateWithoutRecordFallback()
    {
        var rootList = ByAutomationId(LoadMainWindow(), "LibraryRootsList");
        var style = rootList.Descendants().Single(element =>
            element.Name.LocalName == "Style" && (string?)element.Attribute("TargetType") == "ListBoxItem");

        Assert.Contains(style.Elements(), element => IsSetter(element, "AutomationProperties.Name", "{Binding DisplayName}"));
        Assert.Contains(style.Elements(), element => IsSetter(element, "AutomationProperties.HelpText", "{Binding ConfiguredPath}"));

        var offline = style.Descendants().Single(element =>
            element.Name.LocalName == "DataTrigger" && (string?)element.Attribute("Value") == "Offline");
        Assert.Contains(offline.Elements(), element =>
            element.Name.LocalName == "Setter"
            && (string?)element.Attribute("Property") == "AutomationProperties.Name"
            && ((string?)element.Attribute("Value"))?.Contains("オフライン", StringComparison.Ordinal) == true);

        Assert.DoesNotContain(rootList.Descendants(), element =>
            (string?)element.Attribute("AutomationProperties.Name") == "{Binding}");
        Assert.Contains(rootList.Descendants(), element =>
            (string?)element.Attribute("Text") == "{Binding DisplayName}"
            && (string?)element.Attribute("AutomationProperties.Name") == string.Empty);
    }

    [Fact]
    public void UnifiedAndSuggestedRowsBindExplicitUserFacingContainerNames()
    {
        var document = LoadMainWindow();
        AssertRowBindings(ByAutomationId(document, "UnifiedReviewItemsList"));
        AssertRowBindings(ByAutomationId(document, "SuggestedTagItemsList"));

        var unified = LocalizedSource.Read(Path.Combine(RepositoryRoot(), "src", "LocalMediaAI.App", "ViewModels", "UnifiedReviewRow.cs"));
        var tag = LocalizedSource.Read(Path.Combine(RepositoryRoot(), "src", "LocalMediaAI.App", "ViewModels", "TagReviewRow.cs"));
        Assert.Contains("AutomationName => $\"{GeneralType}: {GeneralMediaLabel}\"", unified, StringComparison.Ordinal);
        Assert.Contains("AutomationHelpText => L.F(\"判断: {0}。{1}\", GeneralDecision,", unified, StringComparison.Ordinal);
        Assert.Contains("AutomationName => $\"{DisplayName}: {FileName}\"", tag, StringComparison.Ordinal);
        Assert.Contains("AutomationHelpText => GeneralState", tag, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelDistributionAndRuntimeProfilesRemainInCollapsedTechnicalDisclosure()
    {
        var document = LoadMainWindow();
        var technical = ByAutomationId(document, "SemanticTechnicalDetails");
        Assert.Equal("False", (string?)technical.Attribute("IsExpanded"));
        Assert.Contains(technical.Descendants(), element => (string?)element.Attribute("Text") == "{Binding SemanticModelStatusText}");
        Assert.Contains(technical.Descendants(), element => (string?)element.Attribute("Text") == "{Binding SemanticExecutionStatusText}");
        Assert.Contains(technical.Descendants(), element =>
            (string?)element.Attribute("AutomationProperties.Name") == "{Binding SemanticModelStatusText}");
        Assert.Contains(technical.Descendants(), element =>
            (string?)element.Attribute("AutomationProperties.Name") == "{Binding SemanticExecutionStatusText}");

        var technicalBindings = technical.DescendantsAndSelf()
            .Attributes()
            .Select(attribute => attribute.Value)
            .Where(value => value.Contains("SemanticModelStatusText", StringComparison.Ordinal)
                            || value.Contains("SemanticExecutionStatusText", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(4, technicalBindings.Length);
        Assert.DoesNotContain(document.Descendants().Except(technical.DescendantsAndSelf()), element =>
            element.Attributes().Any(attribute =>
                attribute.Value.Contains("SemanticModelStatusText", StringComparison.Ordinal)
                || attribute.Value.Contains("SemanticExecutionStatusText", StringComparison.Ordinal)));

        var source = LocalizedSource.Read(MainWindowPath());
        Assert.Contains("AutomationProperties.AutomationId=\"SemanticSearchStatus\"", source, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"SemanticScoreContext\"", source, StringComparison.Ordinal);

        var viewModel = LocalizedSource.Read(Path.Combine(RepositoryRoot(), "src", "LocalMediaAI.App", "ViewModels", "MainViewModel.cs"));
        var commandStart = viewModel.IndexOf("SemanticSearchCommand = new AsyncCommand", StringComparison.Ordinal);
        var commandEnd = viewModel.IndexOf("CancelSemanticSearchCommand", commandStart, StringComparison.Ordinal);
        var commandDefinition = viewModel[commandStart..commandEnd];
        Assert.DoesNotContain("DistributionStatus", commandDefinition, StringComparison.Ordinal);
        Assert.DoesNotContain("ApprovedForTechnicalUse", commandDefinition, StringComparison.Ordinal);
        Assert.Contains("!IsBusy && !string.IsNullOrWhiteSpace(SemanticSearchText)", commandDefinition, StringComparison.Ordinal);
    }

    private static void AssertRowBindings(XElement list)
    {
        var style = list.Descendants().Single(element =>
            element.Name.LocalName == "Style" && (string?)element.Attribute("TargetType") == "ListBoxItem");
        Assert.Contains(style.Elements(), element => IsSetter(element, "AutomationProperties.Name", "{Binding AutomationName}"));
        Assert.Contains(style.Elements(), element => IsSetter(element, "AutomationProperties.HelpText", "{Binding AutomationHelpText}"));
    }

    private static bool IsSetter(XElement element, string property, string value)
        => element.Name.LocalName == "Setter"
           && (string?)element.Attribute("Property") == property
           && (string?)element.Attribute("Value") == value;

    private static XElement ByAutomationId(XDocument document, string id)
        => document.Descendants().Single(element => (string?)element.Attribute("AutomationProperties.AutomationId") == id);

    private static XDocument LoadMainWindow() => XDocument.Load(MainWindowPath());
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
