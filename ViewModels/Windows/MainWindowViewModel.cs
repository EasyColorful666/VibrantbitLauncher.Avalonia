using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FluentAvalonia.UI.Controls;
using VibrantbitLauncher.Helpers;
using VibrantbitLauncher.Models;
using VibrantbitLauncher.Services;

namespace VibrantbitLauncher.ViewModels.Windows
{
    public class MainWindowViewModel : ObservableObject
    {
        private static MainModel _mainModel = new();
        public static MainModel MainModel { get => _mainModel; set => _mainModel = value; }

        private string imagePath;

        public string ImagePath
        {
            get => imagePath;
            set => SetProperty(ref imagePath, value);
        }

        private IImage? backgroundImage;
        private double backgroundImageOpacity = 0.85;

        /// <summary>窗口背景图；为 null 时窗口保持纯 Mica 底。</summary>
        public IImage? BackgroundImage
        {
            get => backgroundImage;
            private set
            {
                if (SetProperty(ref backgroundImage, value))
                    OnPropertyChanged(nameof(HasBackgroundImage));
            }
        }

        /// <summary>是否已设置可用背景图（决定遮罩层是否显示）。</summary>
        public bool HasBackgroundImage => backgroundImage != null;

        public double BackgroundImageOpacity
        {
            get => backgroundImageOpacity;
            set => SetProperty(ref backgroundImageOpacity, value);
        }

        private bool hasBackgroundColor;
        private IBrush? backgroundColorBrush;

        /// <summary>用户在「背景 → 纯色」里挑的那个颜色。<see cref="BackgroundColorBrush"/> 是它按明暗加工后的结果。</summary>
        private Color? backgroundColor;

        private double backgroundBlurRadius;
        private double backgroundVignetteOpacity;
        private bool backgroundAnimated;

        /// <summary>是否使用纯色背景（设置里选了「纯色」）。</summary>
        public bool HasBackgroundColor
        {
            get => hasBackgroundColor;
            private set => SetProperty(ref hasBackgroundColor, value);
        }

        /// <summary>纯色背景的画刷。</summary>
        public IBrush? BackgroundColorBrush
        {
            get => backgroundColorBrush;
            private set => SetProperty(ref backgroundColorBrush, value);
        }

        /// <summary>背景图模糊半径（0 表示不模糊）。由「毛玻璃强度」换算而来。</summary>
        public double BackgroundBlurRadius
        {
            get => backgroundBlurRadius;
            private set
            {
                if (SetProperty(ref backgroundBlurRadius, value))
                    OnPropertyChanged(nameof(HasBackgroundBlur));
            }
        }

        public bool HasBackgroundBlur => backgroundBlurRadius > 0.1;

        private IEffect? backgroundEffect;

        /// <summary>
        /// 背景图的模糊效果；为 null 表示不做离屏模糊。
        ///
        /// <para>
        /// 必须是「null 或实例」而不是「半径 0 的实例」：<c>Effect</c> 非空就会让整层
        /// 走一次离屏渲染，半径设 0 也一样要付这笔钱。
        /// </para>
        /// </summary>
        public IEffect? BackgroundEffect
        {
            get => backgroundEffect;
            private set => SetProperty(ref backgroundEffect, value);
        }

        /// <summary>暗角强度（0~1），叠在背景最上层。</summary>
        public double BackgroundVignetteOpacity
        {
            get => backgroundVignetteOpacity;
            private set
            {
                if (SetProperty(ref backgroundVignetteOpacity, value))
                    OnPropertyChanged(nameof(HasBackgroundVignette));
            }
        }

        public bool HasBackgroundVignette => backgroundVignetteOpacity > 0.001;

        /// <summary>背景图是否缓慢缩放（动态背景）。</summary>
        public bool BackgroundAnimated
        {
            get => backgroundAnimated;
            private set => SetProperty(ref backgroundAnimated, value);
        }

        /// <summary>导航服务：由 MainWindow 注入，负责实际页面切换。</summary>
        public INavigationService? Navigation { get; set; }

        private FANavigationViewPaneDisplayMode _navigationMode = FANavigationViewPaneDisplayMode.Left;

        /// <summary>
        /// 导航栏的摆放方式。新设计固定为常驻左侧文字导航（<see cref="FANavigationViewPaneDisplayMode.Left"/>）。
        ///
        /// <para>
        /// 仍然是绑定而不是 XAML 里写死：窗口在小屏上会收窄，那时需要切到紧凑模式。
        /// 但不再由主题决定 —— 主题包机制已经整体移除，界面只有一种形态。
        /// </para>
        /// </summary>
        public FANavigationViewPaneDisplayMode NavigationMode
        {
            get => _navigationMode;
            private set => SetProperty(ref _navigationMode, value);
        }

