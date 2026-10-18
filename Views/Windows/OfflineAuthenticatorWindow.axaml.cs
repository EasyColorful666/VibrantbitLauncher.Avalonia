using Avalonia.Controls;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.Messaging;

namespace VibrantbitLauncher.Views.Windows
{
    /// <summary>
    /// 添加离线账户窗口（Avalonia 版）。
    /// </summary>
    public partial class OfflineAuthenticatorWindow : UserControl, Services.IInlinePage
    {

    /// <summary>内嵌到主窗口时，顶栏显示的标题。</summary>
    public string InlineTitle => "添加离线账户";

        public OfflineAuthenticatorWindow()
        {
            InitializeComponent();
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(textBox1.Text))
            {
                WeakReferenceMessenger.Default.Send<string, string>(textBox1.Text, "OfflineAccountProfile");
                Services.InlineHost.Close();
            }
        }
    }
}
