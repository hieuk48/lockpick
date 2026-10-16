using Avalonia;
using Avalonia.Controls;

namespace Lockpick.App.Controls;

/// <summary>
/// Scrolls a page back to the top when its section changes (<c>c:SectionScroll.Section="{Binding SelectedSection}"</c>
/// on the page's ScrollViewer), so a new section doesn't open halfway down.
/// </summary>
public static class SectionScroll
{
    public static readonly AttachedProperty<object?> SectionProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, object?>("Section", typeof(SectionScroll));

    static SectionScroll() => SectionProperty.Changed.AddClassHandler<ScrollViewer>((viewer, _) => viewer.Offset = default);

    public static object? GetSection(ScrollViewer viewer) => viewer.GetValue(SectionProperty);

    public static void SetSection(ScrollViewer viewer, object? value) => viewer.SetValue(SectionProperty, value);
}
