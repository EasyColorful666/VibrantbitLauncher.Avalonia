using Avalonia.Controls;
using Avalonia.Interactivity;
using VibrantbitLauncher.Services;

namespace VibrantbitLauncher.Views.Pages;

public partial class WelcomePage : UserControl
{
    public WelcomePage()
    {
        InitializeComponent();
    }

    private void NextButton_Click(object? sender, RoutedEventArgs e)
    {
        // 向导第 2 步改为实际配置（游戏目录 / Java / 主题）。
        // 原来的 HelloEffectPage（hello 手写动画）保留在项目中，如需恢复把这里的目标改回去即可。
        AppNavigation.Navigate(typeof(WelcomeSetupPage));
    }
}
