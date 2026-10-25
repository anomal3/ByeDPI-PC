using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Net.Codecrete.QrCodeGenerator;

namespace ByeDpiPc.App.Services;

/// <summary>Отрисовка строки в чёрно-белый QR-код как Avalonia-битмап (для связки с телефоном).</summary>
public static class QrImage
{
    public static WriteableBitmap Create(string text, int scale = 8, int border = 3)
    {
        var qr = QrCode.EncodeText(text, QrCode.Ecc.Medium);
        var size = qr.Size + border * 2;
        var px = size * scale;
        var bitmap = new WriteableBitmap(new PixelSize(px, px), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Opaque);

        using var fb = bitmap.Lock();
        unsafe
        {
            var ptr = (uint*)fb.Address;
            for (var y = 0; y < px; y++)
            {
                var qy = y / scale - border;
                for (var x = 0; x < px; x++)
                {
                    var qx = x / scale - border;
                    var dark = qy >= 0 && qx >= 0 && qy < qr.Size && qx < qr.Size && qr.GetModule(qx, qy);
                    ptr[y * px + x] = dark ? 0xFF000000u : 0xFFFFFFFFu;
                }
            }
        }
        return bitmap;
    }
}
