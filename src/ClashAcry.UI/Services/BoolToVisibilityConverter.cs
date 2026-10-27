using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace ClashAcry.UI.Services;

/// <summary>
/// bool → Visibility。Windows App SDK 的元数据里搜不到 <c>BooleanToVisibilityConverter</c>
/// （实测 grep <c>Microsoft.UI.Xaml.winmd</c> 零命中），所以自带一份；
/// <c>false</c> 落 <c>Collapsed</c> 而非 <c>Hidden</c>：Hidden 仍占位，折叠的组会在页面上留一条空白带。
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility.Visible;
}
