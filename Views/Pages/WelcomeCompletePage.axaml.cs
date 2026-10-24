using Avalonia.Controls;
using Avalonia.Interactivity;
using VibrantbitLauncher.Services;
using VibrantbitLauncher.Views.Windows;

namespace VibrantbitLauncher.Views.Pages;

public partial class WelcomeCompletePage : UserControl
{
    public WelcomeCompletePage()
    {
        InitializeComponent();
    }

    private void StartButton_Click(object? sender, RoutedEventArgs e)
    {
        // 先标记「已走完」再关窗：宿主窗口据此判断是转入主界面还是直接退出程序。
        // 少了这一步，点「开始使用」会和点标题栏 × 一样被当成放弃配置。
        var window = TopLevel.GetTopLevel(this) as Window;
        (window as WelcomeWindow)?.MarkCompleted();

        window?.Close();
    }
}
