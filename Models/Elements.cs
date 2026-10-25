using System.Text.Json;
using System.Text.Json.Serialization;
using WhiteboardSimulator.ViewModels;

namespace WhiteboardSimulator.Models;

/// <summary>Ponto serializável (sem depender de tipos do WPF).</summary>
public readonly record struct PointD(double X, double Y);

/// <summary>Free = todas as alças; Aspect = só cantos, mantendo a proporção; HorizontalOnly = só laterais;
/// Text = cantos (a letra cresce junto) e laterais (só muda a quebra de linha).</summary>
public enum ResizeKind { None, Free, Aspect, HorizontalOnly, Text }

public enum ShapeKind { Line, Rectangle, Ellipse }

/// <summary>
/// Base de tudo que existe no quadro. Cada elemento é um objeto com posição, tamanho e rotação
/// (o que facilita seleção, cópia, recorte e desfazer). A caixa (X, Y, Width, Height) é o retângulo
/// não rotacionado; Rotation gira essa caixa em torno do seu centro, em graus.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(StrokeElement), "stroke")]
[JsonDerivedType(typeof(ShapeElement), "shape")]
[JsonDerivedType(typeof(TextElement), "text")]
[JsonDerivedType(typeof(StickyNoteElement), "sticky")]
[JsonDerivedType(typeof(PinElement), "pin")]
[JsonDerivedType(typeof(ConnectorElement), "connector")]
[JsonDerivedType(typeof(ImageElement), "image")]
[JsonDerivedType(typeof(RulerElement), "ruler")]
public abstract class BoardElement : ViewModelBase
{
    private double _x, _y, _width, _height, _rotation;
    private bool _isSelected;

    public Guid Id { get; set; } = Guid.NewGuid();

    public double X { get => _x; set => Set(ref _x, value); }
    public double Y { get => _y; set => Set(ref _y, value); }
    public double Width { get => _width; set => Set(ref _width, value); }
    public double Height { get => _height; set => Set(ref _height, value); }
    public double Rotation { get => _rotation; set => Set(ref _rotation, value); }

    [JsonIgnore]
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    /// <summary>Como as alças de redimensionar se comportam.</summary>
    [JsonIgnore] public virtual ResizeKind Resize => ResizeKind.Free;

    [JsonIgnore] public virtual bool CanRotate => true;
}

/// <summary>Traço da caneta. Os pontos são relativos ao canto da caixa e acompanham o redimensionamento.</summary>
public sealed class StrokeElement : BoardElement
{
    private string _color = "#1F2328";
    private double _thickness = 4;

    public List<PointD> Points { get; set; } = new();

    /// <summary>Tamanho da caixa quando o traço foi desenhado (base para escalar os pontos).</summary>
    public double BaseWidth { get; set; }
    public double BaseHeight { get; set; }

    public string Color { get => _color; set => Set(ref _color, value); }
    public double Thickness { get => _thickness; set => Set(ref _thickness, value); }
}

/// <summary>Linha, quadrado ou círculo (só contorno).</summary>
public sealed class ShapeElement : BoardElement
{
    private string _color = "#1F2328";
    private double _thickness = 4;
    private bool _flipY;
    private ShapeKind _kind;

    public ShapeKind Kind { get => _kind; set => Set(ref _kind, value); }
    public string Color { get => _color; set => Set(ref _color, value); }
    public double Thickness { get => _thickness; set => Set(ref _thickness, value); }

    /// <summary>Só para linhas: true = de baixo-esquerda para cima-direita.</summary>
    public bool FlipY { get => _flipY; set => Set(ref _flipY, value); }
}

