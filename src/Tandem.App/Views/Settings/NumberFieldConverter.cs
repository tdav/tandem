using System.Globalization;
using Avalonia.Data.Converters;

namespace Tandem.App.Views.Settings;

/// <summary>A number of the settings (double) for a <see cref="Avalonia.Controls.NumericUpDown"/> (decimal?): an
/// empty field gives NaN, which the settings window refuses to apply, instead of keeping the last number.</summary>
public sealed class NumberFieldConverter : IValueConverter
{
    public static NumberFieldConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double number && double.IsFinite(number) ? (decimal)number : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is decimal number ? (double)number : double.NaN;
}
