using System.Collections.ObjectModel;
using Avalonia.Threading;
using VibrantbitLauncher.Helpers;
using VibrantbitLauncher.Models;

namespace VibrantbitLauncher.Services;

/// <summary>单个待下载文件在详情页里的状态。</summary>
public enum ModpackFileState
{
    /// <summary>还没轮到。</summary>
    Pending = 0,

    /// <summary>正在下载。</summary>
    Downloading,

    /// <summary>已完成（含「本地已存在且大小相符」的跳过）。</summary>
    Completed,

    /// <summary>失败（路径非法 / 下载出错）。</summary>
    Failed
}

/// <summary>
/// 进度详情页里的一行文件。<see cref="ObservableObject"/> 子类，UI 直接绑定。
/// </summary>
public sealed class ModpackFileEntry : ObservableObject
{
    private ModpackFileState _state = ModpackFileState.Pending;
    private string _detail = string.Empty;

    public ModpackFileEntry(string path, long size)
    {
        Path = path;
        Size = size;
    }

    /// <summary>相对实例根的路径，如 <c>mods/sodium.jar</c>。</summary>
    public string Path { get; }

    /// <summary>期望大小（0 表示未知）。</summary>
    public long Size { get; }

    /// <summary>文件名（列表主行显示）。</summary>
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>所在子目录（列表副行显示）。根目录文件显示 <c>实例根目录</c>。</summary>
    public string Folder
    {
        get
        {
            var dir = System.IO.Path.GetDirectoryName(Path)?.Replace('\\', '/');
            return string.IsNullOrEmpty(dir) ? "实例根目录" : dir + "/";
        }
    }

    /// <summary>大小文案（未知时为空）。</summary>
    public string SizeText => Size > 0 ? FormatSize(Size) : string.Empty;

    public ModpackFileState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsPending));
                OnPropertyChanged(nameof(IsDownloading));
                OnPropertyChanged(nameof(IsCompleted));
                OnPropertyChanged(nameof(IsFailed));
            }
        }
    }

    /// <summary>附加说明（失败原因等）。</summary>
    public string Detail
    {
        get => _detail;
        set => SetProperty(ref _detail, value);
    }

    public bool IsPending => State == ModpackFileState.Pending;
    public bool IsDownloading => State == ModpackFileState.Downloading;
    public bool IsCompleted => State == ModpackFileState.Completed;
    public bool IsFailed => State == ModpackFileState.Failed;

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} MB",
        >= 1024 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes} B"
    };
}

/// <summary>详情页左侧「阶段条」上的一格。</summary>
public sealed class ModpackStep : ObservableObject
{
    /// <summary>阶段状态（不放进 <see cref="ModpackFileState"/>：语义不同，别复用）。</summary>
    public enum StepState
    {
        Pending = 0,
        Active,
        Done,
        Failed
    }

    private StepState _state = StepState.Pending;
    private string _detail = string.Empty;

    public ModpackStep(ModpackInstaller.ModpackPhase phase, string title, string detail)
    {
        Phase = phase;
        Title = title;
        _detail = detail;
    }

    public ModpackInstaller.ModpackPhase Phase { get; }
    public string Title { get; }

    public string Detail
    {
        get => _detail;
        set => SetProperty(ref _detail, value);
    }

    public StepState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsPending));
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(IsDone));
                OnPropertyChanged(nameof(IsFailed));
            }
        }
    }

    public bool IsPending => State == StepState.Pending;
    public bool IsActive => State == StepState.Active;
    public bool IsDone => State == StepState.Done;
    public bool IsFailed => State == StepState.Failed;
}

/// <summary>
/// 一次整合包安装的**可观察会话**：进度详情页（<c>ModpackProgressWindow</c>）的唯一数据源。
///
/// <para>
/// 为什么单独抽一层、不直接把 <see cref="ModpackInstaller.ModpackProgress"/> 丢给 UI：
/// 进度回调只给「此刻是什么状态」，而详情页要展示**累积**信息 —— 阶段条走到哪了、
/// 文件列表里每个文件是等待还是失败、总共花了多久。这些都得有人记着，
/// 记的人就是这里。回调只负责「把新状态喂进来」。
/// </para>
///
/// <para>
/// <b>线程</b>：<see cref="Apply"/> / 完成与失败方法都只应在 **UI 线程**调用
/// （上层用 <see cref="Progress{T}"/> 投递，它会把回调切回创建时的同步上下文）。
/// </para>
/// </summary>
public sealed class ModpackInstallSession : ObservableObject
{
    // ==================== 全局「当前会话」 ====================

