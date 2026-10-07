using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LocalMediaAI.App.Localization;
using LocalMediaAI.Core;

namespace LocalMediaAI.Infrastructure.Tests;

/// <summary>
/// English counts in XAML bindings ("1 group", not "1 groups") and the first-start language window in the app's dark
/// look. The picker is saved to artifacts/ui-english/language-picker.png for a look.
/// </summary>
[Collection(EnglishUiGroup.Name)]
public sealed class EnglishPluralAndPickerTests : IDisposable
{
    public EnglishPluralAndPickerTests() => L.Use(UiLanguage.English);

    public void Dispose() => L.Use(UiLanguage.Japanese);

    [Theory]
    [InlineData(1, "1 group · 85%+ match")]
    [InlineData(3, "3 groups · 85%+ match")]
    public Task TranslatedBindingsChooseTheWordByTheNumber(int count, string expected) => OnUiThreadAsync(() =>
    {
        var text = new TextBlock { DataContext = new { Count = count } };
        text.SetBinding(TextBlock.TextProperty, new TBinding("Count") { TFormat = "{0}組・一致率85%以上" });
        Assert.Equal(expected, text.Text);
    });

    [Fact]
    public Task FormatsWithoutChoicesStayPlainStringFormats() => OnUiThreadAsync(() =>
    {
        var binding = new TBinding("Name") { TFormat = "{0} を再生" };
        Assert.Equal("Play {0}", binding.StringFormat);
        Assert.Null(binding.Converter);
    });

    [Fact]
    public Task ThePickerUsesTheAppsDarkButtonsWithTheSuggestedLanguageInTheAccent() => OnUiThreadAsync(() =>
    {
        var picker = new LanguagePickerWindow { Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            picker.Show();
            picker.UpdateLayout();
            var buttons = Descendants(picker).OfType<Button>().ToArray();
            Assert.Equal(["日本語", "English"], buttons.Select(AutomationProperties.GetName));
            var suggested = LanguagePickerWindow.SuggestedLanguage(System.Globalization.CultureInfo.CurrentUICulture);
            var primary = buttons.Single(b => b.IsDefault);
            Assert.Equal(suggested == UiLanguage.Japanese ? "日本語" : "English", AutomationProperties.GetName(primary));
            Assert.Equal(Color.FromRgb(0x3C, 0x86, 0xDA), ((SolidColorBrush)Fill(primary)).Color);
            Assert.Equal(Color.FromRgb(0x22, 0x2A, 0x35), ((SolidColorBrush)Fill(buttons.Single(b => !b.IsDefault))).Color);
            Save(picker);
        }
        finally { picker.Close(); }
    });

    /// <summary>The colour the button is drawn with: its template's border, not the Background property alone.</summary>
    private static Brush Fill(Button button) => ((Border)VisualTreeHelper.GetChild(button, 0)).Background;

    private static void Save(Window window)
    {
        var content = (FrameworkElement)window.Content;
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
        var margin = content.Margin;
        var width = content.ActualWidth + margin.Left + margin.Right;
        var height = content.ActualHeight + margin.Top + margin.Bottom;
        var bitmap = new RenderTargetBitmap((int)(width * 1.5), (int)(height * 1.5), 144, 144, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(margin.Left, margin.Top, content.ActualWidth, content.ActualHeight));
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var directory = Path.Combine(RepositoryRoot(), "artifacts", "ui-english");
        Directory.CreateDirectory(directory);
        using var stream = File.Create(Path.Combine(directory, "language-picker.png"));
        encoder.Save(stream);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static Task OnUiThreadAsync(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); done.SetResult(); }
            catch (Exception ex) { done.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "LocalMediaAI.sln"))) return directory.FullName;
        throw new InvalidOperationException("Repository root was not found.");
    }
}
