using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Tropa.App.Views;

/// <summary>
/// Векторная иконка: контур в сетке 24×24, рисуется линией цвета текста. Символы шрифтов
/// (⏻, ↻, ✕, ✓) на части Windows 10 нет — там они превращались в перечёркнутые квадраты.
/// </summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<Icon>();

    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(Thickness), 2);

    static Icon()
    {
        AffectsRender<Icon>(DataProperty, ForegroundProperty, ThicknessProperty);
        WidthProperty.OverrideDefaultValue<Icon>(16);
        HeightProperty.OverrideDefaultValue<Icon>(16);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>Толщина линии в единицах сетки 24×24.</summary>
    public double Thickness
    {
        get => GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Data is not { } data || Foreground is not { } brush)
            return;
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 24;
        if (scale <= 0)
            return;
        var dx = (Bounds.Width - 24 * scale) / 2;
        var dy = (Bounds.Height - 24 * scale) / 2;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(dx, dy)))
        {
            context.DrawGeometry(null, new Pen(brush, Thickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), data);
        }
    }
}
