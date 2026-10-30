// [AI修改] 让刷新按钮在刷新期间禁用，防止重复操作。
using System.Globalization;
using System.Windows.Data;

namespace CodeXPedal.Windows.App.Converters;

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
