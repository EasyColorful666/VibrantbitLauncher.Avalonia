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
using VibrantbitLauncher.Helpers;
using VibrantbitLauncher.Models;
using VibrantbitLauncher.Services;
using VibrantbitLauncher.Services.Download;

namespace VibrantbitLauncher.Views.Pages;

public partial class ModPage : UserControl
{
    /// <summary>两个筛选下拉里代表「不筛选」的条目文字。</summary>
    private const string AllOption = "全部";

    private string _projectId = string.Empty;
    private string _projectType = ModrinthSearchService.TypeMod;

    /// <summary>
    /// 当前项目类型在实例内的子目录（mods / resourcepacks / shaderpacks）。
    /// 不能直接放进实例的类型（整合包、数据包）为 null，此时不提供「添加到实例」入口。
    /// </summary>
    private string? _instanceSubfolder;

    /// <summary>本地已安装实例（含各自的加载器），顺序与下拉框条目一一对应。</summary>
    private List<GameInstance> _instances = new();

    private readonly ObservableCollection<ModrinthModInfo> _modrinthModInfos = new();
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

    /// <summary>
    /// 项目类型 → 实例内的子目录。
    ///
    /// 整合包要解包安装进版本目录、数据包要放进具体存档，都不是「拷一个文件进实例」能表达的动作，
    /// 因此返回 null —— 这两类只保留「另存为」，不给出会落错位置的入口。
    /// </summary>
    private static string? InstanceSubfolder(string projectType) => projectType switch
    {
        ModrinthSearchService.TypeMod => "mods",
        ModrinthSearchService.TypeResourcePack => "resourcepacks",
        ModrinthSearchService.TypeShader => "shaderpacks",
        _ => null
    };

    /// <summary>
    /// 只有「模组」需要挑加载器：资源包 / 光影与加载器无关（它们由游戏或 Iris/OptiFine 读取，
    /// 装在任何实例里都一样），对它们做加载器拦截只会误伤。
    /// </summary>
    private bool RequiresLoaderMatch => _projectType == ModrinthSearchService.TypeMod;

    public async Task LoadForProject(string projectId, string? projectType = null)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            return;

        _projectId = projectId;
        _projectType = string.IsNullOrWhiteSpace(projectType) ? ModrinthSearchService.TypeMod : projectType;
        _instanceSubfolder = InstanceSubfolder(_projectType);

        ApplyProjectTypeChrome();

        // 先扫实例再拉文件列表：列表项上的「添加到实例」按钮是否出现，取决于此刻有没有匹配的实例
        await Dispatcher.UIThread.InvokeAsync(RefreshInstances);

