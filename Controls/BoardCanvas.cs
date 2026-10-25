using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using WhiteboardSimulator.Helpers;
using WhiteboardSimulator.Models;
using WhiteboardSimulator.ViewModels;
using DocElement = System.Windows.Documents.TextElement;
using TextElement = WhiteboardSimulator.Models.TextElement;
using WpfEllipse = System.Windows.Shapes.Ellipse;
using WpfLine = System.Windows.Shapes.Line;
using WpfPath = System.Windows.Shapes.Path;
using WpfRectangle = System.Windows.Shapes.Rectangle;

namespace WhiteboardSimulator.Controls;

/// <summary>
/// A área de trabalho do quadro: desenha os elementos do quadro ativo e trata o mouse conforme a
/// ferramenta (caneta, borracha, texto, formas, post-it, alfinete, ligação, seleção...).
/// Tudo o que muda o quadro passa pelo BoardViewModel, então entra no desfazer/refazer.
/// </summary>
public sealed class BoardCanvas : Grid
{
    // ---- Propriedades de dependência ----

    public static readonly DependencyProperty BoardProperty = DependencyProperty.Register(
        nameof(Board), typeof(BoardViewModel), typeof(BoardCanvas),
        new PropertyMetadata(null, (d, e) => ((BoardCanvas)d).OnBoardChanged((BoardViewModel?)e.OldValue, (BoardViewModel?)e.NewValue)));

    public static readonly DependencyProperty ToolProperty = DependencyProperty.Register(
        nameof(Tool), typeof(ToolState), typeof(BoardCanvas),
        new PropertyMetadata(null, (d, e) => ((BoardCanvas)d).OnToolStateChanged((ToolState?)e.OldValue, (ToolState?)e.NewValue)));

    public BoardViewModel? Board { get => (BoardViewModel?)GetValue(BoardProperty); set => SetValue(BoardProperty, value); }
    public ToolState? Tool { get => (ToolState?)GetValue(ToolProperty); set => SetValue(ToolProperty, value); }

    // ---- Tipos auxiliares ----

    private enum Drag { None, Pen, Shape, Sticky, Marquee, Move, Resize, Rotate, Erase, Connect, Pan }

    private enum Handle { None, TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left, Rotate }

    private sealed record ElementState(double X, double Y, double W, double H, double Rot, Guid? Attached, double Font, string? RunsJson);

    private const string ClipboardFormat = "WhiteboardSimulator.Elements.v1";
    private static readonly Brush Accent = ElementView.BrushFor("#4F6BED");

    // ---- Camadas (todas dentro do "mundo", que recebe zoom e deslocamento) ----

    private readonly Canvas _world = new();
    private readonly Canvas _elementsLayer = new();
    private readonly Canvas _previewLayer = new() { IsHitTestVisible = false };
    private readonly Canvas _selectionLayer = new() { IsHitTestVisible = false };
    private readonly Canvas _editorLayer = new();

    private readonly Dictionary<Guid, ElementView> _views = new();

    // ---- Estado da interação ----

    private Drag _drag = Drag.None;
    private Point _start;                         // onde o mouse foi pressionado (mundo)
    private Point _panStartView, _panOrigin;

    private readonly List<Point> _penPoints = new();
    private WpfPath? _penPath;
    private (Point A, Point B)? _snapEdge;        // borda da régua em que o traço está "preso"

    private ShapeElement? _shapePreview;
    private ElementView? _shapePreviewView;
    private ShapeKind _shapeKind;

    private WpfRectangle? _rubberBand;
    private WpfEllipse? _eraserRing;
    private WpfLine? _connectPreview;

    private Dictionary<BoardElement, ElementState> _dragBefore = new();
    private List<BoardElement> _moveSet = new();
    private BoardElement? _transformTarget;
    private Handle _activeHandle;
    private Point _origCenter;
    private ElementState? _origState;

    private PinElement? _connectFrom;
    private PinElement? _pendingPin;
    private Point _lastMouse;

    private RichTextBox? _editor;
    private BoardViewModel? _editBoard;
    private BoardElement? _editing;
    private bool _editingIsNew;
    private string _editOriginalText = "";
    private string? _editOriginalRuns;
    private BoardElement? _clickEditCandidate;   // clique simples (sem arrastar) num texto/post-it abre a edição

    private string _lastClipboardJson = "";
    private int _pasteCount;
    private bool _selectionRefreshQueued;
    private Transform? _savedTransform;
    private bool _exporting;

    private double Zoom => Board?.Zoom ?? 1.0;

    public BoardCanvas()
    {
        Background = Brushes.Transparent;   // para o Grid receber o mouse em toda a área
        ClipToBounds = true;
        Focusable = true;
        FocusVisualStyle = null;
        AllowDrop = true;

        _world.Children.Add(_elementsLayer);
        _world.Children.Add(_previewLayer);
        _world.Children.Add(_selectionLayer);
        _world.Children.Add(_editorLayer);
        Children.Add(_world);

        SizeChanged += (_, _) => UpdateTransform();
    }

    // =====================================================================
    //  Ligação com o quadro e com a ferramenta
    // =====================================================================

    private void OnBoardChanged(BoardViewModel? oldBoard, BoardViewModel? newBoard)
    {
        CancelInteraction();
        CommitEdit();
        _pendingPin = null;

        if (oldBoard != null)
        {
            oldBoard.Elements.CollectionChanged -= OnElementsChanged;
            oldBoard.SelectionChanged -= OnSelectionChanged;
            oldBoard.PropertyChanged -= OnBoardPropertyChanged;
            foreach (var el in oldBoard.Elements) el.PropertyChanged -= OnAnyElementChanged;
        }

        foreach (var view in _views.Values)
        {
            view.Detach();
            _elementsLayer.Children.Remove(view);
        }
        _views.Clear();

        if (newBoard != null)
        {
            foreach (var el in newBoard.Elements) AddView(el);
            newBoard.Elements.CollectionChanged += OnElementsChanged;
            newBoard.SelectionChanged += OnSelectionChanged;
            newBoard.PropertyChanged += OnBoardPropertyChanged;
            UpdateZ();
        }

        UpdateTransform();
        RefreshSelection();
    }

    private void OnToolStateChanged(ToolState? oldState, ToolState? newState)
    {
        if (oldState != null) oldState.PropertyChanged -= OnToolPropertyChanged;
        if (newState != null) newState.PropertyChanged += OnToolPropertyChanged;
        UpdateCursor();
    }

    private void OnToolPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Mudou o tamanho da borracha (+ / -): o círculo na tela acompanha na hora.
        if (e.PropertyName == nameof(ToolState.EraserSize))
        {
            if (Tool?.Tool == ToolType.Eraser && IsMouseOver) UpdateEraserRing(_lastMouse);
            return;
        }

        // Mudou a espessura (+ / -): a bolinha do pincel acompanha na hora.
        if (e.PropertyName == nameof(ToolState.Thickness))
        {
            if (Tool?.Tool == ToolType.Pen && IsMouseOver && _drag == Drag.None) UpdatePenRing(_lastMouse);
            return;
        }

        if (e.PropertyName != nameof(ToolState.Tool)) return;

        CancelInteraction();
        CommitEdit();
        _pendingPin = null;
        HideEraserRing();
        HidePenRing();
        HideConnectPreview();
        UpdateCursor();

