namespace WhiteboardSimulator.Helpers;

/// <summary>Cores e tamanhos oferecidos pelas ferramentas.</summary>
public static class InkPalette
{
    /// <summary>"Automático": preto no quadro branco, branco (giz) nas lousas e no veludo.</summary>
    public const string Auto = "auto";

    public static readonly (string Name, string Hex)[] Inks =
    {
        ("Automático", Auto),
        ("Preto",    "#1F2328"),
        ("Branco",   "#F5F5F0"),
        ("Vermelho", "#E53935"),
        ("Laranja",  "#FB8C00"),
        ("Amarelo",  "#FDD835"),
        ("Verde",    "#43A047"),
        ("Azul",     "#1E88E5"),
        ("Roxo",     "#8E24AA"),
    };

    public static readonly (string Name, string Hex)[] StickyColors =
    {
        ("Amarelo", "#FFE66D"),
        ("Rosa",    "#FF9EC4"),
        ("Azul",    "#8FD3FF"),
        ("Verde",   "#B6F08A"),
        ("Laranja", "#FFB86B"),
    };

    public static readonly (string Name, string Hex)[] PinColors =
    {
        ("Vermelho", "#D32F2F"),
        ("Azul",     "#1E88E5"),
        ("Verde",    "#2E9E4F"),
        ("Amarelo",  "#F6B800"),
    };

    public static readonly (string Name, double Value)[] Thicknesses =
    {
        ("Fino", 2), ("Médio", 4), ("Grosso", 8), ("Extra grosso", 14),
    };

    public static readonly (string Name, double Value)[] FontSizes =
    {
        ("Pequeno", 16), ("Médio", 22), ("Grande", 32), ("Enorme", 48),
    };

    public const string DefaultFont = "Segoe UI";
    public const string DefaultStickyFont = "Segoe Print";

    /// <summary>As 15 fontes oferecidas (todas já vêm com o Windows).</summary>
    public static readonly string[] Fonts =
    {
        "Segoe UI", "Arial", "Calibri", "Cambria", "Candara",
        "Comic Sans MS", "Consolas", "Georgia", "Impact", "Ink Free",
        "Segoe Print", "Segoe Script", "Times New Roman", "Trebuchet MS", "Verdana"
    };

    public const string DefaultConnectorColor = "#D32F2F";

    public static string ResolveInk(string hex, bool darkSurface) =>
        hex == Auto ? (darkSurface ? "#F5F5F0" : "#1F2328") : hex;
}
