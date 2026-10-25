using WhiteboardSimulator.Helpers;
using WhiteboardSimulator.Models;

namespace WhiteboardSimulator.ViewModels;

/// <summary>Ferramenta ativa e suas configurações (cor, espessura...). Compartilhado entre os quadros.</summary>
public sealed class ToolState : ViewModelBase
{
    private ToolType _tool = ToolType.Pen;
    private string _color = InkPalette.Auto;
    private double _thickness = 4;
    private string _stickyColor = InkPalette.StickyColors[0].Hex;
    private string _pinColor = InkPalette.PinColors[0].Hex;
    private double _fontSize = 22;
    private string _fontFamily = InkPalette.DefaultFont;

    public ToolType Tool { get => _tool; set => Set(ref _tool, value); }

    /// <summary>Cor da tinta; "auto" acompanha a superfície (preto no branco, branco nas lousas).</summary>
    public string Color { get => _color; set => Set(ref _color, value); }

    public const double MinThickness = 1, MaxThickness = 60;

    /// <summary>Espessura do traço (caneta e formas), em unidades do quadro. Ajustada com + e - ou pela barra de opções.</summary>
    public double Thickness
    {
        get => _thickness;
        set => Set(ref _thickness, Math.Max(MinThickness, Math.Min(MaxThickness, Math.Round(value, 1))));
    }
    public string StickyColor { get => _stickyColor; set => Set(ref _stickyColor, value); }
    public string PinColor { get => _pinColor; set => Set(ref _pinColor, value); }
    public double FontSize { get => _fontSize; set => Set(ref _fontSize, value); }
    public string FontFamily { get => _fontFamily; set => Set(ref _fontFamily, value); }

    // Padrão do próximo texto (Texto tool): negrito, itálico e sublinhado.
    private bool _bold, _italic, _underline;
    public bool Bold { get => _bold; set => Set(ref _bold, value); }
    public bool Italic { get => _italic; set => Set(ref _italic, value); }
    public bool Underline { get => _underline; set => Set(ref _underline, value); }

    public const double MinEraserSize = 4, MaxEraserSize = 120;
    private double _eraserSize = 14;

    /// <summary>Raio da borracha, em pixels da tela (não muda com o zoom). Ajustado com + e - ou pela barra de opções.</summary>
    public double EraserSize
    {
        get => _eraserSize;
        set => Set(ref _eraserSize, Math.Max(MinEraserSize, Math.Min(MaxEraserSize, Math.Round(value))));
    }

    /// <summary>Cor real (hex) para a superfície informada.</summary>
    public string GetColor(BoardSurface surface) =>
        InkPalette.ResolveInk(_color, surface != BoardSurface.Whiteboard);
}
