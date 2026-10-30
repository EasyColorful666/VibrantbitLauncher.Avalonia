using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using FluentAvalonia.UI.Controls;
using VibrantbitLauncher.Helpers;
using VibrantbitLauncher.Services;

namespace VibrantbitLauncher.Views.Windows;

/// <summary>
/// 实例工具箱：把「依赖体检」「配装分享」「导出整合包」三个跟**实例内容**相关的功能收在一处。
///
/// <para>
/// 之所以不塞进设置页或下载页：这三件事都以「某个实例」为主语 ——
/// 体检是体检某个实例的 mods，分享是分享某个实例的配装，
/// 导出是把这个实例整份打成包。放在一起，共用顶部那个实例下拉框，
/// 用户不用在两个页面之间反复切换选择。
/// </para>
///
/// <para>
/// 本窗口只做「编排 + 展示」，真正的解析 / 下载 / 打包都在
/// <see cref="ModDependencyResolver"/>、<see cref="LoadoutShareService"/>
/// 与 <see cref="InstancePackExporter"/> 里。
/// </para>
/// </summary>
public partial class InstanceToolsWindow : UserControl, Services.IInlinePage
{

    /// <summary>内嵌到主窗口时，顶栏显示的标题。</summary>
    public string InlineTitle => "实例工具箱";

    private readonly ObservableCollection<MissingItem> _missing = new();

    private List<GameInstance> _instances = new();
    private ModHealthReport? _report;
    private LoadoutManifest? _pendingImport;
    private bool _busy;

    public InstanceToolsWindow() : this(null)
    {
    }

    /// <summary>
    /// <paramref name="preferredInstanceId"/> 由「启动」页的「工具箱」按钮传入，
    /// 让窗口打开时就选中用户点的那一行，而不是让他再挑一次。
    /// </summary>
    public InstanceToolsWindow(string? preferredInstanceId)
    {
        InitializeComponent();

        MissingList.ItemsSource = _missing;
        RefreshInstances(preferredInstanceId);
    }

    /// <summary>当前选中的实例。</summary>
    private GameInstance? SelectedInstance
    {
        get
        {
            var i = InstanceCombo.SelectedIndex;
            return i >= 0 && i < _instances.Count ? _instances[i] : null;
        }
    }

    // ==================== 实例列表 ====================

    private void RefreshInstances(string? preferredInstanceId = null)
    {
        _instances = GameInstanceHelper.GetInstances();

        InstanceCombo.Items.Clear();
        foreach (var instance in _instances)
            InstanceCombo.Items.Add(instance.DisplayName);

        if (_instances.Count == 0)
        {
            InstanceHint.Text = "还没有安装任何实例";
            InstanceCombo.IsEnabled = false;
            AnalyzeButton.IsEnabled = false;
            ExportButton.IsEnabled = false;
            ExportPackButton.IsEnabled = false;
            PackNameBox.Text = string.Empty;
            return;
        }

        // 优先选中调用方指定的实例；否则挑一个「最可能装过模组」的（非原版）
        var index = !string.IsNullOrWhiteSpace(preferredInstanceId)
            ? _instances.FindIndex(i => string.Equals(i.Id, preferredInstanceId, StringComparison.OrdinalIgnoreCase))
            : -1;

        if (index < 0)
            index = _instances.FindIndex(i => i.Loader is not (ModLoaderKind.Vanilla or ModLoaderKind.Unknown));

        InstanceCombo.SelectedIndex = index >= 0 ? index : 0;
    }

    private void InstanceCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var instance = SelectedInstance;

        // 切实例后旧的体检结果 / 待导入状态都必须作废，否则会把上一个实例的结论套到新实例上
        _report = null;
        _missing.Clear();
        MissingEmptyText.IsVisible = true;
        ConflictSection.IsVisible = false;
        InstalledSection.IsVisible = false;
        HealthSummary.Text = string.Empty;
        AnalyzeStatus.Text = string.Empty;
        ResolveButton.IsEnabled = false;

        if (instance is null)
        {
            InstanceHint.Text = string.Empty;
            ExportPackButton.IsEnabled = false;
            return;
        }

        // 导出的整合包默认就叫实例名 —— 用户在「导出整合包」页里改过也没关系，
        // 一旦切实例，包名跟着换是符合预期的（否则容易把 A 的名字用到 B 上）。
        PackNameBox.Text = instance.Id;
        PackStatus.Text = string.Empty;
        ExportPackButton.IsEnabled = true;

