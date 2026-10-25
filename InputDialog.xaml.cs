using System.Windows;

namespace WhiteboardSimulator;

/// <summary>Janela simples para pedir um texto (usada em "Renomear").</summary>
public partial class InputDialog : Window
{
    public InputDialog(string title, string prompt, string initial)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        Input.Text = initial;
        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };
    }

    public string Value => Input.Text.Trim();

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (Value.Length == 0) return;
        DialogResult = true;
    }
}
