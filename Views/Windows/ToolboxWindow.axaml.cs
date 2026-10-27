using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Serilog;
using VibrantbitLauncher.Helpers;
using VibrantbitLauncher.Services;
using VibrantbitLauncher.ViewModels.Pages;

namespace VibrantbitLauncher.Views.Windows;

/// <summary>
/// 游戏内悬浮工具箱（Alt+Shift+T）。
///
/// <para>
/// 一块<b>半透明、置顶、无边框</b>的小窗，盖在游戏画面上，不抢任务栏位置。
/// 三件事都在这里做：快速联机（生成 / 输入联机码并起虚拟网络）、读存档种子、
/// 把常用指令敲进游戏。
/// </para>
///
/// <para>
/// <b>为什么不走 MVVM：</b>这个窗的东西全是「按一下就立刻做一件事」的命令式动作
/// （起进程、发按键、写剪贴板），没有需要持续观察的状态 —— 塞一层 ViewModel 只会多一层转发。
/// 与工程里其它工具窗（实例工具箱 / 补全资源 / 整合包进度）保持一致。
/// </para>
///
/// <para>
/// 它<b>常驻内存一次</b>：关闭走 <see cref="Window.Hide"/> 语义（由 ToolboxService 直接丢弃实例），
/// 每次唤出都是新建，避免上一轮的房间码 / 存档列表残留。
/// </para>
/// </summary>
public partial class ToolboxWindow : Window
{
    /// <summary>常用指令模板。写在这里而不是外部配置：模板本身就是「顺手用」的，不该需要维护。</summary>
    private static readonly (string Name, string Command)[] Templates =
    {
        ("给予物品", "/give @s minecraft:diamond 64"),
        ("召唤生物", "/summon minecraft:zombie ~ ~ ~"),
        ("设置时间", "/time set day"),
        ("传送坐标", "/tp @s 0 64 0"),
        ("切换模式", "/gamemode creative"),
        ("天气", "/weather clear"),
        ("添加效果", "/effect give @s minecraft:speed 60 1"),
        ("增加经验", "/xp add @s 100 levels"),
        ("清空背包", "/clear @s"),
        ("开启死亡不掉落", "/gamerule keepInventory true"),
        ("切换难度", "/difficulty peaceful"),
        ("生成结构", "/place structure minecraft:village_plains")
    };

