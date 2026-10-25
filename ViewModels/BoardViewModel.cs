using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;
using WhiteboardSimulator.Helpers;
using WhiteboardSimulator.Models;

namespace WhiteboardSimulator.ViewModels;

/// <summary>Um quadro aberto (uma aba): aparência, elementos, seleção, histórico de desfazer e zoom.</summary>
public sealed class BoardViewModel : ViewModelBase
{
    public const string UntitledName = "Quadro sem título";
    public const double MinZoom = 0.25, MaxZoom = 4.0;

    private string _name;
    private string? _filePath;
    private bool _forceDirty;
    private bool _isActive;
    private double _zoom = 1.0;
    private double _panX, _panY;

    private BoardFrame _frame;
    private BoardSurface _surface;
    private string _tintName;

    private readonly List<BoardElement> _selection = new();

    public BoardViewModel(string name, BoardDocument document, Guid? id = null)
    {
        _name = name;
        _frame = document.Frame;
        _surface = document.Surface;
        _tintName = document.Tint;
        Id = id ?? Guid.NewGuid();

        foreach (var element in document.Elements) Elements.Add(element);
        Elements.CollectionChanged += OnElementsChanged;
        Undo.Changed += (_, _) => NotifyDirty();
    }

    public Guid Id { get; }
    public UndoManager Undo { get; } = new();

    /// <summary>Disparado quando moldura, superfície ou cor mudam (inclusive por desfazer/refazer).</summary>
    public event EventHandler? AppearanceChanged;

    // ---- Projeto ----

    public string Name
    {
        get => _name;
        set { if (Set(ref _name, value)) NotifyDirty(); }
    }

    public string? FilePath
    {
        get => _filePath;
        set => Set(ref _filePath, value);
    }

    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    /// <summary>Tem alterações não salvas (ou nunca foi salvo depois de recuperado/duplicado).</summary>
    public bool IsDirty => _forceDirty || !Undo.IsAtSavedState;

    public string TabTitle => IsDirty ? $"● {Name}" : Name;
    public string WindowTitle => $"{(IsDirty ? "● " : "")}{Name} — Whiteboard Simulator";

    public void MarkDirty()
    {
        _forceDirty = true;
        NotifyDirty();
    }

    public void MarkSaved()
    {
        _forceDirty = false;
        Undo.MarkSaved();
        NotifyDirty();
    }

    private void NotifyDirty()
    {
        Notify(nameof(IsDirty));
        Notify(nameof(TabTitle));
        Notify(nameof(WindowTitle));
    }

    /// <summary>Documento para gravar em disco (usa os mesmos objetos; serialize logo em seguida).</summary>
    public BoardDocument ToDocument() => new()
    {
        Frame = _frame,
        Surface = _surface,
        Tint = _tintName,
        Elements = Elements.ToList()
    };

    /// <summary>Cópia independente do quadro (usada em "Duplicar quadro").</summary>
    public BoardDocument CloneDocument()
    {
        var doc = ToDocument();
        doc.Elements = ElementSerializer.CloneSet(Elements);
        return doc;
    }

    // ---- Elementos do quadro ----

    public ObservableCollection<BoardElement> Elements { get; } = new();

    public bool IsEmpty => Elements.Count == 0;

    private void OnElementsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Notify(nameof(IsEmpty));

