using System.Windows.Input;
using System.Windows.Media;

namespace WhiteboardSimulator.ViewModels;

/// <summary>Opção de escolha única (moldura ou superfície) com miniatura.</summary>
public sealed class ChoiceOption : ViewModelBase
{
    private bool _isSelected;

    public ChoiceOption(string name, object value, Brush preview, Action<object> onSelect)
    {
        Name = name;
        Value = value;
        Preview = preview;
        SelectCommand = new RelayCommand(_ => onSelect(value));
    }

    public string Name { get; }
    public object Value { get; }
    public Brush Preview { get; }
    public ICommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}
