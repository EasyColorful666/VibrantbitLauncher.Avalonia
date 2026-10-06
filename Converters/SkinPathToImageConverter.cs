using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace VibrantbitLauncher.Converters
{
    /// <summary>
    /// 把「图片路径字符串」转成 <see cref="Avalonia.Media.Imaging.Bitmap"/>，用于 <c>Image.Source</c> 绑定。
    ///
    /// <para>
    /// 为什么不直接把路径字符串绑给 <c>Image.Source</c>：
    /// Avalonia 的 Bitmap 对同一个 URI 会走 URIDataReader 的全局缓存。
    /// 只要旧的位图还被某个 <c>Image</c> 持有（换肤时界面上正是如此），
    /// 再从同一路径加载就会直接命中旧缓存，永远显示换肤前的样子 ——
    /// 表现就是「皮肤改了但不生效」。
    /// </para>
    ///
    /// <para>
    /// 这里改为**读取文件字节流**再解码：从内存流加载不参与 URI 缓存，
    /// 每次调用都是全新的解码结果，因此覆盖同路径文件后能立即显示新皮肤。
    /// </para>
    /// </summary>
    public sealed class SkinPathToImageConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var path = value as string;
            if (string.IsNullOrWhiteSpace(path))
                return null;

            // 内置资源（avares:// 或 /Assets/xxx.png）：本地文件不存在，
            // 但它是编译期资源、只读且不会被覆盖，交给 Avalonia 正常解析即可。
            if (!File.Exists(path))
            {
                try
                {
                    var uri = path.StartsWith("avares://", StringComparison.OrdinalIgnoreCase)
                        ? new Uri(path)
                        : new Uri($"avares://VibrantbitLauncher.Avalonia{path}");

                    using var stream = Avalonia.Platform.AssetLoader.Open(uri);
                    return new Avalonia.Media.Imaging.Bitmap(stream);
                }
                catch
                {
                    return null;
                }
            }

            try
            {
                // 从内存流加载：不参与 URI 缓存，覆盖同路径文件后能立即刷新
                using var stream = File.OpenRead(path);
                return new Avalonia.Media.Imaging.Bitmap(stream);
            }
            catch
            {
                return null;
            }
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
