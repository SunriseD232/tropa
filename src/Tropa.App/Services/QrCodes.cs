using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace Tropa.App.Services;

/// <summary>
/// QR-коды (ADR-053, ZXing.Net): показ ссылки сервера и распознавание с экрана или картинки.
/// Найденный текст — недоверенные данные: дальше он идёт в окно «Добавить серверы» как вставленный руками.
/// </summary>
internal static class QrCodes
{
    /// <summary>Больше этого картинку не распознаём: огромный файл — не QR, а способ занять память.</summary>
    public const int MaxImagePixels = 40_000_000;

    /// <summary>Чёрно-белая картинка QR-кода: модули по 8 пикселей, поле в 4 модуля.</summary>
    public static WriteableBitmap Render(string text)
    {
        var matrix = new QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, 0, 0, new Dictionary<EncodeHintType, object>
        {
            [EncodeHintType.ERROR_CORRECTION] = ZXing.QrCode.Internal.ErrorCorrectionLevel.M,
            [EncodeHintType.MARGIN] = 4,
            [EncodeHintType.CHARACTER_SET] = "UTF-8",
        });
        const int scale = 8;
        var size = matrix.Width * scale;
        var bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var fb = bitmap.Lock();
        var row = new byte[fb.RowBytes];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var v = matrix[x / scale, y / scale] ? (byte)0 : (byte)255;
                var i = x * 4;
                row[i] = row[i + 1] = row[i + 2] = v;
                row[i + 3] = 255;
            }

            System.Runtime.InteropServices.Marshal.Copy(row, 0, fb.Address + (y * fb.RowBytes), fb.RowBytes);
        }

        return bitmap;
    }

    /// <summary>Все QR-коды на картинке BGRA (снимок экрана или файл). Пусто — не нашлось.</summary>
    public static IReadOnlyList<string> Decode(byte[] bgra, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (width <= 0 || height <= 0 || (long)width * height > MaxImagePixels || bgra.Length < width * height * 4)
            return [];
        var source = new RGBLuminanceSource(bgra, width, height, RGBLuminanceSource.BitmapFormat.BGRA32);
        var reader = new QRCodeReader();
        var hints = new Dictionary<DecodeHintType, object> { [DecodeHintType.TRY_HARDER] = true, [DecodeHintType.CHARACTER_SET] = "UTF-8" };
        var found = new List<string>();
        foreach (var binarizer in new Binarizer[] { new HybridBinarizer(source), new GlobalHistogramBinarizer(source) })
        {
            try
            {
                // На экране может быть несколько кодов: ищем все, потом — один как запасной путь.
                var multi = new ZXing.Multi.QrCode.QRCodeMultiReader().decodeMultiple(new BinaryBitmap(binarizer), hints);
                if (multi is { Length: > 0 })
                    found.AddRange(multi.Select(r => r.Text));
                else if (reader.decode(new BinaryBitmap(binarizer), hints) is { } one)
                    found.Add(one.Text);
            }
            catch (ReaderException)
            {
            }

            if (found.Count > 0)
                break;
        }

        return found.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Пиксели картинки, открытой Avalonia (PNG, JPEG, BMP…).</summary>
    public static IReadOnlyList<string> Decode(Bitmap image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var size = image.PixelSize;
        if ((long)size.Width * size.Height > MaxImagePixels)
            return [];
        // Приводим к BGRA: исходный файл может быть в любом формате — перерисовываем его в известный.
        using var target = new RenderTargetBitmap(size, new Vector(96, 96));
        using (var ctx = target.CreateDrawingContext())
        {
            var rect = new Rect(0, 0, size.Width, size.Height);
            ctx.FillRectangle(Avalonia.Media.Brushes.White, rect);
            ctx.DrawImage(image, new Rect(image.Size), rect);
        }

        var stride = size.Width * 4;
        var bytes = new byte[stride * size.Height];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            target.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), bytes.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        return Decode(bytes, size.Width, size.Height);
    }
}
