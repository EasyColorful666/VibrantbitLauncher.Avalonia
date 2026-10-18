using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Messaging;
using VibrantbitLauncher.Helpers;
using VibrantbitLauncher.Models;
using VibrantbitLauncher.Services;

namespace VibrantbitLauncher.Views.Pages;

/// <summary>
/// 下载中心：左侧分类单选栏 + 右侧内容区。
///
/// 每个分类独占一个页面实例，切换只改 IsVisible ——
/// 避免内容容器反复换内容导致整棵视觉树被卸载重建（切换卡顿的根源）。
///
/// 实现 <see cref="INavigationAware"/>：**每次进入下载中心都刷新当前分类的数据**，
/// 保证用户看到的是最新的资源与版本列表（配合每个页面自身的节流，不会重复请求）。
/// </summary>
public partial class DownloadHubPage : UserControl, INavigationAware
{
    /// <summary>
    /// 分类序号 → Modrinth 项目类型；0 是原版游戏，不走 Modrinth。
    /// 顺序必须与 XAML 里左侧 ListBox 的 Tag 一一对应（1=模组、2=整合包、3=数据包、4=资源包、5=光影）。
    /// </summary>
    private static readonly string[] ResourceTypes =
    {
        ModrinthSearchService.TypeMod,
        ModrinthSearchService.TypeModpack,
        ModrinthSearchService.TypeDatapack,
        ModrinthSearchService.TypeResourcePack,
        ModrinthSearchService.TypeShader
    };

    private readonly Dictionary<int, Control> _resourcePages = new();
    private DownloadPage? _downloadPage;

    /// <summary>上一次显示的分类序号；-1 表示还没显示过。</summary>
    private int _lastShownIndex = -1;

    public DownloadHubPage()
    {
        InitializeComponent();

        // InitializeComponent 期间 XAML 的 IsSelected="True" 会触发 SelectionChanged，
        // 那时容器还没建好，这里按当前选中项补一次。
        ApplyCurrentCategory();

        // 接收下载任务中心的分类切换请求（0 = 原版游戏，1 = Mod）
        WeakReferenceMessenger.Default.Register<DownloadHubTabMessage>(this, (_, message) => SelectCategory(message.Index));
    }

    /// <summary>导航进入下载中心：把当前显示的分类刷新一遍。</summary>
    Task INavigationAware.OnNavigatedToAsync()
    {
        RefreshCategory(CurrentCategoryIndex);
        return Task.CompletedTask;
    }

    Task INavigationAware.OnNavigatedFromAsync() => Task.CompletedTask;

    /// <summary>左侧当前选中的分类序号；取不到时返回 -1。</summary>
    private int CurrentCategoryIndex
        => CategoryList?.SelectedItem is ListBoxItem { Tag: string tag } && int.TryParse(tag, out var index)
            ? index
            : -1;

    private void CategoryList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        => ApplyCurrentCategory();

    /// <summary>按左侧当前选中项切换右侧内容；分组标题（Tag 非数字）会被忽略。</summary>
    private void ApplyCurrentCategory()
    {
        if (HostGrid == null || CategoryList == null)
            return;

        if (CategoryList.SelectedItem is not ListBoxItem item
            || item.Tag is not string tag
            || !int.TryParse(tag, out var index))
        {
            return;
        }

        var watch = Stopwatch.StartNew();

        if (index <= 0)
            ShowVersionPage();
        else
            ShowResourcePage(index);

        Serilog.Log.Information("[Hub] 切换分类 {Index}，耗时 {Ms}ms（首次含页面构造）", index, watch.ElapsedMilliseconds);

        // 分类**发生切换**时刷新一次数据；首次显示不刷（页面刚建好，它自己的初始化正在拉）。
        if (_lastShownIndex != index)
        {
            var isFirstShow = _lastShownIndex == -1;
            _lastShownIndex = index;

            if (!isFirstShow)
                RefreshCategory(index);
        }
    }

    /// <summary>刷新指定分类的数据。页面还没创建 / 类型对不上时静默跳过。</summary>
    private void RefreshCategory(int index)
    {
        if (index < 0)
            return;

        if (index == 0)
        {
            _downloadPage?.RefreshVersions();
            return;
        }

        if (_resourcePages.TryGetValue(index, out var page) && page is DownloadResourcesPage resources)
            resources.RefreshData();
    }

    private void ShowVersionPage()
    {
        _downloadPage ??= ResolveOrCreate<DownloadPage>();
        if (_downloadPage != null && !HostGrid.Children.Contains(_downloadPage))
            HostGrid.Children.Add(_downloadPage);

        if (_downloadPage != null)
        {
            _downloadPage.IsVisible = true;
            PageTransitionHelper.PlayEnterTransition(_downloadPage);
        }

        foreach (var page in _resourcePages.Values)
            page.IsVisible = false;
    }

    private void ShowResourcePage(int index)
    {
        if (!_resourcePages.TryGetValue(index, out var page))
        {
            var projectType = index - 1 < ResourceTypes.Length ? ResourceTypes[index - 1] : ResourceTypes[0];

            // 每个分类必须是**独立实例**。
            //
            // 注意不要改成 AppServices.Provider.GetService<DownloadResourcesPage>()：
            // 本页面（以及它的 ViewModel）是「一分类一实例」的用法，若从 DI 拿单例，
            // 所有分类会共用同一个控件对象，第二次 HostGrid.Children.Add 就会因
            // 「already has a visual parent」抛 InvalidOperationException（切到光影即崩）；
            // 即便侥幸不崩，五个分类的筛选条件 / 分页游标 / 搜索结果也会互相串数据。
            page = new DownloadResourcesPage();
            (page as DownloadResourcesPage)?.SetProjectType(projectType);

            page.IsVisible = false;

            // 双保险：万一将来有别的路径把它加进来过，这里不再重复 Add
            if (!HostGrid.Children.Contains(page))
                HostGrid.Children.Add(page);

            _resourcePages[index] = page;
        }

        if (_downloadPage != null)
            _downloadPage.IsVisible = false;

        foreach (var pair in _resourcePages)
            pair.Value.IsVisible = pair.Key == index;

        if (_resourcePages.TryGetValue(index, out var shown))
            PageTransitionHelper.PlayEnterTransition(shown);
    }

    /// <summary>按分类序号选中左侧对应项；内容切换由 SelectionChanged 统一驱动。</summary>
    private void SelectCategory(int index)
    {
        var target = CategoryList.Items
            .OfType<ListBoxItem>()
            .FirstOrDefault(li => li.Tag is string tag && tag == index.ToString());

        if (target != null && !ReferenceEquals(CategoryList.SelectedItem, target))
            CategoryList.SelectedItem = target;
    }

    private static T? ResolveOrCreate<T>() where T : Control
    {
        if (AppServices.Provider?.GetService(typeof(T)) is T fromDi)
            return fromDi;

        return System.Activator.CreateInstance<T>();
    }
}
