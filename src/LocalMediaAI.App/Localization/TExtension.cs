using System.Windows.Data;
using System.Windows.Markup;

namespace LocalMediaAI.App.Localization;

/// <summary>
/// Display text in XAML: <c>Text="{l:T '日本語'}"</c> shows the text in the current language (see <see cref="L"/>).
/// The language is fixed for the life of the app, so the value is resolved once when the XAML loads.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }

    public TExtension(string text) => Text = text;

    [ConstructorArgument("text")]
    public string Text { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Text);
}

/// <summary>
/// A binding whose display format is translated: <c>{l:TBinding Count, TFormat={}{0}件}</c> works as
/// <c>{Binding Count, StringFormat={}{0}件}</c> with the format in the current language.
/// </summary>
public sealed class TBinding : Binding
{
    public TBinding() { }

    public TBinding(string path) : base(path) { }

    private string _format = string.Empty;

    /// <summary>
    /// The Japanese composite format, set as the binding's StringFormat after translation. A translation that chooses
    /// words by the number (<c>{0|group|groups}</c>) is filled in by <see cref="L.Fill"/> instead, which StringFormat can't do.
    /// </summary>
    public string TFormat
    {
        get => _format;
        set
        {
            _format = L.T(value);
            if (L.HasPluralChoice(_format)) Converter = new FillConverter(_format);
            else StringFormat = _format;
        }
    }

    private sealed class FillConverter(string format) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => L.Fill(format, value);

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
