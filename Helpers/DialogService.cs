using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace WhiteboardSimulator.Helpers;

/// <summary>Diálogos de arquivo e de confirmação usados pelo MainViewModel.</summary>
public sealed class DialogService
{
    private const string Caption = "Whiteboard Simulator";

    public const string OpenFilter =
        "Quadros do Whiteboard Simulator (*.wboard;*.wbpack)|*.wboard;*.wbpack|Quadro (*.wboard)|*.wboard|Pacote compartilhável (*.wbpack)|*.wbpack";

    private static string DefaultFolder => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public string? PickOpenPath()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Abrir quadro",
            Filter = OpenFilter,
            InitialDirectory = DefaultFolder
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickSavePath(string title, string filter, string extension, string suggestedName, string? folder = null)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            DefaultExt = extension,
            AddExtension = true,
            FileName = suggestedName,
            InitialDirectory = folder != null && Directory.Exists(folder) ? folder : DefaultFolder
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <summary>true = salvar, false = descartar, null = cancelar.</summary>
    public bool? AskSaveChanges(string projectName)
    {
        var result = MessageBox.Show(
            $"O quadro \"{projectName}\" tem alterações não salvas.\n\nDeseja salvar antes de continuar?",
            Caption, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return result switch
        {
            MessageBoxResult.Yes => true,
            MessageBoxResult.No => false,
            _ => null
        };
    }

    public bool Confirm(string message) =>
        MessageBox.Show(message, Caption, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void ShowError(string message) =>
        MessageBox.Show(message, Caption, MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>Pede um texto ao usuário (ex.: novo nome). null = cancelado.</summary>
    public string? PromptText(string title, string prompt, string initial)
    {
        var dialog = new InputDialog(title, prompt, initial) { Owner = Application.Current.MainWindow };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }
}
