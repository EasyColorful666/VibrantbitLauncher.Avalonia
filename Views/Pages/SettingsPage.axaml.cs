using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.LogicalTree;
using VibrantbitLauncher.Controls;
using VibrantbitLauncher.Helpers;
using VibrantbitLauncher.Models;
using VibrantbitLauncher.Services;
using VibrantbitLauncher.ViewModels.Pages;

namespace VibrantbitLauncher.Views.Pages;

public partial class SettingsPage : UserControl, INavigationAware
{
    private SettingsPageViewModel? _viewModel;

    /// <summary>六个面板，顺序与左栏 <c>Tag</c> 一一对应。构造里补一次，省得每次切换都重新拼数组。</summary>
    private ScrollViewer[] _panels = Array.Empty<ScrollViewer>();

    /// <summary>每一节上次滚到哪儿。切走再切回来时原地不动，而不是又跳回顶部。</summary>
    private readonly Dictionary<int, Vector> _scrollMemory = new();

    private int _activeCategory;

    /// <summary>从搜索结果点过来的那一行：切换面板后要滚到它、闪一下。</summary>
    private SettingRow? _pendingJumpRow;

    private SettingRow? _flashedRow;
    private DispatcherTimer? _flashTimer;

    /// <summary>搜索索引。懒建 —— 第一次敲字时才扫一遍视觉树。</summary>
    private List<SettingSearchHit>? _searchIndex;

    /// <summary>左栏六节的名字，顺序与 Tag 一致（搜索结果显示「属于哪一节」用）。</summary>
    private static readonly string[] SectionTitles = { "通用", "游戏", "外观", "网络", "高级", "关于" };

    public SettingsPage()
    {
        // 关键：必须在这里显式把 ViewModel 挂到 DataContext 上。
        // 本页是 DI 单例，若不赋值就会继承主窗口的 DataContext（MainWindowViewModel），
        // 结果整页绑定全部落空 —— 界面看起来「空白」，日志里刷满 "Could not find a matching
        // property accessor ... on MainWindowViewModel"。其他页面都是在构造里 DataContext = vm。
        _viewModel = AppServices.Provider?.GetService(typeof(SettingsPageViewModel)) as SettingsPageViewModel;
        DataContext = _viewModel;

        InitializeComponent();

        _panels = new[] { GeneralPanel, GamePanel, AppearancePanel, NetworkPanel, AdvancedPanel, AboutPanel };

        // InitializeComponent 期间 XAML 的 IsSelected="True" 会触发 SelectionChanged，
        // 那时容器还没建好，这里按当前选中项补一次。
        ApplyCurrentCategory();

        // 主题列表每次打开设置页都重扫：用户常常直接往 VBL/themes 里丢文件夹，
        // 不重扫就只能重启才看得见自己刚放进去的那套。
        _viewModel?.ReloadThemes();
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
        if (index < 0 || index >= _panels.Length)
            return;

        // 离开之前先把当前这一节滚到哪儿记下来
        if (_activeCategory != index && _activeCategory < _panels.Length)
            _scrollMemory[_activeCategory] = _panels[_activeCategory].Offset;

        for (var i = 0; i < _panels.Length; i++)
            _panels[i].IsVisible = i == index;

        _activeCategory = index;

        if (_pendingJumpRow is { } jumpRow)
        {
            // 从搜索跳过来的：定位到那一行比「还原上次滚到哪儿」重要，两者不叠加
            _pendingJumpRow = null;
            ScrollToRow(_panels[index], jumpRow, index);
        }
        else if (_scrollMemory.TryGetValue(index, out var offset) && offset.Y > 0.5)
        {
            RestoreOffset(_panels[index], offset);
        }

        PageTransitionHelper.PlayEnterTransition(_panels[index]);

        // 磁盘占用只在真的打开「高级」时才量：统计要枚举整个游戏目录，不该在进设置页时就跑
        if (index == 4)
            _viewModel?.EnsureDiskUsageLoaded();
    }

    /// <summary>
    /// 把滚动位置还原回去。
    ///
    /// <para>
    /// 不能直接赋值了事：切过去的第一帧里内容还没量完，ScrollViewer 的可滚动范围是 0，
    /// 赋什么值都会被夹成 0（表现就是「记住位置没生效」）。所以先等一次布局，
    /// 再盯几帧兜底，直到值真的对得上或放弃。
    /// </para>
    /// </summary>
    private static void RestoreOffset(ScrollViewer viewer, Vector offset)
    {
        var attempts = 0;

        void Apply()
        {
            if (Math.Abs(viewer.Offset.Y - offset.Y) <= 0.5)
                return;

            viewer.Offset = offset;
        }

        void OnLayout(object? sender, EventArgs e)
        {
            Apply();

            // 最多盯 5 帧：正常第二三帧就到位了，再多说明这一节内容比记的短，不必纠缠
            if (++attempts >= 5 || Math.Abs(viewer.Offset.Y - offset.Y) <= 0.5)
                viewer.LayoutUpdated -= OnLayout;
        }

        Dispatcher.UIThread.Post(Apply, DispatcherPriority.Loaded);
        viewer.LayoutUpdated += OnLayout;
    }

