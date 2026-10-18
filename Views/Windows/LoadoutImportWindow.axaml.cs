using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using VibrantbitLauncher.Helpers;
using VibrantbitLauncher.Services;

namespace VibrantbitLauncher.Views.Windows;

/// <summary>
/// 「配装码」窗口：粘贴一串分享码，直接把对方的整包装成自己机器上的一个实例。
///
/// <para>
/// 与「实例工具箱 → 配装分享」的区别在于**使用场景**：那边是「已经有实例，想导出/补模组」；
/// 这边是「本机什么都没有」，粘贴即可从零装出 Minecraft 本体 + 加载器 + 模组。
/// 所以入口放在「启动」页，且不依赖任何已存在的实例。
/// </para>
///
/// <para>
/// 这里只做「识别 + 编排」，真正的安装复用
/// <see cref="LoadoutShareService.ImportAsNewInstanceAsync"/>（内部走整合包协调器，
/// 与拖入 <c>.mrpack</c> 同一条路径，所以进度详情页、任务中心、完成提示全都一致）。
/// </para>
/// </summary>
public partial class LoadoutImportWindow : UserControl, Services.IInlinePage
{

    /// <summary>内嵌到主窗口时，顶栏显示的标题。</summary>
    public string InlineTitle => "配装码";

    private LoadoutManifest? _manifest;
    private bool _busy;
    private bool _cancelRequested;

    /// <summary>
    /// 「准备阶段」的取消令牌：生成 .mrpack、解析清单这些步骤在建安装会话**之前**，
    /// 那会儿还没有会话令牌可用。少了这条通道，用户在这个窗口里点取消会被静默吞掉，
    /// 安装照常开始（典型的「取消不了」）。
    /// </summary>
    private CancellationTokenSource? _prepareCts;

    public LoadoutImportWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 是否真的装出了实例。调用方（启动页）据此决定要不要刷新版本列表 ——
    /// 装成功了才刷新，用户只是打开看一眼就关掉时不打扰列表。
    /// </summary>
    public bool Installed { get; private set; }

    /// <summary>装好之后的实例 id，便于调用方选中它。</summary>
    public string? InstalledInstanceId { get; private set; }

    // ==================== 识别 ====================

    private void OnCodeChanged(object? sender, TextChangedEventArgs e) => TryDecode();

    /// <summary>
    /// 边粘贴边识别。识别本身是纯本地计算（Base64 → GZip → JSON），几十 KB 的码也在毫秒级，
    /// 所以不加防抖；但太短的输入直接跳过，免得每敲一个字符都抛一次「格式不对」。
    /// </summary>
    private void TryDecode()
    {
        var text = CodeBox.Text?.Trim();

        if (string.IsNullOrEmpty(text) || text.Length < 16)
        {
            ClearResult();
            StatusText.Text = string.Empty;
            return;
        }

        try
        {
            var manifest = LoadoutShareService.Decode(text);
            _manifest = manifest;

            var loaderKind = LoadoutShareService.ParseLoaderTag(manifest.Loader);
            var loaderName = ModLoaderHelper.DisplayNameOf(loaderKind);

            ResultTitle.Text = string.IsNullOrWhiteSpace(manifest.Name) ? "未命名配装" : manifest.Name;

            ResultLine1.Text = string.IsNullOrWhiteSpace(manifest.Minecraft)
                ? "游戏版本：分享码里没有记录"
                : $"Minecraft {manifest.Minecraft}"
                  + (loaderKind == ModLoaderKind.Vanilla ? string.Empty : $" + {loaderName}")
                  + (string.IsNullOrWhiteSpace(manifest.LoaderVersion) ? string.Empty : $" {manifest.LoaderVersion}");

            var line2 = $"可自动下载 {manifest.ShareableCount} 个模组";
            if (manifest.LocalOnly.Count > 0)
                line2 += $"；另有 {manifest.LocalOnly.Count} 个未收录（自制品，需向分享者索取）";
            ResultLine2.Text = line2;

            ResultLine3.Text = manifest.MaxMemoryMb > 0
                ? $"分享者设置的最大内存：{manifest.MaxMemoryMb} MB"
                : string.Empty;
            ResultLine3.IsVisible = manifest.MaxMemoryMb > 0;

            ResultCard.IsVisible = true;
            StatusText.Text = string.Empty;

            // 一个模组都没有的码（纯原版配装）也允许装 —— 它至少能装出本体 + 加载器
            ImportButton.IsEnabled = !string.IsNullOrWhiteSpace(manifest.Minecraft);
            if (!ImportButton.IsEnabled)
                StatusText.Text = "这个分享码里没有记录 Minecraft 版本，无法自动安装。";
        }
        catch (LoadoutShareException ex)
        {
            // 粘贴到一半时必然解析失败，这是正常的 —— 只在有内容后给出提示，不弹窗打扰
            ClearResult();
            StatusText.Text = ex.Message;
        }
    }

