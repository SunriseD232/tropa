using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Tropa.Infrastructure;

namespace Tropa.App.Services;

/// <summary>
/// Значок трея рисуется на лету: цветной круг состояния, внутри — флаг страны выхода (только при подключении)
/// или знак Тропы. Цвета круга заметно разные, чтобы состояние читалось и на 16 пикселях.
/// </summary>
internal static class TrayIconArt
{
    private const int Size = 32;

    private static readonly Color Connected = Color.Parse("#22C55E");
    private static readonly Color Busy = Color.Parse("#3B82F6");
    private static readonly Color Error = Color.Parse("#EAB308");
    private static readonly Color Off = Color.Parse("#8A8F98");

    private static readonly Dictionary<string, WindowIcon> Cache = new(StringComparer.Ordinal);

    /// <summary>Значок для состояния; country — код страны выхода, учитывается только при подключении.</summary>
    public static WindowIcon Get(ConnectionState state, string? country)
    {
        var flag = state == ConnectionState.Connected ? Flags.Get(country) : null;
        var key = $"{state}:{(flag is null ? "" : country)}";
        if (Cache.TryGetValue(key, out var icon))
            return icon;
        var color = state switch
        {
            ConnectionState.Connected => Connected,
            ConnectionState.Connecting or ConnectionState.Disconnecting => Busy,
            ConnectionState.Error => Error,
            _ => Off,
        };
        icon = Render(color, flag);
        Cache[key] = icon;
        return icon;
    }

    private static WindowIcon Render(Color color, Bitmap? flag)
    {
        // Не освобождаем: WindowIcon хранит ссылку на картинку, а значков всего несколько и они в кэше.
        var target = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        using (var ctx = target.CreateDrawingContext())
        {
            var full = new Rect(0, 0, Size, Size);
            ctx.DrawEllipse(new SolidColorBrush(color), null, full.Center, Size / 2.0, Size / 2.0);
            if (flag is not null)
            {
                // Кольцо 5 px из 32 (≈2.5 px на 16): зелёный край остаётся виден вокруг любого флага.
                var inner = full.Deflate(5);
                using (ctx.PushClip(new RoundedRect(inner, inner.Width / 2)))
                {
                    var src = flag.Size;
                    var side = Math.Min(src.Width, src.Height);
                    var crop = new Rect((src.Width - side) / 2, (src.Height - side) / 2, side, side);
                    ctx.DrawImage(flag, crop, inner);
                }
            }
            else
            {
                DrawLogo(ctx);
            }
        }

        return new WindowIcon(target);
    }

    /// <summary>Знак Тропы (S-образная тропинка с точками на концах) белым по кругу.</summary>
    private static void DrawLogo(DrawingContext ctx)
    {
        var white = Brushes.White;
        var pen = new Pen(white, 3.2, lineCap: PenLineCap.Round);
        var start = new Point(10, 23);
        var end = new Point(22, 9);
        var path = new StreamGeometry();
        using (var g = path.Open())
        {
            g.BeginFigure(start, false);
            g.CubicBezierTo(new Point(17, 23), new Point(15, 9), end);
            g.EndFigure(false);
        }

        ctx.DrawGeometry(null, pen, path);
        ctx.DrawEllipse(white, null, start, 2.6, 2.6);
        ctx.DrawEllipse(white, null, end, 2.6, 2.6);
    }
}
