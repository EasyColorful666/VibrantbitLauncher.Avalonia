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

        /// <summary>导航服务：由 MainWindow 注入，负责实际页面切换。</summary>
        public INavigationService? Navigation { get; set; }

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
        }

        /// <summary>
        /// 响应背景图变更：安全加载图片（失败则退回 Mica），并同步不透明度。
        /// </summary>
        private void ApplyBackground(BackgroundChangedMessage message)
        {
            BackgroundImage = LoadBackgroundImage(message.ImagePath);
            BackgroundImageOpacity = Math.Clamp(message.Opacity, 0.05, 1.0);
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