        // Elementos que saíram do quadro (inclusive por desfazer) saem da seleção.
        var gone = _selection.Where(s => !Elements.Contains(s)).ToList();
        if (gone.Count > 0)
        {
            foreach (var g in gone) { g.IsSelected = false; _selection.Remove(g); }
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Adiciona elementos (um passo no histórico de desfazer).</summary>
    public void AddElements(IReadOnlyList<BoardElement> items, string description)
    {
        if (items.Count == 0) return;
        var list = items.ToList();
        Undo.Execute(new UndoableAction(description,
            () => { foreach (var el in list) if (!Elements.Contains(el)) Elements.Add(el); },
            () => { foreach (var el in list) Elements.Remove(el); }));
    }

    /// <summary>Remove elementos e as ligações que dependem deles; desfazer devolve tudo na mesma ordem.</summary>
    public void RemoveElements(IEnumerable<BoardElement> items, string description)
    {
        var set = new HashSet<BoardElement>(items.Where(Elements.Contains));
        var pinIds = set.OfType<PinElement>().Select(p => p.Id).ToHashSet();
        foreach (var c in Elements.OfType<ConnectorElement>())
            if (pinIds.Contains(c.FromId) || pinIds.Contains(c.ToId)) set.Add(c);
        if (set.Count == 0) return;

        var removed = Elements.Select((el, index) => (el, index)).Where(t => set.Contains(t.el)).ToList();
        Undo.Execute(new UndoableAction(description,
            () => { foreach (var (el, _) in removed) Elements.Remove(el); },
            () => { foreach (var (el, index) in removed) Elements.Insert(Math.Min(index, Elements.Count), el); }));
    }

    /// <summary>Registra uma mudança que já foi aplicada (mover, redimensionar, editar texto...).</summary>
    public void PushChange(string description, Action undo, Action redo) =>
        Undo.Execute(new UndoableAction(description, redo, undo));

    public BoardElement? Find(Guid id) => Elements.FirstOrDefault(e => e.Id == id);

    // ---- Seleção ----

    public IReadOnlyList<BoardElement> Selection => _selection;

    public event EventHandler? SelectionChanged;

    public void SetSelection(IEnumerable<BoardElement> items)
    {
        var next = items.Where(Elements.Contains).Distinct().ToList();
        if (next.SequenceEqual(_selection)) return;

        foreach (var el in _selection) el.IsSelected = false;
        _selection.Clear();
        _selection.AddRange(next);
        foreach (var el in _selection) el.IsSelected = true;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearSelection() => SetSelection(Array.Empty<BoardElement>());

    /// <summary>Aplica cor, espessura, tamanho de fonte etc. aos elementos selecionados (um passo de desfazer).</summary>
    public void ApplyStyleToSelection(string? ink, double? thickness, string? noteColor, string? pinColor,
                                      double? fontSize = null, string? fontFamily = null)
    {
        var undo = new List<Action>();
        var redo = new List<Action>();

        void Track<T>(Func<T> get, Action<T> set, T value)
        {
            var old = get();
            if (EqualityComparer<T>.Default.Equals(old, value)) return;
            undo.Add(() => set(old));
            redo.Add(() => set(value));
        }

        foreach (var el in _selection)
        {
            switch (el)
            {
                case StrokeElement s:
                    if (ink != null) Track(() => s.Color, v => s.Color = v, ink);
                    if (thickness != null) Track(() => s.Thickness, v => s.Thickness = v, thickness.Value);
                    break;
                case ShapeElement s:
                    if (ink != null) Track(() => s.Color, v => s.Color = v, ink);
                    if (thickness != null) Track(() => s.Thickness, v => s.Thickness = v, thickness.Value);
                    break;
                case StickyNoteElement n:
                    if (noteColor != null) Track(() => n.Color, v => n.Color = v, noteColor);   // cor do papel
                    break;
                case PinElement p:
                    if (pinColor != null) Track(() => p.Color, v => p.Color = v, pinColor);
                    break;
            }
        }

        if (redo.Count == 0) return;
        PushChange("Alterar estilo", () => undo.ForEach(a => a()), () => redo.ForEach(a => a()));
    }

    // ---- Texto: formatação do texto/post-it inteiro (a de trechos é feita no editor) ----

    private sealed record TextState(string Family, double Size, bool Bold, bool Italic, bool Underline, string Color, string Text, string? Runs);

    private static TextState Snapshot(RichTextElement e) =>
        new(e.FontFamily, e.FontSize, e.Bold, e.Italic, e.Underline, e.DefaultColor, e.Text, e.SnapshotRuns());

    private static void Restore(RichTextElement e, TextState s)
    {
        e.FontFamily = s.Family; e.FontSize = s.Size; e.Bold = s.Bold; e.Italic = s.Italic; e.Underline = s.Underline;
        e.DefaultColor = s.Color;
        e.RestoreContent(s.Text, s.Runs);
    }

    /// <summary>
    /// Formata o texto/post-it selecionado inteiro. O padrão do elemento muda e os trechos deixam de
    /// ter formatação própria naquilo (o elemento todo fica igual). Um passo de desfazer.
    /// </summary>
    public void ApplyTextFormat(TextFormatChange change)
    {
        var items = _selection.OfType<RichTextElement>().ToList();
        if (items.Count == 0) return;

        var before = items.ToDictionary(e => e, Snapshot);
        foreach (var el in items)
        {
            if (change.FontFamily is { } family)
            {
                el.FontFamily = family;
                el.MapRuns(r => r.FontFamily = null);
            }
            if (change.SizeFactor is { } factor) el.ScaleFonts(factor);
            if (change.Color is { } color)
            {
                el.DefaultColor = color;
                el.MapRuns(r => r.Color = null);
            }
            if (change.Bold is { } bold) { el.Bold = bold; el.MapRuns(r => r.Bold = null); }
            if (change.Italic is { } italic) { el.Italic = italic; el.MapRuns(r => r.Italic = null); }
            if (change.Underline is { } underline) { el.Underline = underline; el.MapRuns(r => r.Underline = null); }
        }

        var after = items.ToDictionary(e => e, Snapshot);
        if (before.All(kv => kv.Value == after[kv.Key])) return;

        PushChange("Formatar texto",
            () => { foreach (var (el, s) in before) Restore(el, s); },
            () => { foreach (var (el, s) in after) Restore(el, s); });
    }

    // ---- Camadas: qual elemento fica na frente ----

    /// <summary>Elementos que podem mudar de camada (alfinetes, ligações e régua têm camada própria, sempre por cima).</summary>
    public static bool HasLayer(BoardElement e) => e is not (PinElement or ConnectorElement or RulerElement);

    public bool CanReorderSelection => _selection.Any(HasLayer);

    /// <summary>Leva a seleção para a frente de tudo (true) ou para trás de tudo (false). Um passo de desfazer.</summary>
    public void ChangeOrder(bool toFront)
    {
        var moving = _selection.Where(HasLayer).ToList();
        if (moving.Count == 0) return;

        var oldOrder = Elements.ToList();
        var selected = oldOrder.Where(moving.Contains).ToList();
        var others = oldOrder.Where(e => !moving.Contains(e)).ToList();
        var newOrder = toFront ? others.Concat(selected).ToList() : selected.Concat(others).ToList();
        if (newOrder.SequenceEqual(oldOrder)) return;

        Undo.Execute(new UndoableAction(toFront ? "Trazer para frente" : "Enviar para trás",
            () => ApplyOrder(newOrder),
            () => ApplyOrder(oldOrder)));
    }

    private void ApplyOrder(List<BoardElement> order)
    {
        for (int i = 0; i < order.Count; i++)
        {
            int at = Elements.IndexOf(order[i]);
            if (at >= 0 && at != i) Elements.Move(at, i);
        }
    }

    // ---- Régua: horizontal ou vertical ----

    public static bool IsVertical(RulerElement r)
    {
        double a = ((r.Rotation % 180) + 180) % 180;
        return a > 45 && a < 135;
    }

    /// <summary>Deita ou levanta a régua (gira em torno do centro). Um passo de desfazer.</summary>
    public void SetRulerOrientation(IEnumerable<RulerElement> rulers, bool vertical)
    {
        var list = rulers.Where(r => IsVertical(r) != vertical).ToList();
        if (list.Count == 0) return;

        var before = list.ToDictionary(r => r, r => r.Rotation);
        double target = vertical ? 90 : 0;
        PushChange(vertical ? "Régua na vertical" : "Régua na horizontal",
            () => { foreach (var (r, rot) in before) r.Rotation = rot; },
            () => { foreach (var r in list) r.Rotation = target; });
    }

    public void ToggleRulerOrientation(RulerElement ruler) => SetRulerOrientation(new[] { ruler }, !IsVertical(ruler));

    // ---- Aparência (cada mudança entra no histórico de desfazer) ----

    public BoardFrame Frame => _frame;
    public BoardSurface Surface => _surface;
    public string TintName => _tintName;

    public void ChangeFrame(BoardFrame frame)
    {
        if (frame == _frame) return;
        var old = _frame;
        Undo.Execute(new UndoableAction("Mudar moldura",
            () => { _frame = frame; NotifyAppearance(); },
            () => { _frame = old; NotifyAppearance(); }));
    }

    public void ChangeSurface(BoardSurface surface)
    {
        if (surface == _surface) return;
        var old = _surface;
        Undo.Execute(new UndoableAction("Mudar superfície",
            () => { _surface = surface; NotifyAppearance(); },
            () => { _surface = old; NotifyAppearance(); }));
    }

    public void ChangeTint(string tintName)
    {
        if (tintName == _tintName) return;
        var old = _tintName;
        Undo.Execute(new UndoableAction("Mudar cor do quadro",
            () => { _tintName = tintName; NotifyAppearance(); },
            () => { _tintName = old; NotifyAppearance(); }));
    }

    private Brush TintBrush
    {
        get
        {
            var tint = BoardPalette.Tints.FirstOrDefault(t => t.Name == _tintName);
            var hex = tint.Hex ?? BoardPalette.Tints[0].Hex;
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }
    }

    public Brush FrameBrush => BoardBrushes.Frame(_frame);
    public Brush SurfaceBrush => BoardBrushes.Surface(_surface, TintBrush);
    public Brush CapBrush => new SolidColorBrush(BoardBrushes.CapColor(_frame));
    public Thickness FrameThickness => new(_frame == BoardFrame.Wood ? 14 : 13);
    public double CapSize => _frame == BoardFrame.Wood ? 24 : 22;

    public bool IsWhiteboard => _surface == BoardSurface.Whiteboard;
    public bool IsChalkboard => _surface is BoardSurface.BlackSlate or BoardSurface.GreenSlate;
    public double VignetteOpacity => IsWhiteboard ? 0 : _surface == BoardSurface.GreenFelt ? 0.8 : 1;
    public Brush HintBrush => IsWhiteboard ? Hint(0x8A8E96) : Hint(0xDDE4E0);

    private static Brush Hint(int rgb)
    {
        var b = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        b.Freeze();
        return b;
    }

    private void NotifyAppearance()
    {
        Notify(nameof(Frame));
        Notify(nameof(Surface));
        Notify(nameof(TintName));
        Notify(nameof(FrameBrush));
        Notify(nameof(SurfaceBrush));
        Notify(nameof(CapBrush));
        Notify(nameof(FrameThickness));
        Notify(nameof(CapSize));
        Notify(nameof(IsWhiteboard));
        Notify(nameof(IsChalkboard));
        Notify(nameof(VignetteOpacity));
        Notify(nameof(HintBrush));
        AppearanceChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- Visualização: zoom e deslocamento (por quadro; não vão para o arquivo) ----

    public double Zoom
    {
        get => _zoom;
        set
        {
            var clamped = Math.Max(MinZoom, Math.Min(MaxZoom, value));
            if (Set(ref _zoom, clamped)) Notify(nameof(ZoomText));
        }
    }

    public double PanX { get => _panX; set => Set(ref _panX, value); }
    public double PanY { get => _panY; set => Set(ref _panY, value); }

    public string ZoomText => $"{Math.Round(_zoom * 100)}%";

    public void ResetView()
    {
        Zoom = 1.0;
        PanX = 0;
        PanY = 0;
    }
}
