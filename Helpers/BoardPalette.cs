namespace WhiteboardSimulator.Helpers;

/// <summary>Cores disponíveis para o quadro branco (nome + hex).</summary>
public static class BoardPalette
{
    public const string DefaultTint = "Branco suave";

    public static readonly IReadOnlyList<(string Name, string Hex)> Tints = new[]
    {
        ("Branco suave", "#F5F3EE"),
        ("Branco",       "#FFFFFF"),
        ("Creme",        "#FBF1DC"),
        ("Cinza claro",  "#E9EAEC"),
        ("Azul gelo",    "#E3EEF8"),
        ("Verde menta",  "#E2F1E7"),
        ("Rosa pálido",  "#F8E6EA"),
    };
}
