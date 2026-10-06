using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace VibrantbitLauncher.Services
{
    /// <summary>
    /// 文件选择服务：替代 WPF 的 Microsoft.Win32.OpenFileDialog / SaveFileDialog / OpenFolderDialog。
    /// Avalonia 统一走 TopLevel.StorageProvider（异步）。
    /// </summary>
    public interface IFilePickerService
    {
        /// <summary>选择单个文件，返回路径；取消返回 null。</summary>
        Task<string?> PickFileAsync(string title, params string[] extensions);

        /// <summary>选择多个文件，返回路径列表；取消或未选返回空列表。</summary>
        Task<List<string>> PickFilesAsync(string title, params string[] extensions);

        /// <summary>选择保存位置，返回路径；取消返回 null。</summary>
        Task<string?> PickSaveFileAsync(string title, string suggestedName, params string[] extensions);

        /// <summary>选择文件夹，返回路径；取消返回 null。</summary>
        Task<string?> PickFolderAsync(string title);
    }

    /// <summary>
    /// 基于 Avalonia StorageProvider 的文件选择服务实现。
    /// </summary>
    public sealed class FilePickerService : IFilePickerService
    {
        private static Window? Owner =>
            (Avalonia.Application.Current?.ApplicationLifetime
                as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;

        public async Task<string?> PickFileAsync(string title, params string[] extensions)
        {
            var storage = Owner?.StorageProvider;
            if (storage is null) return null;

            var types = BuildTypes(extensions);
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = types
            });

            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }

        public async Task<List<string>> PickFilesAsync(string title, params string[] extensions)
        {
            var storage = Owner?.StorageProvider;
            if (storage is null) return new List<string>();

            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = true,
                FileTypeFilter = BuildTypes(extensions)
            });

            return files.Select(f => f.TryGetLocalPath())
                        .Where(p => !string.IsNullOrEmpty(p))
                        .Select(p => p!)
                        .ToList();
        }

        public async Task<string?> PickSaveFileAsync(string title, string suggestedName, params string[] extensions)
        {
            var storage = Owner?.StorageProvider;
            if (storage is null) return null;

            var types = BuildTypes(extensions);
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggestedName,
                FileTypeChoices = types
            });

            return file?.TryGetLocalPath();
        }

        public async Task<string?> PickFolderAsync(string title)
        {
            var storage = Owner?.StorageProvider;
            if (storage is null) return null;

            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false
            });

            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        }

        private static List<FilePickerFileType>? BuildTypes(string[] extensions)
        {
            if (extensions is null || extensions.Length == 0)
                return null;

            return new List<FilePickerFileType>
            {
                new("支持的文件")
                {
                    Patterns = extensions.Select(e => e.StartsWith("*") ? e : "*." + e.TrimStart('.')).ToList()
                }
            };
        }
    }
}
