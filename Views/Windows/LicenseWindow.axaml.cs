using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using VibrantbitLauncher.Services;
using VibrantbitLauncher.ViewModels.Pages;

namespace VibrantbitLauncher.Views.Windows;

/// <summary>
/// 开源许可全文。
///
/// <para>
/// 协议正文在编译期被打进程序集（见 csproj 里那条 <c>AvaloniaResource Include="LICENSE.txt"</c>），
/// 所以这里不依赖任何外部文件：玩家手上只有一个 exe 时也能离线读到完整条款，
/// 这正是 AGPL 对分发的要求 —— 协议要跟着程序走。
/// </para>
///
/// <para>
/// 三个动作：在线查看（原文在 gnu.org，永远是最新的）、获取源代码（AGPL 第 13 条）、关闭。
/// </para>
/// </summary>
public partial class LicenseWindow : UserControl, IInlinePage
{
    /// <summary>程序集内的协议资源地址（段名用 AssemblyName，不是 RootNamespace）。</summary>
    private const string ResourceUri = "avares://VibrantbitLauncher.Avalonia/LICENSE.txt";

    /// <summary>协议官方页面。用 https://www.gnu.org/licenses/agpl-3.0.html 而不是 .txt，方便手机上看。</summary>
    private const string OnlineLicenseUrl = "https://www.gnu.org/licenses/agpl-3.0.html";

    /// <summary>内嵌到主窗口时，顶栏显示的标题。</summary>
    public string InlineTitle => "开源许可";

    public LicenseWindow()
    {
        InitializeComponent();
        LoadLicenseText();
    }

    /// <summary>把内嵌的协议全文读进界面；读不到也不该让页面开不出来，给一句可操作的提示即可。</summary>
    private void LoadLicenseText()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(ResourceUri));
            using var reader = new StreamReader(stream);

            var text = reader.ReadToEnd();
            licenseText.Text = string.IsNullOrWhiteSpace(text)
                ? "协议全文为空，请点「在线查看」或到仓库根目录阅读 LICENSE.txt。"
                : text;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "读取内嵌的协议全文失败");
            licenseText.Text = "协议全文没能读出来。可以点下面的「在线查看」，或到仓库根目录打开 LICENSE.txt。";
        }
    }

    private void OpenOnline_Click(object? sender, RoutedEventArgs e)
        => SettingsPageViewModel.OpenUrl(OnlineLicenseUrl);

    private void OpenSource_Click(object? sender, RoutedEventArgs e)
        => SettingsPageViewModel.OpenUrl(UpdateService.RepositoryUrl);

    private void Close_Click(object? sender, RoutedEventArgs e)
        => InlineHost.Close();
}
