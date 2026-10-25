using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WhiteboardSimulator.Helpers;

/// <summary>Converte uma chave (string) no recurso correspondente de Application.Resources.</summary>
public sealed class ResourceKeyConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string key ? Application.Current.TryFindResource(key) : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
