using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace FinancialTerminal.App.Converters;

/// <summary>
/// Convierte valores numéricos a color verde (positivo) o rojo (negativo).
/// Usado en columnas de variación % y P&amp;L.
/// </summary>
public class PnLColorConverter : IValueConverter
{
    private static readonly SolidColorBrush Green = new(Color.FromRgb(52, 211, 153));   // #34D399
    private static readonly SolidColorBrush Red = new(Color.FromRgb(248, 113, 113));    // #F87171
    private static readonly SolidColorBrush Neutral = new(Color.FromRgb(139, 148, 158)); // #8B949E

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is decimal d) return d > 0 ? Green : d < 0 ? Red : Neutral;
        if (value is double dbl) return dbl > 0 ? Green : dbl < 0 ? Red : Neutral;
        return Neutral;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Formatea decimales con signo + para positivos.
/// </summary>
public class SignedPercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is decimal d) return d >= 0 ? $"+{d:N2}%" : $"{d:N2}%";
        return "0.00%";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Formatea moneda ARS con separador de miles argentino.
/// </summary>
public class CurrencyARSConverter : IValueConverter
{
    private static readonly CultureInfo ArgCulture = new("es-AR");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is decimal d) return $"${d.ToString("N2", ArgCulture)}";
        return "$0,00";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Convierte bool a Visibility.
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

/// <summary>
/// Convierte estado de conexión a color del indicador.
/// </summary>
public class ConnectionStatusColorConverter : IValueConverter
{
    private static readonly SolidColorBrush Connected = new(Color.FromRgb(52, 211, 153));
    private static readonly SolidColorBrush Disconnected = new(Color.FromRgb(239, 68, 68));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Connected : Disconnected;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
