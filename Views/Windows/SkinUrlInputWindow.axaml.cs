using Avalonia.Controls;
using Avalonia.Interactivity;

namespace VibrantbitLauncher.Views.Windows
{
    /// <summary>
    /// 从网址加载皮肤的输入窗口（Avalonia 版）。
    /// </summary>
    public partial class SkinUrlInputWindow : UserControl, Services.IInlinePage
    {

    /// <summary>内嵌到主窗口时，顶栏显示的标题。</summary>
    public string InlineTitle => "从网址加载皮肤";

        /// <summary>用户输入的皮肤直链。</summary>
        public string SkinUrl { get; private set; } = string.Empty;

        public SkinUrlInputWindow()
        {
            InitializeComponent();
        }

        private void OnConfirmClick(object sender, RoutedEventArgs e)
        {
            SkinUrl = textBox1.Text?.Trim() ?? string.Empty;
            Services.InlineHost.Close();
        }
    }
}