    private void ClearResult()
    {
        _manifest = null;
        ResultCard.IsVisible = false;
        ResultTitle.Text = string.Empty;
        ResultLine1.Text = string.Empty;
        ResultLine2.Text = string.Empty;
        ResultLine3.Text = string.Empty;
        ImportButton.IsEnabled = false;
    }

    // ==================== 安装 ====================

    /// <summary>
    /// 同一个按钮承担两种角色：空闲时是「开始添加」，安装中是「取消添加」。
    ///
    /// <para>
    /// 之前安装期间把按钮整个禁用、只留一个关不掉的等待框，用户点了取消（进度页上那个）
    /// 又发现没有立即停下，体验上就是「取消不了」。这里让取消入口就近可见，
    /// 并且取消后立刻给出「正在取消…」的反馈。
    /// </para>
    /// </summary>
    private void OnImportClick(object? sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            RequestCancel();
            return;
        }

        if (_manifest is { } manifest)
            _ = RunImportAsync(manifest);
    }

    private void RequestCancel()
    {
        if (_cancelRequested)
            return;

        _cancelRequested = true;
        ImportButton.IsEnabled = false;
        ImportButton.Content = "正在取消…";
        StatusText.Text = "正在取消：当前文件下载完就会停下（已下载的文件会保留）。";

        // 两条取消通道，缺一不可：
        // ① 还在「生成安装包 / 解析清单」阶段 → 安装会话尚未建立，只能掐本窗口的令牌；
        // ② 已经建了会话 → 走会话令牌，让底层下载真正停手。
        try { _prepareCts?.Cancel(); }
        catch (ObjectDisposedException) { /* 已结束，忽略 */ }

        ModpackInstallSession.Current?.RequestCancel();
    }

    private async Task RunImportAsync(LoadoutManifest manifest)
    {
        _busy = true;
        _cancelRequested = false;

        _prepareCts?.Dispose();
        _prepareCts = new CancellationTokenSource();

        // 安装期间锁输入：用户可以继续粘贴，但那份清单不会被这次安装采用，
        // 界面显示的与实际在装的就对不上了。
        CodeBox.IsEnabled = false;
        ImportButton.IsEnabled = true;
        ImportButton.Content = "取消添加";

        // 关闭窗口允许 —— 安装会在后台继续（并在「下载任务」里可见），
        // 强留一个关不掉的窗口只会让用户以为整个程序卡死。
        CloseButton.IsEnabled = true;
        StatusText.Text = "正在生成安装包…";

        try
        {
            var instanceId = await LoadoutShareService.ImportAsNewInstanceAsync(
                manifest, "配装码", _prepareCts.Token);

            if (!string.IsNullOrEmpty(instanceId))
            {
                Installed = true;
                InstalledInstanceId = instanceId;
                ImportButton.IsEnabled = false;
                ImportButton.Content = "已添加";
                StatusText.Text = $"已添加为实例「{instanceId}」。关闭本窗口后在「启动」页选中它就能开始游戏。";
            }
            else
            {
                ImportButton.Content = "开始添加";
                ImportButton.IsEnabled = true;
                CodeBox.IsEnabled = true;
                StatusText.Text = _cancelRequested
                    ? "已取消这次添加。已经下载的文件会保留，重新粘贴配装码可以再来一次。"
                    : "添加未完成，请查看提示信息后重试。";
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "按配装码添加实例失败");
            ImportButton.Content = "重试";
            ImportButton.IsEnabled = true;
            CodeBox.IsEnabled = true;
            StatusText.Text = "添加失败：" + FriendlyError.Describe(ex);
        }
        finally
        {
            _busy = false;
            CloseButton.IsEnabled = true;

            // 这次流程走完了，准备阶段的令牌没用了；下次点「开始添加」会新建一个。
            // 注意这里只清引用不 Cancel —— 正常完成时不该留下取消痕迹。
            try { _prepareCts?.Dispose(); } catch (ObjectDisposedException) { }
            _prepareCts = null;
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Services.InlineHost.Close();
}