    /// <summary>
    /// 把某一节滚到指定那一行，并闪一下。
    ///
    /// <para>
    /// 没有用 <see cref="Control.BringIntoView()"/>：它按元素当前的 <c>Bounds</c> 算目标矩形，
    /// 而刚切过来的分区在同一帧里还没布局（<c>Bounds</c> 是 0），算出来就是「滚到原点」——
    /// 表现成「跳过去了但停在顶部」。折叠展开器里的行更糟：收起时它压根没有布局位置。
    /// 所以这里自己算：先把折叠的祖先展开，等布局稳定后再按行相对内容的位置直接设 <c>Offset</c>。
    /// </para>
    /// </summary>
    private void ScrollToRow(ScrollViewer viewer, SettingRow row, int section)
    {
        var expanded = ExpandAncestorsOf(row);

        // 展开有高度动画，行位置会连着变几帧，所以盯得久一些；没展开的话两三帧就稳
        var limit = expanded ? 30 : 6;
        var attempts = 0;
        var lastTarget = double.NaN;

        void Align()
        {
            var content = viewer.Content as Visual;
            var point = content is null || row.Bounds.Height <= 0 || content.Bounds.Height <= 0
                ? null
                : row.TranslatePoint(new Point(0, 0), content);

            if (point is null)
            {
                if (++attempts < limit)
                {
                    Dispatcher.UIThread.Post(Align, DispatcherPriority.Loaded);
                    return;
                }

                // 位置始终算不出来：至少把高亮打上，别让用户觉得点了没反应
                Flash(row);
                return;
            }

            var max = Math.Max(0, content!.Bounds.Height - viewer.Viewport.Height);

            // 上方留一点上下文，别让目标行正好贴着顶边
            var target = Math.Clamp(point.Value.Y - ScrollTopPadding, 0, max);
            viewer.Offset = new Vector(0, target);

            var reached = Math.Abs(viewer.Offset.Y - target) <= 0.5;
            var stable = !double.IsNaN(lastTarget) && Math.Abs(target - lastTarget) <= 0.5;
            lastTarget = target;

            if ((reached && stable) || ++attempts >= limit)
            {
                _scrollMemory[section] = viewer.Offset;
                Flash(row);
                return;
            }

            Dispatcher.UIThread.Post(Align, DispatcherPriority.Loaded);
        }

        Dispatcher.UIThread.Post(Align, DispatcherPriority.Loaded);
    }

    /// <summary>目标行上方留出的空白（像素）。</summary>
    private const double ScrollTopPadding = 24;

    /// <summary>
    /// 把目标行所在的所有折叠展开器都打开，返回「确实展开了」。
    ///
    /// <para>
    /// 用属性名判断而不是引 <c>SettingsExpander</c> 的类型：那一层控件来自 FluentAvalonia，
    /// 换控件或升级包时不该让这一行代码编译不过。展开器收起时其内容不参与布局，
    /// 不展开就永远算不出这一行在哪儿。
    /// </para>
    /// </summary>
    private static bool ExpandAncestorsOf(Control row)
    {
        var expanded = false;

        foreach (var ancestor in row.GetLogicalAncestors().OfType<Control>())
        {
            var property = ancestor.GetType().GetProperty("IsExpanded");
            if (property is not { CanRead: true, CanWrite: true }
                || property.PropertyType != typeof(bool)
                || property.GetValue(ancestor) is not false)
            {
                continue;
            }

            property.SetValue(ancestor, true);
            expanded = true;
        }

        return expanded;
    }

