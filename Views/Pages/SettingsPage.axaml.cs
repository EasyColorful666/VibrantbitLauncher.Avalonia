using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using VibrantbitLauncher.Helpers;
using VibrantbitLauncher.Services;
using VibrantbitLauncher.ViewModels.Pages;

namespace VibrantbitLauncher.Views.Pages;

public partial class SettingsPage : UserControl, INavigationAware
{
    private SettingsPageViewModel? _viewModel;

    public SettingsPage()
    {
        // 关键：必须在这里显式把 ViewModel 挂到 DataContext 上。
        // 本页是 DI 单例，若不赋值就会继承主窗口的 DataContext（MainWindowViewModel），
        // 结果整页绑定全部落空 —— 界面看起来「空白」，日志里刷满 "Could not find a matching
        // property accessor ... on MainWindowViewModel"。其他页面都是在构造里 DataContext = vm。
        _viewModel = AppServices.Provider?.GetService(typeof(SettingsPageViewModel)) as SettingsPageViewModel;
        DataContext = _viewModel;

        InitializeComponent();

        // InitializeComponent 期间 XAML 的 IsSelected="True" 会触发 SelectionChanged，
        // 那时容器还没建好，这里按当前选中项补一次。
        ApplyCurrentCategory();
    }

    /// <summary>
    /// 导航进入本页时由 NavigationService 回调。
    ///
    /// 注意：导航服务把「页面控件」设为 Content，因此回调落在 UserControl 上，
    /// 而不是它内部的 ViewModel —— 所以这里必须显式转发给 ViewModel。
    /// 每次进入都重新检测 Java（而不仅是首次初始化），这样装了新 JDK / 换了路径后切回来即可看到。
    /// </summary>
    Task INavigationAware.OnNavigatedToAsync()
        => _viewModel?.OnNavigatedToAsync() ?? Task.CompletedTask;

    Task INavigationAware.OnNavigatedFromAsync() => Task.CompletedTask;

    private void CategoryList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        => ApplyCurrentCategory();

    /// <summary>按左侧当前选中项切换右侧面板；只改 IsVisible，不重建内容。</summary>
    private void ApplyCurrentCategory()
    {
        if (PanelHost == null || CategoryList == null)
            return;

        if (CategoryList.SelectedItem is not ListBoxItem item
            || item.Tag is not string tag
            || !int.TryParse(tag, out var index))
        {
            return;
        }

        // 顺序必须与左栏 ListBoxItem 的 Tag 一一对应：通用 / 游戏 / 外观 / 网络 / 高级 / 关于
        var panels = new[]
        {
            GeneralPanel, GamePanel, AppearancePanel, NetworkPanel, AdvancedPanel, AboutPanel
        };

        if (index < 0 || index >= panels.Length)
            return;

        for (var i = 0; i < panels.Length; i++)
            panels[i].IsVisible = i == index;

        PageTransitionHelper.PlayEnterTransition(panels[index]);
    }

    /// <summary>选择本地图片作为窗口背景。</summary>
    private async void OnChooseBackgroundImage(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择背景图片",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("图片文件")
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp", "*.gif" }
                },
                FilePickerFileTypes.All
            }
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
            _viewModel?.SetBackgroundImage(path);
    }

    /// <summary>把最新一个日志文件另存到用户选定的位置。</summary>
    private async void OnExportLog(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出日志",
            SuggestedFileName = $"vibrantbit-{DateTime.Now:yyyyMMdd}.log",
            DefaultExtension = "log",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("日志文件") { Patterns = new[] { "*.log" } },
                new FilePickerFileType("文本文件") { Patterns = new[] { "*.txt" } },
                FilePickerFileTypes.All
            }
        });

        var path = file?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
            _viewModel?.ExportLog(path);
    }

    /// <summary>把整个日志目录打包成 zip 导出（比单个文件更适合发给开发者）。</summary>
    private async void OnExportLogArchive(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出日志包",
            SuggestedFileName = $"vibrantbit-logs-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            DefaultExtension = "zip",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("压缩包") { Patterns = new[] { "*.zip" } },
                FilePickerFileTypes.All
            }
        });

        var path = file?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path) && _viewModel is not null)
            await _viewModel.ExportLogArchiveAsync(path);
    }

    /// <summary>选择 Minecraft 游戏文件夹。</summary>
    private async void OnSelectMinecraftFolder(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync("选择 Minecraft 文件夹");
        if (!string.IsNullOrEmpty(path) && _viewModel is not null)
            _viewModel.MinecraftFolder = path;
    }

    /// <summary>手动指定 java / javaw 可执行文件。</summary>
    private async void OnSelectJavaExe(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null || _viewModel is null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 java 可执行文件",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Java 可执行文件") { Patterns = new[] { "javaw.exe", "java.exe", "java", "javaw" } },
                FilePickerFileTypes.All
            }
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
        {
            _viewModel.UseManualJava = true;
            _viewModel.ManualJavaPath = path;
        }
    }

    /// <summary>把资源包目录挪到别的盘（通过目录链接）。</summary>
    private async void OnSelectResourcePacksFolder(object? sender, RoutedEventArgs e)
        => await RelocateAsync("resourcepacks", "选择资源包目录的新位置",
            path => _viewModel!.ResourcePacksFolder = path);

    /// <summary>把存档目录挪到别的盘（通过目录链接）。</summary>
    private async void OnSelectSavesFolder(object? sender, RoutedEventArgs e)
        => await RelocateAsync("saves", "选择存档目录的新位置",
            path => _viewModel!.SavesFolder = path);

    /// <summary>
    /// 选一个新位置 + 在原位置建链接。
    ///
    /// 两步是绑在一起的：只改设置不建链接，游戏根本读不到新目录；
    /// 只建链接不改设置，界面上看不到自己选了什么。所以这里必须一起做。
    /// </summary>
    private async Task RelocateAsync(string subFolder, string title, Action<string> store)
    {
        if (_viewModel is null)
            return;

        var target = await PickFolderAsync(title);
        if (string.IsNullOrEmpty(target))
            return;

        var gameRoot = SettingsService.ResolveMinecraftFolder();
        var linkPath = Path.Combine(gameRoot, subFolder);

        var error = FolderLinkService.Link(linkPath, target);
        if (error is not null)
        {
            AppServices.Notifications?.Warning(error);
            return;
        }

        store(target);
        AppServices.Notifications?.Success($"已迁移到 {target}");
    }

    /// <summary>从文件安装一个界面字体。</summary>
    private async void OnSelectFontFile(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null || _viewModel is null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择字体文件",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("字体文件") { Patterns = new[] { "*.ttf", "*.otf", "*.ttc" } },
                FilePickerFileTypes.All
            }
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
            return;

        // 字体族名必须从文件里读：Avalonia 的 FontFamily 需要 "来源#族名" 才知道用哪一个。
        var family = FontNameReader.TryReadFamilyName(path);
        if (string.IsNullOrEmpty(family))
        {
            AppServices.Notifications?.Warning("这个字体文件里读不到字体名，换一个试试");
            return;
        }

        _viewModel.SetFontFromFile(path, family);
        AppServices.Notifications?.Success($"已切换到 {family}");
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return null;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        return folders.FirstOrDefault()?.TryGetLocalPath();
    }
}

