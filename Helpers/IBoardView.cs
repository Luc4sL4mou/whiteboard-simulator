using System.Windows.Media.Imaging;

namespace WhiteboardSimulator.Helpers;

/// <summary>O que o ViewModel precisa da tela para exportar e imprimir o quadro.</summary>
public interface IBoardView
{
    /// <summary>Desenha o quadro como imagem (sem os elementos de interface, como a pílula de status).</summary>
    /// <param name="includeFrame">true = com moldura; false = só a superfície.</param>
    /// <param name="scale">Multiplicador de resolução (2 = o dobro da tela).</param>
    /// <param name="opaque">true = cantos arredondados preenchidos de branco (necessário para JPEG/PDF/impressão).</param>
    BitmapSource RenderBoard(bool includeFrame, double scale, bool opaque);

    /// <summary>Há um texto ou post-it sendo editado (a formatação vale só para o trecho selecionado).</summary>
    bool IsEditingText { get; }

    /// <summary>Aplica a formatação ao trecho selecionado no editor.</summary>
    void FormatEditor(TextFormatChange change);

    /// <summary>Fecha a edição em andamento, gravando o texto no quadro (antes de salvar, exportar...).</summary>
    void CommitPendingEdit();
}
