using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using FluentAvalonia.UI.Controls;

namespace VibrantbitLauncher.Views.Windows
{
    /// <summary>
    /// 微软账户设备码登录窗口（Avalonia 版）。
    /// </summary>
    public partial class MicrosoftAuthWindow : Window
    {
        /// <summary>用户是否点击了取消。</summary>
        public bool IsCancelled { get; private set; }

        private string _verificationUrl = string.Empty;

        public MicrosoftAuthWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 更新设备代码和验证 URL（在 DeviceFlowAuthAsync 回调中调用）。
        /// </summary>
        public void SetCode(string userCode, string verificationUrl)
        {
            CodeText.Text = userCode;
            _verificationUrl = verificationUrl;

            // 自动复制代码到剪贴板
            try
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                clipboard?.SetTextAsync(userCode);            }
            catch { /* 剪贴板访问失败忽略 */ }
        }

        private void OpenBrowserButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_verificationUrl))
                return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    UseShellExecute = true,
                    FileName = _verificationUrl
                });
            }
            catch (Exception ex)
            {
                _ = ShowErrorAsync("无法打开浏览器", ex.Message);
            }
        }

        /// <summary>显示一个跟随主题的错误提示框（模态，挂在当前窗口上）。</summary>
        private async Task ShowErrorAsync(string title, string message)
        {
            var dialog = new FAContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "确定",
                DefaultButton = FAContentDialogButton.Primary,
            };

            await dialog.ShowAsync();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            IsCancelled = true;
            Close();
        }
    }
}
