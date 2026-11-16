using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Serilog;
using VibrantbitLauncher.Localization;
using VibrantbitLauncher.Services;
using VibrantbitLauncher.Services.Download;

namespace VibrantbitLauncher.Views.Windows;

/// <summary>
/// 发现新版本时弹出的更新窗口。
///
/// <para>
/// 窗口里做三件事：告诉用户新版本改了什么、让用户选一条能走通的下载线路、
/// 把安装包下下来并校验。安装本身不在程序里做 —— 换掉正在运行的 exe 属于
/// 「自己改自己」，成功率取决于杀毒软件和文件锁，失败时的表现是「更新完打不开」，
/// 比手动解压覆盖更难排查。所以下完给出路径，由用户解压覆盖。
/// </para>
///
/// <para>
/// 下载走统一下载内核（分块并发 + 换源 + 续传），三条线路一次全部作为候选传进去：
/// 选中的排第一，另外两条兜底，用户不用守在这里盯着换线。
/// </para>
/// </summary>
public partial class UpdateWindow : UserControl, IInlinePage
{
    /// <summary>内嵌到主窗口时，标题栏显示的标题。</summary>
    public string InlineTitle => "发现新版本";

    /// <summary>
    /// 更新包的分块数（= 并发连接数）。
    ///
    /// 固定抬到 8：这个包永远是二十多 MB 的单个文件，而设置页的「单文件分块数」
    /// 默认 4、还可能被用户调成 1（那是给镜像限流准备的逃生阀）。
    /// GitHub 直链和加速站都支持 Range，8 条连接基本能跑满家用带宽。
    /// </summary>
    private const int UpdateChunkCount = 8;

    private enum UpdateState
    {
        /// <summary>还没开始（或已取消），按钮是「下载并更新」。</summary>
        Idle,

        /// <summary>正在下载，按钮是「取消下载」。</summary>
        Downloading,

        /// <summary>正在解压 / 替换，按钮不可点（此时程序马上就要退出了）。</summary>
        Installing
    }

    private ReleaseInfo? _release;
    private ReleaseAsset? _asset;
    private UpdateSourceChannel _channel = UpdateSourceChannel.ProxyV4;
    private UpdateState _state = UpdateState.Idle;
    private CancellationTokenSource? _cts;

