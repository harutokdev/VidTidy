using System.Globalization;
using LocalMediaAI.Core;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace LocalMediaAI.App.Localization;

/// <summary>
/// Asked on the first start: which language the app shows. Written in both languages, since none is chosen yet.
/// <see cref="Choice"/> stays null when the window is closed without choosing.
/// </summary>
public sealed class LanguagePickerWindow : Window
{
    private static readonly Brush Surface = new SolidColorBrush(Color.FromRgb(0x1A, 0x20, 0x29));
    private static readonly Brush Text = new SolidColorBrush(Color.FromRgb(0xE7, 0xED, 0xF4));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x9A, 0xA7, 0xB6));

    public UiLanguage? Choice { get; private set; }

    public LanguagePickerWindow()
    {
        Title = "VidTidy — 言語 / Language";  // i18n: keep (shown in both languages)
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Surface;
        Foreground = Text;
        FontFamily = new FontFamily("Yu Gothic UI, Meiryo UI, Segoe UI");
        FontSize = 14;
        AutomationProperties.SetAutomationId(this, "LanguagePicker");

        var suggested = SuggestedLanguage(CultureInfo.CurrentUICulture);
        var panel = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };
        panel.Children.Add(new TextBlock { Text = "表示する言語を選んでください", FontSize = 18, FontWeight = FontWeights.SemiBold });  // i18n: keep (shown in both languages)
        panel.Children.Add(new TextBlock { Text = "Choose the display language", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 0) });
        panel.Children.Add(new TextBlock
        {
            Text = "あとから設定で変えられます（再起動後に反映）。\nYou can change it later in Settings (takes effect after a restart).",  // i18n: keep (shown in both languages)
            Foreground = Muted, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 18)
        });
        panel.Children.Add(ChoiceButton("日本語", "Japanese", UiLanguage.Japanese, suggested));  // i18n: keep (shown in both languages)
        panel.Children.Add(ChoiceButton("English", "英語", UiLanguage.English, suggested));  // i18n: keep (shown in both languages)
        Content = panel;
    }

    /// <summary>The language offered first: Japanese on a Japanese Windows, English otherwise.</summary>
    public static UiLanguage SuggestedLanguage(CultureInfo culture) =>
        culture.TwoLetterISOLanguageName == "ja" ? UiLanguage.Japanese : UiLanguage.English;

    /// <summary>
    /// The organize screen's button look (BtnBase / Primary there): the suggested language in the accent colour, the
    /// other on the raised surface, a lighter fill on hover and an accent outline on keyboard focus.
    /// </summary>
    private static Style ButtonStyle(bool primary)
    {
        var (fill, border, hover, text) = primary ? ("#3C86DA", "#3C86DA", "#5A9CE6", "White") : ("#222A35", "#3B4757", "#2A3441", "#E7EDF4");
        return (Style)XamlReader.Parse($$"""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
              <Setter Property="Foreground" Value="{{text}}"/>
              <Setter Property="Background" Value="{{fill}}"/>
              <Setter Property="BorderBrush" Value="{{border}}"/>
              <Setter Property="BorderThickness" Value="1"/>
              <Setter Property="Cursor" Value="Hand"/>
              <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
              <Setter Property="Template">
                <Setter.Value>
                  <ControlTemplate TargetType="Button">
                    <Border x:Name="B" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="6" Padding="{TemplateBinding Padding}">
                      <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="B" Property="Background" Value="{{hover}}"/></Trigger>
                      <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="B" Property="BorderBrush" Value="#5A9CE6"/><Setter TargetName="B" Property="BorderThickness" Value="2"/></Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value>
              </Setter>
            </Style>
            """);
    }

    private Button ChoiceButton(string label, string otherName, UiLanguage language, UiLanguage suggested)
    {
        var primary = language == suggested;
        var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(new TextBlock { Text = label, FontSize = 16, FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal, HorizontalAlignment = HorizontalAlignment.Center });
        content.Children.Add(new TextBlock { Text = otherName, FontSize = 11.5, Opacity = 0.75, HorizontalAlignment = HorizontalAlignment.Center });
        var button = new Button
        {
            Content = content,
            Style = ButtonStyle(primary),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 10),
            IsDefault = primary
        };
        AutomationProperties.SetName(button, label);
        AutomationProperties.SetAutomationId(button, "LanguagePicker" + language);
        button.Click += (_, _) =>
        {
            Choice = language;
            DialogResult = true;
        };
        if (language == suggested) Loaded += (_, _) => button.Focus();
        return button;
    }
}
