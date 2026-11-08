using Avalonia.Controls;
using Avalonia.Interactivity;
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
        // 向导现在是主窗口里的一整层，不再是独立窗口：交给主窗口收尾
        // （记 IsFirstRun=false、铺启动页、把这层淡出）。
        if (TopLevel.GetTopLevel(this) is MainWindow main)
            main.CompleteWelcomeWizard();
    }
}
