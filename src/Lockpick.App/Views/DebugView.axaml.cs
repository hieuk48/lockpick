using Avalonia.Controls;

namespace Lockpick.App.Views;

public partial class DebugView : UserControl
{
    /// <summary>Below this height the page scrolls, so the field table and the log keep room for some rows.</summary>
    public const double MinContentHeight = 640;

    public DebugView()
    {
        InitializeComponent();
        Scroller.SizeChanged += (_, e) => Root.Height = Math.Max(e.NewSize.Height, MinContentHeight);
    }
}
