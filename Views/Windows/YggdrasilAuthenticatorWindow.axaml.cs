using Avalonia.Controls;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.Messaging;
using VibrantbitLauncher.Services;
using VibrantbitLauncher.ViewModels.Pages;

namespace VibrantbitLauncher.Views.Windows;

/// <summary>
/// 添加外置（Yggdrasil）账户：收集服务器地址 / 邮箱 / 密码，
/// 通过 Messenger 把资料发回 <c>AccountPageViewModel</c>。
/// </summary>
public partial class YggdrasilAuthenticatorWindow : Window
{
    public YggdrasilAuthenticatorWindow()
    {
        InitializeComponent();

        // 兜底：无论从哪里 new 出来，都挂到主窗口上。
        // CenterOwner 在 Owner 为 null 时会退化，窗口就跑到屏幕角落去了。
        Owner ??= AppLifetime.MainWindow;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private void Button_Click(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(textBox1.Text)
            && !string.IsNullOrEmpty(textBox2.Text)
            && !string.IsNullOrEmpty(textBox3.Text))
        {
            WeakReferenceMessenger.Default.Send<YggdrasilAccountProfile, string>(
                new YggdrasilAccountProfile
                {
                    Server = textBox1.Text,
                    Email = textBox2.Text,
                    Password = textBox3.Text
                },
                "YggdrasilAccountProfile");

            Close();
        }
    }
}
