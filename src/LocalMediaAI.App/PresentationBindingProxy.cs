using System.Windows;

namespace LocalMediaAI.App;

/// <summary>Provides the existing view's setting to columns outside its visual tree.</summary>
public sealed class PresentationBindingProxy : Freezable
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(bool), typeof(PresentationBindingProxy), new PropertyMetadata(false));
    public bool Value { get => (bool)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    protected override Freezable CreateInstanceCore() => new PresentationBindingProxy();
}
