using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VibrantbitLauncher.Models
{
    /// <summary>
    /// 主题色预设。
    ///
    /// Primary 是主色调（决定按钮、选中项等所有控件的强调色）；
    /// GradientEnd 是渐变终点色，只有在「主题渐变色」开启时才参与显示。
    /// 两个颜色都存成 #AARRGGBB 字符串，与 settings.json 里的字段保持一致。
    /// </summary>
    public sealed class AccentPreset : ObservableObject
    {
        public AccentPreset(string name, string primary, string gradientEnd)
        {
            Name = name;
            Primary = primary;
            GradientEnd = gradientEnd;

            PrimaryColor = Parse(primary);
            GradientEndColor = Parse(gradientEnd);

            SwatchBrush = new SolidColorBrush(PrimaryColor);
            GradientEndBrush = new SolidColorBrush(GradientEndColor);
            GradientBrush = new LinearGradientBrush
            {
                StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
                EndPoint = new Avalonia.RelativePoint(1, 1, Avalonia.RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(PrimaryColor, 0),
                    new GradientStop(GradientEndColor, 1)
                }
            };
        }

        /// <summary>预设名，用于提示文本。</summary>
        public string Name { get; }

        /// <summary>主色（#AARRGGBB）。</summary>
        public string Primary { get; }

        /// <summary>渐变终点色（#AARRGGBB）。</summary>
        public string GradientEnd { get; }

        public Color PrimaryColor { get; }

        public Color GradientEndColor { get; }

        /// <summary>主色实心圆点，用于主题色色板。</summary>
        public SolidColorBrush SwatchBrush { get; }

        /// <summary>终点色实心圆点，用于渐变终点色色板。</summary>
        public SolidColorBrush GradientEndBrush { get; }

        /// <summary>该预设对应的「主色 → 终点色」渐变。</summary>
        public LinearGradientBrush GradientBrush { get; }

        private bool _isAccentSelected;
        private bool _isGradientSelected;

        /// <summary>是否被选为当前主题色。</summary>
        public bool IsAccentSelected
        {
            get => _isAccentSelected;
            set
            {
                if (SetProperty(ref _isAccentSelected, value))
                    OnPropertyChanged(nameof(AccentRingBrush));
            }
        }

        /// <summary>是否被选为当前渐变终点色。</summary>
        public bool IsGradientSelected
        {
            get => _isGradientSelected;
            set
            {
                if (SetProperty(ref _isGradientSelected, value))
                    OnPropertyChanged(nameof(GradientRingBrush));
            }
        }

        /// <summary>
        /// 主题色色板的选中环画刷：选中时为主前景色，未选中时透明。
        ///
        /// 早期版本用 `Style Selector="Button[Tag=True]"` 表达选中态 —— Avalonia 的属性选择器
        /// 拿 Boxed bool 做字符串比较并不可靠，经常画不出环。这里直接在模型侧算好画刷，
        /// 模板只做一次绑定，行为确定。
        /// </summary>
        public IBrush AccentRingBrush =>
            _isAccentSelected ? SelectionRingBrush : Brushes.Transparent;

        /// <summary>渐变终点色色板的选中环画刷。</summary>
        public IBrush GradientRingBrush =>
            _isGradientSelected ? SelectionRingBrush : Brushes.Transparent;

        /// <summary>选中环颜色，两种主题下都有足够对比度。</summary>
        private static readonly IBrush SelectionRingBrush =
            new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5A));

        private static Color Parse(string hex)
        {
            return Color.TryParse(hex, out var color)
                ? color
                : Color.FromRgb(0x00, 0x78, 0xD4);
        }
    }

    /// <summary>
    /// 内置壁纸选项。
    ///
    /// ImagePath 存的是「应用内相对路径」（形如 /Assets/bg1.png），写进 settings.json 时用这一份，
    /// 换机器 / 改程序集名都不会失效；Preview 则为可直接绑定 Image.Source 的位图。
    /// 早期版本把 /Assets/xxx 直接绑到 Image.Source，Avalonia 解析不了这种相对资源 URI，
    /// 结果就是壁纸画廊一片空白 —— 这里改成显式加载成 Bitmap。
    /// </summary>
    public sealed class WallpaperOption
    {
        public WallpaperOption(string name, string imagePath)
        {
            Name = name;
            ImagePath = imagePath;
            Preview = LoadPreview(imagePath);
        }

        public string Name { get; }

        /// <summary>应用内相对路径，用于持久化与实际应用背景。</summary>
        public string ImagePath { get; }

        /// <summary>画廊缩略图位图；资源缺失时为 null，界面回退成空图框。</summary>
        public IImage? Preview { get; }

        private static IImage? LoadPreview(string imagePath)
        {
            try
            {
                var uri = new Uri($"avares://VibrantbitLauncher.Avalonia{imagePath}");
                using var stream = AssetLoader.Open(uri);
                return new Bitmap(stream);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "内置壁纸缩略图加载失败：{Path}", imagePath);
                return null;
            }
        }
    }
}