        var isolation = GameInstanceHelper.IsVersionIsolated ? "版本隔离：开" : "版本隔离：关";
        var mods = GameInstanceHelper.DescribeInstanceFolder(instance.Id, "mods");
        InstanceHint.Text = $"{instance.LoaderName} · {isolation} → {mods}";
    }

    // ==================== 依赖体检 ====================

    private async void Analyze_Click(object? sender, RoutedEventArgs e)
    {
        var instance = SelectedInstance;
        if (instance is null || _busy)
            return;

        _busy = true;
        AnalyzeButton.IsEnabled = false;
        AnalyzeStatus.Text = "正在读取模组元数据…";
        ResolveButton.IsEnabled = false;

        try
        {
            var report = await Task.Run(() => ModDependencyResolver.Analyze(instance.Id));
            _report = report;

            _missing.Clear();
            foreach (var m in report.Missing)
            {
                _missing.Add(new MissingItem
                {
                    ModId = m.ModId,
                    StatusText = "待补全",
                    Detail = m.Describe()
                             + (string.IsNullOrWhiteSpace(m.VersionRange) ? string.Empty : $" · 需要版本 {m.VersionRange}"),
                });
            }

            MissingEmptyText.IsVisible = _missing.Count == 0;

            ConflictList.ItemsSource = report.Conflicts
                .Select(c => $"{c.ModId}：装了 {c.Files.Count} 份 —— " + string.Join("、", c.Files.Select(f => f.FileName)))
                .ToList();
            ConflictSection.IsVisible = report.Conflicts.Count > 0;

            // 加载器不匹配的模组照样列出来，但要标清楚 —— 它们根本不会被加载，
            // 用户看到「装了却没生效」时要能一眼找到原因。
            var incompatiblePaths = new HashSet<string>(
                report.Incompatible.Select(m => m.FilePath), StringComparer.OrdinalIgnoreCase);

            var installedLines = report.Installed
                .Select(m =>
                {
                    var name = $"{m.Name} {m.Version}（{m.FileName}）";
                    return incompatiblePaths.Contains(m.FilePath)
                        ? $"{name}　⚠ {ModDependencyResolver.LoaderText(m.Loader)} 模组，与当前实例不匹配，不会加载"
                        : name;
                })
                .ToList();

            if (report.Unrecognized.Count > 0)
                installedLines.AddRange(report.Unrecognized.Select(f => $"{f}（没有模组元数据，可能是普通资源包或已损坏）"));

            InstalledList.ItemsSource = installedLines;
            InstalledSummary.Text = $"共 {report.Installed.Count} 个可识别模组"
                                    + (report.Incompatible.Count > 0
                                        ? $"（其中 {report.Incompatible.Count} 个加载器不匹配）"
                                        : string.Empty)
                                    + (report.GameVersion.Length > 0 ? $"，游戏版本 {report.GameVersion}" : string.Empty);
            InstalledSection.IsVisible = true;

            HealthSummary.Text = report.Summary();
            AnalyzeStatus.Text = report.Warnings.Count > 0
                ? string.Join("；", report.Warnings)
                : string.Empty;

            ResolveButton.IsEnabled = report.Missing.Count > 0;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "依赖体检失败：{Id}", instance.Id);
            AnalyzeStatus.Text = "体检失败：" + ex.Message;
        }
        finally
        {
            AnalyzeButton.IsEnabled = true;
            _busy = false;
        }
    }

    /// <summary>一键补齐：把缺失依赖解析成 Modrinth 下载项，然后直接下进实例的 mods 目录。</summary>
    private async void Resolve_Click(object? sender, RoutedEventArgs e)
    {
        var instance = SelectedInstance;
        var report = _report;
        if (instance is null || report is null || _busy)
            return;

        if (report.Missing.Count == 0)
            return;

        _busy = true;
        ResolveButton.IsEnabled = false;

        try
        {
            var progress = new Progress<string>(s => AnalyzeStatus.Text = s);

            var candidates = await ModDependencyResolver.ResolveAllAsync(
                report,
                (i, total, id) => ((IProgress<string>)progress).Report($"正在查询 Modrinth（{i}/{total}）：{id}"),
                CancellationToken.None);

            // 把解析结果同步到界面上
            for (var i = 0; i < report.Missing.Count && i < _missing.Count; i++)
            {
                var c = candidates.FirstOrDefault(x => x.ModId == report.Missing[i].ModId);
                if (c is null)
                    continue;

                _missing[i].StatusText = c.HasDownload ? $"可补全 → {c.Title}" : "查不到";
                _missing[i].Detail = c.HasDownload
                    ? $"{report.Missing[i].Describe()} · {c.Title} {c.VersionNumber}（{c.FileName}）"
                    : $"{report.Missing[i].Describe()} · {c.FailureReason}";
                _missing[i].NotifyAll();
            }

            var downloadable = candidates.Where(c => c.HasDownload).ToList();
            if (downloadable.Count == 0)
            {
                AnalyzeStatus.Text = "没有找到可以自动补齐的依赖 —— 这些模组可能没发布到 Modrinth，需要手动安装。";
                return;
            }

            // 复用配装分享的下载器：把候选转成同样的清单结构即可
            var manifest = new LoadoutManifest
            {
                Name = instance.Id,
                Files = downloadable.Select(c => new LoadoutFile
                {
                    Path = "mods/" + c.FileName,
                    Url = c.Url,
                    Sha1 = c.Sha1,
                    Size = c.Size,
                    Name = c.Title,
                    Project = c.Slug,
                }).ToList(),
            };

            var done = await LoadoutShareService.ImportIntoInstanceAsync(
                manifest, instance.Id,
                (i, total, name) => ((IProgress<string>)progress).Report($"正在下载（{i}/{total}）：{name}"),
                CancellationToken.None);

            AnalyzeStatus.Text = $"已补齐 {done}/{downloadable.Count} 个依赖。"
                                 + "建议重新点「开始体检」复核，然后启动游戏验证。";
            AppServices.Notifications?.Success($"已补齐 {done} 个缺失依赖（{instance.Id}）");
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "补齐依赖失败：{Id}", instance.Id);
            var reason = VibrantbitLauncher.Helpers.FriendlyError.Describe(ex);
            AnalyzeStatus.Text = "补齐失败：" + reason;
            AppServices.Notifications?.Error("补齐依赖失败：" + reason);
        }
        finally
        {
            ResolveButton.IsEnabled = _report?.Missing.Count > 0;
            _busy = false;
        }
    }

    // ==================== 配装分享：导出 ====================

    private async void Export_Click(object? sender, RoutedEventArgs e)
    {
        var instance = SelectedInstance;
        if (instance is null || _busy)
            return;

        _busy = true;
        ExportButton.IsEnabled = false;
        CopyButton.IsEnabled = false;
        SaveCodeButton.IsEnabled = false;
        ExportStatus.Text = "正在识别模组…";

        try
        {
            var progress = new Progress<string>(s => ExportStatus.Text = s);

            var manifest = await LoadoutShareService.BuildFromInstanceAsync(
                instance.Id,
                (i, total, name) => ((IProgress<string>)progress).Report($"正在识别（{i}/{total}）：{name}"),
                CancellationToken.None);

            var code = LoadoutShareService.Encode(manifest);
            CodeBox.Text = code;

            ExportStatus.Text =
                $"完成：{manifest.ShareableCount} 个模组可复现，分享码 {code.Length} 字符"
                + (manifest.TotalCount > manifest.ShareableCount
                    ? $"；另有 {manifest.TotalCount - manifest.ShareableCount} 个未收录的模组（对方需自行准备）"
                    : string.Empty);

            CopyButton.IsEnabled = true;
            SaveCodeButton.IsEnabled = true;
            _pendingImport = null;
            ImportNewButton.IsEnabled = false;
            ImportIntoButton.IsEnabled = false;
            ImportInfo.Text = string.Empty;
        }
        catch (LoadoutShareException ex)
        {
            ExportStatus.Text = ex.Message;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "生成配装分享码失败");
            ExportStatus.Text = "生成失败：" + ex.Message;
        }
        finally
        {
            ExportButton.IsEnabled = true;
            _busy = false;
        }
    }

    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        var text = CodeBox.Text;
        if (string.IsNullOrWhiteSpace(text))
            return;

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(text);
                ExportStatus.Text = "分享码已复制到剪贴板，发给好友粘贴即可。";
            }
        }
        catch (Exception ex)
        {
            ExportStatus.Text = "复制失败：" + ex.Message;
        }
    }

    private async void SaveCode_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(CodeBox.Text))
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        try
        {
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存配装分享码",
                SuggestedFileName = (SelectedInstance?.Id ?? "loadout") + ".vblshare",
                DefaultExtension = "vblshare",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Vibrantbit 配装分享码") { Patterns = new[] { "*.vblshare" } }
                }
            });

            var path = file?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path))
                return;

            // 内容用「分享码 + 可读 JSON」两种都写：前者能直接粘贴，后者能人工查看
            var manifest = _pendingImport ?? LoadoutShareService.Decode(CodeBox.Text);
            await File.WriteAllTextAsync(path,
                "# VibrantbitLauncher 配装分享码\n"
                + "# 下面这一行就是分享码，整行复制即可；再往后的 JSON 只是方便你查看内容。\n"
                + CodeBox.Text.Trim() + "\n\n"
                + LoadoutShareService.ToJson(manifest));

            ExportStatus.Text = "已保存到：" + path;
        }
        catch (LoadoutShareException ex)
        {
            ExportStatus.Text = ex.Message;
        }
        catch (Exception ex)
        {
            ExportStatus.Text = "保存失败：" + ex.Message;
        }
    }

    // ==================== 配装分享：导入 ====================

    private void ImportCheck_Click(object? sender, RoutedEventArgs e)
    {
        var text = CodeBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            ImportInfo.Text = "请先把分享码粘贴到上面的文本框。";
            return;
        }

        try
        {
            var manifest = LoadoutShareService.Decode(text);
            _pendingImport = manifest;

            var loaderName = ModLoaderHelper.DisplayNameOf(LoadoutShareService.ParseLoaderTag(manifest.Loader));
            ImportInfo.Text =
                $"识别成功：{manifest.Name}\n"
                + $"Minecraft {manifest.Minecraft}"
                + (string.IsNullOrWhiteSpace(manifest.Loader) ? string.Empty : $" + {loaderName}")
                + (string.IsNullOrWhiteSpace(manifest.LoaderVersion) ? string.Empty : $" {manifest.LoaderVersion}")
                + $"\n可自动下载 {manifest.ShareableCount} 个模组"
                + $"，另有 {manifest.LocalOnly.Count} 个未收录（需自行准备）"
                + (manifest.MaxMemoryMb > 0 ? $"\n分享者的最大内存设置：{manifest.MaxMemoryMb} MB" : string.Empty);

            ImportNewButton.IsEnabled = manifest.ShareableCount > 0;
            ImportIntoButton.IsEnabled = manifest.ShareableCount > 0 && SelectedInstance is not null;
        }
        catch (LoadoutShareException ex)
        {
            _pendingImport = null;
            ImportNewButton.IsEnabled = false;
            ImportIntoButton.IsEnabled = false;
            ImportInfo.Text = ex.Message;
        }
    }

    private async void ImportNew_Click(object? sender, RoutedEventArgs e)
    {
        var manifest = _pendingImport;
        if (manifest is null || _busy)
            return;

        _busy = true;
        try
        {
            ImportInfo.Text = "正在新建实例并安装（进度见弹出的安装详情页）…";
            var instanceId = await LoadoutShareService.ImportAsNewInstanceAsync(manifest, "配装分享码");

            if (instanceId is not null)
            {
                ImportInfo.Text = $"已导入为实例：{instanceId}。可在「启动」页选择它开始游戏。";
                RefreshInstances();
            }
            else
            {
                ImportInfo.Text = "导入未完成，请查看提示信息。";
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "按分享码新建实例失败");
            ImportInfo.Text = "导入失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private async void ImportInto_Click(object? sender, RoutedEventArgs e)
    {
        var manifest = _pendingImport;
        var instance = SelectedInstance;
        if (manifest is null || instance is null || _busy)
            return;

        _busy = true;
        ImportIntoButton.IsEnabled = false;
        try
        {
            var progress = new Progress<string>(s => ImportInfo.Text = s);

            var done = await LoadoutShareService.ImportIntoInstanceAsync(
                manifest, instance.Id,
                (i, total, name) => ((IProgress<string>)progress).Report($"正在下载（{i}/{total}）：{name}"),
                CancellationToken.None);

            ImportInfo.Text = $"已把 {done}/{manifest.ShareableCount} 个模组补进实例 {instance.Id}。"
                              + "若分享者的加载器 / 游戏版本与这个实例不同，请自行确认兼容性。";
            AppServices.Notifications?.Success($"已补入 {done} 个模组（{instance.Id}）");
        }
        catch (LoadoutShareException ex)
        {
            ImportInfo.Text = ex.Message;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "按分享码补模组失败");
            ImportInfo.Text = "导入失败：" + ex.Message;
        }
        finally
        {
            ImportIntoButton.IsEnabled = true;
            _busy = false;
        }
    }

    // ==================== 导出整合包 ====================

    /// <summary>
    /// 把当前实例导出成整合包。
    ///
    /// <para>
    /// 这里只负责「问用户要什么、把参数翻译成 <see cref="PackExportOptions"/>、报告结果」，
    /// 真正的哈希、联网反查与打包都在 <see cref="InstancePackExporter"/> 里 ——
    /// 那段逻辑需要被「以后可能的命令行导出」复用，不能绑在窗口上。
    /// </para>
    /// </summary>
    private async void ExportPack_Click(object? sender, RoutedEventArgs e)
    {
        var instance = SelectedInstance;
        if (instance is null || _busy)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        var isMrpack = FormatMrpackRadio.IsChecked == true;
        var extension = isMrpack ? "mrpack" : "zip";

        var packName = string.IsNullOrWhiteSpace(PackNameBox.Text) ? instance.Id : PackNameBox.Text.Trim();
        var packVersion = string.IsNullOrWhiteSpace(PackVersionBox.Text) ? "1.0.0" : PackVersionBox.Text.Trim();

        var fileType = isMrpack
            ? new FilePickerFileType("Modrinth 整合包") { Patterns = new[] { "*.mrpack" } }
            : new FilePickerFileType("整合包压缩包") { Patterns = new[] { "*.zip" } };

        Avalonia.Platform.Storage.IStorageFile? file;
        try
        {
            file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出整合包",
                SuggestedFileName = $"{SafeFileName(packName, "modpack")}.{extension}",
                DefaultExtension = extension,
                FileTypeChoices = new[] { fileType },
            });
        }
        catch (Exception ex)
        {
            PackStatus.Text = $"打开保存对话框失败：{ex.Message}";
            return;
        }

        var path = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
            return;

        var options = new PackExportOptions
        {
            Name = packName,
            Version = packVersion,
            Summary = PackSummaryBox.Text?.Trim() ?? string.Empty,
            Format = isMrpack ? PackFormat.Mrpack : PackFormat.Zip,
            IncludeConfig = PackConfigToggle.IsChecked == true,
            IncludeResourcePacks = PackResourcePackToggle.IsChecked == true,
            IncludeShaderPacks = PackShaderToggle.IsChecked == true,
            IncludeSaves = PackSavesToggle.IsChecked == true,
            IncludeOptionsFile = PackOptionsToggle.IsChecked == true,
            BundleUnresolvedMods = PackBundleToggle.IsChecked == true,
        };

        _busy = true;
        ExportPackButton.IsEnabled = false;
        PackStatus.Text = "正在准备…";

        try
        {
            var progress = new Progress<string>(s => PackStatus.Text = s);

            var result = await InstancePackExporter.ExportAsync(
                instance.Id, path, options,
                s => ((IProgress<string>)progress).Report(s),
                CancellationToken.None);

            // 一句话说完，用一条词条就能整句翻译。拆成「有条件才出现」的几段拼接，
            // 词表里就得为每种组合各写一条（2^3 种），维护不动。
            var summary = $"已导出 {Path.GetFileName(path)}（{result.DescribeSize()}）"
                          + $"：模组 {result.ModTotal} 个，自动下载 {result.ModLinked} 个，"
                          + $"随包 {result.ModBundled} 个，未包含 {result.ModSkipped} 个";

            PackStatus.Text = result.Warnings.Count == 0
                ? summary
                : summary + "\n" + string.Join("\n", result.Warnings.Select(w => "· " + w));

            AppServices.Notifications?.Success($"整合包已导出：{Path.GetFileName(path)}");
        }
        catch (PackExportException ex)
        {
            PackStatus.Text = ex.Message;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "导出整合包失败：{Id}", instance.Id);
            PackStatus.Text = $"导出失败：{FriendlyError.Describe(ex)}";
        }
        finally
        {
            _busy = false;
            ExportPackButton.IsEnabled = SelectedInstance is not null;
        }
    }

    /// <summary>把用户填的名称变成合法文件名（整合包名里常有冒号、斜杠）。</summary>
    private static string SafeFileName(string name, string fallback)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray())
            .Replace(" ", string.Empty)
            .Trim();

        return cleaned.Length == 0 ? fallback : cleaned;
    }

    // ==================== 关闭 ====================

    private void Close_Click(object? sender, RoutedEventArgs e) => Services.InlineHost.Close();
}

/// <summary>缺失依赖列表里的一行（需要可更新，补全后再刷新状态文案）。</summary>
public sealed class MissingItem : INotifyPropertyChanged
{
    private string _statusText = "待补全";
    private string _detail = string.Empty;

    public string ModId { get; init; } = string.Empty;

    public string StatusText
    {
        get => _statusText;
        set
        {
            _statusText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        }
    }

    public string Detail
    {
        get => _detail;
        set
        {
            _detail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Detail)));
        }
    }

    public void NotifyAll()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Detail)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
