using Avalonia.Controls;
using Avalonia.Interactivity;
using FluentAvalonia.UI.Controls;
using MinecraftLaunch.Base.Models.Game;
using MinecraftLaunch.Components.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using VibrantbitLauncher.Services;

namespace VibrantbitLauncher.Views.Windows
{
    /// <summary>
    /// 崩溃分析窗口（Avalonia 版）。
    /// </summary>
    public partial class CrashAnalysisWindow : Window
    {
        private readonly string _crashReportFolder;

        public CrashAnalysisWindow(MinecraftEntry? minecraft)
        {
            InitializeComponent();

            if (minecraft != null)
            {
                var analyzer = new LogAnalyzer(minecraft);
                var result = analyzer.Analyze();

                foreach (var reason in result.CrashReasons)
                    CrashReasonsList.Items.Add(reason.ToString());

                foreach (var mod in result.SuspiciousMods)
                    SuspiciousModsList.Items.Add(mod.ToString());
            }
            else
            {
                // 拿不到版本信息（例如已卸载）时也给个占位，避免空白窗口
                CrashReasonsList.Items.Add("未检测到明确的崩溃原因，请查看崩溃报告文件。");
                SuspiciousModsList.Items.Add("未检测到可疑模组。");
            }

            if (CrashReasonsList.ItemCount == 0)
                CrashReasonsList.Items.Add("未检测到明确的崩溃原因，请查看崩溃报告文件。");

            if (SuspiciousModsList.ItemCount == 0)
                SuspiciousModsList.Items.Add("未检测到可疑模组。");

            var mcFolder = Path.GetFullPath(SettingsService.ResolveMinecraftFolder());
            _crashReportFolder = Path.Combine(mcFolder, "crash-reports");
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void OpenCrashFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Directory.Exists(_crashReportFolder))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", _crashReportFolder) { UseShellExecute = true });
                }
                else
                {
                    _ = ShowMessageAsync("提示", "崩溃报告目录不存在：" + _crashReportFolder);
                }
            }
            catch (Exception ex)
            {
                _ = ShowMessageAsync("错误", "无法打开目录：" + ex.Message);
            }
        }

        /// <summary>显示一个跟随主题的消息框。</summary>
        private Task ShowMessageAsync(string title, string message)
        {
            var box = new FAContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "确定",
                DefaultButton = FAContentDialogButton.Primary,
                
            };

            return box.ShowAsync();
        }
    }
}