    /// <summary>
    /// 「给予物品」的常用物品清单。
    ///
    /// <para>
    /// 只列真正用得上的一批，不追求全量 —— 把一千多个物品塞进下拉框，等于让人自己回忆 id。
    /// 数量按直觉给：材料给满一组（64），装备 / 工具 / 特殊方块给 1。
    /// </para>
    /// </summary>
    private static readonly (string Name, string Id, int Count)[] CommonItems =
    {
        // 材料
        ("钻石", "minecraft:diamond", 64),
        ("下界合金锭", "minecraft:netherite_ingot", 64),
        ("铁锭", "minecraft:iron_ingot", 64),
        ("金锭", "minecraft:gold_ingot", 64),
        ("绿宝石", "minecraft:emerald", 64),
        ("青金石", "minecraft:lapis_lazuli", 64),
        ("红石", "minecraft:redstone", 64),
        ("煤炭", "minecraft:coal", 64),
        ("荧石", "minecraft:glowstone", 64),
        ("黑曜石", "minecraft:obsidian", 64),
        ("圆石", "minecraft:cobblestone", 64),
        ("橡木原木", "minecraft:oak_log", 64),
        ("玻璃", "minecraft:glass", 64),
        ("火把", "minecraft:torch", 64),

        // 食物
        ("金苹果", "minecraft:golden_apple", 64),
        ("附魔金苹果", "minecraft:enchanted_golden_apple", 64),
        ("熟牛排", "minecraft:cooked_beef", 64),
        ("面包", "minecraft:bread", 64),

        // 工具 · 武器
        ("下界合金剑", "minecraft:netherite_sword", 1),
        ("钻石剑", "minecraft:diamond_sword", 1),
        ("下界合金镐", "minecraft:netherite_pickaxe", 1),
        ("钻石镐", "minecraft:diamond_pickaxe", 1),
        ("钻石斧", "minecraft:diamond_axe", 1),
        ("弓", "minecraft:bow", 1),
        ("箭", "minecraft:arrow", 64),
        ("盾牌", "minecraft:shield", 1),
        ("三叉戟", "minecraft:trident", 1),
        ("鞘翅", "minecraft:elytra", 1),
        ("烟花火箭", "minecraft:firework_rocket", 64),

        // 装备
        ("钻石头盔", "minecraft:diamond_helmet", 1),
        ("钻石胸甲", "minecraft:diamond_chestplate", 1),
        ("钻石护腿", "minecraft:diamond_leggings", 1),
        ("钻石靴子", "minecraft:diamond_boots", 1),

        // 特殊
        ("末影珍珠", "minecraft:ender_pearl", 16),
        ("末影之眼", "minecraft:ender_eye", 16),
        ("不死图腾", "minecraft:totem_of_undying", 1),
        ("经验瓶", "minecraft:experience_bottle", 64),
        ("TNT", "minecraft:tnt", 64),
        ("床", "minecraft:red_bed", 1),
        ("水桶", "minecraft:water_bucket", 1),
        ("岩浆桶", "minecraft:lava_bucket", 1),
        ("牛奶桶", "minecraft:milk_bucket", 1),
        ("附魔台", "minecraft:enchanting_table", 1),
        ("铁砧", "minecraft:anvil", 1),
        ("信标", "minecraft:beacon", 1),
        ("命令方块", "minecraft:command_block", 1),
        ("屏障", "minecraft:barrier", 64),
        ("结构方块", "minecraft:structure_block", 1)
    };

