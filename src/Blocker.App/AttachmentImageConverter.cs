using System;
using System.Globalization;
using System.Windows.Data;

namespace Blocker.App;

public class AttachmentImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string imageRef && !string.IsNullOrWhiteSpace(imageRef))
        {
            return NotesAttachmentManager.LoadBitmapSafely(imageRef);
        }
        return null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
