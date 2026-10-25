using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using WhiteboardSimulator.Models;
using TextElement = WhiteboardSimulator.Models.TextElement;
using WpfPath = System.Windows.Shapes.Path;
using WpfEllipse = System.Windows.Shapes.Ellipse;

namespace WhiteboardSimulator.Controls;

/// <summary>
/// Desenho de um elemento do quadro. Só desenha e responde "o ponto está em cima de mim?";
/// quem trata o mouse é o BoardCanvas.
/// </summary>
public sealed class ElementView : Grid
{
    private static readonly Dictionary<string, SolidColorBrush> BrushCache = new();

    private readonly BoardElement _element;
    private readonly Func<Guid, BoardElement?> _resolve;
    private readonly List<BoardElement> _watchedPins = new();

    private Geometry? _outline;          // contorno (traços e formas), em coordenadas locais
    private double _outlineThickness;
    private Geometry? _connectorGeometry; // ligação, em coordenadas do quadro
    private BitmapSource? _bitmap;
    private bool _isEditing;

    public ElementView(BoardElement element, Func<Guid, BoardElement?> resolve)
    {
        _element = element;
        _resolve = resolve;
        IsHitTestVisible = false;   // o mouse é tratado pelo BoardCanvas
        SizeChanged += OnSizeChanged;
        _element.PropertyChanged += OnElementChanged;
        Rebuild();
    }

    public BoardElement Element => _element;

    /// <summary>Curva da ligação (coordenadas do quadro); null se não for uma ligação.</summary>
    public Geometry? ConnectorGeometry => _connectorGeometry;

