using SkiaSharp;

// Иконка: скруглённый квадрат с градиентом, белый щит и «молния» — поток, проходящий сквозь фильтр.
var root = args.Length > 0 ? args[0] : FindRepoRoot();
var assets = Path.Combine(root, "src", "ByeDpiPc.App", "Assets");
Directory.CreateDirectory(assets);
var docs = Path.Combine(root, "docs");
Directory.CreateDirectory(docs);

int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

WriteIco(Path.Combine(assets, "app.ico"), sizes, s => Render(s, Variant.Normal));
WriteIco(Path.Combine(assets, "tray-off.ico"), sizes, s => Render(s, Variant.Off));
WriteIco(Path.Combine(assets, "tray-on.ico"), sizes, s => Render(s, Variant.On));
WriteIco(Path.Combine(assets, "tray-busy.ico"), sizes, s => Render(s, Variant.Busy));
WriteIco(Path.Combine(assets, "tray-error.ico"), sizes, s => Render(s, Variant.Error));
File.WriteAllBytes(Path.Combine(assets, "app.png"), Render(256, Variant.Normal));
File.WriteAllBytes(Path.Combine(assets, "logo-64.png"), Render(64, Variant.Normal));
File.WriteAllBytes(Path.Combine(docs, "icon.png"), Render(512, Variant.Normal));
Console.WriteLine("Иконки записаны в " + assets);

static byte[] Render(int size, Variant variant)
{
    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
    var c = surface.Canvas;
    c.Clear(SKColors.Transparent);
    c.Scale(size / 256f);

    var (from, to) = variant == Variant.Off
        ? (new SKColor(0x8C, 0x95, 0xA8), new SKColor(0x55, 0x5E, 0x70))
        : (new SKColor(0x45, 0x7B, 0xFF), new SKColor(0x7A, 0x4C, 0xFF));

    // Фон
    using (var bg = new SKPaint { IsAntialias = true })
    {
        bg.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(256, 256), new[] { from, to }, SKShaderTileMode.Clamp);
        c.DrawRoundRect(new SKRect(8, 8, 248, 248), 58, 58, bg);
    }
    // Лёгкий блик сверху
    using (var gloss = new SKPaint { IsAntialias = true })
    {
        gloss.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 8), new SKPoint(0, 140),
            new[] { new SKColor(255, 255, 255, 46), new SKColor(255, 255, 255, 0) }, SKShaderTileMode.Clamp);
        c.DrawRoundRect(new SKRect(8, 8, 248, 248), 58, 58, gloss);
    }

    // Щит
    using var shield = SKPath.ParseSvgPathData("M128 40 L200 68 V124 C200 170 170 204 128 220 C86 204 56 170 56 124 V68 Z");
    using (var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 40), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 6) })
    {
        c.Save();
        c.Translate(0, 5);
        c.DrawPath(shield, shadow);
        c.Restore();
    }
    using (var white = new SKPaint { IsAntialias = true, Color = SKColors.White })
        c.DrawPath(shield, white);

    // Молния — цветом фона
    using var bolt = SKPath.ParseSvgPathData("M142 66 L94 138 H124 L112 196 L164 116 H134 L146 66 Z");
    using (var boltPaint = new SKPaint { IsAntialias = true })
    {
        boltPaint.Shader = SKShader.CreateLinearGradient(new SKPoint(90, 66), new SKPoint(170, 196), new[] { from, to }, SKShaderTileMode.Clamp);
        c.DrawPath(bolt, boltPaint);
    }

    // Индикатор состояния для трея
    SKColor? dot = variant switch
    {
        Variant.On => new SKColor(0x22, 0xC5, 0x5E),
        Variant.Busy => new SKColor(0xF5, 0xB8, 0x3D),
        Variant.Error => new SKColor(0xEF, 0x44, 0x44),
        _ => null,
    };
    if (dot is { } d)
    {
        using var ring = new SKPaint { IsAntialias = true, Color = SKColors.White };
        using var fill = new SKPaint { IsAntialias = true, Color = d };
        c.DrawCircle(196, 196, 58, ring);
        c.DrawCircle(196, 196, 46, fill);
    }

    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

static void WriteIco(string path, int[] sizes, Func<int, byte[]> render)
{
    var images = sizes.Select(render).ToArray();
    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);
    w.Write((ushort)0);
    w.Write((ushort)1);
    w.Write((ushort)images.Length);
    var offset = 6 + 16 * images.Length;
    for (int i = 0; i < images.Length; i++)
    {
        var s = sizes[i];
        w.Write((byte)(s >= 256 ? 0 : s));
        w.Write((byte)(s >= 256 ? 0 : s));
        w.Write((byte)0);
        w.Write((byte)0);
        w.Write((ushort)1);
        w.Write((ushort)32);
        w.Write(images[i].Length);
        w.Write(offset);
        offset += images[i].Length;
    }
    foreach (var img in images) w.Write(img);
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ByeDpiPc.sln"))) dir = dir.Parent;
    return dir?.FullName ?? Environment.CurrentDirectory;
}

enum Variant { Normal, Off, On, Busy, Error }
