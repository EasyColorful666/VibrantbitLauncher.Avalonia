using Avalonia.Controls;
using FluentAvalonia.UI.Controls;

namespace VibrantbitLauncher.Services
{
    /// <summary>
    /// 基于 FluentAvalonia FAContentDialog 的对话框服务实现。
    /// </summary>
    public sealed class DialogService : IDialogService
    {
        public Task ShowInfoAsync(string title, string message) => ShowAsync(title, message, "确定", null);
        public Task ShowErrorAsync(string title, string message) => ShowAsync(title, message, "确定", null);

        public Task<bool> ConfirmAsync(string title, string message, string confirmText = "确定", string cancelText = "取消")
            => ShowAsync(title, message, confirmText, cancelText);

        public async Task<bool> ShowContentAsync(string title, Control content, string primaryText = "确定", string? closeText = "取消")
        {
            var owner = AppLifetime.DialogOwner;
            if (owner is null)
                return false;

            var dialog = new FAContentDialog
            {
                Title = title,
                Content = content,
                PrimaryButtonText = primaryText,
                CloseButtonText = closeText,
                DefaultButton = FAContentDialogButton.Primary
            };

            var result = await dialog.ShowAsync(owner);
            return result == FAContentDialogResult.Primary;
        }

        private static async Task<bool> ShowAsync(string title, string message, string primaryText, string? closeText)
        {
            var owner = AppLifetime.DialogOwner;
            if (owner is null)
                return false;

            var dialog = new FAContentDialog
            {
                Title = title,
                Content = message,
                PrimaryButtonText = primaryText,
                CloseButtonText = closeText,
                DefaultButton = FAContentDialogButton.Primary
            };

            var result = await dialog.ShowAsync(owner);
            return result == FAContentDialogResult.Primary;
        }
    }
}
