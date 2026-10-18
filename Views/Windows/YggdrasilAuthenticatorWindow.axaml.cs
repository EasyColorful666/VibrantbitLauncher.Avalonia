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
public partial class YggdrasilAuthenticatorWindow : UserControl, Services.IInlinePage
{

    /// <summary>内嵌到主窗口时，顶栏显示的标题。</summary>
    public string InlineTitle => "添加外置账户";

    public YggdrasilAuthenticatorWindow()
    {
        InitializeComponent();

    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Services.InlineHost.Close();

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

            Services.InlineHost.Close();
        }
    }
}
