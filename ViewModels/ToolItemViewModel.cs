using WhiteboardSimulator.Models;

namespace WhiteboardSimulator.ViewModels;

public sealed class ToolItemViewModel : ViewModelBase
{
    private bool _isSelected;

    public ToolItemViewModel(ToolType type, string name, string iconKey, string shortcut)
    {
        Type = type;
        Name = name;
        IconKey = iconKey;
        Shortcut = shortcut;
    }

    public ToolType Type { get; }
    public string Name { get; }

    /// <summary>Tecla que ativa a ferramenta.</summary>
    public string Shortcut { get; }

    public string Tooltip => $"{Name} ({Shortcut})";

    /// <summary>Chave do recurso Geometry em Themes/Icons.xaml.</summary>
    public string IconKey { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}
