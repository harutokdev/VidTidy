using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LocalMediaAI.App.Organize;

/// <summary>
/// Dialog frame shared by every overlay: a centred modal (header, body, footer) or a right-side drawer,
/// over a dark scrim. Mirrors .modal / .drawer in the prototype.
/// </summary>
public partial class OrganizeView
{
    private bool _dismissable = true;
    private Action? _onClose;

    /// <summary>Opens the modal with a header; the caller fills OverlayBody and then calls SetButtons.</summary>
    private void OpenModal(string title, string? subtitle = null, UIElement? mark = null, UIElement? headerExtra = null, bool dismissable = true, double maxWidth = 780)
    {
        StopPlayer();
        OverlayHeader.Children.Clear();
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, subtitle is null && headerExtra is null ? 0 : 8) };
        if (mark is not null) { titleRow.Children.Add(mark); }
        titleRow.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        OverlayHeader.Children.Add(titleRow);
        if (subtitle is not null) OverlayHeader.Children.Add(new TextBlock { Text = subtitle, FontSize = 13, Foreground = Res("Muted"), TextWrapping = TextWrapping.Wrap });
        if (headerExtra is not null) OverlayHeader.Children.Add(headerExtra);
        OverlayBody.Children.Clear();
        OverlayButtons.Children.Clear();
        OverlayFootNote.Text = string.Empty;
        OverlayCard.MaxWidth = maxWidth;
        OverlayCard.Visibility = Visibility.Visible;
        DrawerCard.Visibility = Visibility.Collapsed;
        _dismissable = dismissable;
        _onClose = null;
        Overlay.Visibility = Visibility.Visible;
    }

    /// <summary>Footer buttons, left to right. Style is a resource key: Primary, BtnBase, Ghost, DangerBtn.</summary>
    private List<Button> SetButtons(params (string Label, string Style, Action OnClick)[] buttons)
    {
        OverlayButtons.Children.Clear();
        var made = new List<Button>();
        foreach (var (label, style, onClick) in buttons)
        {
            var button = new Button { Content = label, Style = (Style)FindResource(style), Margin = new Thickness(10, 0, 0, 0) };
            button.Click += (_, _) => onClick();
            OverlayButtons.Children.Add(button);
            made.Add(button);
        }
        made.LastOrDefault()?.Focus();
        return made;
    }

    /// <summary>Opens the right-side drawer; the caller adds sections to DrawerBody.</summary>
    private void OpenDrawer(string title, UIElement? pill = null)
    {
        StopPlayer();
        DrawerHeader.Children.Clear();
        DrawerHeader.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
        if (pill is not null) DrawerHeader.Children.Add(pill);
        DrawerBody.Children.Clear();
        OverlayCard.Visibility = Visibility.Collapsed;
        DrawerCard.Visibility = Visibility.Visible;
        _dismissable = true;
        _onClose = null;
        Overlay.Visibility = Visibility.Visible;
    }

    /// <summary>One drawer section (.dsec): a heading with optional pill and right-side action, then content.</summary>
    private StackPanel DrawerSection(string title, UIElement? pill = null, UIElement? right = null)
    {
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        if (right is not null) { DockPanel.SetDock(right, Dock.Right); head.Children.Add(right); }
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
        if (pill is not null) left.Children.Add(pill);
        head.Children.Add(left);
        var section = new StackPanel();
        section.Children.Add(head);
        DrawerBody.Children.Add(new Border { Child = section, Padding = new Thickness(18, 14, 18, 14), BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, 1) });
        return section;
    }

    private void HideOverlay()
    {
        StopPlayer();
        Overlay.Visibility = Visibility.Collapsed;
        var onClose = _onClose;
        _onClose = null;
        onClose?.Invoke();
    }

    private void CloseOverlay_OnClick(object sender, RoutedEventArgs e) => HideOverlay();

    private void Scrim_OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_dismissable && ReferenceEquals(e.OriginalSource, Overlay)) HideOverlay();
    }

    private void Card_OnMouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void ShowMessage(string title, string body)
    {
        OpenModal(title);
        OverlayBody.Children.Add(Muted(body, 13, new Thickness(0)));
        SetButtons((L.T("閉じる"), "Primary", HideOverlay));
    }

    // ---------- small building blocks ----------
    private static Border Mark(GroupCategory category, double size = 18)
    {
        var (mark, color, ink) = category switch
        {
            GroupCategory.Exact => ("=", "#3FAE7A", "#0C1B13"),
            GroupCategory.Similar => ("≈", "#E6C547", "#15130A"),
            _ => ("!", "#E4625B", "#1D0B0A")
        };
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = Hex(color),
            Child = new TextBlock { Text = mark, FontWeight = FontWeights.Bold, FontSize = size * 0.66, Foreground = Hex(ink), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
    }

    private TextBlock Muted(string text, double size, Thickness? margin = null, bool bold = false) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0),
        Foreground = bold ? Res("Text") : Res("Muted"), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
    };

    /// <summary>Left-ruled note like the prototype: plain, warn (amber) or ok (green rule).</summary>
    private Border Note(string text, bool warn, bool ok = false) => Note(new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = warn ? Res("WarnText") : Res("Muted") }, warn, ok);

    private Border Note(UIElement content, bool warn, bool ok = false) => new()
    {
        BorderThickness = new Thickness(3, 0, 0, 0),
        BorderBrush = warn ? Res("Warn") : ok ? Res("Keep") : Res("Line2"),
        Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 8, 0, 8), Child = content
    };

    /// <summary>Pill: kind is "keep", "trash", "info", "manual" or plain.</summary>
    private Border PillOf(string text, string kind = "", double size = 11)
    {
        var (style, ink) = kind switch
        {
            "keep" => ("PillKeep", "Keep"),
            "trash" => ("PillTrash", "Trash"),
            "info" => ("PillInfo", "AccentHi"),
            "manual" => ("PillManual", "AccentHi"),
            _ => ("Pill", "Muted")
        };
        return new Border { Style = (Style)FindResource(style), Child = new TextBlock { Text = text, FontSize = size, Foreground = Res(ink) } };
    }

    private Button Action(string label, Action onClick, string style = "BtnBase")
    {
        var button = new Button { Content = label, Style = (Style)FindResource(style), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 8, 0) };
        button.Click += (_, _) => onClick();
        return button;
    }

    private Button Link(string label, Action onClick)
    {
        var button = new Button { Content = label, Style = (Style)FindResource("Link"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>File row used in dialogs (thumbnail, name, folder, facts), optionally with trailing pills.</summary>
    private FrameworkElement FileLine(OrganizeFile file, params UIElement[] tags)
    {
        var line = new ContentPresenter { Content = file, ContentTemplate = (DataTemplate)FindResource("FileLine") };
        if (tags.Length == 0) return line;
        var panel = new DockPanel();
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        foreach (var tag in tags) right.Children.Add(tag);
        DockPanel.SetDock(right, Dock.Right);
        panel.Children.Add(right);
        panel.Children.Add(line);
        return panel;
    }

    /// <summary>Folder glyph like the prototype's .fico.</summary>
    private Grid FolderGlyph(double opacity = 1) => new()
    {
        Width = 20, Height = 19, Opacity = opacity, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
        Children =
        {
            new Border { Width = 9, Height = 5, CornerRadius = new CornerRadius(2, 2, 0, 0), Background = Res("Folder"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top },
            new Border { Height = 15, CornerRadius = new CornerRadius(2), Background = Res("Folder"), VerticalAlignment = VerticalAlignment.Bottom }
        }
    };

    private Border Bar(double percent, double height = 6, string fill = "Accent") => new()
    {
        Height = height, CornerRadius = new CornerRadius(height / 2), Background = Res("Line"), ClipToBounds = true,
        Child = new ProgressBar { Value = percent, Maximum = 100, Height = height, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Res(fill) }
    };
}
