using Avalonia.Controls;
using Avalonia.Interactivity;

namespace VibrantbitLauncher.Views.Windows
{
    /// <summary>
    /// 从网址加载皮肤的输入窗口（Avalonia 版）。
    /// </summary>
    public partial class SkinUrlInputWindow : Window
    {
        /// <summary>用户输入的皮肤直链。</summary>
        public string SkinUrl { get; private set; } = string.Empty;

        public SkinUrlInputWindow()
        {
            InitializeComponent();
        }

        private void OnConfirmClick(object sender, RoutedEventArgs e)
        {
            SkinUrl = textBox1.Text?.Trim() ?? string.Empty;
            Close();
        }
    }
}