    /// <summary>浮窗离屏幕边缘的留白（DIP）。</summary>
    private const double ScreenMargin = 16;

    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1.2) };

    private List<WorldSave> _worlds = new();
    private bool _suppressWorldSync;
    private bool _busy;

    /// <summary>物品下拉在改指令时会把模板下拉也改成「给予物品」，用这个标志掐断那边的重入。</summary>
    private bool _syncingCommand;

    /// <summary>当前画出来的地图位图（null = 还没画 / 没得画）。</summary>
    private WriteableBitmap? _mapBitmap;

    /// <summary>已经渲染过地图的存档目录，避免切来切去反复读盘。</summary>
    private string? _mapLoadedFor;

    /// <summary>地图当前缩放倍数（1 = 整张图刚好放进框里）。</summary>
    private double _mapZoom = 1;

    private Point _mapDragStart;
    private Point _mapDragOrigin;
    private bool _mapDragging;

    private readonly ScaleTransform _mapScale = new(1, 1);
    private readonly TranslateTransform _mapTranslate = new();

    /// <summary>
    /// 上一次启动联机失败的原因。
    ///
    /// 状态栏每 1.2 秒刷新一次，未运行时会被刷成「尚未开启虚拟网络」——
    /// 若不把错误记在这里，「启动失败」那一句会在下一次刷新时被吞掉，玩家只看到「没启动」。
    /// </summary>
    private string? _multiplayerError;

    public ToolboxWindow()
    {
        InitializeComponent();

        InitializeMapTransform();
        InitializeTemplates();
        InitializeItems();
        LoadHostCode();
        ApplyRole();
        UpdateSessionHeader();

        _statusTimer.Tick += (_, _) => RefreshMultiplayerStatus();

        Opened += OnOpened;
        Closed += OnClosed;
        KeyDown += OnKeyDown;
    }

    // ============================ 生命周期 ============================

    private async void OnOpened(object? sender, EventArgs e)
    {
        PlaceAtTopRight();

        // 逐像素透明在部分老系统上不可用（没有 DWM 合成时）。那种情况下窗口是「什么都没画」，
        // 会看起来像一片黑或者干脆看不见 —— 退回不透明底色，至少内容还在。
        if (ActualTransparencyLevel == WindowTransparencyLevel.None)
        {
            if (this.TryFindResource("SurfaceCanvasBrush", ActualThemeVariant, out var brush) && brush is IBrush b)
                Shell.Background = b;
        }

        RefreshMultiplayerStatus();
        _statusTimer.Start();

        await RefreshWorldsAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _statusTimer.Stop();
    }

    /// <summary>
    /// 默认停在屏幕**右上角**。
    ///
    /// <para>
    /// 选右上不是随手定的：游戏 HUD 里血条 / 快捷栏在正下方，小地图与状态类模组大多在左上，
    /// 右上角是遮挡最少的位置。偏移量按「工作区」而不是「整屏」算，任务栏在上面或左侧时也不会跑偏。
    /// </para>
    /// </summary>
    private void PlaceAtTopRight()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
            return;

        var scaling = screen.Scaling > 0 ? screen.Scaling : 1d;
        var area = screen.WorkingArea;

        // Position 是物理像素，Width / Height 是 DIP，跨屏不同缩放时这一步不能省
        var width = (int)Math.Round(Bounds.Width > 0 ? Bounds.Width : Width, MidpointRounding.AwayFromZero);
        var height = (int)Math.Round(Bounds.Height > 0 ? Bounds.Height : Height, MidpointRounding.AwayFromZero);

        var margin = (int)Math.Round(ScreenMargin * scaling);
        var x = area.Right - (int)Math.Round(width * scaling) - margin;
        var y = area.Y + margin;

        // 屏幕比窗口还窄时不把窗口推出屏幕外，宁可贴左边
        Position = new Avalonia.PixelPoint(
            Math.Max(area.X + margin, x),
            Math.Max(area.Y + margin, y));
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void DragBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // 无边框窗口没有系统标题栏可拖，自绘这块负责「按住拖走」
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    // ============================ 会话 ============================

    private static GameSession? Session => AppServices.GameSession?.Current;

    /// <summary>当前<b>实际生效</b>的唤出键位（可能因被占用而退到备选）。</summary>
    private static string HotkeyText => AppServices.Toolbox?.ActiveHotkeyText ?? ToolboxService.DefaultHotkeyText;

    private void UpdateSessionHeader()
    {
        var session = Session;

        InstanceText.Text = session is null
            ? "未检测到正在运行的游戏"
            : $"{session.InstanceId} · {session.DescribeLoader()} · 正在运行";

        // 热键只在游戏运行时才注册，所以底栏那句提示要跟着会话走 ——
        // 从托盘手动打开（没在玩游戏）时不能骗人说「按 X 呼出」。
        var hotkey = HotkeyText;
        HotkeyHint.Text = session is null
            ? $"游戏运行时按 {hotkey} 呼出"
            : $"{hotkey} 随时呼出";

        ToolTip.SetTip(CloseButton, $"关闭（{hotkey} 可再次唤出）");
    }

    // ============================ 快速联机 ============================

    private void LoadHostCode()
    {
        var saved = SettingsService.Current.EasyTierNetworkKey;

        if (RoomCode.TryParse(saved, out var code, out _) && code is not null)
        {
            HostCodeText.Text = code.Code;
            return;
        }

        var generated = RoomCode.Generate();
        HostCodeText.Text = generated;
        SettingsService.Current.EasyTierNetworkKey = generated;
        SettingsService.Save();
    }

    private bool IsHostSelected => HostRadio.IsChecked != false;

    private void Role_Changed(object? sender, RoutedEventArgs e) => ApplyRole();

    private void ApplyRole()
    {
        if (HostPanel is null || GuestPanel is null)
            return;

        var host = IsHostSelected;
        HostPanel.IsVisible = host;
        GuestPanel.IsVisible = !host;
    }

    private void Regenerate_Click(object? sender, RoutedEventArgs e)
    {
        var code = RoomCode.Generate();
        HostCodeText.Text = code;
        SettingsService.Current.EasyTierNetworkKey = code;
        SettingsService.Save();
        MultiplayerDetail.Text = "已换新码。旧码对应的房间不会再有人进来。";
    }

    private async void CopyCode_Click(object? sender, RoutedEventArgs e)
    {
        var text = IsHostSelected ? HostCodeText.Text : GuestCodeBox.Text;
        var ok = await CopyAsync(text);

        MultiplayerDetail.Text = ok
            ? "联机码已复制，发到群里等好友进来。"
            : "复制失败，请手动选中后复制。";
    }

    private async void Paste_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var text = await Clipboard!.TryGetTextAsync();
            if (!string.IsNullOrWhiteSpace(text))
                GuestCodeBox.Text = text.Trim();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "读取剪贴板失败");
            MultiplayerDetail.Text = "读不到剪贴板内容，请手动粘贴。";
        }
    }

    private async void Start_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        var easyTier = AppServices.EasyTier;
        if (easyTier is null)
        {
            MultiplayerStatus.Text = "联机服务未就绪。";
            return;
        }

        var host = IsHostSelected;
        var raw = host ? HostCodeText.Text : GuestCodeBox.Text;

        if (string.IsNullOrWhiteSpace(raw))
        {
            MultiplayerStatus.Text = host ? "还没有房间码。" : "请先填入房主给你的联机码。";
            return;
        }

        if (!RoomCode.TryParse(raw, out var code, out var parseError) || code is null)
        {
            MultiplayerStatus.Text = parseError ?? "联机码无效。";
            return;
        }

        _busy = true;
        StartButton.IsEnabled = false;

        try
        {
            if (host)
            {
                HostCodeText.Text = code.Code;
                SettingsService.Current.EasyTierNetworkKey = code.Code;
                SettingsService.Save();
            }
            else
            {
                GuestCodeBox.Text = code.Code;
            }

            MultiplayerStatus.Text = "正在连接虚拟网络…";

            // 中继那一栏可能是「发地址的接口」而不是真节点，先把当下的真实地址取出来。
            // 取不到也照样往下走：核心自己有重试，我们这边取不到不代表它取不到。
            var relay = SettingsService.Current.EasyTierRelayServer;
            var chain = new List<string> { string.IsNullOrWhiteSpace(relay) ? RoomCode.DefaultRelay : relay };
            if (RoomCode.ExtraRelaysFor(chain[0]) is { } extras)
                chain.AddRange(extras);

            var relays = new List<string>(await PublicRelayResolver.ResolveAllAsync(chain));
            if (relays.Count == 0)
                relays.Add(chain[0]);

            // 先把上一次的失败原因清掉，否则万一这次成功、状态栏还会挂着旧错误
            _multiplayerError = null;

            var error = await easyTier.StartAsync(
                code.NetworkName,
                code.NetworkSecret,
                host,
                relays[0],
                SettingsService.Current.KeepMultiplayerAlive,
                relays.Skip(1).ToArray());

            if (error is not null)
            {
                _multiplayerError = error;
                MultiplayerDetail.Text = string.Empty;
            }
            else
            {
                // 下一步该做什么放 Detail 这行：状态行由定时器占用，写了也留不住
                MultiplayerDetail.Text = host
                    ? "下一步：进游戏「对局域网开放」，让好友在「多人游戏 → 直接连接」里填你的虚拟 IP。"
                    : "下一步：进游戏「多人游戏 → 直接连接」，填房主的虚拟 IP。";
            }
        }
        catch (Exception ex)
        {
            // 命令是 async void 派发过来的，异常漏出去会直接崩掉应用
            Log.Error(ex, "工具箱启动联机失败");
            _multiplayerError = $"启动联机出错：{ex.Message}";
        }
        finally
        {
            _busy = false;
            StartButton.IsEnabled = true;
            RefreshMultiplayerStatus();
        }
    }

    private void Stop_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            _multiplayerError = null;
            AppServices.EasyTier?.Stop();
            MultiplayerDetail.Text = "已断开虚拟网络。";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "工具箱停止联机失败");
            _multiplayerError = $"断开联机出错：{ex.Message}";
        }

        RefreshMultiplayerStatus();
    }

    private void RefreshMultiplayerStatus()
    {
        var easyTier = AppServices.EasyTier;
        if (easyTier is null)
            return;

        var running = easyTier.IsRunning;
        StopButton.IsEnabled = running;

        if (!running)
        {
            MultiplayerStatus.Text = _multiplayerError ?? "尚未开启虚拟网络。";
            return;
        }

        var ip = easyTier.LocalVirtualIp;
        var role = easyTier.IsHost ? "房主" : "房客";
        MultiplayerStatus.Text = string.IsNullOrEmpty(ip)
            ? $"虚拟网络已启动（{role}），正在获取虚拟 IP…"
            : $"虚拟网络已启动（{role}）· 你的虚拟 IP：{ip}";
    }

    // ============================ 种子 ============================

    /// <summary>
    /// 列出「存档可能在哪」的全部候选目录。
    ///
    /// <para>
    /// 不能只算一个：开了版本隔离时存档在 <c>versions/&lt;id&gt;/saves</c>，没开在根 <c>saves</c>，
    /// 用户还可能把存档目录挪到别处。更要紧的是<b>游戏没在跑的时候没有会话</b>，
    /// 这时推不出是哪个版本 —— 以前只回退到根 <c>saves</c>，隔离用户那里那目录根本不存在，
    /// 于是「读不出来种子」。现在改成把所有候选都扫一遍再按真实路径去重。
    /// </para>
    /// </summary>
    private static List<(string Path, string Label)> ResolveSavesFolders()
    {
        var folders = new List<(string Path, string Label)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path, string label)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            string full;
            try { full = Path.GetFullPath(path); }
            catch { return; }

            if (seen.Add(full))
                folders.Add((full, label));
        }

        // 1) 用户显式指定的存档目录最优先 —— 他手动挪过，那就是他真正在用的
        var configured = SettingsService.Current.SavesFolder;
        if (!string.IsNullOrWhiteSpace(configured))
            Add(configured, "自定义");

        // 2) 正在玩的那个实例：会话里记着实际 gameDir，比按设置重新推一遍可靠
        var session = Session;
        if (session is not null && !string.IsNullOrWhiteSpace(session.McFolder))
        {
            Add(Path.Combine(session.McFolder, "versions", session.InstanceId, "saves"), session.InstanceId);
            Add(Path.Combine(session.McFolder, "saves"), "根目录");
        }

        // 3) 兜底：把启动器管的 .minecraft 整个扫一遍（游戏没跑时也要能看种子）
        string mcFolder;
        try { mcFolder = GameInstanceHelper.GetMinecraftFolder(); }
        catch (Exception ex)
        {
            Log.Debug(ex, "解析游戏目录失败");
            return folders;
        }

        Add(Path.Combine(mcFolder, "saves"), "根目录");

        var versionsDir = Path.Combine(mcFolder, "versions");
        try
        {
            if (Directory.Exists(versionsDir))
            {
                foreach (var versionDir in Directory.GetDirectories(versionsDir))
                    Add(Path.Combine(versionDir, "saves"), Path.GetFileName(versionDir));
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "枚举 versions 目录失败：{Dir}", versionsDir);
        }

        return folders;
    }

    private async Task RefreshWorldsAsync()
    {
        try
        {
            var folders = ResolveSavesFolders();
            Log.Debug("种子页扫描存档目录：{Dirs}", string.Join(" | ", folders.Select(f => f.Path)));

            _worlds = await Task.Run(() => WorldSeedReader.ReadAllWorlds(folders));

            // 来源不止一个时才在下拉里标出来，否则全是「根目录 · xxx」很吵
            var labelSources = _worlds
                .Select(w => w.Source)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() > 1;

            _suppressWorldSync = true;
            WorldCombo.ItemsSource = _worlds
                .Select(w => labelSources && w.Source.Length > 0 ? $"{w.Source} · {w.DisplayName}" : w.DisplayName)
                .ToList();
            _suppressWorldSync = false;

            if (_worlds.Count == 0)
            {
                WordHint(folders.Count == 0
                    ? "没找到存档目录，先去「设置 → 游戏目录」确认路径。"
                    : "这些位置都没有存档，换个实例启动一次再来。");
                Log.Information("种子页没有找到存档，扫过：{Dirs}", string.Join(" | ", folders.Select(f => f.Path)));
                return;
            }

            // 默认选最近玩的那个，并把种子填进输入框
            WorldCombo.SelectedIndex = 0;
            ApplyWorld(_worlds[0]);

            var readable = _worlds.Count(w => w.Seed.HasValue);
            WordHint($"共 {_worlds.Count} 个存档，读到种子 {readable} 个。");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "读取存档列表失败");
            WordHint("读取存档失败，可以直接手动填种子。");
        }
    }

    private void WordHint(string text) => SeedStatus.Text = text;

    private void WorldCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressWorldSync)
            return;

        var index = WorldCombo.SelectedIndex;
        if (index < 0 || index >= _worlds.Count)
            return;

        ApplyWorld(_worlds[index]);
    }

    private void ApplyWorld(WorldSave world)
    {
        if (world.Seed.HasValue)
        {
            SeedBox.Text = world.Seed.Value.ToString(CultureInfo.InvariantCulture);
            WordHint($"「{world.Name}」的种子已读出。");
        }
        else
        {
            SeedBox.Text = string.Empty;
            WordHint($"「{world.Name}」的种子读不出来（可能是模组存档或文件损坏），可以手动填。");
        }

        // 地图是读盘 + 解压，不能压在这条同步链上；异常在 LoadMapAsync 里收
        _ = LoadMapAsync(world);
    }

    private bool TryReadSeed(out long seed)
    {
        seed = 0;
        var raw = (SeedBox.Text ?? string.Empty).Trim();

        if (raw.Length == 0)
            return false;

        // 允许玩家从别处粘进来的带分隔符写法（1,234 / 空格）
        raw = raw.Replace(",", string.Empty).Replace("_", string.Empty).Trim();
        return long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out seed);
    }

    private async void CopySeed_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryReadSeed(out var seed))
        {
            WordHint("还没有可复制的种子。先选一个存档，或者手动填一个整数。");
            return;
        }

        var ok = await CopyAsync(seed.ToString(CultureInfo.InvariantCulture));
        WordHint(ok ? $"种子 {seed} 已复制。" : "复制失败，请手动选中后复制。");
    }

    // ============================ 地图 ============================

    /// <summary>
    /// 把当前存档<b>已经生成过</b>的地形画进浮窗里。
    ///
    /// <para>
    /// 数据来自存档本身的区块高度图，所以不需要联网、不需要种子算法、也不会随版本对不上 ——
    /// 代价是只能看到去过的区域，这一点对「边玩边看」正好够用。
    /// </para>
    /// </summary>
    private async Task LoadMapAsync(WorldSave world)
    {
        if (_mapBitmap is not null &&
            string.Equals(_mapLoadedFor, world.FolderPath, StringComparison.Ordinal))
            return;

        _mapLoadedFor = null;
        MapEmptyHint.Text = "正在画地图…";
        MapEmptyHint.IsVisible = true;

        try
        {
            var map = await WorldMapRenderer.RenderAsync(world.FolderPath);

            // 渲染期间玩家可能已经换存档 / 关了窗口
            if (!IsVisible)
                return;

            if (map is null)
            {
                _mapBitmap = null;
                MapImage.Source = null;
                MapEmptyHint.Text = "这个存档还没有生成地形，进去跑两步再来。";
                MapEmptyHint.IsVisible = true;
                WordHint($"「{world.Name}」还没有可画的地形。");
                return;
            }

            var bitmap = new WriteableBitmap(
                new PixelSize(map.PixelWidth, map.PixelHeight),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Unpremul);

            using (var frame = bitmap.Lock())
            {
                var rowBytes = map.PixelWidth * 4;
                for (var y = 0; y < map.PixelHeight; y++)
                {
                    // 不能假设 RowBytes == 行宽：缓冲区可能按 4 字节对齐补过
                    Marshal.Copy(map.Pixels, y * rowBytes, IntPtr.Add(frame.Address, y * frame.RowBytes), rowBytes);
                }
            }

            _mapBitmap = bitmap;
            _mapLoadedFor = world.FolderPath;

            MapImage.Source = bitmap;
            MapEmptyHint.IsVisible = false;
            ResetMapView();

            var (x1, z1) = map.WorldTopLeft;
            var (x2, z2) = map.WorldBottomRight;
            WordHint(map.Note ?? $"「{world.Name}」已探索 {map.FilledChunks} 个区块（x {x1}~{x2}，z {z1}~{z2}）。滚轮缩放、按住拖动可看细节，双击复位。");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "渲染存档地图失败");
            MapEmptyHint.Text = "地图画不出来，可以看日志里的原因。";
            MapEmptyHint.IsVisible = true;
        }
    }

    /// <summary>
    /// 用代码给地图挂上「缩放 + 平移」两层变换。
    ///
    /// <para>
    /// 写进 XAML 也行，但 <c>Transform</c> 不是控件，给它 <c>x:Name</c> 拿不到生成的字段，
    /// 代码里直接建反而少一层猜。
    /// </para>
    /// </summary>
    private void InitializeMapTransform()
    {
        // 以中心为缩放原点：放大时往外扩，视觉上不会「往右下角跑」
        MapImage.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        MapImage.RenderTransform = new TransformGroup { Children = { _mapScale, _mapTranslate } };
    }

    private void ResetMapView()
    {
        _mapZoom = 1;
        _mapScale.ScaleX = 1;
        _mapScale.ScaleY = 1;
        _mapTranslate.X = 0;
        _mapTranslate.Y = 0;
    }

    private void MapHost_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_mapBitmap is null)
            return;

        _mapZoom = Math.Clamp(_mapZoom * (e.Delta.Y > 0 ? 1.25 : 0.8), 1, 10);
        _mapScale.ScaleX = _mapZoom;
        _mapScale.ScaleY = _mapZoom;
        e.Handled = true;
    }

    private void MapHost_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_mapBitmap is null || !e.GetCurrentPoint(MapHost).Properties.IsLeftButtonPressed)
            return;

        _mapDragging = true;
        _mapDragStart = e.GetPosition(MapHost);
        _mapDragOrigin = new Point(_mapTranslate.X, _mapTranslate.Y);
        e.Pointer.Capture(MapHost);
    }

    private void MapHost_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_mapDragging)
            return;

        var now = e.GetPosition(MapHost);
        _mapTranslate.X = _mapDragOrigin.X + (now.X - _mapDragStart.X);
        _mapTranslate.Y = _mapDragOrigin.Y + (now.Y - _mapDragStart.Y);
    }

    private void MapHost_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_mapDragging)
            return;

        _mapDragging = false;
        e.Pointer.Capture(null);
    }

    private void MapHost_DoubleTapped(object? sender, TappedEventArgs e) => ResetMapView();

    // ============================ 快捷指令 ============================

    private void InitializeTemplates()
    {
        TemplateCombo.ItemsSource = Templates.Select(t => t.Name).ToList();
    }

    private void InitializeItems()
    {
        ItemCombo.ItemsSource = CommonItems.Select(i => i.Name).ToList();
    }

    /// <summary>
    /// 选一个常用物品 = 直接生成一条 <c>/give</c>。
    ///
    /// <para>
    /// 只有物品名和时间 / 传送这些模板的差别在于：物品 id 是最难记的部分，
    /// 数量反而是玩家愿意手改的数字，所以这里按「物品给默认数量」处理，
    /// 想改数量就改文本框里的那个数。
    /// </para>
    /// </summary>
    private void ItemCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingCommand)
            return;

        var index = ItemCombo.SelectedIndex;
        if (index < 0 || index >= CommonItems.Length)
            return;

        var item = CommonItems[index];
        CommandBox.Text = $"/give @s {item.Id} {item.Count}";

        // 上方模板下拉同步到「给予物品」——否则会出现「模板写着设置时间、下面却是 give」的错位。
        // 但这次赋值会反过来触发模板的 SelectionChanged 把指令覆盖掉，所以要用标志位掐断。
        _syncingCommand = true;
        try
        {
            TemplateCombo.SelectedIndex = 0;
        }
        finally
        {
            _syncingCommand = false;
        }

        CommandStatus.Text = string.Empty;
    }

    private void TemplateCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingCommand)
            return;

        var index = TemplateCombo.SelectedIndex;
        if (index < 0 || index >= Templates.Length)
            return;

        CommandBox.Text = Templates[index].Command;

        // 换了模板就把物品那栏清空 —— 否则会留下「常用物品写着下界合金剑、指令却是 /time set day」的错位。
        // 清空本身会触发物品下拉的 SelectionChanged，同样要用标志位挡住。
        if (ItemCombo.SelectedIndex >= 0)
        {
            _syncingCommand = true;
            try
            {
                ItemCombo.SelectedIndex = -1;
            }
            finally
            {
                _syncingCommand = false;
            }
        }

        CommandStatus.Text = string.Empty;
    }

    private async void CopyCommand_Click(object? sender, RoutedEventArgs e)
    {
        var text = (CommandBox.Text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            CommandStatus.Text = "指令是空的。";
            return;
        }

        var ok = await CopyAsync(text);
        CommandStatus.Text = ok ? "指令已复制，回到游戏里按 T 粘贴即可。" : "复制失败。";
    }

    private async void SendCommand_Click(object? sender, RoutedEventArgs e)
    {
        var command = (CommandBox.Text ?? string.Empty).Trim();
        if (command.Length == 0)
        {
            CommandStatus.Text = "指令是空的。";
            return;
        }

        var sessions = AppServices.GameSession;
        if (sessions is null)
        {
            CommandStatus.Text = "会话服务未就绪。";
            return;
        }

        var hwnd = sessions.TryGetWindowHandle(out var session);
        if (hwnd == IntPtr.Zero)
        {
            CommandStatus.Text = "没找到正在运行的游戏。指令只能送进已经启动的游戏。";
            return;
        }

        SendCommandButton.IsEnabled = false;
        CommandStatus.Text = "正在切到游戏并输入…";

        try
        {
            // 按键注入会短暂阻塞（等聊天框弹出来），丢到线程池，别让窗口卡住
            var error = await Task.Run(() => GameInputSender.SendCommand(hwnd, command));

            CommandStatus.Text = error
                ?? $"已送进「{session?.InstanceId}」。没反应的话检查一下：单人存档要开作弊，服务器要有 OP。";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "向游戏注入指令失败");
            CommandStatus.Text = $"送进游戏失败：{ex.Message}";
        }
        finally
        {
            SendCommandButton.IsEnabled = true;
        }
    }

    // ============================ 杂项 ============================

    private async Task<bool> CopyAsync(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0)
            return false;

        try
        {
            if (Clipboard is null)
                return false;

            await Clipboard.SetTextAsync(value);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "写剪贴板失败");
            return false;
        }
    }
}
