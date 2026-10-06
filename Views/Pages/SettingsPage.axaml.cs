using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
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

        // 顺序必须与左栏 ListBoxItem 的 Tag 一一对应
        var panels = new[] { HomePanel, LaunchPanel, PersonalizationPanel, NetworkPanel, LogPanel, AboutPanel };
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

    /// <summary>选择 Minecraft 游戏文件夹。</summary>
    private async void OnSelectMinecraftFolder(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择 Minecraft 文件夹",
            AllowMultiple = false
        });

        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path) && _viewModel is not null)
            _viewModel.MinecraftFolder = path;
    }
}