/// <summary>
/// 从 TTF / OTF 的 name 表里读出字体族名。
///
/// <para>
/// 自定义字体必须给出「文件 # 族名」这种形式，而族名只存在于文件内部的 name 表里
/// （nameID 1 是族名，nameID 16 是排版族名）。这里只解析这一张小表，
/// 不做完整字体解析 —— 读不到就返回 null，让上层提示用户换一个文件。
/// </para>
/// </summary>
internal static class FontNameReader
{
    public static string? TryReadFamilyName(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            var sfntVersion = reader.ReadUInt32();          // 0x00010000 / 'OTTO' / 'true'
            if (sfntVersion is not (0x00010000 or 0x4F54544F or 0x74727565))
                return null;

            // 表目录从偏移 12 开始：4 字节版本 + 2 字节表数 + 6 字节搜索参数
            stream.Position = 12;
            var tableCount = ReadUInt16BE(reader);

            long nameOffset = -1;
            int nameLength = 0;

            for (var i = 0; i < tableCount; i++)
            {
                var tag = Encoding.ASCII.GetString(reader.ReadBytes(4));
                reader.ReadUInt32();                        // checksum
                var offset = ReadUInt32BE(reader);
                var length = ReadUInt32BE(reader);

                if (tag == "name")
                {
                    nameOffset = offset;
                    nameLength = (int)length;
                    break;
                }
            }

            if (nameOffset < 0 || nameLength <= 0)
                return null;

            stream.Position = nameOffset;

            ReadUInt16BE(reader);                           // format
            var recordCount = ReadUInt16BE(reader);
            var stringOffset = ReadUInt16BE(reader);        // 相对 name 表起点

            string? fallback = null;

            for (var i = 0; i < recordCount; i++)
            {
                var platformId = ReadUInt16BE(reader);
                ReadUInt16BE(reader);                       // encodingId
                ReadUInt16BE(reader);                       // languageId
                var nameId = ReadUInt16BE(reader);
                var length = ReadUInt16BE(reader);
                var offset = ReadUInt16BE(reader);

                if (nameId is not (1 or 16) || length == 0)
                    continue;

                var back = stream.Position;
                stream.Position = nameOffset + stringOffset + offset;
                var bytes = reader.ReadBytes(length);
                stream.Position = back;

                // 平台 0 / 3 是 Unicode（UTF-16BE），平台 1 是 Mac Roman，这里只取 Unicode
                var text = platformId is 0 or 3
                    ? Encoding.BigEndianUnicode.GetString(bytes)
                    : Encoding.ASCII.GetString(bytes);

                text = text.Replace("\0", string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                if (nameId == 16)
                    return text;                            // 排版族名优先
                fallback ??= text;
            }

            return fallback;
        }
        catch
        {
            return null;
        }
    }

    private static ushort ReadUInt16BE(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(2);
        return bytes.Length < 2 ? (ushort)0 : BinaryPrimitives.ReadUInt16BigEndian(bytes);
    }

    private static uint ReadUInt32BE(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        return bytes.Length < 4 ? 0u : BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }
}
