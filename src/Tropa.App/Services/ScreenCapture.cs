using System.Runtime.InteropServices;

namespace Tropa.App.Services;

/// <summary>
/// Снимок всех мониторов в память — только для поиска QR-кода по кнопке «QR с экрана» (ADR-053).
/// Снимок не сохраняется на диск и никуда не отправляется.
/// </summary>
internal static partial class ScreenCapture
{
    public sealed record Shot(byte[] Bgra, int Width, int Height);

    public static Shot? CaptureVirtualScreen()
    {
        var x = GetSystemMetrics(SmXVirtualScreen);
        var y = GetSystemMetrics(SmYVirtualScreen);
        var w = GetSystemMetrics(SmCxVirtualScreen);
        var h = GetSystemMetrics(SmCyVirtualScreen);
        if (w <= 0 || h <= 0 || (long)w * h > QrCodes.MaxImagePixels)
            return null;

        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero)
            return null;
        var mem = IntPtr.Zero;
        var bmp = IntPtr.Zero;
        try
        {
            mem = CreateCompatibleDC(screen);
            bmp = CreateCompatibleBitmap(screen, w, h);
            if (mem == IntPtr.Zero || bmp == IntPtr.Zero)
                return null;
            var old = SelectObject(mem, bmp);
            var ok = BitBlt(mem, 0, 0, w, h, screen, x, y, SrcCopy | CaptureBlt);
            SelectObject(mem, old);
            if (!ok)
                return null;

            // Сверху вниз (отрицательная высота), 32 бита на точку — BGRA.
            var info = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = w,
                Height = -h,
                Planes = 1,
                BitCount = 32,
            };
            var pixels = new byte[w * h * 4];
            return GetDIBits(mem, bmp, 0, (uint)h, pixels, ref info, 0) == h ? new Shot(pixels, w, h) : null;
        }
        finally
        {
            if (bmp != IntPtr.Zero)
                DeleteObject(bmp);
            if (mem != IntPtr.Zero)
                DeleteDC(mem);
            _ = ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const uint SrcCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetDC(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateCompatibleDC(IntPtr dc);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);

    [LibraryImport("gdi32.dll")]
    private static partial int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, [Out] byte[] bits, ref BitmapInfoHeader info, uint usage);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(IntPtr dc);
}
