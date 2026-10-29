using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using QQSpeedAssistant.Models;

namespace QQSpeedAssistant.Converters;

/// <summary>把日志级别转换为对应的前景色。</summary>
public sealed class LogLevelBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush InfoBrush = new(Color.FromRgb(0xE6, 0xE8, 0xEC));
    private static readonly SolidColorBrush WarnBrush = new(Color.FromRgb(0xFB, 0xBF, 0x24));
    private static readonly SolidColorBrush ErrorBrush = new(Color.FromRgb(0xF8, 0x71, 0x71));
    private static readonly SolidColorBrush SuccessBrush = new(Color.FromRgb(0x34, 0xD3, 0x99));

    /// <summary>转换。</summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is LogLevel level
            ? level switch
            {
                LogLevel.Warning => WarnBrush,
                LogLevel.Error => ErrorBrush,
                LogLevel.Success => SuccessBrush,
                _ => InfoBrush
            }
            : InfoBrush;

    /// <summary>不支持反向转换。</summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
