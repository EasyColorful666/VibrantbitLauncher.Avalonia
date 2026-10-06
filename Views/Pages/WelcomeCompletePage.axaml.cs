using Avalonia.Controls;
using Avalonia.Interactivity;
using VibrantbitLauncher.Services;

namespace VibrantbitLauncher.Views.Pages;

public partial class WelcomeCompletePage : UserControl
{
    public WelcomeCompletePage()
    {
        InitializeComponent();
    }

    private void StartButton_Click(object? sender, RoutedEventArgs e)
    {
        // 关掉向导窗口，进入主界面
        (TopLevel.GetTopLevel(this) as Window)?.Close();
    }
}