        public MainWindowViewModel()
        {
            imagePath = @"avares://VibrantbitLauncher.Avalonia/Assets/Blank.jpg";

            ShowMainWindowCommand = new RelayCommand(ShowMainWindow);
            OpenSettingsCommand = new RelayCommand(OpenSettings);
            ApplicationExitCommand = new RelayCommand(ApplicationExit);

            WeakReferenceMessenger.Default.Register<string, string>(this, "UpdateImagePath", (_, m) => UpdateImagePath(m));
            WeakReferenceMessenger.Default.Register<Type, string>(this, "NavigateTo", (_, m) => NavigateToPage(m));
            WeakReferenceMessenger.Default.Register<BackgroundChangedMessage>(this, (_, m) => ApplyBackground(m));
            InitializeMenuItems();

            // 纯色背景要跟着明暗走：跟随系统换明暗时得重算一次，
            // 否则暗色下会留着亮色时挑的白底（白底 + 深色卡片 + 浅色字 = 看不见字）
            if (Application.Current is { } app)
                app.ActualThemeVariantChanged += OnActualThemeVariantChanged;
        }

        /// <summary>
        /// 响应背景图变更：安全加载图片（失败则退回 Mica），并同步不透明度。
        ///
        /// 性能模式下**不加载任何壁纸**：全屏大图（往往还是 4K 壁纸）在软件渲染路径下
        /// 每帧都要做一次全窗口缩放 + 混合，是低配机器上最贵的一笔开销，
        /// 而它纯粹是装饰性的，关掉不影响任何功能。
        /// </summary>
        private void ApplyBackground(BackgroundChangedMessage message)
        {
            // 纯色模式与图片模式互斥：选了纯色就不再解码任何图片。
            HasBackgroundColor = message.Mode == "Color";
            backgroundColor = HasBackgroundColor && SettingsService.TryParseColor(message.ColorHex, out var color)
                ? color
                : null;
            RebuildBackgroundColorBrush();

            if (SettingsService.Current.PerformanceMode || message.Mode == "None")
            {
                BackgroundImage = null;
                BackgroundBlurRadius = 0;
                BackgroundEffect = null;
                BackgroundVignetteOpacity = 0;
                BackgroundAnimated = false;
                return;
            }

            BackgroundImage = message.Mode == "Image" ? LoadBackgroundImage(message.ImagePath) : null;
            BackgroundImageOpacity = Math.Clamp(message.Opacity, 0.05, 1.0);

            // 毛玻璃强度映射成模糊半径：0~1 → 0~48px。
            // 半径再大下去收益几乎没有，而大图模糊的渲染成本是随半径线性涨的。
            BackgroundBlurRadius = Math.Clamp(message.Blur, 0, 1) * 48;
            BackgroundEffect = BackgroundBlurRadius > 0.1
                ? new BlurEffect { Radius = BackgroundBlurRadius }
                : null;

            // 暗角只在不影响主体可读的前提下才叠，最多 60% —— 再深就是「灯全关了」。
            BackgroundVignetteOpacity = Math.Clamp(message.Vignette, 0, 1) * 0.6;

            // 动态背景本质是缓慢的呼吸缩放；关掉动效时退回静态，省电也省帧。
            BackgroundAnimated = message.Animated && MotionSettings.Enabled && !MotionSettings.Reduced;
        }

        /// <summary>
        /// 按当前明暗算出纯色背景该用的画刷。
        ///
        /// <para>
        /// <b>为什么不能直接用用户挑的那个颜色</b>：那个颜色是用户在看某个明暗时挑的，
        /// 换到另一种明暗就完全不对了。最刺眼的一种是「亮色下挑了白底，之后切到暗色」——
        /// 画布是白的、卡片是深的、标题是浅灰的，字直接看不见（1.0.25.0 实测截图）。
        /// </para>
        ///
        /// <para>
        /// 规则很简单：<b>暗色下只留一成原色，九成用主题画布色</b>。
        /// 白底于是变成比卡片略深的灰，纯色底保留一点自己的色相而不刺眼；
        /// 亮色下原样显示 —— 那是用户自己挑的，没理由替他改。
        /// </para>
        /// </summary>
        private void RebuildBackgroundColorBrush()
        {
            if (backgroundColor is not { } color)
            {
                BackgroundColorBrush = null;
                return;
            }

            BackgroundColorBrush = new SolidColorBrush(
                Application.Current?.ActualThemeVariant == ThemeVariant.Dark ? DimForDark(color) : LiftForLight(color));
        }

