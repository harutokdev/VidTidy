using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
namespace LocalMediaAI.App;
/// <summary>Read-only presentation visibility; never controls workflow commands.</summary>
public static class DeveloperDiagnostics
{
    public static readonly DependencyProperty ShowProperty = DependencyProperty.RegisterAttached("Show", typeof(bool), typeof(DeveloperDiagnostics), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits, OnChanged));
    public static readonly DependencyProperty SectionProperty = DependencyProperty.RegisterAttached("Section", typeof(bool), typeof(DeveloperDiagnostics), new PropertyMetadata(false, OnChanged));
    public static bool GetShow(DependencyObject target) => (bool)target.GetValue(ShowProperty);
    public static void SetShow(DependencyObject target, bool value) => target.SetValue(ShowProperty, value);
    public static bool GetSection(DependencyObject target) => (bool)target.GetValue(SectionProperty);
    public static void SetSection(DependencyObject target, bool value) => target.SetValue(SectionProperty, value);
    private static void OnChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (!GetSection(target) || target is not UIElement element) return;
        var show = GetShow(target);
        element.SetCurrentValue(UIElement.VisibilityProperty, show ? Visibility.Visible : Visibility.Collapsed);
        if (target is Expander expander && !show) expander.SetCurrentValue(Expander.IsExpandedProperty, false);
        if (string.IsNullOrEmpty(AutomationProperties.GetName(target))) AutomationProperties.SetName(target, L.T("開発者向け診断情報"));
    }
}