    /// <summary>给某一行刷一下底色，一秒多后自动褪掉。</summary>
    private void Flash(SettingRow row)
    {
        _flashTimer?.Stop();

        if (_flashedRow is not null)
            _flashedRow.Classes.Remove("searchHit");

        _flashedRow = row;
        row.Classes.Add("searchHit");

        _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.7) };
        _flashTimer.Tick += (_, _) =>
        {
            _flashTimer?.Stop();
            _flashedRow?.Classes.Remove("searchHit");
            _flashedRow = null;
        };
        _flashTimer.Start();
    }

    // ==================== 设置搜索 ====================

    /// <summary>
    /// 扫一遍六个面板里的所有 <see cref="SettingRow"/>，建一份可搜索的索引。
    ///
    /// <para>
    /// 索引的是<b>控件本身</b>而不是配置字段名：搜索要的结果是「点一下就跳过去」，
    /// 而「跳到哪儿」只有控件知道。也因此不需要在 ViewModel 里维护一份与 XAML 平行的清单
    /// （那种清单一定会和界面走散）。
    /// </para>
    ///
    /// <para>
    /// 走<b>逻辑树</b>而不是视觉树：没显示过的分区从不参与布局，模板也就没套上，
    /// 视觉树里连 ScrollViewer 的 ContentPresenter 都还没有 —— 用视觉树建索引会漏掉
    /// 除当前分区以外的全部设置项（表现是「只有第一节搜得到」）。
    /// 逻辑树在 XAML 解析完就完整，不受显示状态影响。
    /// </para>
    /// </summary>
    private void EnsureSearchIndex()
    {
        if (_searchIndex is not null)
            return;

        _searchIndex = new List<SettingSearchHit>();

        for (var i = 0; i < _panels.Length; i++)
        {
            foreach (var row in _panels[i].GetLogicalDescendants().OfType<SettingRow>())
            {
                var header = row.Header;
                if (string.IsNullOrWhiteSpace(header))
                    continue;

                _searchIndex.Add(new SettingSearchHit(header, SectionTitles[i], i, row));
            }
        }
    }

    /// <summary>
    /// 口语化的近义词 → 设置项里的叫法。
    ///
    /// <para>
    /// 用户搜的是「深色」「网速」「卡」，而界面上写的是「明暗」「速度限制」「性能模式」。
    /// 让搜索认这些词，比要求用户记住我们的措辞现实得多。
    /// </para>
    /// </summary>
    private static readonly (string Keyword, string[] Targets)[] Synonyms =
    {
        ("内存", new[] { "内存", "自动推荐", "初始内存", "最大内存" }),
        ("字体", new[] { "字体" }),
        ("深色", new[] { "明暗", "主题" }),
        ("暗色", new[] { "明暗", "主题" }),
        ("黑夜", new[] { "明暗" }),
        ("亮色", new[] { "明暗" }),
        ("主题", new[] { "主题", "明暗" }),
        ("壁纸", new[] { "背景", "内置壁纸" }),
        ("模糊", new[] { "毛玻璃" }),
        ("透明", new[] { "不透明度" }),
        ("网速", new[] { "速度限制", "下载源" }),
        ("限速", new[] { "速度限制" }),
        ("慢", new[] { "下载源", "速度限制", "性能模式" }),
        ("卡", new[] { "性能模式", "动画档位", "垃圾回收" }),
        ("日志", new[] { "记录等级", "保留天数", "日志目录" }),
        ("存档", new[] { "存档", "版本隔离" }),
        ("语言", new[] { "界面语言" }),
        ("英文", new[] { "界面语言" }),
        ("重置", new[] { "恢复默认", "重置设置" }),
        ("空间", new[] { "磁盘占用" }),
        ("备份", new[] { "配置备份" })
    };

    /// <summary>按当前搜索词筛出结果，按「标题命中 &gt; 说明命中 &gt; 近义词命中」排序。</summary>
    private List<SettingSearchHit> Search(string query)
    {
        if (_searchIndex is null)
            return new List<SettingSearchHit>();

        var scored = new List<(SettingSearchHit Hit, int Score)>();

        foreach (var hit in _searchIndex)
        {
            var score = 0;

            var at = hit.Header.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
                score = 100 - Math.Min(at, 20);          // 越靠前越相关

            if (score == 0)
            {
                // 说明文字里的命中：仍然要能找到，但排在标题命中之后
                // （说明来自 SettingRow.Description，这里通过控件取，索引里不重复存一份）
                if (hit.Target is SettingRow { Description: { } description }
                    && description.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    score = 60;
                }
            }

            if (score == 0)
            {
                foreach (var (keyword, targets) in Synonyms)
                {
                    if (!keyword.Contains(query, StringComparison.OrdinalIgnoreCase)
                        && !query.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (targets.Any(t => hit.Header.Contains(t, StringComparison.OrdinalIgnoreCase)))
                    {
                        score = 40;
                        break;
                    }
                }
            }

            if (score > 0)
                scored.Add((hit, score));
        }

        return scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Hit.SectionIndex)
            .Select(x => x.Hit)
            .Take(40)
            .ToList();
    }

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (SearchBox is null || SearchResultList is null)
            return;

        var query = SearchBox.Text?.Trim() ?? string.Empty;

        if (query.Length == 0)
        {
            SearchResultList.IsVisible = false;
            SearchResultList.ItemsSource = null;
            SearchEmptyHint.IsVisible = false;
            if (CategoryList is not null)
                CategoryList.IsVisible = true;
            return;
        }

        EnsureSearchIndex();

        var hits = Search(query);

        CategoryList.IsVisible = false;
        SearchResultList.ItemsSource = hits;
        SearchResultList.IsVisible = hits.Count > 0;
        SearchEmptyHint.IsVisible = hits.Count == 0;
        SearchEmptyHint.Text = hits.Count == 0 ? BuildSearchHint(query) : string.Empty;
    }

    /// <summary>
    /// 一条都没搜到时给点线索：按「两字组合」找最像的几项。
    /// 比干巴巴一句「没有结果」有用 —— 用户往往是词没对上，而不是设置不存在。
    /// </summary>
    private string BuildSearchHint(string query)
    {
        if (query.Length < 2 || _searchIndex is null)
            return "没找到匹配的设置，换个更短的词试试";

        var pairs = new List<string>();
        for (var i = 0; i < query.Length - 1; i++)
            pairs.Add(query.Substring(i, 2));

        var close = _searchIndex
            .Select(hit => (Hit: hit, Score: pairs.Count(p => hit.Header.Contains(p, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(3)
            .Select(x => x.Hit.Header)
            .ToList();

        return close.Count > 0
            ? "没找到匹配的设置。相近的有：" + string.Join("、", close)
            : "没找到匹配的设置，换个更短的词试试";
    }

    private void SearchResultList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SearchResultList?.SelectedItem is not SettingSearchHit hit)
            return;

        // 立刻清掉选中：不然再点同一条不会触发 SelectionChanged，用户会以为点了没反应
        SearchResultList.SelectedItem = null;

        _pendingJumpRow = hit.Target as SettingRow;

        // 用 _activeCategory 判断，而不是 CategoryList.SelectedIndex：搜索时左栏是隐藏的，
        // 隐藏的 ListBox 改选中项未必会走 SelectionChanged。下面再核一次，没切过去就自己接管。
        if (_activeCategory != hit.SectionIndex && CategoryList is not null)
        {
            CategoryList.SelectedIndex = hit.SectionIndex;   // 正常情况下由 ApplyCurrentCategory 接手定位

            // 事件没触发（左栏隐藏时可能出现）：直接补一次。已经切过去就不重复调，
            // 否则会走「还原上次滚动位置」那条分支，把刚定位好的位置冲掉。
            if (_activeCategory != hit.SectionIndex)
                ApplyCurrentCategory();
        }
        else if (_pendingJumpRow is { } row)
        {
            // 已经在目标分区里了，直接从这儿滚过去
            _pendingJumpRow = null;
            ScrollToRow(_panels[_activeCategory], row, _activeCategory);
        }
    }

    /// <summary>某一节「本区恢复默认」。</summary>
    private async void OnResetSection(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not Button { Tag: string section })
            return;

        var title = section switch
        {
            "general" => "通用",
            "game" => "游戏",
            "appearance" => "外观",
            "network" => "网络",
            "advanced" => "高级",
            _ => "设置"
        };

        await _viewModel.ResetSectionAsync(section, title);
    }

    /// <summary>导出配置备份。</summary>
    private async void OnExportSettings(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null || _viewModel is null)
            return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出设置备份",
            SuggestedFileName = $"vibrantbit-settings-{DateTime.Now:yyyyMMdd}.json",
            DefaultExtension = "json",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("设置备份") { Patterns = new[] { "*.json" } },
                FilePickerFileTypes.All
            }
        });

        var path = file?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
            _viewModel.ExportBackup(path);
    }

    /// <summary>导入一份主题包（zip）。</summary>
    private async void OnImportTheme(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null || _viewModel is null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入主题包",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("主题包") { Patterns = new[] { "*.zip" } },
                FilePickerFileTypes.All
            }
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
            await _viewModel.ImportThemeAsync(path);
    }

    /// <summary>在资源管理器里打开主题目录。用文件夹形式做主题的人主要走这条路。</summary>
    private void OnOpenThemeFolder(object? sender, RoutedEventArgs e)
        => ThemeService.OpenThemeDirectory();

    /// <summary>删掉当前选中的那套用户主题（确认框在 ViewModel 里）。</summary>
    private async void OnDeleteTheme(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
            await _viewModel.DeleteThemeAsync();
    }

    /// <summary>导入配置备份。</summary>
    private async void OnImportSettings(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null || _viewModel is null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入设置备份",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("设置备份") { Patterns = new[] { "*.json" } },
                FilePickerFileTypes.All
            }
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
            await _viewModel.ImportBackupAsync(path);
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
