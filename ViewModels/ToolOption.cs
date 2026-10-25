using System.Windows.Input;
using System.Windows.Media;

namespace WhiteboardSimulator.ViewModels;

/// <summary>Uma opção da barra de ferramentas contextual (cor, espessura, tamanho da fonte...).</summary>
public sealed class ToolOption : ViewModelBase
{
    private bool _isSelected;

    public ToolOption(string name, object value, Brush? fill, double size, Action<object> onSelect)
    {
        Name = name;
        Value = value;
        Fill = fill;
        Size = size;
        SelectCommand = new RelayCommand(_ => onSelect(value));
    }

    public string Name { get; }
    public object Value { get; }

    /// <summary>Cor da amostra (opções de cor).</summary>
    public Brush? Fill { get; }

    /// <summary>Tamanho para mostrar (bolinha da espessura, letra da fonte).</summary>
    public double Size { get; }

    public ICommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}
