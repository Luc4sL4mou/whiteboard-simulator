namespace WhiteboardSimulator.Helpers;

/// <summary>
/// Mudança de formatação pedida pela barra de opções. Campos nulos = não mexe naquilo.
/// Vale para o trecho selecionado no editor ou, fora da edição, para o texto/post-it inteiro.
/// </summary>
public sealed record TextFormatChange(
    string? FontFamily = null,
    double? SizeFactor = null,   // multiplicador do tamanho atual (ex.: 1,15 aumenta, 0,87 diminui)
    string? Color = null,        // #RRGGBB
    bool? Bold = null,
    bool? Italic = null,
    bool? Underline = null);

/// <summary>Formatação do trecho selecionado (para mostrar B / I / U ligados na barra).</summary>
public sealed record TextFormatState(
    string? FontFamily,
    double? FontSize,
    bool Bold,
    bool Italic,
    bool Underline,
    string? Color);
