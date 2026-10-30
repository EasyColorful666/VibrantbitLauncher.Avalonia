using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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

        private FANavigationViewPaneDisplayMode _navigationMode = FANavigationViewPaneDisplayMode.LeftCompact;

        /// <summary>
        /// 导航栏的摆放方式。由主题包决定 —— PCL2 那类横向顶部导航就是这里换成
        /// <see cref="FANavigationViewPaneDisplayMode.Top"/>。
        ///
        /// <para>
        /// 以前是 XAML 里写死的 <c>LeftCompact</c>。改成绑定是因为「导航栏长什么样」
        /// 本身就是主题的一部分：一套要求顶部横排的主题，如果导航还挂在左边，
        /// 看起来只是换了个配色，不算换了风格。
        /// </para>
        /// </summary>
        public FANavigationViewPaneDisplayMode NavigationMode
        {
            get => _navigationMode;
            private set => SetProperty(ref _navigationMode, value);
        }

        private bool _isHeaderNavigation;

        /// <summary>
        /// 导航项是不是并进了标题栏（<see cref="ThemeNavigation.Header"/>）。
        ///
        /// <para>
        /// 为 true 时导航视图的导航条不占用任何版面，菜单由标题栏中间那排按钮承担。
        /// 实现方式是把它切成「最小模式 + 不显示展开按钮」：没有钉子的时候，
        /// 收起的导航条就等于不存在，内容区独占整窗。
        /// </para>
        /// </summary>
        public bool IsHeaderNavigation
        {
            get => _isHeaderNavigation;
            private set => SetProperty(ref _isHeaderNavigation, value);
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

            NavigationMode = ToPaneMode(ThemeService.Navigation);
            IsHeaderNavigation = ThemeService.Navigation == ThemeNavigation.Header;
            ThemeService.Changed += OnThemeChanged;
        }

        /// <summary>主题换了：导航栏模式可能跟着变，界面立刻重排。</summary>
        private void OnThemeChanged()
        {
            if (Dispatcher.UIThread.CheckAccess())
                SyncNavigationFromTheme();
            else
                Dispatcher.UIThread.Post(SyncNavigationFromTheme);
        }

        private void SyncNavigationFromTheme()
        {
            var navigation = ThemeService.Navigation;
            NavigationMode = ToPaneMode(navigation);
            IsHeaderNavigation = navigation == ThemeNavigation.Header;
        }

        private static FANavigationViewPaneDisplayMode ToPaneMode(ThemeNavigation navigation) => navigation switch
        {
            // Header = 导航项已经并进标题栏，导航视图这边不该再占版面。
            // 用「最小模式」（导航条是收起状态的浮层）而不是「顶部」：
            // 顶部模式下再把它隐藏，会让控件内部的顶部自适应测量反复重排、整个窗口卡死（1.0.24.0 实测）。
            // 最小模式配 IsPaneToggleButtonVisible=False，收起的浮层等于不存在，内容区独占整窗。
            ThemeNavigation.Header => FANavigationViewPaneDisplayMode.LeftMinimal,
            ThemeNavigation.Top => FANavigationViewPaneDisplayMode.Top,
            ThemeNavigation.Left => FANavigationViewPaneDisplayMode.Left,
            ThemeNavigation.Minimal => FANavigationViewPaneDisplayMode.LeftMinimal,
            _ => FANavigationViewPaneDisplayMode.LeftCompact
        };

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
            BackgroundColorBrush = HasBackgroundColor && SettingsService.TryParseColor(message.ColorHex, out var color)
                ? new SolidColorBrush(color)
                : null;

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
