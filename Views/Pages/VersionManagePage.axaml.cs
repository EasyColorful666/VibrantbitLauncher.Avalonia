using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Interactivity;
using VibrantbitLauncher.Services;
using VibrantbitLauncher.ViewModels.Pages;

namespace VibrantbitLauncher.Views.Pages;

public partial class VersionManagePage : UserControl
{
    private readonly VersionManageViewModel? _viewModel;

    public VersionManagePage()
    {
        _viewModel = AppServices.Provider?.GetService(typeof(VersionManageViewModel)) as VersionManageViewModel;
        DataContext = _viewModel;
        InitializeComponent();

        // 列表内容变化时同步「空状态」提示（加载是异步的，光靠切标签会漏更新）
        if (_viewModel is not null)
        {
            _viewModel.Mods.CollectionChanged += OnCollectionChanged;
            _viewModel.Saves.CollectionChanged += OnCollectionChanged;
        }

        ShowMods();

        // 页面是单例，切走再切回来不会重建。每次重新进入都重读一次目录与列表，
        // 否则「启动页 → 设置」第二次进来看到的还是上次的内容（用户感知即「不刷新」）。
        Loaded += (_, _) => _viewModel?.LoadCommand.Execute(null);
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyHint();

    private void ModsTab_Click(object? sender, RoutedEventArgs e) => ShowMods();

    private void SavesTab_Click(object? sender, RoutedEventArgs e) => ShowSaves();

    private void OpenFolder_Click(object? sender, RoutedEventArgs e)
        => _viewModel?.OpenGameFolderCommand.Execute(null);

    private void ShowMods()
    {
        ModsList.IsVisible = true;
        ModsToolbar.IsVisible = true;
        SavesList.IsVisible = false;
        SavesToolbar.IsVisible = false;

        ModsTab.Classes.Add("accent");
        SavesTab.Classes.Remove("accent");

        UpdateEmptyHint();
    }

    private void ShowSaves()
    {
        ModsList.IsVisible = false;
        ModsToolbar.IsVisible = false;
        SavesList.IsVisible = true;
        SavesToolbar.IsVisible = true;

        ModsTab.Classes.Remove("accent");
        SavesTab.Classes.Add("accent");

        UpdateEmptyHint();
    }

    private void UpdateEmptyHint()
    {
        var showingMods = ModsList.IsVisible;
        var count = showingMods ? ModsList.ItemCount : SavesList.ItemCount;

        EmptyHint.Text = showingMods ? "没有找到模组" : "没有找到存档";
        EmptyHint.IsVisible = count == 0;
    }
}