        /// <summary>把自定义底色压到暗色主题那一档：保留一成原色，其余用画布色垫住。</summary>
        private static Color DimForDark(Color color)
        {
            var canvas = CanvasColorFor(ThemeVariant.Dark);
            const double keep = 0.10;

            return Color.FromArgb(
                0xFF,
                (byte)Math.Round(color.R * keep + canvas.R * (1 - keep)),
                (byte)Math.Round(color.G * keep + canvas.G * (1 - keep)),
                (byte)Math.Round(color.B * keep + canvas.B * (1 - keep)));
        }

        /// <summary>
        /// 亮色主题下，只有「深得离谱」的自定义底色才需要处理。
        ///
        /// <para>
        /// 亮色主题的正文是深灰的。底色要是也很深，页面上不在卡片里的那些字
        /// （页标题、说明、分区名）同样会看不见 —— 和暗色下挑白底是同一件事的镜像。
        /// 但这里不能像暗色那样一律压掉：亮色下挑深色底是常见审美，
        /// 所以只把亮度低于 20% 的那一档往画布方向拉一半，留得住色相也看得清字。
        /// </para>
        /// </summary>
        private static Color LiftForLight(Color color)
        {
            var luminance = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255;
            if (luminance >= 0.20)
                return color;

            var canvas = CanvasColorFor(ThemeVariant.Light);
            const double toward = 0.55;

            return Color.FromArgb(
                0xFF,
                (byte)Math.Round(color.R * (1 - toward) + canvas.R * toward),
                (byte)Math.Round(color.G * (1 - toward) + canvas.G * toward),
                (byte)Math.Round(color.B * (1 - toward) + canvas.B * toward));
        }

        /// <summary>取某个明暗下的画布色（主题包会覆写它，所以不能写死）。</summary>
        private static Color CanvasColorFor(ThemeVariant variant)
        {
            if (Application.Current is { } app
                && app.TryFindResource("SurfaceCanvasBrush", variant, out var resource)
                && resource is ISolidColorBrush brush)
            {
                return brush.Color;
            }

            return variant == ThemeVariant.Dark
                ? Color.FromRgb(0x1F, 0x1F, 0x1F)
                : Color.FromRgb(0xF7, 0xF8, 0xFA);
        }

        /// <summary>明暗变了（设置里切换 / 跟随系统）：纯色背景要跟着重算。</summary>
        private void OnActualThemeVariantChanged(object? sender, EventArgs e) => RebuildBackgroundColorBrush();

        /// <summary>
        /// 加载背景图。支持本地绝对路径与内置壁纸的资源 URI。
        /// </summary>
        private static IImage? LoadBackgroundImage(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            try
            {
                if (path.StartsWith("/", StringComparison.Ordinal))
                {
                    // 内置壁纸：映射到 avares 资源
                    var uri = new Uri($"avares://VibrantbitLauncher.Avalonia{path}");
                    using var stream = AssetLoader.Open(uri);
                    return new Bitmap(stream);
                }

                if (File.Exists(path))
                {
                    using var stream = File.OpenRead(path);
                    return new Bitmap(stream);
                }

                Serilog.Log.Warning("背景图不存在，已忽略：{Path}", path);
                return null;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "背景图加载失败：{Path}", path);
                return null;
            }
        }

        private void NavigateToPage(Type pageType)
        {
            Navigation?.Navigate(pageType);
        }

        private void UpdateImagePath(string obj)
        {
            ImagePath = obj;
        }

        #region 属性

        private ObservableCollection<MenuItemViewModel> _trayMenuItems = new();
        public ObservableCollection<MenuItemViewModel> TrayMenuItems
        {
            get => _trayMenuItems;
            set => SetProperty(ref _trayMenuItems, value);
        }
        #endregion

        #region 命令
        public RelayCommand ShowMainWindowCommand { get; private set; }
        public RelayCommand OpenSettingsCommand { get; private set; }
        public RelayCommand ApplicationExitCommand { get; private set; }
        #endregion

        #region 私有方法
        private void InitializeMenuItems()
        {
            TrayMenuItems = new ObservableCollection<MenuItemViewModel>
            {
                new MenuItemViewModel { Header = "Home", Command = ShowMainWindowCommand },
                new MenuItemViewModel { Header = "Settings", Command = OpenSettingsCommand },
                new MenuItemViewModel { IsSeparator = true },
                new MenuItemViewModel { Header = "Exit", Command = ApplicationExitCommand }
            };
        }

        private void ShowMainWindow()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow?.Show();
                desktop.MainWindow?.Activate();
            }
        }

        private void OpenSettings()
        {
            Navigation?.Navigate(typeof(Views.Pages.SettingsPage));
        }

        private void ApplicationExit()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        }
        #endregion
    }

    public class MenuItemViewModel : ObservableObject
    {
        public string Header { get; set; } = string.Empty;
        public RelayCommand? Command { get; set; }
        public bool IsSeparator { get; set; }
    }
}
