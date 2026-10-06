using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MinecraftLaunch.Base.Models.Network;
using MinecraftLaunch.Components.Provider;
using VibrantbitLauncher.Models;
using VibrantbitLauncher.Services;

namespace VibrantbitLauncher.Views.Pages;

public partial class ModPage : UserControl
{
    private string _projectId = string.Empty;
    private readonly ObservableCollection<ModrinthModInfo> _modrinthModInfos = new();
    private List<ModrinthResourceFile> _modrinthResources = new();
    private readonly ModrinthProvider _provider = new();
    private bool _isDownloading;

    public ModPage()
    {
        InitializeComponent();
        comboBox.SelectionChanged += OnSelectionChanged;
    }

    private static string GetGamePath()
    {
        var path = Path.GetFullPath(SettingsService.ResolveMinecraftFolder());
        if (!Directory.Exists(path))
            Directory.CreateDirectory(path);
        return path;
    }

    public async Task LoadForProject(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            return;

        _projectId = projectId;
        await LoadFileInfos(projectId);
    }

    private async Task LoadFileInfos(string projectId)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            loadingText.IsVisible = true;
            listBox.ItemsSource = null;
        });

        try
        {
            _modrinthModInfos.Clear();
            _modrinthResources.Clear();

            var modInfos = await _provider.GetModFilesByProjectIdAsync(projectId);
            if (modInfos == null)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    loadingText.IsVisible = false;
                    listBox.ItemsSource = null;
                });
                AppServices.Notifications?.Warning("未找到模组文件");
                return;
            }

            _modrinthResources = modInfos.ToList();
            foreach (var modInfo in modInfos)
            {
                _modrinthModInfos.Add(new ModrinthModInfo
                {
                    Name = modInfo.DisplayName,
                    DownloadUrl = modInfo.DownloadUrl
                });
            }

            if (_modrinthModInfos.Count == 0)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    loadingText.IsVisible = false;
                    listBox.ItemsSource = null;
                });
                AppServices.Notifications?.Warning("该项目暂无可用的模组文件");
                return;
            }

            var extensions = await MinecraftVersionCache.GetAsync();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                loadingText.IsVisible = false;
                comboBox.Items.Clear();
                comboBox.Items.Add(string.Empty);
                foreach (var ext in extensions)
                    comboBox.Items.Add(ext.McVersion);
                listBox.ItemsSource = _modrinthModInfos;
                comboBox.SelectedIndex = 0;
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                loadingText.IsVisible = false;
            });
            AppServices.Notifications?.Error($"加载模组文件列表失败: {ex.Message}");
        }
    }

    private void ApplyFilter(string? selected)
    {
        if (string.IsNullOrWhiteSpace(selected))
        {
            listBox.ItemsSource = _modrinthModInfos;
            return;
        }

        var filtered = _modrinthResources
            .Where(m => !string.IsNullOrEmpty(m.FileName) &&
                        m.FileName.Contains(selected, StringComparison.OrdinalIgnoreCase))
            .Select(m => new ModrinthModInfo { Name = m.DisplayName, DownloadUrl = m.DownloadUrl })
            .ToList();
        listBox.ItemsSource = filtered;
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
        => ApplyFilter(comboBox.SelectedItem as string);

    private void comboBox1_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        => ApplyFilter(comboBox1.SelectedItem as string);

    private async void OnSaveAsClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ModrinthModInfo info })
            await DownloadModAsync(info.DownloadUrl);
        else
            AppServices.Notifications?.Error("无法获取文件下载链接");
    }

    /// <summary>
    /// 从下载链接取扩展名（小写、带点）。
    ///
    /// 链接可能带查询串或 CDN 的路径后缀（如 `...sodium.jar?fabric-1.21.4.jar`），
    /// 因此先去查询串和片段，再对路径取扩展名。识别不出来的（版本号路径如 `/download/1.21.4`、
    /// 纯数字后缀 `.4`、无扩展名）一律按 `.jar` 处理 —— Modrinth 上模组/整合包/数据包/资源包/光影
    /// 绝大多数是 `.jar` 或 `.zip`，其中 `.jar` 占绝对多数。
    /// </summary>
    private static string GetDownloadExtension(string url)
    {
        try
        {
            var path = url;

            // 去掉查询串与片段
            var cut = path.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0)
                path = path[..cut];

            var extension = Path.GetExtension(path);

            // 必须是「. + 字母开头的纯字母数字」，且至少两个字符（如 .jar / .zip / .mcpack）。
            // 这样可以挡掉把版本号当扩展名的情况（`/download/1.21.4` 的 `.4`），
            // 也能挡住超长的路径段落（`weird.superlongextension` 是名字不是扩展名）。
            if (string.IsNullOrWhiteSpace(extension) || extension.Length < 3)
                return ".jar";

            var name = extension[1..];
            if (!char.IsLetter(name[0]) || !name.All(char.IsLetterOrDigit))
                return ".jar";

            // 常见扩展名最长 6 个字符（mcpack / mcworld）；更长的当路径段处理
            return name.Length <= 6 ? extension.ToLowerInvariant() : ".jar";
        }
        catch
        {
            return ".jar";
        }
    }

    private static string BuildFilter(string extension) => extension switch
    {
        ".jar" => "模组文件 (*.jar)|*.jar",
        ".zip" => "压缩包 (*.zip)|*.zip",
        ".litemod" => "LiteLoader 模组 (*.litemod)|*.litemod",
        ".txt" => "文本文件 (*.txt)|*.txt",
        ".json" => "JSON 文件 (*.json)|*.json",
        ".yml" or ".yaml" => "YAML 文件 (*.yml;*.yaml)|*.yml;*.yaml",
        ".mcpack" => "基岩版资源包 (*.mcpack)|*.mcpack",
        ".mcworld" => "基岩版世界 (*.mcworld)|*.mcworld",
        _ => $"文件 (*{extension})|*{extension}"
    };

    private async Task DownloadModAsync(string downloadUrl)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            AppServices.Notifications?.Warning("该文件没有有效的下载链接");
            return;
        }

        if (_isDownloading)
        {
            AppServices.Notifications?.Warning("已有下载任务正在进行，请稍候");
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        // 文件类型由下载链接的扩展名决定，不给用户改：
        // 选错类型会存出一个扩展名与实际内容不符的文件（如 .jar 存成 .zip），游戏加载时识别不了。
        var extension = GetDownloadExtension(downloadUrl);

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存模组文件",
            SuggestedFileName = Path.GetFileName(downloadUrl),
            SuggestedStartLocation = await SafeFolderAsync(topLevel, GetGamePath()),
            DefaultExtension = extension.TrimStart('.'),
            FileTypeChoices = new[]
            {
                new FilePickerFileType(BuildFilterName(extension)) { Patterns = new[] { $"*{extension}" } }
            }
        });

        var savePath = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(savePath))
            return;

        _isDownloading = true;

        var taskService = AppServices.Provider?.GetService(typeof(DownloadTaskService)) as DownloadTaskService;
        var taskName = Path.GetFileName(savePath);
        var task = taskService?.CreateTask(taskName, DownloadTaskType.ModDownload);

        try
        {
            using var httpClient = new HttpClient();
            using var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            using var contentStream = await response.Content.ReadAsStreamAsync();
            using var fileStream = File.Create(savePath);

            var buffer = new byte[81920];
            long totalRead = 0;
            long lastReportedBytes = 0;
            int read;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, read);
                totalRead += read;

                // 每 200ms 报告一次进度，避免高频刷新 UI
                if (totalBytes > 0 && sw.ElapsedMilliseconds >= 200)
                {
                    var percent = (int)(totalRead * 100 / totalBytes);
                    var intervalBytes = totalRead - lastReportedBytes;
                    var speed = intervalBytes / sw.Elapsed.TotalSeconds / 1024 / 1024;
                    taskService?.UpdateProgress(task, percent, $"{speed:F1} MB/s",
                        $"{totalRead / 1024 / 1024:F1}/{totalBytes / 1024 / 1024:F1} MB");
                    lastReportedBytes = totalRead;
                    sw.Restart();
                }
            }

            taskService?.CompleteTask(task, $"已保存到：{savePath}");
            AppServices.Notifications?.Success($"文件已保存到：{savePath}");
        }
        catch (Exception ex)
        {
            taskService?.FailTask(task, ex.Message);
            AppServices.Notifications?.Error(ex.Message);
        }
        finally
        {
            _isDownloading = false;
        }
    }

    private static string BuildFilterName(string extension) => extension switch
    {
        ".jar" => "模组文件",
        ".zip" => "压缩包",
        ".litemod" => "LiteLoader 模组",
        ".txt" => "文本文件",
        ".json" => "JSON 文件",
        ".yml" or ".yaml" => "YAML 文件",
        ".mcpack" => "基岩版资源包",
        ".mcworld" => "基岩版世界",
        _ => $"文件 (*{extension})"
    };

    /// <summary>把本地目录包成 IStorageFolder；目录不存在或失败时返回 null（用系统默认位置）。</summary>
    private static async Task<IStorageFolder?> SafeFolderAsync(TopLevel topLevel, string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return null;

            return await topLevel.StorageProvider.TryGetFolderFromPathAsync(path);
        }
        catch
        {
            return null;
        }
    }
}

public class ModrinthModInfo
{
    public string Name { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
}
