using Avalonia;
using System.Globalization;
using Avalonia.Data.Converters;

namespace VibrantbitLauncher.Helpers
{
    /// <summary>
    /// 把枚举值与参数比较，相等返回 true。用于单选框 / 主题切换的选中态绑定。
    ///
    /// 必须是 public：XAML 通过 helpers: 命名空间在资源字典里实例化它，
    /// Avalonia 的编译期 XAML 只能解析可公开访问的类型（internal 会绑定失败）。
    /// </summary>
    public class EnumToBooleanConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (parameter is not string enumString || value is null)
                return false;

            var type = value.GetType();
            if (!type.IsEnum)
                return false;

            return Enum.TryParse(type, enumString, out var enumValue) && enumValue!.Equals(value);
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            // 单选框的 IsChecked 会双向回写。反查时只关心「被选中」那一路，
            // 取消选中一律返回 UnsetValue，避免把 CurrentTheme 覆盖成错误枚举值。
            if (parameter is not string enumString || value is not true)
                return AvaloniaProperty.UnsetValue;

            var enumType = targetType;
            if (enumType is null || !enumType.IsEnum)
                return AvaloniaProperty.UnsetValue;

            return Enum.TryParse(enumType, enumString, out var parsed)
                ? parsed
                : AvaloniaProperty.UnsetValue;
        }
    }
}
