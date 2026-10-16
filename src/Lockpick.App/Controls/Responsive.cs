using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Lockpick.App.Controls;

/// <summary>
/// Adds the <c>compact</c> style class to a control while its width is below <c>CompactBelow</c>,
/// so narrow and portrait windows can switch layouts purely through styles.
/// </summary>
public static class Responsive
{
    public const string CompactClass = "compact";

    public static readonly AttachedProperty<double> CompactBelowProperty =
        AvaloniaProperty.RegisterAttached<Control, double>("CompactBelow", typeof(Responsive), double.NaN);

    static Responsive() => CompactBelowProperty.Changed.AddClassHandler<Control>((control, _) => Attach(control));

    public static double GetCompactBelow(Control control) => control.GetValue(CompactBelowProperty);

    public static void SetCompactBelow(Control control, double value) => control.SetValue(CompactBelowProperty, value);

    public static bool IsCompact(double width, double breakpoint) => !double.IsNaN(breakpoint) && width > 0 && width < breakpoint;

    private static void Attach(Control control)
    {
        control.PropertyChanged -= OnPropertyChanged;
        control.PropertyChanged += OnPropertyChanged;
        Update(control);
    }

    private static void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.BoundsProperty && sender is Control control)
        {
            Update(control);
        }
    }

    private static void Update(Control control) =>
        control.Classes.Set(CompactClass, IsCompact(control.Bounds.Width, GetCompactBelow(control)));
}

/// <summary>Resolves an application resource key (e.g. "Icon.Settings") to its geometry.</summary>
public sealed class IconKeyConverter : IValueConverter
{
    public static readonly IconKeyConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key && Application.Current?.TryGetResource(key, null, out var resource) == true && resource is Geometry geometry
            ? geometry
            : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True when the page's selected section (a <c>PageSection</c>) has the id given as the parameter.</summary>
public sealed class SectionIs : IValueConverter
{
    public static readonly SectionIs Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ViewModels.PageSection section && parameter is string id && section.Id == id;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