        await LoadFileInfos(projectId);
    }

    /// <summary>
    /// 按资源类型调整这一页的外观。
    ///
    /// 这个页面被模组 / 整合包 / 资源包 / 光影 / 数据包五种资源共用，标题与提示文案必须跟着变 ——
    /// 否则打开一个整合包详情页，抬头写着「模组文件」「模组加载器」，用户会以为点错进了别的地方。
    /// </summary>
    private void ApplyProjectTypeChrome()
    {
        var (title, subtitle, filterLabel, needsLoaderFilter) = _projectType switch
        {
            ModrinthSearchService.TypeModpack => (
                "整合包文件",
                "选择一个版本下载安装；整合包会新建一个实例，自动装好 Minecraft、加载器与包内模组",
                "模组加载器", true),

            ModrinthSearchService.TypeResourcePack => (
                "资源包文件",
                "选择游戏版本查看可下载的资源包；可添加到任意实例（资源包与加载器无关）",
                string.Empty, false),

            ModrinthSearchService.TypeShader => (
                "光影文件",
                "选择游戏版本查看可下载的光影；可添加到任意实例（光影与加载器无关，需 Iris / OptiFine）",
                string.Empty, false),

            ModrinthSearchService.TypeDatapack => (
                "数据包文件",
                "选择游戏版本查看可下载的数据包；数据包要放进具体存档，请使用「另存为」",
                string.Empty, false),

            _ => (
                "模组文件",
                "选择游戏版本与加载器，查看可下载的模组文件；只有加载器匹配的实例才能直接添加",
                "模组加载器", true),
        };

        headerTitle.Text = title;
        headerSubtitle.Text = subtitle;
        loaderLabel.Text = filterLabel;
        loaderLabel.IsVisible = needsLoaderFilter;
        comboBox1.IsVisible = needsLoaderFilter;
    }

    // ==================== 目标实例 ====================

    /// <summary>
    /// 重新枚举本地实例并刷新下拉框。
    /// 一个实例都没有（或该类型不能直接放进实例）时整行隐藏，列表里只剩「另存为」。
    /// 下拉框条目与 <see cref="_instances"/> 同序，因此用 SelectedIndex 就能取到对应实例。
    /// </summary>
    private void RefreshInstances()
    {
        _instances = GameInstanceHelper.GetInstances();

        instanceComboBox.Items.Clear();
        foreach (var instance in _instances)
            instanceComboBox.Items.Add(instance.DisplayName);

        var showPanel = _instances.Count > 0 && _instanceSubfolder is not null;
        instancePanel.IsVisible = showPanel;

        if (showPanel && instanceComboBox.SelectedIndex < 0)
            instanceComboBox.SelectedIndex = 0;

        UpdateInstanceHint();
        RefreshInstanceCompat();
    }

    /// <summary>当前下拉框选中的实例；没选中或索引越界时为 null。</summary>
    private GameInstance? SelectedInstance
    {
        get
        {
            var index = instanceComboBox.SelectedIndex;
            return index >= 0 && index < _instances.Count ? _instances[index] : null;
        }
    }

    private void instanceComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateInstanceHint();
        RefreshInstanceCompat();
    }

    /// <summary>
    /// 刷新落点提示：把「加载器」与「版本隔离」两个判断结果直接写出来，
    /// 用户不必猜文件到底进了哪个实例、哪个目录。
    /// </summary>
    private void UpdateInstanceHint()
    {
        if (_instanceSubfolder is null)
        {
            instanceHintText.Text = string.Empty;
            return;
        }

        var instance = SelectedInstance;
        if (instance is null)
        {
            instanceHintText.Text = "尚未选择实例";
            return;
        }

        var isolation = GameInstanceHelper.IsVersionIsolated ? "版本隔离：开" : "版本隔离：关";
        var target = GameInstanceHelper.DescribeInstanceFolder(instance.Id, _instanceSubfolder);
        instanceHintText.Text = $"{instance.LoaderName} · {isolation} → {target}";
    }

    /// <summary>
    /// 这个文件能不能装进当前选中的实例。
    ///
    /// 模组必须加载器匹配：Forge 模组只能进 Forge 实例，Fabric 模组只能进 Fabric / Quilt 实例，
    /// 原版实例（没有加载器）一律只能「另存为」。资源包 / 光影不受此限制。
    /// </summary>
    private bool CanAddToInstance(ModrinthModInfo item)
    {
        if (_instanceSubfolder is null)
            return false;

        var instance = SelectedInstance;
        if (instance is null)
            return false;

        if (!RequiresLoaderMatch)
            return true;

        return ModLoaderHelper.CanLoad(instance, item.RequiredLoaders);
    }

    /// <summary>选中实例变化后，同步每一行「添加到实例」按钮的显隐。</summary>
    private void RefreshInstanceCompat()
    {
        foreach (var item in _modrinthModInfos)
            item.ShowInstanceButton = CanAddToInstance(item);
    }

    /// <summary>
    /// 列表加载完后挑一个默认实例：优先选「能装上其中至少一个文件」的实例。
    /// 否则默认选中第一个实例（很可能是原版），整页按钮全灭，用户会以为功能坏了。
    /// </summary>
    private void SelectDefaultInstance()
    {
        if (_instances.Count == 0)
            return;

        var index = 0;

        if (RequiresLoaderMatch && _modrinthModInfos.Count > 0)
        {
            var matched = _instances.FindIndex(instance =>
                _modrinthModInfos.Any(item => ModLoaderHelper.CanLoad(instance, item.RequiredLoaders)));
            if (matched >= 0)
                index = matched;
        }

        if (instanceComboBox.SelectedIndex != index)
            instanceComboBox.SelectedIndex = index;
    }

    // ==================== 文件列表 ====================

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

            foreach (var modInfo in modInfos)
            {
                // ModLoaders 是 MinecraftLaunch 的 ModLoaderType 枚举（Fabric / Forge / NeoForge / Quilt /
                // LiteLoader / OptiFine / Any / Unknown），转成字符串后统一走 ModLoaderHelper.ParseName，
                // 这样实例侧（枚举）和 Modrinth 侧（字符串）用的是同一张映射表。
                var loaders = modInfo.ModLoaders?.Select(x => x.ToString()).ToList() ?? new List<string>();

                _modrinthModInfos.Add(new ModrinthModInfo
                {
                    Name = modInfo.DisplayName,
                    DownloadUrl = modInfo.DownloadUrl,
                    FileName = modInfo.FileName ?? string.Empty,
                    RequiredLoaders = loaders,
                    GameVersions = modInfo.MinecraftVersions?.ToList() ?? new List<string>(),
                    LoaderHint = RequiresLoaderMatch ? ModLoaderHelper.DescribeRequired(loaders) : string.Empty,
                    ShowLoaderHint = RequiresLoaderMatch,
                    // 整合包走「安装整合包」而不是「添加到实例」：它的语义是新建实例。
                    ShowInstallModpackButton = _projectType == ModrinthSearchService.TypeModpack,
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
                comboBox.Items.Add(AllOption);
                foreach (var ext in extensions)
                    comboBox.Items.Add(ext.McVersion);
                listBox.ItemsSource = _modrinthModInfos;
                comboBox.SelectedIndex = 0;

                // 选出一个真能用的实例，再按它刷新按钮显隐
                SelectDefaultInstance();
                RefreshInstanceCompat();
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

    /// <summary>
    /// 按游戏版本 + 加载器筛选。两个条件都作用在**同一批对象**上（不重建列表项），
    /// 否则筛选后新造出来的行会丢掉当前的「添加到实例」显隐状态。
    /// </summary>
    private void ApplyFilter()
    {
        var version = comboBox.SelectedItem as string;
        if (version == AllOption)
            version = null;

        var loader = ModLoaderHelper.ParseName(ComboItemText(comboBox1.SelectedItem));

        var view = _modrinthModInfos
            .Where(item => MatchesVersion(item, version) && MatchesLoader(item, loader))
            .ToList();

        listBox.ItemsSource = view;
    }

    /// <summary>
    /// 游戏版本筛选：优先用 Modrinth 标注的 <c>game_versions</c>（真正的支持列表），
    /// 取不到时才退回「文件名里含这个版本号」的旧做法。
    /// </summary>
    private static bool MatchesVersion(ModrinthModInfo item, string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return true;

        if (item.GameVersions.Count > 0)
            return item.GameVersions.Any(v => string.Equals(v, version, StringComparison.OrdinalIgnoreCase));

        return item.FileName.Contains(version, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>加载器筛选：按文件标注的加载器比对（未指定筛选时全通过）。</summary>
    private static bool MatchesLoader(ModrinthModInfo item, ModLoaderKind? loader)
    {
        if (loader is null)
            return true;

        return ModLoaderHelper.ParseAll(item.RequiredLoaders).Contains(loader.Value);
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
        => ApplyFilter();

    private void comboBox1_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        => ApplyFilter();

    /// <summary>
    /// 取下拉条目的文字。
    /// comboBox1 的条目是 <c>&lt;ComboBoxItem Content="Fabric" /&gt;</c>，<c>SelectedItem</c> 拿到的是
    /// ComboBoxItem 而不是 string —— 原来直接 <c>as string</c> 恒为 null，这个筛选其实一直没生效过。
    /// </summary>
    private static string? ComboItemText(object? item) => item switch
    {
        ComboBoxItem comboBoxItem => comboBoxItem.Content as string,
        string text => text,
        _ => null
    };

    // ==================== 下载 ====================

    private async void OnSaveAsClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ModrinthModInfo info })
            await SaveModAsAsync(info.DownloadUrl);
        else
            AppServices.Notifications?.Error("无法获取文件下载链接");
    }

    private async void OnAddToInstanceClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ModrinthModInfo info })
            await AddToInstanceAsync(info);
        else
            AppServices.Notifications?.Error("无法获取文件下载链接");
    }

    /// <summary>
    /// 「安装整合包」：下载 .mrpack 到临时目录，再交给协调器新建实例。
    ///
    /// 不同于「添加到实例」（拷一个文件进去），整合包必须**先完整下载到本地**才能解包 ——
    /// 所以这里多一步「下到临时文件」，下完立即安装，装完就删掉临时文件（不留在用户机器上占地）。
    /// </summary>
    private async void OnInstallModpackClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ModrinthModInfo info })
        {
            AppServices.Notifications?.Error("无法获取文件下载链接");
            return;
        }

        if (string.IsNullOrWhiteSpace(info.DownloadUrl))
        {
            AppServices.Notifications?.Warning("该文件没有有效的下载链接");
            return;
        }

        if (ModpackInstallCoordinator.IsRunning)
        {
            AppServices.Notifications?.Warning("已有整合包正在安装，请等它完成后再试");
            return;
        }

        // 下载到临时目录：文件名沿用 Modrinth 的原始名（保留 .mrpack 扩展名，解包时靠内容也认得出）
        var tempDir = Path.Combine(Path.GetTempPath(), "VibrantbitLauncher", "modpacks");
        string tempFile;
        try
        {
            Directory.CreateDirectory(tempDir);
            tempFile = Path.Combine(tempDir, GetDownloadFileName(info.DownloadUrl));
        }
        catch (Exception ex)
        {
            AppServices.Notifications?.Error($"无法创建临时目录：{ex.Message}");
            return;
        }

        if (_isDownloading)
        {
            AppServices.Notifications?.Warning("已有下载任务正在进行，请稍候");
            return;
        }

        var taskService = AppServices.Provider?.GetService(typeof(DownloadTaskService)) as DownloadTaskService;
        var downloadTask = taskService?.CreateTask($"下载整合包：{info.Name}", DownloadTaskType.ModDownload);

        _isDownloading = true;
        var ok = false;
        try
        {
            AppServices.Notifications?.Show($"正在下载「{info.Name}」…", "安装整合包", NotificationSeverity.Informational);

            using (var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(300) })
            {
                using var response = await httpClient.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                await using var contentStream = await response.Content.ReadAsStreamAsync();
                await using var fileStream = new FileStream(
                    tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                var buffer = new byte[81920];
                long totalRead = 0;
                long lastReported = 0;
                int read;
                var sw = System.Diagnostics.Stopwatch.StartNew();

                while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, read);
                    totalRead += read;

                    if (totalBytes > 0 && sw.ElapsedMilliseconds >= 200)
                    {
                        var percent = (int)(totalRead * 100 / totalBytes);
                        var speed = (totalRead - lastReported) / sw.Elapsed.TotalSeconds / 1024 / 1024;
                        taskService?.UpdateProgress(downloadTask, percent, $"{speed:F1} MB/s",
                            $"{totalRead / 1024 / 1024:F1}/{totalBytes / 1024 / 1024:F1} MB");
                        lastReported = totalRead;
                        sw.Restart();
                    }
                }

                await fileStream.FlushAsync();
                ok = true;
            }

            taskService?.CompleteTask(downloadTask, "下载完成，开始安装");
        }
        catch (Exception ex)
        {
            var message = DescribeDownloadError(ex);
            taskService?.FailTask(downloadTask, message);
            AppServices.Notifications?.Error(message, "整合包下载失败");
        }
        finally
        {
            _isDownloading = false;
        }

        if (!ok)
        {
            TryDeletePartialFile(tempFile);
            return;
        }

        // 下载完成 → 走与拖入完全相同的安装流程
        try
        {
            var instanceId = await ModpackInstallCoordinator.InstallAsync(tempFile, "下载中心");
            if (instanceId is not null)
            {
                CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default
                    .Send<VibrantbitLauncher.Models.InstancesChangedMessage, string>(
                        new VibrantbitLauncher.Models.InstancesChangedMessage(), "InstancesChanged");
            }
        }
        finally
        {
            // 安装器已经把内容解包进实例了，临时包留着只会白占几百 MB
            TryDeletePartialFile(tempFile);
        }
    }

    /// <summary>「另存为」：走系统保存对话框，位置由用户决定。</summary>
    private async Task SaveModAsAsync(string downloadUrl)
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
            SuggestedFileName = GetDownloadFileName(downloadUrl),
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
        try
        {
            await DownloadFileAsync(downloadUrl, savePath);
        }
        finally
        {
            _isDownloading = false;
        }
    }

    /// <summary>
    /// 「添加到实例」：直接下到所选实例的对应目录，不弹保存对话框。
    ///
    /// 落点由「版本隔离」决定（<see cref="GameInstanceHelper"/> 与启动时 gameDir 用同一条件）：
    /// 开启 → <c>versions/&lt;版本&gt;/mods</c>，关闭 → <c>.minecraft/mods</c>。
    /// 同名文件不覆盖，自动追加 (1) (2)，避免把玩家已有的模组冲掉。
    /// </summary>
    private async Task AddToInstanceAsync(ModrinthModInfo info)
    {
        var downloadUrl = info.DownloadUrl;
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

        if (_instanceSubfolder is null)
        {
            AppServices.Notifications?.Warning("该类型的资源不能直接添加到实例，请使用「另存为」");
            return;
        }

        var instance = SelectedInstance;
        if (instance is null)
        {
            AppServices.Notifications?.Warning("没有可用的 Minecraft 实例，请先在「下载」页安装一个版本");
            return;
        }

        // 按钮已经不显示了，这里再挡一次：选中实例可能在下载开始前被改掉。
        if (!CanAddToInstance(info))
        {
            AppServices.Notifications?.Warning(
                $"{instance.Id} 是{instance.LoaderName}实例，装不了需要「{ModLoaderHelper.DescribeRequired(info.RequiredLoaders)}」的文件，" +
                "请换一个加载器匹配的实例，或使用「另存为」");
            return;
        }

        string targetPath;
        try
        {
            var folder = GameInstanceHelper.ResolveInstanceFolder(instance.Id, _instanceSubfolder);
            Directory.CreateDirectory(folder);

            var fileName = GetDownloadFileName(downloadUrl);
            targetPath = BuildUniquePath(folder, fileName);
        }
        catch (Exception ex)
        {
            AppServices.Notifications?.Error($"无法定位实例目录：{ex.Message}");
            return;
        }

        _isDownloading = true;
        try
        {
            var ok = await DownloadFileAsync(downloadUrl, targetPath);
            if (ok)
            {
                var shown = GameInstanceHelper.DescribeInstanceFolder(instance.Id, _instanceSubfolder);
                AppServices.Notifications?.Success($"已添加到 {instance.Id}：{shown}/{Path.GetFileName(targetPath)}");
            }
        }
        finally
        {
            _isDownloading = false;
        }
    }

    /// <summary>
    /// 下载核心：两条路径（另存为 / 添加到实例）共用。
    ///
    /// <para>
    /// 1.0.7.15 起交给 <see cref="FileDownloader"/>：分块并行（Modrinth 的 CDN 支持 range 请求，
    /// 单连接经常只有几百 KB/s，开 8 块能翻好几倍）、断点续传、失败自动重试。
    /// 落盘仍然是「先临时文件、校验通过再改名」——中途断网不会在 mods/ 里留半截 .jar。
    /// </para>
    ///
    /// <para>
    /// 速度文案取全局速度表：同一时刻可能还有整合包/加载器在并行下载，
    /// 界面上那个数字要跟「任务中心的总速度」一致，而不是这个文件自己的局部速度。
    /// </para>
    /// </summary>
    private static async Task<bool> DownloadFileAsync(string downloadUrl, string savePath)
    {
        var taskService = AppServices.Provider?.GetService(typeof(DownloadTaskService)) as DownloadTaskService;
        var task = taskService?.CreateTask(Path.GetFileName(savePath), DownloadTaskType.ModDownload);

        try
        {
            var item = new DownloadItem(
                downloadUrl,
                savePath,
                DisplayName: Path.GetFileName(savePath));

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var lastReportMs = 0L;

            await FileDownloader.DownloadAsync(item, onProgress: (received, total) =>
            {
                // 每 200ms 刷一次，避免分块进度把 UI 线程打满
                if (clock.ElapsedMilliseconds - lastReportMs < 200)
                    return;
                lastReportMs = clock.ElapsedMilliseconds;

                var percent = total > 0 ? (int)(received * 100 / total) : 0;
                var sizeText = total > 0
                    ? $"{DownloadSpeedometer.FormatBytes(received)}/{DownloadSpeedometer.FormatBytes(total)}"
                    : DownloadSpeedometer.FormatBytes(received);

                taskService?.UpdateProgress(
                    task, percent, DownloadSpeedometer.FormatSpeed(), sizeText);
            });

            taskService?.CompleteTask(task, $"已保存到：{savePath}");
            AppServices.Notifications?.Success($"文件已保存到：{savePath}");
            return true;
        }
        catch (OperationCanceledException)
        {
            taskService?.CancelTask(task, "已取消");
            return false;
        }
        catch (Exception ex)
        {
            var message = DescribeDownloadError(ex);
            taskService?.FailTask(task, message);
            AppServices.Notifications?.Error(message);
            return false;
        }
    }

    /// <summary>
    /// 把下载异常翻成中文：文件系统类错误（被占用 / 没权限 / 磁盘满）优先，
    /// 其次是 HTTP 状态码与超时。原来直接把 <c>ex.Message</c> 甩出去，
    /// 用户看到的就是一句英文原文。
    /// </summary>
    private static string DescribeDownloadError(Exception ex)
    {
        // 内核已经把这几种情况翻成中文了（含「下到的是半截文件 / 校验失败 / 已尝试哪些源」），
        // 再包一层前缀只会变成「下载失败：下载失败（…）」。
        if (ex is DownloadFailedException or DownloadIntegrityException)
            return ex.Message;

        var fileSystem = DownloadErrors.DescribeFileSystem(ex);
        if (fileSystem is not null)
            return fileSystem;

        if (ex is HttpRequestException { StatusCode: not null } httpEx)
            return $"下载失败：服务器返回 {(int)httpEx.StatusCode!.Value}，请稍后重试";

        if (ex is OperationCanceledException or TimeoutException)
            return "下载超时，请检查网络后重试";

        if (ex is HttpRequestException)
            return "网络错误，请检查网络或代理设置后重试";

        return $"下载失败：{ex.Message}";
    }

    private static void TryDeletePartialFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 删不掉也无妨，不能因为清理失败再抛一次
        }
    }

    /// <summary>
    /// 从下载链接推文件名：去掉查询串与片段后取最后一段，过一遍非法字符清洗，
    /// 没带扩展名时补上推断出来的扩展名（Modrinth CDN 通常是 <c>.../xxx.jar</c>，但版本号路径没有扩展名）。
    /// </summary>
    private static string GetDownloadFileName(string url)
    {
        try
        {
            var path = url;
            var cut = path.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0)
                path = path[..cut];

            var name = SanitizeFileName(Path.GetFileName(path));
            if (string.IsNullOrEmpty(Path.GetExtension(name)))
                name += GetDownloadExtension(url);

            return name;
        }
        catch
        {
            return "mod" + GetDownloadExtension(url);
        }
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "mod";

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray())
            .Trim()
            .TrimEnd('.');

        return string.IsNullOrEmpty(cleaned) ? "mod" : cleaned;
    }

    /// <summary>目标文件已存在时追加 (1) (2)…，绝不覆盖玩家已有的模组。</summary>
    private static string BuildUniquePath(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        if (!File.Exists(path))
            return path;

        var name = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);

        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(folder, $"{name} ({i}){ext}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(folder, $"{name} ({Guid.NewGuid():N}){ext}");
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

public class ModrinthModInfo : System.ComponentModel.INotifyPropertyChanged
{
    public string Name { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;

    /// <summary>文件名（不含路径）。仅作游戏版本筛选的兜底依据（Modrinth 未给 game_versions 时用）。</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Modrinth 给该文件标注的加载器标签（fabric / forge / neoforge / quilt …）。</summary>
    public List<string> RequiredLoaders { get; set; } = new();

    /// <summary>Modrinth 给该文件标注的支持版本号列表。</summary>
    public List<string> GameVersions { get; set; } = new();

    /// <summary>界面上一句「需要什么加载器」，例如 <c>Fabric / Quilt</c>。</summary>
    public string LoaderHint { get; set; } = string.Empty;

    /// <summary>是否显示加载器标注（只有模组需要，资源包 / 光影不显示）。</summary>
    public bool ShowLoaderHint { get; set; }

    private bool _showInstallModpackButton;

    /// <summary>
    /// 是否显示「安装整合包」按钮（只有整合包文件为 true）。
    /// 整合包的安装语义是「新建一个实例」，不是往已有实例拷文件，所以与「添加到实例」互斥。
    /// </summary>
    public bool ShowInstallModpackButton
    {
        get => _showInstallModpackButton;
        set
        {
            if (_showInstallModpackButton == value)
                return;

            _showInstallModpackButton = value;
            PropertyChanged?.Invoke(this,
                new System.ComponentModel.PropertyChangedEventArgs(nameof(ShowInstallModpackButton)));
        }
    }

    private bool _showInstanceButton;

    /// <summary>
    /// 是否显示「添加到实例」按钮（选中的实例能装这个文件时为 true）。
    /// 单独实现 INotifyPropertyChanged 而不是普通自动属性：切换目标实例、或列表加载完成后
    /// 都会重新判定，没有通知的话按钮显隐会停在旧状态。
    /// </summary>
    public bool ShowInstanceButton
    {
        get => _showInstanceButton;
        set
        {
            if (_showInstanceButton == value)
                return;

            _showInstanceButton = value;
            PropertyChanged?.Invoke(this,
                new System.ComponentModel.PropertyChangedEventArgs(nameof(ShowInstanceButton)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