    /// <summary>XAML 需要一个无参构造；真正使用时走带参数那个。</summary>
    public UpdateWindow()
    {
        InitializeComponent();

        Bar.Title = Localize.T("发现新版本");

        RadioV4.IsCheckedChanged += OnSourceChanged;
        RadioV6.IsCheckedChanged += OnSourceChanged;
        RadioDirect.IsCheckedChanged += OnSourceChanged;

        PrimaryButton.Click += OnPrimaryClick;
        SecondaryButton.Click += OnSecondaryClick;

        // 订阅了就要在退场时断开：这个页每次检查更新都会新建一份，
        // 不断开的话事件会一直挂在静态资源上（虽然是实例事件，留着也无妨，
        // 但显式收尾更容易看出这里的生命周期）。
        DetachedFromVisualTree += (_, _) =>
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        };
    }

    public UpdateWindow(ReleaseInfo release) : this()
    {
        ApplyRelease(release);
    }

    // ==================== 初始化 ====================

    private void ApplyRelease(ReleaseInfo release)
    {
        _release = release;
        _asset = UpdateService.PickAssetForCurrentPlatform(release);

        var remote = NormalizeVersion(release.TagName);
        HeaderText.Text = Localize.Tf("发现新版本 {0}", remote);

        MetaText.Text = Localize.Tf(
            "当前版本 {0} · 发布于 {1} · 安装包 {2}",
            UpdateService.CurrentVersion.ToString(),
            release.PublishedAt.ToLocalTime().ToString("yyyy-MM-dd"),
            _asset is null ? "—" : DiskUsageService.FormatSize(_asset.Size));

        var body = (release.Body ?? string.Empty).Trim();
        BodyText.Text = body.Length == 0
            ? Localize.T("这次发布没有写更新说明。")
            : FormatReleaseBody(body);

        if (_asset is null)
        {
            // 这次发布没挂当前平台的包（例如只发了 win / linux 而用户在 macOS）。
            // 这时候不让点「下载」—— 选错架构的包下下来也是白下。
            SourcePanel.IsVisible = false;
            ProgressBar.IsVisible = false;
            StatusText.Text = Localize.T("这次发布没有适配当前系统的安装包，请到发布页手动下载。");
            PrimaryButton.Content = Localize.T("打开下载页面");
            SecondaryButton.Content = Localize.T("稍后");
            Log.Information("更新包缺失当前平台版本：{Tag}", release.TagName);
            return;
        }

        StatusText.Text = Localize.Tf(
            "点「下载并更新」开始。安装包 {0}，支持多线程分块与断点续传。",
            DiskUsageService.FormatSize(_asset.Size));

        SetState(UpdateState.Idle);
    }

    /// <summary>把 <c>v1.0.34.7</c> 这类 tag 显示成 <c>1.0.34.7</c>。</summary>
    private static string NormalizeVersion(string tag)
    {
        var text = (tag ?? string.Empty).Trim();
        return text.StartsWith('v') || text.StartsWith('V') ? text[1..] : text;
    }

    /// <summary>
    /// 发布说明是 Markdown，界面上只有一块纯文本框。
    ///
    /// <para>
    /// 不做完整渲染（那要引一个 Markdown 引擎），只把最刺眼的标记去掉：
    /// 标题的 <c>#</c>、列表的 <c>-</c>、粗体与行内代码的反引号。
    /// 留着原始标记的话，窗口里第一眼看到的是 <c>## [1.0.34.7] - 2026-11-10</c>，
    /// 像调试输出而不像给人看的说明。
    /// </para>
    /// </summary>
    private static string FormatReleaseBody(string body)
    {
        var lines = body.Replace("\r\n", "\n").Split('\n');
        var builder = new System.Text.StringBuilder(body.Length);

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            // 标题行：去掉井号，保留文字
            var index = 0;
            while (index < line.Length && line[index] == '#')
                index++;

            if (index > 0 && index < line.Length && line[index] == ' ')
                line = line[(index + 1)..];

            // 列表标记统一成中点，视觉上更像条目
            if (line.StartsWith("- ", StringComparison.Ordinal) ||
                line.StartsWith("* ", StringComparison.Ordinal))
            {
                line = "· " + line[2..];
            }

            line = line.Replace("**", string.Empty).Replace("`", string.Empty);

            builder.AppendLine(line);
        }

        return builder.ToString().Trim();
    }

    // ==================== 交互 ====================

    private void OnSourceChanged(object? sender, RoutedEventArgs e)
    {
        _channel = RadioV6.IsChecked == true
            ? UpdateSourceChannel.ProxyV6
            : RadioDirect.IsChecked == true
                ? UpdateSourceChannel.Direct
                : UpdateSourceChannel.ProxyV4;
    }

    private async void OnPrimaryClick(object? sender, RoutedEventArgs e)
    {
        switch (_state)
        {
            case UpdateState.Downloading:
                _cts?.Cancel();
                return;

            case UpdateState.Installing:
                return;

            default:
                if (_asset is null)
                {
                    OpenReleasePage();
                    return;
                }

                await StartDownloadAsync();
                return;
        }
    }

    private void OnSecondaryClick(object? sender, RoutedEventArgs e)
        => InlineHost.Close();

    // ==================== 下载 ====================

    private async Task StartDownloadAsync()
    {
        if (_asset is null)
            return;

        var asset = _asset;

        SetState(UpdateState.Downloading);
        ProgressBar.IsVisible = true;
        ProgressBar.Value = 0;
        HintText.IsVisible = false;
        StatusText.Text = Localize.T("正在连接…");

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // 三条线路一次全部塞进候选：选中的排第一，其余兜底。
        var candidates = UpdateService.BuildCandidates(asset, _channel);
        var folder = UpdateService.ResolveDownloadFolder();
        var savePath = Path.Combine(folder, asset.Name);
        var item = new DownloadItem(candidates[0], savePath, asset.Size, DisplayName: asset.Name);

        var clock = Stopwatch.StartNew();
        long lastTick = -1000;

        try
        {
            await FileDownloader.DownloadAsync(
                item,
                onStatus: text => Post(() => StatusText.Text = Localize.T(text)),
                onProgress: (received, total) =>
                {
                    // 进度事件是**多块并发**回调的：不节流的话一帧能来上百次，
                    // 全丢给 UI 线程只会把下载本身拖慢。
                    var now = clock.ElapsedMilliseconds;
                    if (now - lastTick < 100)
                        return;
                    lastTick = now;

                    var known = total > 0 ? total : asset.Size;
                    Post(() => ShowProgress(received, known));
                },
                ct: token,
                chunkCount: UpdateChunkCount,
                extraSources: candidates);

            Post(() => StatusText.Text = Localize.T("正在校验安装包…"));

            // 包要经过第三方加速站转发，必须对着 GitHub 给的 sha256 核一遍再交给用户。
            var verifyError = await UpdateService.VerifySha256Async(savePath, asset.Sha256, token);
            if (verifyError is not null)
            {
                TryDelete(savePath);
                throw new InvalidOperationException(Localize.T(verifyError));
            }

            Log.Information("更新包已下载并校验通过：{File}", savePath);

            // 校验过了就立刻装：解压 → 起更新脚本 → 退出自己。
            // 替换必须由外部脚本完成（正在运行的 exe 在 Windows 上是锁着的），
            // 所以这里只负责把脚本拉起来，真正的复制发生在进程退出之后。
            SetState(UpdateState.Installing);
            StatusText.Text = Localize.T("正在解压安装包…");

            var installError = await UpdateInstaller.InstallAsync(
                savePath, NormalizeVersion(_release?.TagName ?? string.Empty), token);

            if (installError is not null)
            {
                // 「下载失败」这种措辞会把人引到网络上去排查，而这里其实是装不了
                // （开发环境 / 目录只读 / 包里没有对应文件）。直接把这句原因显示出来。
                Post(() =>
                {
                    SetState(UpdateState.Idle);
                    StatusText.Text = installError;
                    HintText.Text = Localize.Tf("安装包已保存在 {0}", savePath);
                    HintText.IsVisible = true;
                });
                return;
            }

            StatusText.Text = Localize.T("正在重启启动器，几秒后会自动打开新版本…");
            HintText.Text = Localize.Tf("更新包已保存在 {0}", savePath);
            HintText.IsVisible = true;

            // 给用户一点时间看清这句话，再退出；脚本那边也在等这个 PID 消失。
            await Task.Delay(900);

            Log.Information("更新脚本已就绪，关闭启动器（版本 {Version}）", NormalizeVersion(_release?.TagName ?? string.Empty));
            AppLifetime.Shutdown();
            Log.Information("更新包已下载并校验通过：{File}", savePath);
        }
        catch (OperationCanceledException)
        {
            Post(() =>
            {
                SetState(UpdateState.Idle);
                StatusText.Text = Localize.T("已取消，已下载的部分会保留，下次接着下。");
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "更新包下载失败");

            Post(() =>
            {
                SetState(UpdateState.Idle);
                StatusText.Text = Localize.Tf("下载失败：{0}", ex.Message);
            });
        }
    }

    private void ShowProgress(long received, long total)
    {
        if (total > 0)
            ProgressBar.Value = Math.Clamp(received * 100d / total, 0, 100);

        var text = total > 0
            ? Localize.Tf(
                "{0}%（{1} / {2}）",
                (int)ProgressBar.Value,
                DiskUsageService.FormatSize(received),
                DiskUsageService.FormatSize(total))
            : Localize.Tf("已下载 {0}", DiskUsageService.FormatSize(received));

        var speed = DownloadSpeedometer.FormatSpeed();
        if (speed.Length > 0)
            text += " · " + speed;

        StatusText.Text = text;
    }

    private void SetState(UpdateState state)
    {
        _state = state;

        switch (state)
        {
            case UpdateState.Downloading:
                PrimaryButton.Content = Localize.T("取消下载");
                PrimaryButton.IsEnabled = true;
                SecondaryButton.IsEnabled = false;
                SourcePanel.IsEnabled = false;
                break;

            case UpdateState.Installing:
                // 这时候按钮不能再点：程序马上要退出，让用户以为还能取消只会更糟
                PrimaryButton.Content = Localize.T("正在安装…");
                PrimaryButton.IsEnabled = false;
                SecondaryButton.IsEnabled = false;
                SourcePanel.IsEnabled = false;
                break;

            default:
                PrimaryButton.Content = Localize.T("下载并更新");
                PrimaryButton.IsEnabled = true;
                SecondaryButton.IsEnabled = true;
                SecondaryButton.Content = Localize.T("稍后");
                SourcePanel.IsEnabled = true;
                break;
        }
    }

    /// <summary>把一段逻辑丢回 UI 线程（下载回调都在后台线程上）。</summary>
    private static void Post(Action action)
        => Dispatcher.UIThread.Post(action);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "删除未通过校验的更新包失败：{File}", path);
        }
    }

    // ==================== 外部动作 ====================

    private void OpenReleasePage()
    {
        try
        {
            var url = _release?.HtmlUrl;
            if (string.IsNullOrWhiteSpace(url))
                url = UpdateService.ReleasesPageUrl;

            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "打开发布页失败");
        }
    }

}
