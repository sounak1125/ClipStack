using System.Globalization;
using System.Windows;
using System.Windows.Data;
using ClipStack.Core.Models;

namespace ClipStack.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase))
            flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class KindToGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            ClipboardItemKind.Text => "Aa",
            ClipboardItemKind.RichText => "¶",
            ClipboardItemKind.Image => "▣",
            ClipboardItemKind.Files => "▤",
            _ => "•",
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class KindToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            ClipboardItemKind.Text => "TextKindBrush",
            ClipboardItemKind.RichText => "RichTextKindBrush",
            ClipboardItemKind.Image => "ImageKindBrush",
            ClipboardItemKind.Files => "FilesKindBrush",
            _ => "MutedBrush",
        };

        return Application.Current.TryFindResource(key) as System.Windows.Media.Brush
               ?? System.Windows.Media.Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
