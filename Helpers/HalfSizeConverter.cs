using System.Globalization;
using System.Windows.Data;

namespace WhiteboardSimulator.Helpers;

/// <summary>Reduz o tamanho de fonte para caber na amostra "A" da barra de opções (16 → 11, 48 → 22).</summary>
public sealed class HalfSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double d ? Math.Round(9 + d * 0.27, 1) : 14.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
