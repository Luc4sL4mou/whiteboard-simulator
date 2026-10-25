namespace WhiteboardSimulator.Models;

/// <summary>
/// Conteúdo de um arquivo de quadro (.wboard): aparência e todos os elementos do quadro.
/// Versão 1 (só aparência) continua abrindo normalmente: Elements fica vazio.
/// </summary>
public sealed class BoardDocument
{
    public int FormatVersion { get; set; } = 2;
    public BoardFrame Frame { get; set; } = BoardFrame.Wood;
    public BoardSurface Surface { get; set; } = BoardSurface.Whiteboard;

    /// <summary>Nome da cor do quadro branco (ver BoardPalette).</summary>
    public string Tint { get; set; } = "Branco suave";

    public List<BoardElement> Elements { get; set; } = new();
}