/// <summary>
/// Trecho de texto com formatação própria. Campos nulos = "usa o padrão do elemento".
/// O texto pode ter várias linhas (separadas por '\n').
/// </summary>
public sealed class TextRun
{
    public string Text { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? FontFamily { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? FontSize { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Color { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Bold { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Italic { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Underline { get; set; }

    [JsonIgnore]
    public bool HasOverrides => FontFamily != null || FontSize != null || Color != null || Bold != null || Italic != null || Underline != null;

    public TextRun Clone() => (TextRun)MemberwiseClone();

    public bool SameFormat(TextRun o) =>
        FontFamily == o.FontFamily && FontSize == o.FontSize && Color == o.Color &&
        Bold == o.Bold && Italic == o.Italic && Underline == o.Underline;
}

/// <summary>
/// Base de texto formatado (texto livre e post-it): um padrão para o elemento inteiro (fonte, tamanho, cor,
/// negrito, itálico, sublinhado) e, opcionalmente, trechos com formatação diferente (Runs).
/// </summary>
public abstract class RichTextElement : BoardElement
{
    private string _text = "";
    private List<TextRun>? _runs;
    private string _fontFamily;
    private double _fontSize;
    private bool _bold, _italic, _underline;

    protected RichTextElement(string fontFamily, double fontSize)
    {
        _fontFamily = fontFamily;
        _fontSize = fontSize;
    }

    /// <summary>Texto puro (sempre em dia com Runs).</summary>
    public string Text { get => _text; set => Set(ref _text, value); }

    /// <summary>Trechos formatados; null = o texto todo usa o padrão do elemento.</summary>
    public List<TextRun>? Runs { get => _runs; set => Set(ref _runs, value); }

    public string FontFamily { get => _fontFamily; set => Set(ref _fontFamily, value); }
    public double FontSize { get => _fontSize; set => Set(ref _fontSize, value); }
    public bool Bold { get => _bold; set => Set(ref _bold, value); }
    public bool Italic { get => _italic; set => Set(ref _italic, value); }
    public bool Underline { get => _underline; set => Set(ref _underline, value); }

    /// <summary>Cor padrão da letra (texto livre: Color; post-it: TextColor).</summary>
    [JsonIgnore] public abstract string DefaultColor { get; set; }

    /// <summary>Trechos prontos para desenhar (um só, sem formatação própria, quando não há Runs).</summary>
    [JsonIgnore]
    public IReadOnlyList<TextRun> EffectiveRuns =>
        _runs ?? (_text.Length == 0 ? new List<TextRun>() : new List<TextRun> { new() { Text = _text } });

    /// <summary>Troca o conteúdo: junta trechos vizinhos iguais e descarta o que não tem formatação própria.</summary>
    public void SetRuns(IEnumerable<TextRun> runs)
    {
        var merged = new List<TextRun>();
        foreach (var run in runs)
        {
            if (run.Text.Length == 0) continue;
            if (merged.Count > 0 && merged[^1].SameFormat(run)) merged[^1].Text += run.Text;
            else merged.Add(run.Clone());
        }

        Text = string.Concat(merged.Select(r => r.Text));
        Runs = merged.Any(r => r.HasOverrides) ? merged : null;
    }

    /// <summary>Aplica uma alteração a todos os trechos (cria cópias para o elemento perceber a mudança).</summary>
    public void MapRuns(Action<TextRun> change)
    {
        if (_runs == null) return;
        var copy = _runs.Select(r => r.Clone()).ToList();
        foreach (var run in copy) change(run);
        SetRuns(copy);
    }

    /// <summary>Muda o tamanho da letra do elemento todo (padrão e trechos), mantendo as proporções.</summary>
    public void ScaleFonts(double factor)
    {
        FontSize = Math.Max(6, Math.Min(300, Math.Round(_fontSize * factor, 1)));
        MapRuns(r => { if (r.FontSize is { } s) r.FontSize = Math.Max(6, Math.Min(300, Math.Round(s * factor, 1))); });
    }

    // ---- Instantâneo do conteúdo (para desfazer/refazer) ----

    private static readonly JsonSerializerOptions SnapshotOptions = new();

    public string? SnapshotRuns() => _runs == null ? null : JsonSerializer.Serialize(_runs, SnapshotOptions);

    public void RestoreContent(string text, string? runsJson)
    {
        Runs = runsJson == null ? null : JsonSerializer.Deserialize<List<TextRun>>(runsJson, SnapshotOptions);
        Text = text;
    }
}

/// <summary>Texto livre. A largura quebra as linhas; a altura acompanha o texto.</summary>
public sealed class TextElement : RichTextElement
{
    private string _color = "#1F2328";

    public TextElement() : base("Segoe UI", 22) { }

    public string Color { get => _color; set => Set(ref _color, value); }

    [JsonIgnore] public override string DefaultColor { get => _color; set => Color = value; }
    [JsonIgnore] public override ResizeKind Resize => ResizeKind.Text;
}

/// <summary>Post-it editável.</summary>
public sealed class StickyNoteElement : RichTextElement
{
    private string _color = "#FFE66D";
    private string _textColor = "#3B3320";

    public StickyNoteElement() : base("Segoe Print", 16) { }

    /// <summary>Cor do papel.</summary>
    public string Color { get => _color; set => Set(ref _color, value); }

    /// <summary>Cor padrão da letra.</summary>
    public string TextColor { get => _textColor; set => Set(ref _textColor, value); }

    [JsonIgnore] public override string DefaultColor { get => _textColor; set => TextColor = value; }
}

/// <summary>Alfinete. Pode estar preso a outro elemento (e se mover junto com ele).</summary>
public sealed class PinElement : BoardElement
{
    public const double Size = 26;

    private string _color = "#D32F2F";

    public string Color { get => _color; set => Set(ref _color, value); }

    /// <summary>Elemento em que o alfinete está espetado (null = solto no quadro).</summary>
    public Guid? AttachedToId { get; set; }

    [JsonIgnore] public override ResizeKind Resize => ResizeKind.None;
    [JsonIgnore] public override bool CanRotate => false;
}

/// <summary>Linha vermelha ligando dois alfinetes. Acompanha os alfinetes quando eles se movem.</summary>
public sealed class ConnectorElement : BoardElement
{
    private string _color = "#D32F2F";
    private double _thickness = 3;

    public Guid FromId { get; set; }
    public Guid ToId { get; set; }
    public string Color { get => _color; set => Set(ref _color, value); }
    public double Thickness { get => _thickness; set => Set(ref _thickness, value); }

    [JsonIgnore] public override ResizeKind Resize => ResizeKind.None;
    [JsonIgnore] public override bool CanRotate => false;
}

/// <summary>Imagem colada ou inserida. Os bytes ficam no próprio arquivo do quadro.</summary>
public sealed class ImageElement : BoardElement
{
    /// <summary>Arquivo de imagem original (PNG, JPG...). Vai como base64 no JSON.</summary>
    public byte[] Data { get; set; } = Array.Empty<byte>();

    [JsonIgnore] public override ResizeKind Resize => ResizeKind.Aspect;
}

/// <summary>Régua: pode ser movida e girada; traços e linhas desenhados junto à borda dela ficam retos.</summary>
public sealed class RulerElement : BoardElement
{
    public const double Thickness = 56;

    [JsonIgnore] public override ResizeKind Resize => ResizeKind.HorizontalOnly;
}