        // "Imagem" e "Régua" são ações: executam e voltam para Selecionar.
        var tool = Tool?.Tool;
        if (tool is ToolType.Image or ToolType.Ruler)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (tool == ToolType.Image) InsertImageFromFile(); else PlaceRuler();
                if (Tool != null) Tool.Tool = ToolType.Select;
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void OnBoardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BoardViewModel.Zoom) or nameof(BoardViewModel.PanX) or nameof(BoardViewModel.PanY))
            UpdateTransform();
    }

    private void UpdateCursor()
    {
        Cursor = Tool?.Tool switch
        {
            ToolType.Select => Cursors.Arrow,
            ToolType.Text => Cursors.IBeam,
            ToolType.Eraser => Cursors.None,
            _ => Cursors.Cross
        };
    }

    // =====================================================================
    //  Zoom e deslocamento
    // =====================================================================

    private void UpdateTransform()
    {
        if (_exporting) return;
        if (ClampPan()) return;      // o ajuste dispara de novo (PanX/PanY mudaram)
        double z = Zoom;
        double cx = ActualWidth / 2, cy = ActualHeight / 2;
        var m = Matrix.Identity;
        m.Translate(-cx, -cy);
        m.Scale(z, z);
        m.Translate(cx + (Board?.PanX ?? 0), cy + (Board?.PanY ?? 0));
        _world.RenderTransform = new MatrixTransform(m);
        RefreshSelection();
    }

    /// <summary>
    /// O quadro é como um quadro de verdade: não rola para além das bordas. Com zoom maior que 100% dá para
    /// passear por ele, mas a borda nunca entra na tela; com zoom menor, ele fica centralizado.
    /// Devolve true se precisou corrigir o deslocamento.
    /// </summary>
    private bool ClampPan()
    {
        if (Board == null || ActualWidth <= 0 || ActualHeight <= 0) return false;
        double z = Zoom;
        double limitX = Math.Max(0, ActualWidth / 2 * (z - 1));
        double limitY = Math.Max(0, ActualHeight / 2 * (z - 1));
        double px = Math.Max(-limitX, Math.Min(limitX, Board.PanX));
        double py = Math.Max(-limitY, Math.Min(limitY, Board.PanY));
        if (Math.Abs(px - Board.PanX) < 0.0001 && Math.Abs(py - Board.PanY) < 0.0001) return false;
        Board.PanX = px;
        Board.PanY = py;
        return true;
    }

    /// <summary>Zoom mantendo o ponto sob o mouse no mesmo lugar.</summary>
    public void ZoomAt(Point viewPoint, double factor)
    {
        if (Board == null) return;
        double z = Board.Zoom;
        double nz = Math.Max(BoardViewModel.MinZoom, Math.Min(BoardViewModel.MaxZoom, Math.Round(z * factor, 2)));
        if (Math.Abs(nz - z) < 0.0001) return;

        double cx = ActualWidth / 2, cy = ActualHeight / 2;
        var world = new Point((viewPoint.X - cx - Board.PanX) / z + cx, (viewPoint.Y - cy - Board.PanY) / z + cy);
        Board.Zoom = nz;      // primeiro o zoom: o limite do deslocamento depende dele
        Board.PanX = viewPoint.X - cx - (world.X - cx) * nz;
        Board.PanY = viewPoint.Y - cy - (world.Y - cy) * nz;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Board == null) return;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            ZoomAt(e.GetPosition(this), e.Delta > 0 ? 1.1 : 1 / 1.1);
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            Board.PanX += e.Delta;
        else
            Board.PanY += e.Delta;
        e.Handled = true;
    }

    // =====================================================================
    //  Elementos na tela
    // =====================================================================

    private BoardElement? Resolve(Guid id) => Board?.Find(id);

    private void AddView(BoardElement el)
    {
        if (_views.ContainsKey(el.Id)) return;
        var view = new ElementView(el, Resolve);
        _views[el.Id] = view;
        _elementsLayer.Children.Add(view);
        el.PropertyChanged += OnAnyElementChanged;
    }

    private void RemoveView(BoardElement el)
    {
        if (!_views.TryGetValue(el.Id, out var view)) return;
        view.Detach();
        _elementsLayer.Children.Remove(view);
        _views.Remove(el.Id);
        el.PropertyChanged -= OnAnyElementChanged;
    }

    private void OnElementsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset && Board != null)
        {
            OnBoardChanged(Board, Board);
            return;
        }
        if (e.OldItems != null) foreach (BoardElement el in e.OldItems) RemoveView(el);
        if (e.NewItems != null) foreach (BoardElement el in e.NewItems) AddView(el);
        UpdateZ();
        RefreshSelection();
    }

    /// <summary>Pinos ficam por cima de tudo, depois as ligações, depois a régua; o resto na ordem de criação.</summary>
    private static int ZOf(BoardElement el, int index) => el switch
    {
        PinElement => 30000 + index,
        ConnectorElement => 20000 + index,
        RulerElement => 10000 + index,
        _ => index
    };

    private void UpdateZ()
    {
        if (Board == null) return;
        int i = 0;
        foreach (var el in Board.Elements)
        {
            if (_views.TryGetValue(el.Id, out var view)) Canvas.SetZIndex(view, ZOf(el, i));
            i++;
        }
    }

    /// <summary>Elementos do mais alto (na frente) para o mais baixo.</summary>
    private IEnumerable<BoardElement> TopFirst() =>
        Board == null
            ? Enumerable.Empty<BoardElement>()
            : Board.Elements.Select((el, i) => (el, z: ZOf(el, i))).OrderByDescending(t => t.z).Select(t => t.el);

    private BoardElement? TopmostAt(Point p, double tolerance, Func<BoardElement, bool>? filter = null)
    {
        foreach (var el in TopFirst())
        {
            if (filter != null && !filter(el)) continue;
            if (_views.TryGetValue(el.Id, out var view) && view.HitTest(p, tolerance)) return el;
        }
        return null;
    }

    private void OnAnyElementChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is BoardElement { IsSelected: true } &&
            e.PropertyName is nameof(BoardElement.X) or nameof(BoardElement.Y) or nameof(BoardElement.Width)
                or nameof(BoardElement.Height) or nameof(BoardElement.Rotation))
            QueueSelectionRefresh();
    }

    private void OnSelectionChanged(object? sender, EventArgs e) => RefreshSelection();

    private void QueueSelectionRefresh()
    {
        if (_selectionRefreshQueued) return;
        _selectionRefreshQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _selectionRefreshQueued = false;
            RefreshSelection();
        }, System.Windows.Threading.DispatcherPriority.Render);
    }

    // =====================================================================
    //  Seleção: contorno e alças
    // =====================================================================

    private static readonly (Handle Handle, int Hx, int Hy)[] HandleDirections =
    {
        (Handle.TopLeft, -1, -1), (Handle.Top, 0, -1), (Handle.TopRight, 1, -1), (Handle.Right, 1, 0),
        (Handle.BottomRight, 1, 1), (Handle.Bottom, 0, 1), (Handle.BottomLeft, -1, 1), (Handle.Left, -1, 0)
    };

    private static (int Hx, int Hy) DirectionOf(Handle h) =>
        HandleDirections.First(d => d.Handle == h) is var d2 ? (d2.Hx, d2.Hy) : (0, 0);

    /// <summary>Alças disponíveis para o elemento (posição em coordenadas locais da caixa).</summary>
    private List<(Handle Handle, Point Local)> HandlesOf(BoardElement el)
    {
        var list = new List<(Handle, Point)>();
        double w = el.Width, h = el.Height;

        foreach (var (handle, hx, hy) in HandleDirections)
        {
            bool corner = hx != 0 && hy != 0;
            bool allowed = el.Resize switch
            {
                ResizeKind.Free => true,
                ResizeKind.Aspect => corner,
                ResizeKind.HorizontalOnly => hx != 0 && hy == 0,
                ResizeKind.Text => corner || (hx != 0 && hy == 0),   // cantos: a letra cresce; laterais: só a quebra de linha
                _ => false
            };
            if (!allowed) continue;
            list.Add((handle, new Point(w / 2 + hx * w / 2, h / 2 + hy * h / 2)));
        }

        if (el.CanRotate) list.Add((Handle.Rotate, new Point(w / 2, -30 / Zoom)));
        return list;
    }

    private void RefreshSelection()
    {
        _selectionLayer.Children.Clear();
        if (Board == null || _exporting) return;

        double z = Zoom;
        double pad = 4 / z, hs = 10 / z, line = 1.5 / z;

        foreach (var el in Board.Selection)
        {
            if (el is ConnectorElement connector)
            {
                if (_views.TryGetValue(connector.Id, out var cv) && cv.ConnectorGeometry != null)
                {
                    _selectionLayer.Children.Add(new WpfPath
                    {
                        Data = cv.ConnectorGeometry,
                        Stroke = new SolidColorBrush(Color.FromArgb(120, 0x4F, 0x6B, 0xED)),
                        StrokeThickness = connector.Thickness + 6 / z,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round
                    });
                }
                continue;
            }

            double w = el.Width, h = el.Height;
            var host = new Canvas
            {
                Width = w,
                Height = h,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(el.Rotation)
            };
            Canvas.SetLeft(host, el.X);
            Canvas.SetTop(host, el.Y);

            var outline = new WpfRectangle
            {
                Width = w + 2 * pad,
                Height = h + 2 * pad,
                Stroke = Accent,
                StrokeThickness = line,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                RadiusX = 3 / z,
                RadiusY = 3 / z
            };
            Canvas.SetLeft(outline, -pad);
            Canvas.SetTop(outline, -pad);
            host.Children.Add(outline);

            // Alças só quando há um único elemento selecionado.
            if (Board.Selection.Count == 1)
            {
                foreach (var (handle, local) in HandlesOf(el))
                {
                    if (handle == Handle.Rotate)
                    {
                        host.Children.Add(new WpfLine
                        {
                            X1 = w / 2, Y1 = -pad, X2 = local.X, Y2 = local.Y,
                            Stroke = Accent, StrokeThickness = line
                        });
                        var knob = new WpfEllipse { Width = hs * 1.2, Height = hs * 1.2, Fill = Brushes.White, Stroke = Accent, StrokeThickness = line };
                        Canvas.SetLeft(knob, local.X - hs * 0.6);
                        Canvas.SetTop(knob, local.Y - hs * 0.6);
                        host.Children.Add(knob);
                    }
                    else
                    {
                        var square = new WpfRectangle { Width = hs, Height = hs, Fill = Brushes.White, Stroke = Accent, StrokeThickness = line, RadiusX = 2 / z, RadiusY = 2 / z };
                        Canvas.SetLeft(square, local.X - hs / 2);
                        Canvas.SetTop(square, local.Y - hs / 2);
                        host.Children.Add(square);
                    }
                }
            }

            _selectionLayer.Children.Add(host);
        }
    }

    private Handle HitHandle(Point world)
    {
        if (Board is not { Selection.Count: 1 }) return Handle.None;
        var el = Board.Selection[0];
        if (el is ConnectorElement) return Handle.None;

        var local = ElementView.ToLocal(el, world);
        double tol = 9 / Zoom;
        foreach (var (handle, pos) in HandlesOf(el))
            if (Math.Abs(local.X - pos.X) <= tol && Math.Abs(local.Y - pos.Y) <= tol) return handle;
        return Handle.None;
    }

    // =====================================================================
    //  Mouse
    // =====================================================================

    private Point WorldPoint(MouseEventArgs e) => PointerOverride ?? e.GetPosition(_world);

    // ---- Ganchos para o autoteste (versão de depuração): simulam o mouse sem tocar no mouse real ----

    internal Point? PointerOverride { get; set; }
    internal RichTextBox? ActiveEditor => _editor;
    internal void CommitEditForTest() => CommitEdit();

    /// <summary>Troca todo o texto do editor por um texto simples (usado pelo autoteste).</summary>
    internal void TestSetEditorText(string text)
    {
        if (_editor == null) return;
        new TextRange(_editor.Document.ContentStart, _editor.Document.ContentEnd).Text = text;
    }

    /// <summary>Seleciona um trecho do texto do editor, por posição de caracteres (autoteste).</summary>
    internal void TestSelectInEditor(int start, int length)
    {
        if (_editor == null) return;
        var begin = _editor.Document.ContentStart.GetPositionAtOffset(0)!;
        var all = new TextRange(_editor.Document.ContentStart, _editor.Document.ContentEnd);
        var from = GetPositionAtCharOffset(_editor.Document.ContentStart, start);
        var to = GetPositionAtCharOffset(_editor.Document.ContentStart, start + length);
        _ = begin; _ = all;
        _editor.Selection.Select(from, to);
    }

    private static TextPointer GetPositionAtCharOffset(TextPointer origin, int count)
    {
        var pointer = origin;
        int left = count;
        while (pointer != null)
        {
            if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                int run = pointer.GetTextRunLength(LogicalDirection.Forward);
                if (left <= run) return pointer.GetPositionAtOffset(left)!;
                left -= run;
                pointer = pointer.GetPositionAtOffset(run)!;
            }
            else
            {
                var next = pointer.GetNextContextPosition(LogicalDirection.Forward);
                if (next == null) break;
                pointer = next;
            }
        }
        return origin.DocumentEnd;
    }

    internal void TestDown(Point world)
    {
        PointerOverride = world;
        OnMouseDown(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent });
    }

    internal void TestMove(Point world)
    {
        PointerOverride = world;
        OnMouseMove(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseMoveEvent });
    }

    internal void TestUp(Point world)
    {
        PointerOverride = world;
        OnMouseUp(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });
        PointerOverride = null;
    }

    internal void TestDrag(Point from, Point to, int steps = 8)
    {
        TestDown(from);
        for (int i = 1; i <= steps; i++)
            TestMove(new Point(from.X + (to.X - from.X) * i / steps, from.Y + (to.Y - from.Y) * i / steps));
        TestUp(to);
    }

    internal void TestInsertImage(byte[] data, double width, double height, Point center) => InsertImage(data, width, height, center);
    internal bool HasView(Guid id) => _views.ContainsKey(id);
    internal ElementView? ViewOf(Guid id) => _views.TryGetValue(id, out var v) ? v : null;

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (Board == null || Tool == null) return;

        Focus();
        CommitEdit();
        var p = WorldPoint(e);
        _lastMouse = p;

        if (e.ChangedButton == MouseButton.Middle)
        {
            _drag = Drag.Pan;
            _panStartView = e.GetPosition(this);
            _panOrigin = new Point(Board.PanX, Board.PanY);
            CaptureMouse();
            Cursor = Cursors.SizeAll;
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;

        switch (Tool.Tool)
        {
            case ToolType.Select: SelectDown(p, e); break;
            case ToolType.Pen: BeginPen(p); break;
            case ToolType.Line: BeginShape(p, ShapeKind.Line); break;
            case ToolType.Rectangle: BeginShape(p, ShapeKind.Rectangle); break;
            case ToolType.Ellipse: BeginShape(p, ShapeKind.Ellipse); break;
            case ToolType.Text: TextDown(p); break;
            case ToolType.StickyNote: BeginSticky(p); break;
            case ToolType.Pin: PlacePin(p); break;
            case ToolType.Connector: ConnectDown(p); break;
            case ToolType.Eraser: BeginErase(p); break;
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Board == null || Tool == null) return;

        var p = WorldPoint(e);
        _lastMouse = p;

        switch (_drag)
        {
            case Drag.Pan:
                var v = e.GetPosition(this);
                Board.PanX = _panOrigin.X + (v.X - _panStartView.X);
                Board.PanY = _panOrigin.Y + (v.Y - _panStartView.Y);
                return;
            case Drag.Pen: ContinuePen(p); return;
            case Drag.Shape: UpdateShape(p); return;
            case Drag.Sticky: UpdateStickyBand(p); return;
            case Drag.Marquee: UpdateMarquee(p); return;
            case Drag.Move: DoMove(p); return;
            case Drag.Resize: DoResize(p); return;
            case Drag.Rotate: DoRotate(p); return;
            case Drag.Erase: EraseMove(p); return;
            case Drag.Connect: UpdateConnectPreview(p); return;
        }

        // Sem botão pressionado: feedback de passar o mouse.
        if (Tool.Tool == ToolType.Eraser) UpdateEraserRing(p);
        if (Tool.Tool == ToolType.Pen) UpdatePenRing(p);
        if (Tool.Tool == ToolType.Connector && _pendingPin != null) UpdateConnectPreview(p);
        if (Tool.Tool == ToolType.Select) Cursor = CursorFor(p);
    }

    private Cursor CursorFor(Point p)
    {
        switch (HitHandle(p))
        {
            case Handle.Rotate: return Cursors.Hand;
            case Handle.Top or Handle.Bottom: return Cursors.SizeNS;
            case Handle.Left or Handle.Right: return Cursors.SizeWE;
            case Handle.TopLeft or Handle.BottomRight: return Cursors.SizeNWSE;
            case Handle.TopRight or Handle.BottomLeft: return Cursors.SizeNESW;
        }
        return TopmostAt(p, 4 / Zoom) != null ? Cursors.SizeAll : Cursors.Arrow;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (Board == null || _drag == Drag.None) return;
        if (e.ChangedButton != MouseButton.Left && !(e.ChangedButton == MouseButton.Middle && _drag == Drag.Pan)) return;

        var p = WorldPoint(e);
        var finished = _drag;
        _drag = Drag.None;
        ReleaseMouseCapture();

        switch (finished)
        {
            case Drag.Pen: EndPen(); break;
            case Drag.Shape: EndShape(p); break;
            case Drag.Sticky: EndSticky(p); break;
            case Drag.Marquee: EndMarquee(p); break;
            case Drag.Move: EndMove(p); break;
            case Drag.Resize or Drag.Rotate: EndTransform(finished == Drag.Rotate ? "Girar" : "Redimensionar"); break;
            case Drag.Erase: EndErase(); break;
            case Drag.Connect: EndConnect(p); break;
            case Drag.Pan: UpdateCursor(); break;
        }
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_drag == Drag.None)
        {
            HideEraserRing();
            HidePenRing();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            CancelInteraction();
            _pendingPin = null;
            HideConnectPreview();
            Board?.ClearSelection();
            e.Handled = true;
        }
    }

    /// <summary>Abandona o que estiver sendo feito com o mouse (troca de ferramenta, de aba, Esc).</summary>
    private void CancelInteraction()
    {
        if (_drag == Drag.Erase) EndErase();    // o que já foi apagado fica (e entra no desfazer)
        if (_dragBefore.Count > 0 && (_drag is Drag.Move or Drag.Resize or Drag.Rotate))
            foreach (var (el, state) in _dragBefore) Restore(el, state);

        _drag = Drag.None;
        ReleaseMouseCapture();
        _previewLayer.Children.Clear();
        _penPath = null;
        _penPoints.Clear();
        _shapePreview = null;
        _shapePreviewView = null;
        _rubberBand = null;
        _eraserRing = null;
        _penRing = null;
        _connectPreview = null;
        _connectFrom = null;
        _snapEdge = null;
        _dragBefore = new Dictionary<BoardElement, ElementState>();
        _moveSet = new List<BoardElement>();
        RefreshSelection();
    }

    // =====================================================================
    //  Caneta, formas e régua
    // =====================================================================

    private string InkColor => Tool!.GetColor(Board!.Surface);

    /// <summary>Se o ponto está junto a uma borda longa da régua, devolve essa borda (em coordenadas do quadro).</summary>
    private (Point A, Point B)? FindRulerEdge(Point p)
    {
        if (Board == null) return null;
        double tol = 14 / Zoom;
        foreach (var ruler in Board.Elements.OfType<RulerElement>())
        {
            var local = ElementView.ToLocal(ruler, p);
            if (local.X < -tol || local.X > ruler.Width + tol) continue;

            // O traço fica rente à borda, do lado de fora, para não ficar escondido sob a régua.
            double half = (Tool?.Thickness ?? 4) / 2 + 0.5;
            foreach (var (y, outY) in new[] { (0.0, -half), (ruler.Height, ruler.Height + half) })
            {
                if (Math.Abs(local.Y - y) <= tol)
                    return (ElementView.ToWorld(ruler, new Point(0, outY)), ElementView.ToWorld(ruler, new Point(ruler.Width, outY)));
            }
        }
        return null;
    }

    private static Point ProjectOn((Point A, Point B) edge, Point p)
    {
        var ab = edge.B - edge.A;
        double len2 = ab.LengthSquared;
        if (len2 < 1e-9) return edge.A;
        double t = ((p - edge.A) * ab) / len2;
        return edge.A + ab * t;
    }

    // Bolinha do pincel: mostra, antes de desenhar, o tamanho real do traço sob o mouse.
    private WpfEllipse? _penRing;

    private void UpdatePenRing(Point p)
    {
        if (Tool == null) return;
        if (_penRing == null)
        {
            _penRing = new WpfEllipse { Fill = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)) };
            _previewLayer.Children.Add(_penRing);
        }
        double d = Math.Max(Tool.Thickness, 2 / Zoom);
        _penRing.Stroke = ElementView.BrushFor(Board != null ? InkColor : "#1F2328");
        _penRing.StrokeThickness = 1 / Zoom;
        _penRing.Width = _penRing.Height = d;
        Canvas.SetLeft(_penRing, p.X - d / 2);
        Canvas.SetTop(_penRing, p.Y - d / 2);
    }

    private void HidePenRing()
    {
        if (_penRing == null) return;
        _previewLayer.Children.Remove(_penRing);
        _penRing = null;
    }

    internal bool HasPenRing => _penRing != null;

    private void BeginPen(Point p)
    {
        HidePenRing();
        _drag = Drag.Pen;
        _snapEdge = FindRulerEdge(p);
        _penPoints.Clear();
        _penPoints.Add(_snapEdge is { } edge ? ProjectOn(edge, p) : p);

        _penPath = new WpfPath
        {
            Stroke = ElementView.BrushFor(InkColor),
            StrokeThickness = Tool!.Thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
        _previewLayer.Children.Add(_penPath);
        UpdatePenGeometry();

        // Por último: capturar o mouse faz o WPF disparar um MouseMove na hora.
        CaptureMouse();
    }

    private void ContinuePen(Point p)
    {
        if (_penPoints.Count == 0) return;

        if (_snapEdge is { } edge)
        {
            // Junto à régua o traço fica reto: só o início e o ponto atual.
            var q = ProjectOn(edge, p);
            if (_penPoints.Count == 1) _penPoints.Add(q); else _penPoints[1] = q;
        }
        else
        {
            if ((p - _penPoints[^1]).Length < 1.5 / Zoom) return;
            _penPoints.Add(p);
        }
        UpdatePenGeometry();
    }

    private void UpdatePenGeometry()
    {
        if (_penPath != null) _penPath.Data = ElementView.BuildSmoothGeometry(_penPoints);
    }

    private void EndPen()
    {
        _previewLayer.Children.Clear();
        _penPath = null;
        if (_penPoints.Count == 0 || Board == null) return;

        double minX = _penPoints.Min(q => q.X), maxX = _penPoints.Max(q => q.X);
        double minY = _penPoints.Min(q => q.Y), maxY = _penPoints.Max(q => q.Y);

        var stroke = new StrokeElement
        {
            X = minX, Y = minY, Width = maxX - minX, Height = maxY - minY,
            BaseWidth = maxX - minX, BaseHeight = maxY - minY,
            Color = InkColor,
            Thickness = Tool!.Thickness,
            Points = _penPoints.Select(q => new PointD(q.X - minX, q.Y - minY)).ToList()
        };
        _penPoints.Clear();
        _snapEdge = null;
        Board.AddElements(new[] { stroke }, "Desenhar");
    }

    private void BeginShape(Point p, ShapeKind kind)
    {
        _drag = Drag.Shape;
        _start = p;
        _shapeKind = kind;
        _snapEdge = kind == ShapeKind.Line ? FindRulerEdge(p) : null;
        if (_snapEdge is { } edge) _start = ProjectOn(edge, p);

        _shapePreview = new ShapeElement { Kind = kind, Color = InkColor, Thickness = Tool!.Thickness, X = _start.X, Y = _start.Y };
        _shapePreviewView = new ElementView(_shapePreview, Resolve);
        _previewLayer.Children.Add(_shapePreviewView);
        CaptureMouse();
    }

    private void UpdateShape(Point p)
    {
        if (_shapePreview == null) return;
        ApplyShapeBox(_shapePreview, _start, p);
    }

    /// <summary>Ajusta a caixa da forma entre o ponto inicial e o atual (Shift = quadrado/círculo/45°).</summary>
    private void ApplyShapeBox(ShapeElement shape, Point start, Point end)
    {
        if (_snapEdge is { } edge && shape.Kind == ShapeKind.Line) end = ProjectOn(edge, end);

        double dx = end.X - start.X, dy = end.Y - start.Y;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _snapEdge == null)
        {
            if (shape.Kind == ShapeKind.Line)
            {
                double angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
                double len = Math.Sqrt(dx * dx + dy * dy);
                dx = Math.Cos(angle) * len;
                dy = Math.Sin(angle) * len;
            }
            else
            {
                double size = Math.Max(Math.Abs(dx), Math.Abs(dy));
                dx = Math.Sign(dx == 0 ? 1 : dx) * size;
                dy = Math.Sign(dy == 0 ? 1 : dy) * size;
            }
        }

        shape.X = Math.Min(start.X, start.X + dx);
        shape.Y = Math.Min(start.Y, start.Y + dy);
        shape.Width = Math.Abs(dx);
        shape.Height = Math.Abs(dy);
        shape.FlipY = shape.Kind == ShapeKind.Line && dx * dy < 0;
    }

    private void EndShape(Point p)
    {
        var shape = _shapePreview;
        _previewLayer.Children.Clear();
        _shapePreview = null;
        _shapePreviewView = null;
        var snap = _snapEdge;
        _snapEdge = null;
        if (shape == null || Board == null) return;

        bool tooSmall = shape.Kind == ShapeKind.Line
            ? Math.Sqrt(shape.Width * shape.Width + shape.Height * shape.Height) < 3 / Zoom
            : shape.Width < 3 / Zoom && shape.Height < 3 / Zoom;
        if (tooSmall) return;

        var element = new ShapeElement
        {
            Kind = shape.Kind, Color = shape.Color, Thickness = shape.Thickness,
            X = shape.X, Y = shape.Y, Width = shape.Width, Height = shape.Height, FlipY = shape.FlipY
        };
        _ = snap;
        Board.AddElements(new[] { element }, shape.Kind switch
        {
            ShapeKind.Line => "Desenhar linha",
            ShapeKind.Rectangle => "Desenhar quadrado",
            _ => "Desenhar círculo"
        });
    }

    /// <summary>Coloca uma régua no centro da área visível (ou seleciona a que já existe).</summary>
    public void PlaceRuler()
    {
        if (Board == null) return;
        var existing = Board.Elements.OfType<RulerElement>().FirstOrDefault();
        if (existing != null)
        {
            // Já tem régua: o botão da régua alterna entre horizontal e vertical.
            Board.SetSelection(new[] { existing });
            Board.ToggleRulerOrientation(existing);
            return;
        }

        var center = ViewCenter();
        var ruler = new RulerElement { Width = 560, Height = RulerElement.Thickness };
        ruler.X = center.X - ruler.Width / 2;
        ruler.Y = center.Y - ruler.Height / 2;
        Board.AddElements(new[] { ruler }, "Colocar régua");
        Board.SetSelection(new[] { ruler });
    }

    // =====================================================================
    //  Texto e post-it
    // =====================================================================

    private void TextDown(Point p)
    {
        var existing = TopmostAt(p, 2 / Zoom, el => el is TextElement or StickyNoteElement);
        if (existing != null)
        {
            Board!.SetSelection(new[] { existing });
            BeginEdit(existing, isNew: false, clickAt: p);
            return;
        }

        var text = new TextElement
        {
            X = p.X,
            Y = p.Y - Tool!.FontSize * 0.7,
            Width = 280,
            Height = Tool.FontSize * 1.4,
            FontSize = Tool.FontSize,
            FontFamily = Tool.FontFamily,
            Bold = Tool.Bold,
            Italic = Tool.Italic,
            Underline = Tool.Underline,
            Color = InkColor
        };
        BeginEdit(text, isNew: true);
    }

    private void BeginSticky(Point p)
    {
        _drag = Drag.Sticky;
        _start = p;
        CaptureMouse();
    }

    private void UpdateStickyBand(Point p)
    {
        if ((p - _start).Length < 8 / Zoom) return;
        if (_rubberBand == null)
        {
            _rubberBand = NewBand(ElementView.BrushFor(Tool!.StickyColor), 0.6);
            _previewLayer.Children.Add(_rubberBand);
        }
        PositionBand(_rubberBand, _start, p);
    }

    private void EndSticky(Point p)
    {
        _previewLayer.Children.Clear();
        _rubberBand = null;
        if (Board == null || Tool == null) return;

        double w = Math.Abs(p.X - _start.X), h = Math.Abs(p.Y - _start.Y);
        var note = new StickyNoteElement { Color = Tool.StickyColor };
        var rnd = new Random();
        note.Rotation = Math.Round((rnd.NextDouble() - 0.5) * 5, 1);   // levemente torto, como um post-it de verdade

        if (w >= 60 / Zoom && h >= 60 / Zoom)
        {
            note.X = Math.Min(_start.X, p.X); note.Y = Math.Min(_start.Y, p.Y);
            note.Width = w; note.Height = h;
        }
        else
        {
            note.Width = 190; note.Height = 190;
            note.X = _start.X - 95; note.Y = _start.Y - 95;
        }

        Board.AddElements(new[] { note }, "Adicionar post-it");
        Board.SetSelection(new[] { note });
        Tool.Tool = ToolType.Select;       // CancelInteraction/CommitEdit rodam aqui
        BeginEdit(note, isNew: false);
    }

    // ---- Edição de texto no próprio quadro ----

    /// <summary>Há um texto sendo editado (a barra de opções formata só o trecho selecionado).</summary>
    internal bool IsEditing => _editor != null;

    /// <summary>Formatação do trecho selecionado no editor (null quando a edição termina).</summary>
    public event EventHandler<TextFormatState?>? EditorFormatChanged;

    public void CommitPendingEdit() => CommitEdit();

    private void BeginEdit(BoardElement element, bool isNew, Point? clickAt = null)
    {
        CommitEdit();
        if (element is not RichTextElement rich) return;

        _editing = element;
        _editBoard = Board;
        _editingIsNew = isNew;
        _editOriginalText = rich.Text;
        _editOriginalRuns = rich.SnapshotRuns();

        bool sticky = element is StickyNoteElement;
        var editor = new RichTextBox
        {
            Document = RichTextConverter.ToDocument(rich),
            Background = sticky ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb(40, 79, 107, 237)),
            BorderThickness = new Thickness(0),
            Padding = sticky ? new Thickness(14) : new Thickness(0),
            FontFamily = new FontFamily(rich.FontFamily),
            FontSize = rich.FontSize,
            Foreground = ElementView.BrushFor(rich.DefaultColor),
            Width = Math.Max(1, element.Width),
            MinHeight = sticky ? element.Height : 24,
            Height = sticky ? element.Height : double.NaN,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(element.Rotation),
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsInactiveSelectionHighlightEnabled = true,
            AcceptsTab = false
        };
        // Parágrafos sem espaço entre eles (cada Enter é só uma linha nova, como no desenho do texto).
        editor.Resources.Add(typeof(Paragraph), new Style(typeof(Paragraph)) { Setters = { new Setter(Block.MarginProperty, new Thickness(0)) } });
        Canvas.SetLeft(editor, element.X);
        Canvas.SetTop(editor, element.Y);

        editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { CommitEdit(cancel: true); e.Handled = true; }
            else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { CommitEdit(); e.Handled = true; }
        };
        editor.SelectionChanged += (_, _) => RaiseEditorFormat();
        DataObject.AddPastingHandler(editor, OnEditorPasting);

        // O editor NÃO fecha ao perder o foco: assim os botões da barra (negrito, cor, tamanho...) podem agir
        // sobre a seleção. Ele fecha ao clicar no quadro, trocar de ferramenta/aba, salvar ou apertar Esc / Ctrl+Enter.
        _editor = editor;
        _editorLayer.Children.Add(editor);
        if (_views.TryGetValue(element.Id, out var view)) view.IsEditing = true;

        editor.Focus();
        if (isNew)
        {
            editor.SelectAll();
        }
        else
        {
            editor.CaretPosition = editor.Document.ContentEnd;
            if (clickAt is { } click)
            {
                // Põe o cursor onde o usuário clicou (depois do primeiro layout).
                Dispatcher.BeginInvoke(() =>
                {
                    if (_editor != editor) return;
                    var local = ElementView.ToLocal(element, click);
                    if (editor.GetPositionFromPoint(local, true) is { } at) editor.CaretPosition = at;
                    editor.Focus();
                }, System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }
        RaiseEditorFormat();
    }

    /// <summary>Colar no editor entra como texto simples: a formatação de fora (Word, site) não vem junto.</summary>
    private static void OnEditorPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not RichTextBox box) return;
        if (e.DataObject.GetDataPresent(DataFormats.UnicodeText) && e.DataObject.GetData(DataFormats.UnicodeText) is string text)
        {
            e.CancelCommand();
            box.Selection.Text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        }
    }

    private void CommitEdit(bool cancel = false)
    {
        if (_editor == null || _editing is not RichTextElement rich) return;

        var editor = _editor;
        bool isNew = _editingIsNew;
        string originalText = _editOriginalText;
        string? originalRuns = _editOriginalRuns;
        _editor = null;
        _editing = null;

        // O quadro em que a edição começou (a aba pode ter mudado enquanto o editor estava aberto).
        var board = _editBoard;
        _editBoard = null;

        var runs = cancel ? null : RichTextConverter.FromDocument(editor.Document, rich);
        _editorLayer.Children.Remove(editor);
        if (_views.TryGetValue(rich.Id, out var view)) view.IsEditing = false;
        EditorFormatChanged?.Invoke(this, null);
        if (board == null || cancel) return;      // cancelado: o elemento não foi tocado (texto novo nem chega a existir)

        string text = string.Concat(runs!.Select(r => r.Text));

        if (rich is TextElement && string.IsNullOrWhiteSpace(text))
        {
            if (!isNew) board.RemoveElements(new[] { (BoardElement)rich }, "Apagar texto");
            return;
        }

        rich.SetRuns(runs!);
        if (isNew && rich is TextElement)
        {
            board.AddElements(new[] { (BoardElement)rich }, "Adicionar texto");
            board.SetSelection(new[] { (BoardElement)rich });
            return;
        }

        string newText = rich.Text;
        string? newRuns = rich.SnapshotRuns();
        if (newText == originalText && newRuns == originalRuns) return;
        board.PushChange("Editar texto",
            () => rich.RestoreContent(originalText, originalRuns),
            () => rich.RestoreContent(newText, newRuns));
    }

    // ---- Formatação do trecho selecionado no editor ----

    /// <summary>Aplica a formatação só ao trecho selecionado (ou ao que for digitado, se não houver seleção).</summary>
    internal void FormatEditor(TextFormatChange change)
    {
        if (_editor == null) return;
        var selection = _editor.Selection;

        if (change.FontFamily != null)
            selection.ApplyPropertyValue(DocElement.FontFamilyProperty, new FontFamily(change.FontFamily));
        if (change.Color != null)
            selection.ApplyPropertyValue(DocElement.ForegroundProperty, ElementView.BrushFor(change.Color));
        if (change.Bold is { } bold)
            selection.ApplyPropertyValue(DocElement.FontWeightProperty, bold ? FontWeights.Bold : FontWeights.Normal);
        if (change.Italic is { } italic)
            selection.ApplyPropertyValue(DocElement.FontStyleProperty, italic ? FontStyles.Italic : FontStyles.Normal);
        if (change.Underline is { } underline)
            selection.ApplyPropertyValue(Inline.TextDecorationsProperty, underline ? TextDecorations.Underline : new TextDecorationCollection());
        if (change.SizeFactor is { } factor)
            ScaleSelectionSize(selection, factor);

        _editor.Focus();
        RaiseEditorFormat();
    }

    private static double NextSize(double size, double factor)
    {
        double next = Math.Round(size * factor);
        if (Math.Abs(next - size) < 0.5) next = size + (factor > 1 ? 1 : -1);   // sempre muda ao menos 1
        return Math.Max(6, Math.Min(300, next));
    }

    /// <summary>Cada trecho selecionado cresce/diminui a partir do seu próprio tamanho (palavras de tamanhos diferentes mantêm a proporção).</summary>
    private void ScaleSelectionSize(TextSelection selection, double factor)
    {
        double baseSize = (_editing as RichTextElement)?.FontSize ?? 16;

        if (selection.IsEmpty)
        {
            double current = selection.GetPropertyValue(DocElement.FontSizeProperty) is double d ? d : baseSize;
            selection.ApplyPropertyValue(DocElement.FontSizeProperty, NextSize(current, factor));
            return;
        }

        var pieces = new List<(TextRange Range, double Size)>();
        var pointer = selection.Start;
        while (pointer != null && pointer.CompareTo(selection.End) < 0)
        {
            if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var runEnd = pointer.GetPositionAtOffset(pointer.GetTextRunLength(LogicalDirection.Forward));
                var end = runEnd != null && runEnd.CompareTo(selection.End) > 0 ? selection.End : runEnd ?? selection.End;
                var range = new TextRange(pointer, end);
                double size = range.GetPropertyValue(DocElement.FontSizeProperty) is double s ? s : baseSize;
                pieces.Add((range, size));
                pointer = end;
                if (pointer.CompareTo(selection.End) >= 0) break;
            }
            pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
        }

        foreach (var (range, size) in pieces)
            range.ApplyPropertyValue(DocElement.FontSizeProperty, NextSize(size, factor));
    }

    private void RaiseEditorFormat()
    {
        if (_editor == null) return;
        var sel = _editor.Selection;

        var family = (sel.GetPropertyValue(DocElement.FontFamilyProperty) as FontFamily)?.Source;
        double? size = sel.GetPropertyValue(DocElement.FontSizeProperty) is double s ? s : null;
        bool bold = sel.GetPropertyValue(DocElement.FontWeightProperty) is FontWeight w && w == FontWeights.Bold;
        bool italic = sel.GetPropertyValue(DocElement.FontStyleProperty) is FontStyle st && st == FontStyles.Italic;
        bool underline = sel.GetPropertyValue(Inline.TextDecorationsProperty) is TextDecorationCollection tdc &&
                         tdc.Any(d => d.Location == TextDecorationLocation.Underline);
        string? color = sel.GetPropertyValue(DocElement.ForegroundProperty) is SolidColorBrush b
            ? $"#{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}" : null;

        EditorFormatChanged?.Invoke(this, new TextFormatState(family, size, bold, italic, underline, color));
    }

    // =====================================================================
    //  Alfinetes e ligações
    // =====================================================================

    private void PlacePin(Point p)
    {
        if (Board == null || Tool == null) return;
        if (TopmostAt(p, 0, el => el is PinElement) != null) return;   // já tem um alfinete aqui

        var pin = new PinElement
        {
            Color = Tool.PinColor,
            Width = PinElement.Size,
            Height = PinElement.Size,
            X = p.X - PinElement.Size / 2,
            Y = p.Y - PinElement.Size / 2,
            AttachedToId = AttachableAt(p)?.Id
        };
        Board.AddElements(new[] { pin }, "Colocar alfinete");
    }

    /// <summary>Elemento em que um alfinete solto neste ponto fica preso (post-it, imagem ou texto).</summary>
    private BoardElement? AttachableAt(Point p) =>
        TopmostAt(p, 0, el => el is StickyNoteElement or ImageElement or TextElement);

    private void ConnectDown(Point p)
    {
        var pin = TopmostAt(p, 4 / Zoom, el => el is PinElement) as PinElement;

        // Segundo clique: fecha a ligação com o alfinete escolhido antes.
        if (_pendingPin != null && pin != null && pin != _pendingPin)
        {
            CreateConnector(_pendingPin, pin);
            _pendingPin = null;
            HideConnectPreview();
            return;
        }

        if (pin == null)
        {
            _pendingPin = null;
            HideConnectPreview();
            return;
        }

        _connectFrom = pin;
        _pendingPin = pin;
        _drag = Drag.Connect;
        CaptureMouse();
        UpdateConnectPreview(p);
    }

    private void EndConnect(Point p)
    {
        var from = _connectFrom;
        _connectFrom = null;
        var target = TopmostAt(p, 4 / Zoom, el => el is PinElement) as PinElement;

        if (from != null && target != null && target != from)
        {
            CreateConnector(from, target);
            _pendingPin = null;
            HideConnectPreview();
        }
        // Soltou no mesmo alfinete: continua "pendente" para o próximo clique.
    }

    private void CreateConnector(PinElement from, PinElement to)
    {
        if (Board == null) return;
        bool exists = Board.Elements.OfType<ConnectorElement>().Any(c =>
            (c.FromId == from.Id && c.ToId == to.Id) || (c.FromId == to.Id && c.ToId == from.Id));
        if (exists) return;

        var connector = new ConnectorElement { FromId = from.Id, ToId = to.Id, Color = InkPalette.DefaultConnectorColor };
        Board.AddElements(new[] { connector }, "Ligar alfinetes");
    }

    private void UpdateConnectPreview(Point p)
    {
        var from = _connectFrom ?? _pendingPin;
        if (from == null) return;

        var a = ElementView.PinCenter(from);
        if (_connectPreview == null)
        {
            _connectPreview = new WpfLine
            {
                Stroke = ElementView.BrushFor(InkPalette.DefaultConnectorColor),
                StrokeThickness = 3,
                StrokeDashArray = new DoubleCollection { 3, 2 },
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Opacity = 0.8
            };
            _previewLayer.Children.Add(_connectPreview);
        }
        _connectPreview.X1 = a.X;
        _connectPreview.Y1 = a.Y;
        _connectPreview.X2 = p.X;
        _connectPreview.Y2 = p.Y;
    }

    private void HideConnectPreview()
    {
        if (_connectPreview == null) return;
        _previewLayer.Children.Remove(_connectPreview);
        _connectPreview = null;
    }

    // =====================================================================
    //  Borracha
    // =====================================================================

    private double EraserRadius => (Tool?.EraserSize ?? 14) / Zoom;

    // A borracha apaga só o que o círculo toca: traços e formas são "cortados" naquele ponto.
    // Post-its, imagens, textos e alfinetes não são apagados por ela (selecione e use Delete).

    private sealed class ErasePatch
    {
        public readonly List<(BoardElement Element, int Index)> Originals = new();
        public readonly List<BoardElement> Replacements = new();
    }

    private ErasePatch? _erase;
    private Point _lastErasePoint;

    private void BeginErase(Point p)
    {
        _drag = Drag.Erase;
        _erase = new ErasePatch();
        _lastErasePoint = p;
        UpdateEraserRing(p);
        EraseSegment(p, p);
        CaptureMouse();
    }

    private void EraseMove(Point p)
    {
        UpdateEraserRing(p);
        EraseSegment(_lastErasePoint, p);   // varre o trajeto inteiro, mesmo com o mouse rápido
        _lastErasePoint = p;
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        double len2 = ab.LengthSquared;
        if (len2 < 1e-9) return (p - a).Length;
        double t = Math.Max(0, Math.Min(1, ((p - a) * ab) / len2));
        return (p - (a + ab * t)).Length;
    }

    /// <summary>Pontos do desenho no quadro, com pontos intermediários para o corte ficar preciso.</summary>
    private static List<Point> DensePoints(BoardElement el, double step)
    {
        var raw = new List<Point>();
        switch (el)
        {
            case StrokeElement s:
                double sx = s.BaseWidth > 0.001 ? s.Width / s.BaseWidth : 1;
                double sy = s.BaseHeight > 0.001 ? s.Height / s.BaseHeight : 1;
                raw.AddRange(s.Points.Select(q => ElementView.ToWorld(el, new Point(q.X * sx, q.Y * sy))));
                break;
            case ShapeElement { Kind: ShapeKind.Line } l:
                raw.Add(ElementView.ToWorld(el, l.FlipY ? new Point(0, l.Height) : new Point(0, 0)));
                raw.Add(ElementView.ToWorld(el, l.FlipY ? new Point(l.Width, 0) : new Point(l.Width, l.Height)));
                break;
            case ShapeElement { Kind: ShapeKind.Rectangle } r:
                double t = r.Thickness / 2;
                foreach (var c in new[] { new Point(t, t), new Point(r.Width - t, t), new Point(r.Width - t, r.Height - t), new Point(t, r.Height - t), new Point(t, t) })
                    raw.Add(ElementView.ToWorld(el, c));
                break;
            case ShapeElement { Kind: ShapeKind.Ellipse } e:
                double rx = Math.Max(0, (e.Width - e.Thickness) / 2), ry = Math.Max(0, (e.Height - e.Thickness) / 2);
                for (int i = 0; i <= 120; i++)
                {
                    double a = i * 2 * Math.PI / 120;
                    raw.Add(ElementView.ToWorld(el, new Point(e.Width / 2 + rx * Math.Cos(a), e.Height / 2 + ry * Math.Sin(a))));
                }
                break;
        }

        var dense = new List<Point>();
        for (int i = 0; i < raw.Count; i++)
        {
            if (i == 0) { dense.Add(raw[0]); continue; }
            var from = raw[i - 1];
            double dist = (raw[i] - from).Length;
            int n = Math.Max(1, (int)Math.Ceiling(dist / step));
            for (int k = 1; k <= n; k++)
                dense.Add(new Point(from.X + (raw[i].X - from.X) * k / n, from.Y + (raw[i].Y - from.Y) * k / n));
        }
        return dense;
    }

    private static (string Color, double Thickness) InkOf(BoardElement el) => el switch
    {
        StrokeElement s => (s.Color, s.Thickness),
        ShapeElement s => (s.Color, s.Thickness),
        _ => ("#1F2328", 4)
    };

    private StrokeElement CreateStroke(IReadOnlyList<Point> points, string color, double thickness)
    {
        double minX = points.Min(q => q.X), maxX = points.Max(q => q.X);
        double minY = points.Min(q => q.Y), maxY = points.Max(q => q.Y);
        return new StrokeElement
        {
            X = minX, Y = minY, Width = maxX - minX, Height = maxY - minY,
            BaseWidth = maxX - minX, BaseHeight = maxY - minY,
            Color = color,
            Thickness = thickness,
            Points = points.Select(q => new PointD(q.X - minX, q.Y - minY)).ToList()
        };
    }

    private void EraseSegment(Point a, Point b)
    {
        if (Board == null || _erase == null) return;

        double r = EraserRadius;
        var zone = new Rect(Math.Min(a.X, b.X) - r, Math.Min(a.Y, b.Y) - r, Math.Abs(a.X - b.X) + 2 * r, Math.Abs(a.Y - b.Y) + 2 * r);

        foreach (var el in Board.Elements.Where(e => e is StrokeElement or ShapeElement).ToList())
        {
            var (color, thickness) = InkOf(el);
            if (!_views.TryGetValue(el.Id, out var view)) continue;

            var bounds = view.WorldBounds();
            bounds.Inflate(thickness, thickness);
            if (!bounds.IntersectsWith(zone)) continue;

            double reach = r + thickness / 2;
            var points = DensePoints(el, Math.Max(1.5, Math.Min(4, r / 3)));
            var kept = points.Select(q => DistanceToSegment(q, a, b) > reach).ToList();
            if (kept.All(k => k)) continue;       // o círculo passou perto, mas não encostou

            // Trechos que sobraram viram traços novos (um desenho cortado ao meio vira dois).
            var runs = new List<List<Point>>();
            List<Point>? run = null;
            for (int i = 0; i < points.Count; i++)
            {
                if (kept[i]) { (run ??= new List<Point>()).Add(points[i]); }
                else if (run != null) { runs.Add(run); run = null; }
            }
            if (run != null) runs.Add(run);

            int index = Board.Elements.IndexOf(el);
            if (!_erase.Replacements.Remove(el))
                _erase.Originals.Add((el, index));
            Board.Elements.Remove(el);

            foreach (var piece in runs)
            {
                var stroke = CreateStroke(piece, color, thickness);
                Board.Elements.Insert(Math.Min(index++, Board.Elements.Count), stroke);
                _erase.Replacements.Add(stroke);
            }
        }
    }

    private void EndErase()
    {
        var patch = _erase;
        _erase = null;
        if (patch == null || Board == null || patch.Originals.Count == 0) return;

        // Tudo o que mudou durante o arrasto vira UM passo de desfazer.
        var board = Board;
        var originals = patch.Originals.OrderBy(o => o.Index).ToList();
        var replacements = patch.Replacements.Select(el => (el, index: board.Elements.IndexOf(el))).OrderBy(t => t.index).ToList();

        board.PushChange("Apagar",
            () =>
            {
                foreach (var (el, _) in replacements) board.Elements.Remove(el);
                foreach (var (el, index) in originals)
                    if (!board.Elements.Contains(el)) board.Elements.Insert(Math.Min(index, board.Elements.Count), el);
            },
            () =>
            {
                foreach (var (el, _) in originals) board.Elements.Remove(el);
                foreach (var (el, index) in replacements)
                    if (!board.Elements.Contains(el)) board.Elements.Insert(Math.Min(index, board.Elements.Count), el);
            });
    }

    private void UpdateEraserRing(Point p)
    {
        if (_eraserRing == null)
        {
            _eraserRing = new WpfEllipse
            {
                Stroke = Accent,
                StrokeThickness = 1.5 / Zoom,
                Fill = new SolidColorBrush(Color.FromArgb(25, 79, 107, 237))
            };
            _previewLayer.Children.Add(_eraserRing);
        }
        double r = EraserRadius;
        _eraserRing.StrokeThickness = 1.5 / Zoom;
        _eraserRing.Width = _eraserRing.Height = r * 2;
        Canvas.SetLeft(_eraserRing, p.X - r);
        Canvas.SetTop(_eraserRing, p.Y - r);
    }

    private void HideEraserRing()
    {
        if (_eraserRing == null) return;
        _previewLayer.Children.Remove(_eraserRing);
        _eraserRing = null;
    }

    // =====================================================================
    //  Selecionar, mover, redimensionar e girar
    // =====================================================================

    private static ElementState Capture(BoardElement e) =>
        new(e.X, e.Y, e.Width, e.Height, e.Rotation, (e as PinElement)?.AttachedToId,
            (e as RichTextElement)?.FontSize ?? 0, (e as RichTextElement)?.SnapshotRuns());

    private static void Restore(BoardElement e, ElementState s)
    {
        e.X = s.X; e.Y = s.Y; e.Width = s.W; e.Height = s.H; e.Rotation = s.Rot;
        if (e is PinElement pin) pin.AttachedToId = s.Attached;
        if (e is RichTextElement rich && s.Font > 0)
        {
            rich.FontSize = s.Font;
            rich.RestoreContent(rich.Text, s.RunsJson);     // tamanhos por trecho voltam também
        }
    }

    private void SelectDown(Point p, MouseButtonEventArgs e)
    {
        var board = Board!;

        // Alças do elemento selecionado têm prioridade.
        var handle = HitHandle(p);
        if (handle != Handle.None)
        {
            BeginTransform(board.Selection[0], handle, p);
            return;
        }

        // Alt+clique: escolhe o elemento que está por baixo, no mesmo ponto (um por clique).
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            CycleSelectionAt(p);
            return;
        }

        bool additive = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var hit = TopmostAt(p, 4 / Zoom);

        if (hit == null)
        {
            if (!additive) board.ClearSelection();
            _drag = Drag.Marquee;
            _start = p;
            CaptureMouse();
            return;
        }

        // Duplo clique na régua: deita ou levanta.
        if (e.ClickCount == 2 && hit is RulerElement doubleClicked)
        {
            board.SetSelection(new[] { doubleClicked });
            board.ToggleRulerOrientation(doubleClicked);
            return;
        }

        if (e.ClickCount == 2 && hit is TextElement or StickyNoteElement)
        {
            board.SetSelection(new[] { hit });
            BeginEdit(hit, isNew: false);
            return;
        }

        if (additive)
        {
            var next = board.Selection.ToList();
            if (!next.Remove(hit)) next.Add(hit);
            board.SetSelection(next);
            return;
        }

        if (!hit.IsSelected) board.SetSelection(new[] { hit });

        // Clique simples (sem arrastar) em texto ou post-it: abre a edição ao soltar o mouse.
        _clickEditCandidate = hit is RichTextElement && board.Selection.Count == 1 ? hit : null;
        BeginMove(p);
    }

    /// <summary>Seleciona o próximo elemento da pilha sob o ponto (do de cima para o de baixo, e volta ao primeiro).</summary>
    internal void CycleSelectionAt(Point p)
    {
        if (Board == null) return;
        var hits = TopFirst().Where(el => _views.TryGetValue(el.Id, out var v) && v.HitTest(p, 4 / Zoom)).ToList();
        if (hits.Count == 0) return;

        int current = hits.FindIndex(h => h.IsSelected);
        Board.SetSelection(new[] { hits[(current + 1) % hits.Count] });
    }

    /// <summary>Elementos que se movem: a seleção mais os alfinetes presos a ela.</summary>
    private List<BoardElement> BuildMoveSet()
    {
        var set = new List<BoardElement>();
        if (Board == null) return set;

        foreach (var el in Board.Selection)
            if (el is not ConnectorElement) set.Add(el);

        var carriers = set.Where(e => e is not PinElement).Select(e => e.Id).ToHashSet();
        foreach (var pin in Board.Elements.OfType<PinElement>())
            if (pin.AttachedToId is { } id && carriers.Contains(id) && !set.Contains(pin)) set.Add(pin);

        return set;
    }

    private void BeginMove(Point p)
    {
        _moveSet = BuildMoveSet();
        if (_moveSet.Count == 0) return;

        _drag = Drag.Move;
        _start = p;
        _dragBefore = _moveSet.ToDictionary(e => e, Capture);
        CaptureMouse();
    }

    private void DoMove(Point p)
    {
        double dx = p.X - _start.X, dy = p.Y - _start.Y;
        foreach (var (el, s) in _dragBefore)
        {
            el.X = s.X + dx;
            el.Y = s.Y + dy;
        }
    }

    private void EndMove(Point p)
    {
        var candidate = _clickEditCandidate;
        _clickEditCandidate = null;
        if (_dragBefore.Count == 0) return;

        // Alfinetes que o usuário moveu sozinhos prendem no que estiver embaixo deles.
        foreach (var pin in Board!.Selection.OfType<PinElement>())
            pin.AttachedToId = AttachableAt(ElementView.PinCenter(pin))?.Id;

        CommitStates("Mover", _dragBefore);
        _dragBefore = new Dictionary<BoardElement, ElementState>();
        _moveSet = new List<BoardElement>();

        // Só clicou (não arrastou): o texto/post-it entra em modo de edição, com o cursor onde foi o clique.
        bool dragged = (p - _start).Length * Zoom > 3;
        if (candidate != null && !dragged && Board.Selection.Count == 1 && Board.Selection[0] == candidate)
            BeginEdit(candidate, isNew: false, clickAt: p);
    }

    private void CommitStates(string description, Dictionary<BoardElement, ElementState> before)
    {
        var after = before.Keys.ToDictionary(e => e, Capture);
        if (before.All(kv => kv.Value == after[kv.Key])) return;

        var undo = before.ToList();
        var redo = after.ToList();
        Board!.PushChange(description,
            () => { foreach (var (el, s) in undo) Restore(el, s); },
            () => { foreach (var (el, s) in redo) Restore(el, s); });
    }

    private void BeginTransform(BoardElement el, Handle handle, Point p)
    {
        _transformTarget = el;
        _activeHandle = handle;
        _origState = Capture(el);
        _origCenter = new Point(el.X + el.Width / 2, el.Y + el.Height / 2);
        _dragBefore = new Dictionary<BoardElement, ElementState> { [el] = _origState };
        _drag = handle == Handle.Rotate ? Drag.Rotate : Drag.Resize;
        _start = p;
        CaptureMouse();
    }

    private void DoRotate(Point p)
    {
        var el = _transformTarget;
        if (el == null) return;

        double angle = Math.Atan2(p.Y - _origCenter.Y, p.X - _origCenter.X) * 180 / Math.PI + 90;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            angle = Math.Round(angle / 15) * 15;
        }
        else
        {
            double nearest = Math.Round(angle / 45) * 45;     // "imã" nos ângulos retos e diagonais
            if (Math.Abs(angle - nearest) < 3) angle = nearest;
        }

        while (angle > 180) angle -= 360;
        while (angle <= -180) angle += 360;
        el.Rotation = Math.Round(angle, 1);
    }

    private void DoResize(Point p)
    {
        var el = _transformTarget;
        var o = _origState;
        if (el == null || o == null) return;

        var (hx, hy) = DirectionOf(_activeHandle);
        double rot = o.Rot * Math.PI / 180;

        // Mouse no sistema de eixos do elemento (origem no centro original).
        double dx = p.X - _origCenter.X, dy = p.Y - _origCenter.Y;
        double mx = dx * Math.Cos(-rot) - dy * Math.Sin(-rot);
        double my = dx * Math.Sin(-rot) + dy * Math.Cos(-rot);

        double w0 = o.W, h0 = o.H;
        double ax = -hx * w0 / 2, ay = -hy * h0 / 2;     // lado/canto oposto: fica parado

        double w1 = hx != 0 ? hx * (mx - ax) : w0;
        double h1 = hy != 0 ? hy * (my - ay) : h0;

        double minW = el is RulerElement ? 120 : Math.Min(12, w0);
        double minH = Math.Min(12, h0);
        w1 = Math.Max(minW, w1);
        h1 = Math.Max(minH, h1);

        bool corner = hx != 0 && hy != 0;
        bool textBox = el is TextElement;

        if (corner && w0 > 0 && h0 > 0 && (el.Resize == ResizeKind.Aspect || textBox || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))
        {
            double s = Math.Max(w1 / w0, h1 / h0);
            w1 = w0 * s;
            h1 = h0 * s;
        }

        // A letra acompanha o tamanho da caixa: puxar um canto aumenta/diminui o texto proporcionalmente
        // (inclusive os trechos com tamanho próprio, a partir do que eram antes de começar o arrasto).
        if (corner && w0 > 0 && h0 > 0 && o.Font > 0 && el is RichTextElement rich)
        {
            double scale = textBox ? w1 / w0 : Math.Sqrt(w1 * h1 / (w0 * h0));
            scale = Math.Max(8 / o.Font, Math.Min((textBox ? 240 : 96) / o.Font, scale));
            rich.RestoreContent(rich.Text, o.RunsJson);
            rich.FontSize = o.Font;
            rich.ScaleFonts(scale);
        }

        double ncx = hx != 0 ? ax + hx * w1 / 2 : 0;
        double ncy = hy != 0 ? ay + hy * h1 / 2 : 0;
        double wx = _origCenter.X + ncx * Math.Cos(rot) - ncy * Math.Sin(rot);
        double wy = _origCenter.Y + ncx * Math.Sin(rot) + ncy * Math.Cos(rot);

        el.Width = w1;
        if (!textBox) el.Height = h1;
        el.X = wx - w1 / 2;
        el.Y = wy - (textBox ? h1 : el.Height) / 2;   // texto: a altura real ainda vai ser calculada pelo layout
    }

    private void EndTransform(string description)
    {
        if (_dragBefore.Count > 0) CommitStates(description, _dragBefore);
        _dragBefore = new Dictionary<BoardElement, ElementState>();
        _transformTarget = null;
        _origState = null;
    }

    // ---- Seleção por retângulo ----

    private WpfRectangle NewBand(Brush stroke, double opacity) => new()
    {
        Stroke = stroke is SolidColorBrush ? Accent : stroke,
        StrokeThickness = 1.5 / Zoom,
        StrokeDashArray = new DoubleCollection { 4, 3 },
        Fill = stroke is SolidColorBrush sb && sb != Accent
            ? new SolidColorBrush(Color.FromArgb(120, sb.Color.R, sb.Color.G, sb.Color.B))
            : new SolidColorBrush(Color.FromArgb(30, 79, 107, 237)),
        Opacity = opacity
    };

    private static void PositionBand(WpfRectangle band, Point a, Point b)
    {
        Canvas.SetLeft(band, Math.Min(a.X, b.X));
        Canvas.SetTop(band, Math.Min(a.Y, b.Y));
        band.Width = Math.Abs(a.X - b.X);
        band.Height = Math.Abs(a.Y - b.Y);
    }

    private void UpdateMarquee(Point p)
    {
        if ((p - _start).Length < 3 / Zoom && _rubberBand == null) return;
        if (_rubberBand == null)
        {
            _rubberBand = NewBand(Accent, 1);
            _previewLayer.Children.Add(_rubberBand);
        }
        PositionBand(_rubberBand, _start, p);
    }

    private void EndMarquee(Point p)
    {
        _previewLayer.Children.Clear();
        _rubberBand = null;
        if (Board == null) return;

        var area = new Rect(_start, p);
        if (area.Width < 3 / Zoom && area.Height < 3 / Zoom) return;

        var inside = Board.Elements.Where(el =>
            _views.TryGetValue(el.Id, out var v) && v.WorldBounds().IntersectsWith(area)).ToList();

        bool additive = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        Board.SetSelection(additive ? Board.Selection.Union(inside) : inside);
    }

    // =====================================================================
    //  Ações de edição (usadas por atalhos e menus)
    // =====================================================================

    public bool HasSelection => Board?.Selection.Count > 0;

    public void SelectAll()
    {
        if (Board != null) Board.SetSelection(Board.Elements.ToList());
    }

    public void DeleteSelection()
    {
        if (Board == null || Board.Selection.Count == 0) return;
        Board.RemoveElements(Board.Selection.ToList(), "Excluir");
    }

    /// <summary>Move a seleção alguns pixels (setas do teclado).</summary>
    public void Nudge(double dx, double dy)
    {
        if (Board == null || Board.Selection.Count == 0) return;
        var set = BuildMoveSet();
        if (set.Count == 0) return;

        var before = set.ToDictionary(e => e, Capture);
        foreach (var el in set) { el.X += dx; el.Y += dy; }
        CommitStates("Mover", before);
    }

    /// <summary>Seleção mais o que vai junto: alfinetes presos e ligações entre alfinetes copiados.</summary>
    private List<BoardElement> ExpandForCopy()
    {
        var set = new List<BoardElement>();
        if (Board == null) return set;

        foreach (var el in Board.Selection) if (el is not ConnectorElement) set.Add(el);
        var carriers = set.Where(e => e is not PinElement).Select(e => e.Id).ToHashSet();
        foreach (var pin in Board.Elements.OfType<PinElement>())
            if (pin.AttachedToId is { } id && carriers.Contains(id) && !set.Contains(pin)) set.Add(pin);

        var pinIds = set.OfType<PinElement>().Select(p => p.Id).ToHashSet();
        foreach (var c in Board.Elements.OfType<ConnectorElement>())
            if (pinIds.Contains(c.FromId) && pinIds.Contains(c.ToId)) set.Add(c);

        // Mantém a ordem do quadro (as ligações dependem dos alfinetes virem antes).
        return Board.Elements.Where(set.Contains).ToList();
    }

    public void Copy()
    {
        var set = ExpandForCopy();
        if (set.Count == 0) return;

        try
        {
            var data = new DataObject();
            data.SetData(ClipboardFormat, ElementSerializer.Serialize(set));
            if (set.Count == 1 && set[0] is ImageElement image && ElementView.Decode(image.Data) is { } bitmap)
                data.SetImage(bitmap);
            else if (set.Count == 1 && set[0] is TextElement text)
                data.SetText(text.Text);
            Clipboard.SetDataObject(data, true);
        }
        catch (ExternalException) { /* área de transferência em uso por outro programa */ }
    }

    public void Cut()
    {
        Copy();
        DeleteSelection();
    }

    public void Paste()
    {
        if (Board == null) return;

        try
        {
            if (Clipboard.ContainsData(ClipboardFormat) && Clipboard.GetData(ClipboardFormat) is string json)
            {
                // Colar várias vezes o mesmo conteúdo escalona as cópias para não ficarem empilhadas.
                _pasteCount = json == _lastClipboardJson ? _pasteCount + 1 : 1;
                _lastClipboardJson = json;
                AddCopies(ElementSerializer.Deserialize(json), 24 * _pasteCount, "Colar");
            }
            else if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } bitmap)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var ms = new MemoryStream();
                encoder.Save(ms);
                InsertImage(ms.ToArray(), bitmap.Width, bitmap.Height, ViewCenter());
            }
            else if (Clipboard.ContainsText() && !string.IsNullOrWhiteSpace(Clipboard.GetText()))
            {
                var center = ViewCenter();
                var text = new TextElement
                {
                    Text = Clipboard.GetText().Trim(),
                    FontSize = Tool?.FontSize ?? 22,
                    FontFamily = Tool?.FontFamily ?? InkPalette.DefaultFont,
                    Color = Tool != null ? InkColor : "#1F2328",
                    Width = 320,
                    Height = 40,
                    X = center.X - 160,
                    Y = center.Y - 20
                };
                Board.AddElements(new[] { text }, "Colar texto");
                Board.SetSelection(new[] { text });
            }
        }
        catch (ExternalException) { /* área de transferência em uso por outro programa */ }
    }

    public void DuplicateSelection()
    {
        var set = ExpandForCopy();
        if (set.Count == 0) return;
        AddCopies(ElementSerializer.CloneSet(set), 24, "Duplicar");
    }

    private void AddCopies(List<BoardElement> copies, double offset, string description)
    {
        if (Board == null || copies.Count == 0) return;

        // Cópias novas (ids novos) para a colagem não competir com os originais.
        var fresh = ElementSerializer.CloneSet(copies);
        foreach (var el in fresh.Where(e => e is not ConnectorElement))
        {
            el.X += offset;
            el.Y += offset;
        }
        Board.AddElements(fresh, description);
        Board.SetSelection(fresh.Where(e => e is not ConnectorElement));
    }

    // =====================================================================
    //  Imagens
    // =====================================================================

    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp" };

    /// <summary>Centro da área visível, em coordenadas do quadro.</summary>
    private Point ViewCenter()
    {
        var inverse = _world.RenderTransform?.Inverse;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        return inverse != null ? inverse.Transform(center) : center;
    }

    public void InsertImageFromFile()
    {
        if (Board == null) return;

        var dialog = new OpenFileDialog
        {
            Title = "Inserir imagem",
            Filter = "Imagens|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp|Todos os arquivos|*.*"
        };
        if (dialog.ShowDialog() != true) return;
        InsertImageFile(dialog.FileName, ViewCenter());
    }

    private void InsertImageFile(string path, Point center)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            var bitmap = ElementView.Decode(data);
            if (bitmap == null)
            {
                MessageBox.Show("Não foi possível abrir essa imagem.", "Whiteboard Simulator", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            InsertImage(data, bitmap.Width, bitmap.Height, center);
        }
        catch (IOException ex)
        {
            MessageBox.Show($"Não foi possível ler o arquivo.\n\n{ex.Message}", "Whiteboard Simulator", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void InsertImage(byte[] data, double width, double height, Point center)
    {
        if (Board == null || width < 1 || height < 1) return;

        // Cabe em 60% da área visível, sem aumentar imagens pequenas.
        double maxW = Math.Max(160, ActualWidth / Zoom * 0.6), maxH = Math.Max(120, ActualHeight / Zoom * 0.6);
        double scale = Math.Min(1, Math.Min(maxW / width, maxH / height));

        var image = new ImageElement
        {
            Data = data,
            Width = width * scale,
            Height = height * scale
        };
        image.X = center.X - image.Width / 2;
        image.Y = center.Y - image.Height / 2;

        Board.AddElements(new[] { image }, "Inserir imagem");
        Board.SetSelection(new[] { image });
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        if (Board == null || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;

        var at = e.GetPosition(_world);
        int n = 0;
        foreach (var file in files.Where(f => ImageExtensions.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant())))
            InsertImageFile(file, new Point(at.X + 24 * n, at.Y + 24 * n++));
        e.Handled = true;
    }

    // =====================================================================
    //  Exportação: desenha o quadro sem interface e sem zoom/deslocamento
    // =====================================================================

    public void BeginExport()
    {
        CommitEdit();
        _exporting = true;
        _savedTransform = _world.RenderTransform;
        _world.RenderTransform = Transform.Identity;
        _selectionLayer.Visibility = Visibility.Collapsed;
        _previewLayer.Visibility = Visibility.Collapsed;
        _editorLayer.Visibility = Visibility.Collapsed;
    }

    public void EndExport()
    {
        _exporting = false;
        _world.RenderTransform = _savedTransform ?? Transform.Identity;
        _selectionLayer.Visibility = Visibility.Visible;
        _previewLayer.Visibility = Visibility.Visible;
        _editorLayer.Visibility = Visibility.Visible;
        UpdateTransform();
    }
}
