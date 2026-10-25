using System.Windows.Media;

namespace WhiteboardSimulator.ViewModels;

public sealed class ColorOption : ViewModelBase
{
    private bool _isSelected;

    public ColorOption(string name, string hex)
    {
        Name = name;
        Brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        Brush.Freeze();
    }

    public string Name { get; }
    public SolidColorBrush Brush { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}
