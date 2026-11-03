using System.Windows;
using System.Windows.Controls;
using Blocker.Core;

namespace Blocker.App;

public class AppPickerTemplateSelector : DataTemplateSelector
{
    public DataTemplate? HeaderTemplate { get; set; }
    public DataTemplate? AppRowTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is AppPickerRow row)
        {
            return row.IsHeader ? HeaderTemplate : AppRowTemplate;
        }
        return base.SelectTemplate(item, container);
    }
}