    /// <summary>Durante a edição o texto some do desenho (quem mostra é a caixa de edição).</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value) return;
            _isEditing = value;
            Rebuild();
        }
    }

    public void Detach()
    {
        _element.PropertyChanged -= OnElementChanged;
        UnwatchPins();
    }

    public static SolidColorBrush BrushFor(string hex)
    {
        if (!BrushCache.TryGetValue(hex, out var brush))
        {
            brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            BrushCache[hex] = brush;
        }
        return brush;
    }

    private void OnElementChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(BoardElement.X):
            case nameof(BoardElement.Y):
            case nameof(BoardElement.Rotation):
                ApplyLayout();
                break;
            case nameof(BoardElement.IsSelected):
                break;
            case nameof(BoardElement.Height) when _element is TextElement:
                ApplyLayout();   // a altura do texto é calculada pela própria tela
                break;
            default:
                Rebuild();
                break;
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Texto: a altura do elemento acompanha o texto quebrado.
        if (_element is TextElement && Math.Abs(_element.Height - e.NewSize.Height) > 0.5)
            _element.Height = e.NewSize.Height;
    }

    // ---- Layout ----

    private void ApplyLayout()
    {
        if (_element is ConnectorElement)
        {
            Canvas.SetLeft(this, 0);
            Canvas.SetTop(this, 0);
            Width = double.NaN;
            Height = double.NaN;
            RenderTransform = Transform.Identity;
            return;
        }

        Canvas.SetLeft(this, _element.X);
        Canvas.SetTop(this, _element.Y);

        // Traços e linhas retos têm caixa com largura ou altura zero; com tamanho 0 o WPF não desenha nada.
        Width = Math.Max(1, _element.Width);
        Height = _element is TextElement ? double.NaN : Math.Max(1, _element.Height);
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = Math.Abs(_element.Rotation) < 0.001 ? Transform.Identity : new RotateTransform(_element.Rotation);
    }

    private void Rebuild()
    {
        Children.Clear();
        _outline = null;
        _connectorGeometry = null;
        UnwatchPins();
        ApplyLayout();

        switch (_element)
        {
            case StrokeElement s: BuildStroke(s); break;
            case ShapeElement s: BuildShape(s); break;
            case TextElement t: BuildText(t); break;
            case StickyNoteElement n: BuildSticky(n); break;
            case PinElement p: BuildPin(p); break;
            case ConnectorElement c: BuildConnector(c); break;
            case ImageElement i: BuildImage(i); break;
            case RulerElement r: BuildRuler(r); break;
        }
    }

    // ---- Construção de cada tipo ----

    /// <summary>Linha suave (curvas quadráticas pelos pontos médios) a partir dos pontos do mouse.</summary>
    public static Geometry BuildSmoothGeometry(IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            if (points.Count == 0) return geometry;

            ctx.BeginFigure(points[0], false, false);
            if (points.Count == 1)
            {
                // Um toque só: um ponto (com as pontas arredondadas fica um círculo).
                ctx.LineTo(new Point(points[0].X + 0.01, points[0].Y), true, true);
            }
            else if (points.Count == 2)
            {
                ctx.LineTo(points[1], true, true);
            }
            else
            {
                for (int i = 1; i < points.Count - 1; i++)
                {
                    var mid = new Point((points[i].X + points[i + 1].X) / 2, (points[i].Y + points[i + 1].Y) / 2);
                    ctx.QuadraticBezierTo(points[i], mid, true, true);
                }
                ctx.LineTo(points[^1], true, true);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private static void RoundCaps(WpfPath path)
    {
        path.StrokeStartLineCap = PenLineCap.Round;
        path.StrokeEndLineCap = PenLineCap.Round;
        path.StrokeLineJoin = PenLineJoin.Round;
    }

    private void BuildStroke(StrokeElement s)
    {
        double sx = s.BaseWidth > 0.001 ? s.Width / s.BaseWidth : 1;
        double sy = s.BaseHeight > 0.001 ? s.Height / s.BaseHeight : 1;
        var points = s.Points.Select(p => new Point(p.X * sx, p.Y * sy)).ToList();

        var geometry = BuildSmoothGeometry(points);
        _outline = geometry;
        _outlineThickness = s.Thickness;

        var path = new WpfPath { Data = geometry, Stroke = BrushFor(s.Color), StrokeThickness = s.Thickness };
        RoundCaps(path);
        Children.Add(path);
    }

    private void BuildShape(ShapeElement s)
    {
        double w = Math.Max(0, s.Width), h = Math.Max(0, s.Height), t = s.Thickness;

        Geometry geometry = s.Kind switch
        {
            ShapeKind.Rectangle => new RectangleGeometry(new Rect(t / 2, t / 2, Math.Max(0, w - t), Math.Max(0, h - t)), 3, 3),
            ShapeKind.Ellipse => new EllipseGeometry(new Point(w / 2, h / 2), Math.Max(0, (w - t) / 2), Math.Max(0, (h - t) / 2)),
            _ => s.FlipY
                ? new LineGeometry(new Point(0, h), new Point(w, 0))
                : new LineGeometry(new Point(0, 0), new Point(w, h)),
        };
        _outline = geometry;
        _outlineThickness = t;

        var path = new WpfPath { Data = geometry, Stroke = BrushFor(s.Color), StrokeThickness = t };
        RoundCaps(path);
        Children.Add(path);
    }

    /// <summary>Preenche o TextBlock com os trechos do elemento, cada um com a sua formatação.</summary>
    public static void FillInlines(TextBlock block, RichTextElement el)
    {
        block.Inlines.Clear();
        block.FontFamily = new FontFamily(el.FontFamily);
        block.FontSize = el.FontSize;
        block.Foreground = BrushFor(el.DefaultColor);

        bool any = false;
        foreach (var run in el.EffectiveRuns)
        {
            var lines = run.Text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) block.Inlines.Add(new LineBreak());
                if (lines[i].Length == 0) continue;

                var inline = new Run(lines[i])
                {
                    FontFamily = new FontFamily(run.FontFamily ?? el.FontFamily),
                    FontSize = run.FontSize ?? el.FontSize,
                    Foreground = BrushFor(run.Color ?? el.DefaultColor),
                    FontWeight = (run.Bold ?? el.Bold) ? FontWeights.Bold : FontWeights.Normal,
                    FontStyle = (run.Italic ?? el.Italic) ? FontStyles.Italic : FontStyles.Normal
                };
                if (run.Underline ?? el.Underline) inline.TextDecorations = TextDecorations.Underline;
                block.Inlines.Add(inline);
                any = true;
            }
        }

        // Texto vazio: um espaço mantém a altura de uma linha.
        if (!any) block.Inlines.Add(new Run(" "));
    }

    private void BuildText(TextElement t)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = _isEditing ? 0 : 1 };
        FillInlines(block, t);
        Children.Add(block);
    }

    private void BuildSticky(StickyNoteElement n)
    {
        var baseColor = (Color)ColorConverter.ConvertFromString(n.Color);
        var light = Color.FromRgb(Lighten(baseColor.R), Lighten(baseColor.G), Lighten(baseColor.B));

        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, Padding = new Thickness(14), Opacity = _isEditing ? 0 : 1 };
        FillInlines(block, n);

        var note = new Border
        {
            CornerRadius = new CornerRadius(2, 2, 2, 14),
            Background = new LinearGradientBrush(light, baseColor, 90),
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 4, Direction = 300, Opacity = 0.3 },
            Child = block
        };
        Children.Add(note);
    }

    private static byte Lighten(byte c) => (byte)(c + (255 - c) * 0.45);

    private void BuildPin(PinElement p)
    {
        var color = (Color)ColorConverter.ConvertFromString(p.Color);
        var dark = Color.FromRgb((byte)(color.R * 0.55), (byte)(color.G * 0.55), (byte)(color.B * 0.55));
        var light = Color.FromRgb(Lighten(color.R), Lighten(color.G), Lighten(color.B));

        Children.Add(new WpfEllipse
        {
            Fill = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)),
            Margin = new Thickness(3, 5, -3, -5),
            Effect = new BlurEffect { Radius = 4 }
        });
        Children.Add(new WpfEllipse
        {
            Fill = new RadialGradientBrush(new GradientStopCollection
            {
                new GradientStop(light, 0), new GradientStop(color, 0.55), new GradientStop(dark, 1)
            }) { GradientOrigin = new Point(0.35, 0.3) }
        });
        Children.Add(new WpfEllipse
        {
            Width = 7,
            Height = 5,
            Fill = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(6, 4, 0, 0)
        });
    }

    private void BuildConnector(ConnectorElement c)
    {
        foreach (var id in new[] { c.FromId, c.ToId })
        {
            if (_resolve(id) is PinElement pin)
            {
                pin.PropertyChanged += OnPinChanged;
                _watchedPins.Add(pin);
            }
        }

        if (_resolve(c.FromId) is not PinElement a || _resolve(c.ToId) is not PinElement b) return;

        var from = PinCenter(a);
        var to = PinCenter(b);

        // Fio levemente caído no meio, como uma linha de verdade entre dois alfinetes.
        var distance = (to - from).Length;
        var control = new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2 + Math.Min(70, distance * 0.14));

        var figure = new PathFigure { StartPoint = from, IsClosed = false };
        figure.Segments.Add(new QuadraticBezierSegment(control, to, true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        _connectorGeometry = geometry;

        var path = new WpfPath { Data = geometry, Stroke = BrushFor(c.Color), StrokeThickness = c.Thickness };
        RoundCaps(path);
        Children.Add(path);
    }

    private void OnPinChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BoardElement.X) or nameof(BoardElement.Y))
            Rebuild();
    }

    private void UnwatchPins()
    {
        foreach (var pin in _watchedPins) pin.PropertyChanged -= OnPinChanged;
        _watchedPins.Clear();
    }

    public static Point PinCenter(PinElement p) => new(p.X + p.Width / 2, p.Y + p.Height / 2);

    private void BuildImage(ImageElement image)
    {
        _bitmap ??= Decode(image.Data);
        if (_bitmap == null)
        {
            Children.Add(new Border { Background = BrushFor("#DDDDDD"), BorderBrush = BrushFor("#999999"), BorderThickness = new Thickness(1) });
            return;
        }

        Children.Add(new Border
        {
            Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 3, Direction = 300, Opacity = 0.28 },
            Child = new Image { Source = _bitmap, Stretch = Stretch.Fill }
        });
    }

    public static BitmapSource? Decode(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            frame.Freeze();
            return frame;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or InvalidOperationException or FileFormatException)
        {
            return null;
        }
    }

    private void BuildRuler(RulerElement r)
    {
        double w = Math.Max(1, r.Width), h = Math.Max(1, r.Height);

        Children.Add(new Border
        {
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = BrushFor("#B89B2E"),
            Background = new LinearGradientBrush(Color.FromArgb(235, 250, 238, 172), Color.FromArgb(235, 234, 208, 110), 90),
            Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 3, Direction = 300, Opacity = 0.3 }
        });

        // Marcas a cada 10 unidades; maiores a cada 50 e 100 (com número).
        var ticks = new StreamGeometry();
        using (var ctx = ticks.Open())
        {
            for (int i = 0; i * 10 <= w - 2; i++)
            {
                double x = i * 10 + 0.5;
                double len = i % 10 == 0 ? 20 : i % 5 == 0 ? 13 : 8;
                ctx.BeginFigure(new Point(x, 1), false, false);
                ctx.LineTo(new Point(x, len), true, false);
            }
        }
        ticks.Freeze();
        Children.Add(new WpfPath { Data = ticks, Stroke = BrushFor("#7A6414"), StrokeThickness = 1, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top });

        var labels = new Canvas { IsHitTestVisible = false };
        for (int i = 10; i * 10 <= w - 14; i += 10)
        {
            var label = new TextBlock { Text = (i / 10).ToString(), FontSize = 10, Foreground = BrushFor("#7A6414") };
            Canvas.SetLeft(label, i * 10 + 3);
            Canvas.SetTop(label, 21);
            labels.Children.Add(label);
        }
        Children.Add(labels);
    }

    // ---- Geometria e acertos ----

    /// <summary>Converte um ponto do quadro para as coordenadas da caixa do elemento (origem no canto sup. esquerdo).</summary>
    public static Point ToLocal(BoardElement el, Point world)
    {
        double cx = el.X + el.Width / 2, cy = el.Y + el.Height / 2;
        double rad = -el.Rotation * Math.PI / 180;
        double dx = world.X - cx, dy = world.Y - cy;
        double lx = dx * Math.Cos(rad) - dy * Math.Sin(rad);
        double ly = dx * Math.Sin(rad) + dy * Math.Cos(rad);
        return new Point(lx + el.Width / 2, ly + el.Height / 2);
    }

    public static Point ToWorld(BoardElement el, Point local)
    {
        double cx = el.X + el.Width / 2, cy = el.Y + el.Height / 2;
        double rad = el.Rotation * Math.PI / 180;
        double dx = local.X - el.Width / 2, dy = local.Y - el.Height / 2;
        return new Point(cx + dx * Math.Cos(rad) - dy * Math.Sin(rad), cy + dx * Math.Sin(rad) + dy * Math.Cos(rad));
    }

    /// <summary>Retângulo (alinhado aos eixos) que contém o elemento, já considerando a rotação.</summary>
    public Rect WorldBounds()
    {
        if (_element is ConnectorElement)
            return _connectorGeometry?.Bounds ?? Rect.Empty;

        var corners = new[]
        {
            ToWorld(_element, new Point(0, 0)), ToWorld(_element, new Point(_element.Width, 0)),
            ToWorld(_element, new Point(_element.Width, _element.Height)), ToWorld(_element, new Point(0, _element.Height))
        };
        double minX = corners.Min(c => c.X), maxX = corners.Max(c => c.X);
        double minY = corners.Min(c => c.Y), maxY = corners.Max(c => c.Y);
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>O ponto (em coordenadas do quadro) está sobre o elemento? tolerance = folga em unidades do quadro.</summary>
    public bool HitTest(Point world, double tolerance)
    {
        if (_element is ConnectorElement c)
        {
            return _connectorGeometry != null &&
                   _connectorGeometry.StrokeContains(new Pen(Brushes.Black, c.Thickness + tolerance * 2), world);
        }

        var local = ToLocal(_element, world);

        // Traços e formas: só o contorno conta (clicar no "vazio" de um quadrado não o seleciona).
        if (_outline != null)
        {
            var pen = new Pen(Brushes.Black, _outlineThickness + tolerance * 2)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            return _outline.StrokeContains(pen, local);
        }

        double h = _element is TextElement ? Math.Max(ActualHeight, _element.Height) : _element.Height;
        return local.X >= -tolerance && local.X <= _element.Width + tolerance &&
               local.Y >= -tolerance && local.Y <= h + tolerance;
    }
}
