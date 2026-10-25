using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WhiteboardSimulator.Models;

namespace WhiteboardSimulator.Helpers;

/// <summary>Gera (proceduralmente) os pincéis e texturas da moldura e das superfícies do quadro.</summary>
public static class BoardBrushes
{
    private static readonly Dictionary<string, Brush> Cache = new();

    public static Brush Frame(BoardFrame frame) => frame switch
    {
        BoardFrame.Wood => Cached("frame.wood", () => Tiled(Texture(256, 256, Color.FromRgb(214, 170, 125), 7, 9, 11))),
        _ => Cached("frame.metal", () => Gradient(0xD2D5D8, 0x9DA2A8, 0xBDC1C5)),
    };

    public static Brush Surface(BoardSurface surface, Brush whiteboardTint) => surface switch
    {
        BoardSurface.BlackSlate => Cached("surface.black", () => Tiled(Texture(256, 256, Color.FromRgb(46, 50, 52), 4, 2, 21))),
        BoardSurface.GreenSlate => Cached("surface.green", () => Tiled(Texture(256, 256, Color.FromRgb(30, 76, 57), 4, 2, 33))),
        BoardSurface.GreenFelt => Cached("surface.felt", () => Tiled(Texture(256, 256, Color.FromRgb(40, 106, 74), 17, 0, 47))),
        _ => whiteboardTint,
    };

    /// <summary>Fundo da janela (atrás do quadro): gradiente radial suave.</summary>
    public static Brush Backdrop(AppTheme theme) => theme switch
    {
        AppTheme.Light => Cached("backdrop.light", () => Vertical(0xF4F1EA, 0xE6E2D9, 0xD6D2C7)),
        _ => Cached("backdrop.dark", () => Vertical(0x454B5C, 0x30343F, 0x20232B)),
    };

    /// <summary>Cor do topo do fundo; a barra de título do Windows usa a mesma para ficar contínua.</summary>
    public static Color BackdropTop(AppTheme theme) =>
        theme == AppTheme.Light ? Rgb(0xF4F1EA) : Rgb(0x454B5C);

    public static Color TitleText(AppTheme theme) =>
        theme == AppTheme.Light ? Rgb(0x2B2D31) : Rgb(0xE8EAEF);

    private static Color Rgb(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    // Degradê só vertical: o topo é uniforme e casa com a barra de título.
    private static Brush Vertical(int top, int middle, int bottom)
    {
        var brush = new LinearGradientBrush(new GradientStopCollection
        {
            new(Rgb(top), 0), new(Rgb(middle), 0.5), new(Rgb(bottom), 1)
        }, new Point(0, 0), new Point(0, 1));
        brush.Freeze();
        return brush;
    }

    public static Color CapColor(BoardFrame frame) =>
        frame == BoardFrame.Wood ? Color.FromRgb(236, 208, 166) : Color.FromRgb(142, 146, 152);

    private static Brush Cached(string key, Func<Brush> create)
    {
        if (!Cache.TryGetValue(key, out var brush))
            Cache[key] = brush = create();
        return brush;
    }

    private static Brush Gradient(int top, int middle, int bottom)
    {
        static Color C(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        var brush = new LinearGradientBrush(new GradientStopCollection
        {
            new(C(top), 0), new(C(middle), 0.5), new(C(bottom), 1)
        }, new Point(0, 0), new Point(0, 1));
        brush.Freeze();
        return brush;
    }

    private static Brush Tiled(BitmapSource texture)
    {
        var brush = new ImageBrush(texture)
        {
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, texture.PixelWidth, texture.PixelHeight),
            Stretch = Stretch.Fill
        };
        brush.Freeze();
        return brush;
    }

    /// <param name="noise">Variação aleatória por pixel.</param>
    /// <param name="streak">Variação por linha (veios horizontais).</param>
    private static BitmapSource Texture(int w, int h, Color baseColor, double noise, double streak, int seed)
    {
        var rng = new Random(seed);
        var pixels = new byte[w * h * 4];
        double bias = 0;

        for (int y = 0; y < h; y++)
        {
            bias = bias * 0.6 + (rng.NextDouble() - 0.5) * 2 * streak * 0.4 + 0;
            for (int x = 0; x < w; x++)
            {
                double v = (rng.NextDouble() - 0.5) * 2 * noise + bias;
                int i = (y * w + x) * 4;
                pixels[i] = Clamp(baseColor.B + v);
                pixels[i + 1] = Clamp(baseColor.G + v);
                pixels[i + 2] = Clamp(baseColor.R + v);
                pixels[i + 3] = 255;
            }
        }

        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        bmp.Freeze();
        return bmp;
    }

    private static byte Clamp(double v) => (byte)Math.Max(0, Math.Min(255, v));
}
