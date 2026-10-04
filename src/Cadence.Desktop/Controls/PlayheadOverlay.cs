using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Cadence.Desktop.Theme;

namespace Cadence.Desktop.Controls;

/// <summary>The playhead, drawn above the timeline so moving it never redraws the notes.</summary>
public sealed class PlayheadOverlay : Control
{
    public static readonly StyledProperty<double> XProperty = AvaloniaProperty.Register<PlayheadOverlay, double>(nameof(X));

    private static readonly IBrush Line = new SolidColorBrush(Palette.Accent);
    private static readonly StreamGeometry Head = StreamGeometry.Parse("M -6,0 L 6,0 L 0,8 Z");

    static PlayheadOverlay()
    {
        AffectsRender<PlayheadOverlay>(XProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<PlayheadOverlay>(false);
    }

    public double X
    {
        get => GetValue(XProperty);
        set => SetValue(XProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var x = Math.Round(X) + 0.5;
        context.FillRectangle(Line, new Rect(x - 0.75, 0, 1.5, Bounds.Height));
        using (context.PushTransform(Matrix.CreateTranslation(x, 0)))
        {
            context.DrawGeometry(Line, null, Head);
        }
    }
}
