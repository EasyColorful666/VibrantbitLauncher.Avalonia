using Avalonia.Controls;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.Messaging;

namespace VibrantbitLauncher.Views.Windows
{
    /// <summary>
    /// 添加离线账户窗口（Avalonia 版）。
    /// </summary>
    public partial class OfflineAuthenticatorWindow : Window
    {
        public OfflineAuthenticatorWindow()
        {
            InitializeComponent();
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(textBox1.Text))
            {
                WeakReferenceMessenger.Default.Send<string, string>(textBox1.Text, "OfflineAccountProfile");
                Close();
            }
        }
    }
}