    private static ModpackInstallSession? _current;

    /// <summary>当前（或最近一次）安装会话。没有安装过时为 null。</summary>
    public static ModpackInstallSession? Current => _current;

    /// <summary>是否有安装正在进行（用于按钮可用性、重复拖入拦截）。</summary>
    public static bool IsRunning => _current is { IsFinished: false };

    /// <summary>把某次安装设为「当前会话」，并接管它的令牌。</summary>
    internal static void SetCurrent(ModpackInstallSession session) => _current = session;

    // ==================== 构造 ====================

    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, ModpackFileEntry> _fileIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly DateTime _startedAt = DateTime.Now;

    /// <summary>
    /// 每秒刷新「已用时间 / 剩余时间」的计时器。**只为显示服务**：
    /// 拿不到 UI 调度器（无界面环境）时允许为 null，绝不能因此影响安装本身。
    /// </summary>
    private DispatcherTimer? _timer;

    private ModpackInstallSession(
        ModpackInstaller.ModpackManifest manifest,
        string sourceLabel,
        string filePath,
        InstallKind kind)
    {
        Manifest = manifest;
        SourceLabel = sourceLabel;
        FilePath = filePath;
        Kind = kind;

        Subject = kind == InstallKind.Loadout ? "配装" : "整合包";

        PackName = string.IsNullOrWhiteSpace(manifest.Name)
            ? (kind == InstallKind.Loadout ? "未命名配装" : "未命名整合包")
            : manifest.Name;

        // 配装码在内部也是打成 .mrpack 再装的，但显示「Modrinth .mrpack」会让用户莫名其妙
        // （他从没下载过什么文件）。所以按来源改文案。
        FormatText = kind == InstallKind.Loadout
            ? "配装分享码"
            : manifest.Format switch
            {
                ModpackInstaller.ModpackFormat.Modrinth => "Modrinth .mrpack",
                ModpackInstaller.ModpackFormat.CurseForge => "CurseForge .zip",
                _ => "整合包"
            };

        GameVersionText = string.IsNullOrWhiteSpace(manifest.MinecraftVersion)
            ? "未知版本"
            : "Minecraft " + manifest.MinecraftVersion;

        var loaderName = ModLoaderHelper.DisplayNameOf(manifest.Loader);
        LoaderText = manifest.Loader is ModLoaderKind.Vanilla or ModLoaderKind.Unknown
            ? "原版（无加载器）"
            : string.IsNullOrWhiteSpace(manifest.LoaderVersion)
                ? loaderName
                : $"{loaderName} {manifest.LoaderVersion}";

        FilesTotal = manifest.Files.Count;

        BuildSteps();
        BuildFiles();

        foreach (var w in manifest.Warnings)
            Warnings.Add(w);

        // 已用时间每秒刷新一次（只在没结束时跑，结束后停在最终值）。
        // 计时器纯显示用，拿不到 UI 调度器也不能拖累安装 —— 所以整段包在 try 里。
        try
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) =>
            {
                OnPropertyChanged(nameof(ElapsedText));
                OnPropertyChanged(nameof(RemainingText));
            };
            _timer.Start();
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "进度详情页计时器不可用（不影响安装）");
            _timer = null;
        }
    }

    /// <summary>开始一次安装会话（由 <see cref="ModpackInstallCoordinator"/> 调用）。</summary>
    internal static ModpackInstallSession Begin(
        ModpackInstaller.ModpackManifest manifest,
        string sourceLabel,
        string filePath,
        InstallKind kind = InstallKind.Modpack)
    {
        var session = new ModpackInstallSession(manifest, sourceLabel, filePath, kind);
        SetCurrent(session);
        return session;
    }

    /// <summary>清单（详情页展示用）。</summary>
    public ModpackInstaller.ModpackManifest Manifest { get; }

    /// <summary>安装包文件路径。</summary>
    public string FilePath { get; }

    // ==================== 基本信息 ====================

    /// <summary>这次安装的来源类型（整合包 / 配装码）。界面上所有「整合包」字样都要按它分支。</summary>
    public InstallKind Kind { get; }

    /// <summary>主语：「整合包」或「配装」。拼标题、提示文案时用，避免到处写三元。</summary>
    public string Subject { get; }

    public string PackName { get; }
    public string SourceLabel { get; }
    public string FormatText { get; }

    /// <summary>窗口标题（任务栏 / 自绘标题栏用）：区分「整合包安装」与「配装码安装」。</summary>
    public string WindowTitle => Kind == InstallKind.Loadout ? "配装码安装" : "整合包安装";

    /// <summary>「没有需要在线下载的文件」时的说明文案（整合包 / 配装用词不同）。</summary>
    public string NoFilesText => $"这个{Subject}没有需要在线下载的文件，内容全部随包携带";
    public string GameVersionText { get; }
    public string LoaderText { get; }

    private string _instanceIdText = "尚未创建";
    /// <summary>目标实例 id（安装过程中会填上）。</summary>
    public string InstanceIdText
    {
        get => _instanceIdText;
        private set => SetProperty(ref _instanceIdText, value);
    }

    // ==================== 阶段 / 文件 ====================

    /// <summary>阶段条。</summary>
    public ObservableCollection<ModpackStep> Steps { get; } = new();

    /// <summary>待下载文件清单。</summary>
    public ObservableCollection<ModpackFileEntry> Files { get; } = new();

    /// <summary>跳过 / 失败的文件说明（收尾时填）。</summary>
    public ObservableCollection<string> Skipped { get; } = new();

    /// <summary>提示信息（解析告警、装完的补充说明）。</summary>
    public ObservableCollection<string> Warnings { get; } = new();

    // ==================== 进度 ====================

    private int _percent;
    public int Percent
    {
        get => _percent;
        private set => SetProperty(ref _percent, value);
    }

    private string _stepText = "准备中…";
    public string StepText
    {
        get => _stepText;
        private set => SetProperty(ref _stepText, value);
    }

    private string _speed = string.Empty;
    public string Speed
    {
        get => _speed;
        private set => SetProperty(ref _speed, value);
    }

    public int FilesTotal { get; }

    private int _filesDone;
    public int FilesDone
    {
        get => _filesDone;
        private set
        {
            if (SetProperty(ref _filesDone, value))
                OnPropertyChanged(nameof(FilesText));
        }
    }

    /// <summary>「12 / 48」形式的文件计数。没有文件时给出说明而不是「0 / 0」。</summary>
    public string FilesText => FilesTotal == 0
        ? "无待下载文件（全靠随包文件）"
        : $"{FilesDone} / {FilesTotal}";

    public bool HasFiles => FilesTotal > 0;

    /// <summary>已用时间（mm:ss）。</summary>
    public string ElapsedText => FormatDuration(DateTime.Now - _startedAt);

    /// <summary>按平均速度估算的剩余时间。进度太靠前或已结束时给空串。</summary>
    public string RemainingText
    {
        get
        {
            if (IsFinished || Percent is <= 0 or >= 100)
                return string.Empty;

            var elapsed = (DateTime.Now - _startedAt).TotalSeconds;
            if (elapsed <= 2)
                return string.Empty;

            // 已用 / 已完成比例 → 总时长 → 剩余。丢给用户一个量级就够了，不追求精确。
            var remaining = elapsed / (Percent / 100.0) - elapsed;
            if (remaining <= 0 || remaining > TimeSpan.FromHours(6).TotalSeconds)
                return string.Empty;

            return "剩余约 " + FormatDuration(TimeSpan.FromSeconds(remaining));
        }
    }

    // ==================== 结束状态 ====================

    private bool _isFinished;
    public bool IsFinished
    {
        get => _isFinished;
        private set
        {
            if (SetProperty(ref _isFinished, value))
            {
                OnPropertyChanged(nameof(IsInProgress));
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(TitleText));
            }
        }
    }

    private bool _isCanceled;
    public bool IsCanceled
    {
        get => _isCanceled;
        private set => SetProperty(ref _isCanceled, value);
    }

    private string _errorMessage = string.Empty;
    /// <summary>失败原因（为空表示没失败）。</summary>
    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(TitleText));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>这一次安装是否还在跑（含「正在取消」）。
    /// 注意与静态的 <see cref="IsRunning"/> 区分：那个说的是「全局有没有安装在跑」。</summary>
    public bool IsInProgress => !IsFinished;

    /// <summary>正在取消中（取消已请求但还没结束）。</summary>
    private bool _isCanceling;
    public bool IsCanceling
    {
        get => _isCanceling;
        private set
        {
            if (SetProperty(ref _isCanceling, value))
            {
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(TitleText));
            }
        }
    }

    /// <summary>取消按钮是否可用。</summary>
    public bool CanCancel => IsInProgress && !IsCanceling;

    /// <summary>标题文案：随状态变化（进行中 / 已完成 / 已失败 / 已取消）。</summary>
    public string TitleText
    {
        get
        {
            if (IsCanceling) return "正在取消…";
            if (IsInProgress) return $"正在安装{Subject}";
            if (HasError) return "安装失败";
            if (IsCanceled) return "安装已取消";
            return "安装完成";
        }
    }

    private string _resultText = string.Empty;
    /// <summary>结束后的结果摘要。</summary>
    public string ResultText
    {
        get => _resultText;
        private set => SetProperty(ref _resultText, value);
    }

    /// <summary>成功时的新实例 id（供「打开实例目录」用）。</summary>
    public string InstanceId { get; private set; } = string.Empty;

    // ==================== 取消 ====================

    /// <summary>传给安装器的取消令牌。</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>请求取消安装。</summary>
    public void RequestCancel()
    {
        if (!IsInProgress || IsCanceling)
            return;

        IsCanceling = true;
        StepText = "正在取消…";
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { /* 已结束，忽略 */ }
    }

    // ==================== 状态写入 ====================

    /// <summary>把一次进度回调灌进来（**仅 UI 线程**）。</summary>
    public void Apply(ModpackInstaller.ModpackProgress p)
    {
        Percent = p.Percent;
        if (!string.IsNullOrEmpty(p.Step))
            StepText = p.Step;
        Speed = p.Speed ?? string.Empty;

        AdvanceSteps(p.Phase);

        if (p.FilesTotal > 0)
            FilesDone = p.FilesDone;

        // 文件级事件
        if (!string.IsNullOrEmpty(p.StartedFile) && _fileIndex.TryGetValue(p.StartedFile, out var started))
            started.State = ModpackFileState.Downloading;

        if (!string.IsNullOrEmpty(p.CompletedFile) && _fileIndex.TryGetValue(p.CompletedFile, out var done))
            done.State = ModpackFileState.Completed;

        if (!string.IsNullOrEmpty(p.FailedFile) && _fileIndex.TryGetValue(p.FailedFile, out var failed))
        {
            failed.State = ModpackFileState.Failed;
            failed.Detail = p.FailedReason ?? "下载失败";
        }
    }

    /// <summary>安装成功收尾。</summary>
    internal void FinishSuccess(ModpackInstaller.ModpackInstallResult result)
    {
        InstanceId = result.InstanceId;
        InstanceIdText = string.IsNullOrEmpty(result.InstanceId) ? "未创建" : result.InstanceId;

        foreach (var s in result.Skipped)
            Skipped.Add(s);

        foreach (var w in result.Warnings)
            Warnings.Add(w);

        // 失败的文件补齐说明（回调里可能因为并发/截断漏掉几条）
        foreach (var entry in Files.Where(f => f.State == ModpackFileState.Downloading))
            entry.State = ModpackFileState.Completed;

        var parts = new List<string>
        {
            $"已创建实例 {result.InstanceId}",
            $"下载 {result.DownloadedFiles} 个文件"
        };
        if (result.OverriddenFiles > 0)
            parts.Add($"写入 {result.OverriddenFiles} 个随包文件");
        if (result.Skipped.Count > 0)
            parts.Add($"{result.Skipped.Count} 个文件未成功");

        ResultText = string.Join("，", parts) + "。";
        Percent = 100;
        StepText = "安装完成";
        Speed = string.Empty;
        AdvanceSteps(ModpackInstaller.ModpackPhase.Done);
        Stop();
    }

    /// <summary>安装失败收尾。</summary>
    internal void FinishFailure(string message)
    {
        ErrorMessage = message;
        StepText = message;
        Speed = string.Empty;

        // 当前进行中的那一步标记失败，用户一眼看得出卡在哪
        var active = Steps.FirstOrDefault(s => s.IsActive);
        if (active is not null)
            active.State = ModpackStep.StepState.Failed;

        foreach (var entry in Files.Where(f => f.State == ModpackFileState.Downloading))
            entry.State = ModpackFileState.Pending;

        Stop();
    }

    /// <summary>安装被取消收尾。</summary>
    internal void FinishCanceled()
    {
        IsCanceled = true;
        StepText = "已取消";
        Speed = string.Empty;
        Stop();
    }

    private void Stop()
    {
        IsCanceling = false;
        IsFinished = true;
        _timer?.Stop();
        OnPropertyChanged(nameof(ElapsedText));
    }

    // ==================== 内部构造 ====================

    private void BuildSteps()
    {
        var m = Manifest;

        Steps.Add(new ModpackStep(ModpackInstaller.ModpackPhase.Preparing, "准备安装", "解析清单、确认目录"));

        Steps.Add(new ModpackStep(
            ModpackInstaller.ModpackPhase.GameInstall,
            "安装游戏版本",
            string.IsNullOrWhiteSpace(m.MinecraftVersion) ? "Minecraft" : m.MinecraftVersion));

        if (m.Loader is not (ModLoaderKind.Vanilla or ModLoaderKind.Unknown))
        {
            Steps.Add(new ModpackStep(
                ModpackInstaller.ModpackPhase.LoaderInstall,
                "安装 " + ModLoaderHelper.DisplayNameOf(m.Loader),
                string.IsNullOrWhiteSpace(m.LoaderVersion) ? "最新版" : m.LoaderVersion));
        }

        if (m.Files.Count > 0)
        {
            Steps.Add(new ModpackStep(
                ModpackInstaller.ModpackPhase.DownloadFiles,
                Kind == InstallKind.Loadout ? "下载配装模组" : "下载整合包文件",
                $"{m.Files.Count} 个文件"));
        }

        if (m.OverrideFolders.Count > 0)
        {
            Steps.Add(new ModpackStep(
                ModpackInstaller.ModpackPhase.Overrides,
                "写入随包文件",
                string.Join(" + ", m.OverrideFolders)));
        }

        Steps.Add(new ModpackStep(ModpackInstaller.ModpackPhase.Verifying, "校验实例", "确认可被启动器识别"));
    }

    private void BuildFiles()
    {
        foreach (var f in Manifest.Files)
        {
            var entry = new ModpackFileEntry(f.Path, f.Size);
            Files.Add(entry);
            _fileIndex[f.Path] = entry;
        }
    }

    /// <summary>
    /// 按新到的阶段推进阶段条：之前的都算完成，当前的高亮。
    /// 注意阶段可能被「跳过」（原版包没有加载器步骤），所以按枚举顺序比较而不是按索引。
    /// </summary>
    private void AdvanceSteps(ModpackInstaller.ModpackPhase phase)
    {
        if (phase == ModpackInstaller.ModpackPhase.Done)
        {
            foreach (var s in Steps)
                if (!s.IsFailed)
                    s.State = ModpackStep.StepState.Done;
            return;
        }

        foreach (var s in Steps)
        {
            if (s.Phase < phase)
            {
                if (s.IsActive || s.IsPending)
                    s.State = ModpackStep.StepState.Done;
            }
            else if (s.Phase == phase)
            {
                s.State = ModpackStep.StepState.Active;
            }
        }
    }

    private static string FormatDuration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;

        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}";
    }
}
