using System.Xml.Linq;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class Phase84SemanticScoreContextTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void AiSearchShowsAlwaysVisibleNonConfidenceScoreContextWithoutInventedThreshold()
    {
        var document = XDocument.Load(MainWindowPath());
        var context = document.Descendants()
            .Single(element => (string?)element.Attribute("AutomationProperties.AutomationId") == "SemanticScoreContext");
        var visibleText = string.Join(" ", context.Descendants()
            .Select(element => (string?)element.Attribute("Text"))
            .Where(text => !string.IsNullOrWhiteSpace(text)));

        Assert.Contains("順位用の値", visibleText, StringComparison.Ordinal);
        Assert.Contains("高いほど上位", visibleText, StringComparison.Ordinal);
        Assert.Contains("確率・信頼度・正解率ではなく", visibleText, StringComparison.Ordinal);
        Assert.Contains("検索意図への一致を保証しません", visibleText, StringComparison.Ordinal);
        Assert.Contains("関連性の低い候補も含まれます", visibleText, StringComparison.Ordinal);
        Assert.Contains("プレビューで確認して判断", visibleText, StringComparison.Ordinal);
        Assert.DoesNotContain("0.5", visibleText, StringComparison.Ordinal);
        Assert.DoesNotContain("低信頼", visibleText, StringComparison.Ordinal);
        Assert.DoesNotContain("中信頼", visibleText, StringComparison.Ordinal);
        Assert.DoesNotContain("高信頼", visibleText, StringComparison.Ordinal);
    }

    [Fact]
    public void ResultAndSelectedDetailUseAiSimilarityTerminologyAndRemainSelectable()
    {
        var document = XDocument.Load(MainWindowPath());
        var grid = document.Descendants().Single(element => (string?)element.Attribute(Xaml + "Name") == "SemanticResultsDataGrid");
        var source = LocalizedSource.Read(MainWindowPath());

        Assert.Equal("330", (string?)grid.Attribute("MinHeight"));
        Assert.Equal("{Binding SelectedSemanticResult}", (string?)grid.Attribute("SelectedItem"));
        Assert.Equal("Single", (string?)grid.Attribute("SelectionMode"));
        Assert.Contains("Header=\"AI類似度（cosine）\"", source, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"SelectedSemanticResultScoreValue\"", source, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"SelectedSemanticScoreContext\"", source, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{Binding SelectedSemanticResultScore}\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"信頼度\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"一致率\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"正解率\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplanationMatchesCurrentTopKRankingWithoutAbsoluteRelevanceCutoff()
    {
        var service = LocalizedSource.Read(Path.Combine(RepositoryRoot(), "src", "LocalMediaAI.Infrastructure", "SemanticSearchService.cs"));
        var viewModel = LocalizedSource.Read(Path.Combine(RepositoryRoot(), "src", "LocalMediaAI.App", "ViewModels", "MainViewModel.cs"));

        Assert.Contains("OrderByDescending(candidate => candidate.Score)", service, StringComparison.Ordinal);
        Assert.Contains(".Take(topK)", service, StringComparison.Ordinal);
        Assert.Contains("topK = Math.Clamp(topK, 1, 100);", service, StringComparison.Ordinal);
        Assert.Contains("SearchAsync(query, 100", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("candidate.Score >=", service, StringComparison.Ordinal);
        Assert.DoesNotContain("candidate.Score >", service, StringComparison.Ordinal);
    }

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
