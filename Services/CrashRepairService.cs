using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MinecraftLaunch.Base.Enums;
using MinecraftLaunch.Base.Models.Game;
using VibrantbitLauncher.Helpers;
using VibrantbitLauncher.ViewModels.Windows;

namespace VibrantbitLauncher.Services;

/// <summary>修复动作的类别（决定图标与配色）。</summary>
public enum RepairKind
{
    /// <summary>调内存。</summary>
    Memory = 0,

    /// <summary>换 / 修 Java。</summary>
    Java,

    /// <summary>禁用某个模组。</summary>
    DisableMod,

    /// <summary>禁用重复 / 冲突的模组副本。</summary>
    DisableDuplicate,

    /// <summary>只是建议，没有可执行动作（例如更新显卡驱动）。</summary>
    Guidance,

    /// <summary>打开目录 / 报告，辅助排查。</summary>
    OpenFolder,
}

/// <summary>一次修复的执行结果。</summary>
public sealed class RepairOutcome
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;

    public static RepairOutcome Ok(string message) => new() { Success = true, Message = message };
    public static RepairOutcome Fail(string message) => new() { Success = false, Message = message };
}

/// <summary>
/// 一条可执行的修复建议。
/// </summary>
public sealed class RepairAction
{
    public RepairKind Kind { get; init; }

    /// <summary>标题，例如「内存不足」。</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>为什么这么判、会发生什么。</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>按钮上的字，例如「把最大内存调到 4096 MB」。</summary>
    public string ActionLabel { get; init; } = string.Empty;

    /// <summary>能否执行；false 表示只展示建议（按钮置灰）。</summary>
    public bool CanExecute { get; init; }

    /// <summary>执行体。返回 null 表示这条只是展示。</summary>
    public Func<CancellationToken, Task<RepairOutcome>>? Execute { get; init; }

    /// <summary>界面上用来做显隐 / 配色的辅助标签。</summary>
    public string KindText => Kind switch
    {
        RepairKind.Memory => "内存",
        RepairKind.Java => "Java",
        RepairKind.DisableMod => "模组",
        RepairKind.DisableDuplicate => "冲突",
        RepairKind.OpenFolder => "排查",
        _ => "建议",
    };
}

/// <summary>
/// 崩溃一键修复。
///
/// <para>
/// <b>定位</b>：<see cref="MinecraftLaunch.Components.Logging.LogAnalyzer"/> 负责「说出原因」，
/// 这个类负责「把原因翻译成能点的动作」。它不重复做日志匹配，而是把
/// <see cref="CrashReasons"/> 归类，再补齐日志分析覆盖不到的场景（例如现网最常见的
/// <c>OutOfMemoryError</c>）。
/// </para>
///
/// <para>
/// <b>三条原则</b>：
/// <list type="number">
/// <item>能给一次点击解决的就给动作（改内存、换 Java、禁用模组都只动本地配置 / 文件，可逆）</item>
/// <item>判断不出「改成多少」时给建议而不是瞎猜（显卡驱动、OptiFine 冲突这类只能靠人）</item>
/// <item>禁用模组一律用**改名**而不是删除 —— 玩家随时能改回来，误判的代价是 0</item>
/// </list>
/// </para>
/// </summary>
public static class CrashRepairService
{
    /// <summary>模组被禁用后追加的后缀（加载器只认 .jar，改掉扩展名即可让它不加载）。</summary>
    public const string DisabledSuffix = ".disabled";

    /// <summary>
    /// 根据分析结果生成修复清单（按「能一键解决」优先排序）。
    /// </summary>
    public static List<RepairAction> Build(
        MinecraftEntry? minecraft,
        IReadOnlyCollection<CrashReasons> reasons,
        IReadOnlyCollection<string> suspiciousMods,
        string? crashReportText)
    {
        var actions = new List<RepairAction>();
        var signals = new HashSet<CrashReasons>(reasons);

        // 日志分析没覆盖的常见信号，从报告文本里补
        foreach (var extra in ScanText(crashReportText))
            signals.Add(extra);

        var instanceId = minecraft?.Id ?? string.Empty;
        var modsFolder = string.IsNullOrWhiteSpace(instanceId)
            ? string.Empty
            : GameInstanceHelper.ResolveInstanceFolder(instanceId, "mods");

        // ---------- 1. 内存 ----------
        if (signals.Contains(CrashReasons.InsufficientMemory)
            || signals.Contains(CrashReasons.Using32BitJavaCausedInsufficientJVMMemory))
        {
            var is32Bit = signals.Contains(CrashReasons.Using32BitJavaCausedInsufficientJVMMemory);
            actions.Add(BuildMemoryRepair(is32Bit));
        }

        // ---------- 2. Java ----------
        var javaIssue = signals.Contains(CrashReasons.JavaVersionTooHigh)
                        || signals.Contains(CrashReasons.UnsupportedJavaClassVersionError)
                        || signals.Contains(CrashReasons.LowVersionForgeIncompatibleWithHighVersionJava)
                        || signals.Contains(CrashReasons.UsingJDK)
                        || signals.Contains(CrashReasons.UsingOpenJ9);

        if (javaIssue)
        {
            var wanted = SuggestedJavaMajor(instanceId, signals);
            actions.Add(BuildJavaRepair(signals, wanted));
        }

        // ---------- 3. 冲突 / 重复 ----------
        if (signals.Contains(CrashReasons.ModInstalledRepeatedly) && !string.IsNullOrWhiteSpace(instanceId))
        {
            var conflict = TryFindDuplicate(instanceId);
            if (conflict is not null)
                actions.Add(BuildDuplicateRepair(conflict));
        }

        // ---------- 4. 禁用可疑模组 ----------
        foreach (var name in suspiciousMods)
        {
            var file = TryLocateModFile(modsFolder, name);
            if (file is null)
                continue;

            // 重复项（同名模组的多个副本）已经由上面统一处理，不再逐个重复列
            if (actions.Any(a => a.Kind == RepairKind.DisableDuplicate
                                 && a.Detail.Contains(Path.GetFileName(file), StringComparison.OrdinalIgnoreCase)))
                continue;

            actions.Add(BuildDisableModRepair(file));
        }

        // ---------- 5. 纯建议 ----------
        if (signals.Contains(CrashReasons.GraphicsCardDoesNotSupportOpenGL)
            || signals.Contains(CrashReasons.ShaderOrResourcePackCausedOpenGL1282Error)
            || signals.Contains(CrashReasons.TextureTooLargeOrInsufficientGraphicsConfig))
        {
            actions.Add(new RepairAction
            {
                Kind = RepairKind.Guidance,
                Title = "显卡 / 图形设置问题",
                Detail = "游戏报告 OpenGL 相关错误。请先更新显卡驱动（核显也要更新）；"
                         + "若装了光影或高清材质包，先把它们移出实例再试。",
                ActionLabel = "需要手动处理",
                CanExecute = false,
            });
        }

        if (signals.Contains(CrashReasons.OptiFineIncompatibleWithForge))
        {
            actions.Add(new RepairAction
            {
                Kind = RepairKind.Guidance,
                Title = "OptiFine 与 Forge 不兼容",
                Detail = "这个 Forge 版本不能直接用 OptiFine。请改用 OptiFine 的 Forge 兼容版本，"
                         + "或改用量钠（Sodium）/ Iris 这类加载器专用的性能模组。",
                ActionLabel = "需要手动处理",
                CanExecute = false,
            });
        }

        // ---------- 6. 兜底：打开报告目录 ----------
        actions.Add(new RepairAction
        {
            Kind = RepairKind.OpenFolder,
            Title = "查看完整崩溃报告",
            Detail = "日志分析只能覆盖常见原因。若上面的建议都没解决，请把崩溃报告发给整合包作者。",
            ActionLabel = "打开崩溃报告目录",
            CanExecute = true,
            Execute = _ => Task.FromResult(OpenCrashFolder()),
        });

        return actions;
    }

    /// <summary>把 <see cref="CrashReasons"/> 翻成一句中文（原有界面直接显示枚举名，看不懂）。</summary>
    public static string Describe(CrashReasons reason) => reason switch
    {
        CrashReasons.FileOrContentCheckFailed => "游戏文件校验失败（文件被改动或下载不完整）",
        CrashReasons.SpecificBlockCausedCrash => "某个方块导致了崩溃",
        CrashReasons.SpecificEntityCausedCrash => "某个实体导致了崩溃",
        CrashReasons.TextureTooLargeOrInsufficientGraphicsConfig => "材质过大，或显卡设置不足",
        CrashReasons.ShaderOrResourcePackCausedOpenGL1282Error => "光影 / 资源包引发 OpenGL 错误",
        CrashReasons.UnableToLoadTexture => "无法加载纹理",
        CrashReasons.ModConfigCausedGameCrash => "某个模组的配置导致崩溃",
        CrashReasons.ModMixinFailed => "模组 Mixin 注入失败（通常是与其它模组冲突）",
        CrashReasons.ModLoaderError => "模组加载器报错",
        CrashReasons.ModInitializationFailed => "模组初始化失败",
        CrashReasons.ModFileDecompressed => "模组文件被解压过（应保持 .jar 原样）",
        CrashReasons.TooManyModsExceededIdLimit => "模组太多，超出了 ID 上限",
        CrashReasons.ModCausedGameCrash => "某个模组导致游戏崩溃",
        CrashReasons.ModInstalledRepeatedly => "同一个模组装了多份（重复）",
        CrashReasons.OptiFineIncompatibleWithForge => "OptiFine 与 Forge 不兼容",
        CrashReasons.FabricError => "Fabric 相关错误",
        CrashReasons.FabricErrorWithSolution => "Fabric 相关错误（有已知解决方案）",
        CrashReasons.ForgeError => "Forge 相关错误",
        CrashReasons.LowVersionForgeIncompatibleWithHighVersionJava => "旧版 Forge 不兼容当前高版本 Java",
        CrashReasons.MultipleForgeInVersionJson => "版本 JSON 里重复声明了 Forge",
        CrashReasons.OptiFineCausedWorldLoadingFailure => "OptiFine 导致世界加载失败",
        CrashReasons.CrashLogStackAnalysisFoundKeyword => "崩溃堆栈里出现了已知问题关键字",
        CrashReasons.CrashLogStackAnalysisFoundModName => "崩溃堆栈里出现了模组名",
        CrashReasons.MCLogStackAnalysisFoundKeyword => "日志里出现了已知问题关键字",
        CrashReasons.InsufficientMemory => "内存不足（Java 堆太小或分配失败）",
        CrashReasons.UsingJDK => "正在使用 JDK 而不是 JRE（可能引发兼容问题）",
        CrashReasons.GraphicsCardDoesNotSupportOpenGL => "显卡不支持所需的 OpenGL 版本",
        CrashReasons.UsingOpenJ9 => "正在使用 OpenJ9 虚拟机（部分模组不兼容）",
        CrashReasons.JavaVersionTooHigh => "Java 版本过高",
        CrashReasons.UnsupportedJavaClassVersionError => "Java 版本与模组不匹配（类文件版本错误）",
        CrashReasons.Using32BitJavaCausedInsufficientJVMMemory => "32 位 Java 导致可用内存不足",
        CrashReasons.PlayerTriggeredDebugCrash => "玩家手动触发了调试崩溃",
        _ => reason.ToString(),
    };

    // ==================== 内存 ====================

    private static RepairAction BuildMemoryRepair(bool is32BitJava)
    {
        var current = SettingsService.Current.MaxMemoryMb;
        var total = TryGetTotalMemoryMb();
        var target = SuggestMaxMemory(current, total, is32BitJava);

        var detail = is32BitJava
            ? "日志显示是 32 位 Java 导致的内存不足 —— 32 位虚拟机最多只能用到约 1.5 GB，"
              + "即使把 -Xmx 调大也没用。建议换成 64 位 Java（本机可用内存约 "
              + $"{total} MB）。"
            : $"日志里出现了内存不足的信号，当前最大堆内存是 {current} MB。"
              + $"本机可用内存约 {total} MB，建议提高到 {target} MB。";

        var canRaise = !is32BitJava && target > current;

        return new RepairAction
        {
            Kind = RepairKind.Memory,
            Title = "内存不足",
            Detail = detail,
            ActionLabel = canRaise ? $"把最大内存调到 {target} MB" : "需要手动处理",
            CanExecute = canRaise,
            Execute = canRaise ? (Func<CancellationToken, Task<RepairOutcome>>)(_ =>
            {
                var cfg = SettingsService.Current;
                cfg.MaxMemoryMb = target;
                if (cfg.MinMemoryMb > target / 2)
                    cfg.MinMemoryMb = Math.Max(512, target / 2);
                SettingsService.Save();

                return Task.FromResult(RepairOutcome.Ok(
                    $"已把最大内存从 {current} MB 提高到 {target} MB（初始堆 {cfg.MinMemoryMb} MB），重新启动游戏即可生效"));
            }) : null,
        };
    }

    /// <summary>
    /// 建议的最大堆内存：不低于当前的 2 倍、不低于 4 GB，且不超过物理内存的 3/4。
    /// 32 位 Java 或物理内存不足时返回当前值（表示「没法靠调参数解决」）。
    /// </summary>
    private static int SuggestMaxMemory(int current, int totalMb, bool is32BitJava)
    {
        if (is32BitJava || totalMb <= 0)
            return current;

        var desired = Math.Max(current * 2, 4096);
        var cap = (int)(totalMb * 0.75);
        var target = Math.Min(desired, cap);

        return target > current ? target : current;
    }

    private static int TryGetTotalMemoryMb()
    {
        try
        {
            var bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            return bytes > 0 ? (int)(bytes / 1024 / 1024) : 0;
        }
        catch
        {
            return 0;
        }
    }

    // ==================== Java ====================

    private static RepairAction BuildJavaRepair(ISet<CrashReasons> signals, int wantedMajor)
    {
        // 32 位 Java 的场景由内存条目负责引导，这里不重复
        if (signals.Contains(CrashReasons.Using32BitJavaCausedInsufficientJVMMemory)
            && signals.Count == 1)
        {
            return new RepairAction
            {
                Kind = RepairKind.Java,
                Title = "32 位 Java",
                Detail = "需要换成 64 位 Java。",
                ActionLabel = "需要手动处理",
                CanExecute = false,
            };
        }

        var currentPath = SettingsService.Current.JavaPath;

        return new RepairAction
        {
            Kind = RepairKind.Java,
            Title = "Java 版本问题",
            Detail = $"日志显示当前 Java 与这个游戏 / 模组不匹配。这个版本建议使用 Java {wantedMajor}"
                     + (string.IsNullOrWhiteSpace(currentPath) ? "（当前未指定 Java）" : $"（当前：{currentPath}）")
                     + "。点击后会扫描本机已安装的 Java 并自动切换到匹配的那一个。",
            ActionLabel = $"自动选择 Java {wantedMajor}",
            CanExecute = true,
            Execute = async ct =>
            {
                List<JavaEntry> javas;
                try
                {
                    javas = await JavaHelper.FindJavasAsync();
                }
                catch (Exception ex)
                {
                    return RepairOutcome.Fail($"扫描 Java 失败：{ex.Message}");
                }

                var match = javas.FirstOrDefault(j => MajorOf(j.JavaVersion) == wantedMajor && j.Is64bit)
                            ?? javas.FirstOrDefault(j => MajorOf(j.JavaVersion) == wantedMajor);

                if (match is null)
                {
                    var installed = javas.Count == 0
                        ? "本机没有检测到任何可用的 Java"
                        : "本机已安装：" + string.Join("、", javas.Select(j => j.JavaVersion).Distinct());
                    return RepairOutcome.Fail(
                        $"没有找到 Java {wantedMajor}（{installed}）。请先安装 Java {wantedMajor} 后重试，"
                        + "或到「设置 → 启动」手动指定 Java 路径");
                }

                SettingsService.Current.JavaPath = match.JavaPath;
                SettingsService.Save();
                MainWindowViewModel.MainModel.JavaPath = match.JavaPath;

                return RepairOutcome.Ok($"已切换到这个 Java：{match.JavaPath}（{match.JavaVersion}）");
            },
        };
    }

    private static int MajorOf(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return 0;

        var m = Regex.Match(version, @"\d+");
        if (!m.Success)
            return 0;

        var major = int.Parse(m.Value);
        return major == 1 ? 8 : major;
    }

    /// <summary>
    /// 这个 Minecraft 版本该配哪个 Java 主版本。信号里明确「Java 过高」时向下取一档。
    /// </summary>
    private static int SuggestedJavaMajor(string instanceId, ISet<CrashReasons> signals)
    {
        var mc = ModDependencyResolver.TryGetGameVersion(instanceId);
        var parts = (mc ?? string.Empty).Split('.');
        var minor = 0;
        if (parts.Length >= 2)
            int.TryParse(parts[1], out minor);

        var wanted = minor switch
        {
            >= 21 => 21,     // 1.21+
            >= 18 => 17,     // 1.18 ~ 1.20
            _ => 8,          // 1.17 及更早
        };

        // 明确「版本过高」时，往下一档找
        if (signals.Contains(CrashReasons.JavaVersionTooHigh)
            || signals.Contains(CrashReasons.LowVersionForgeIncompatibleWithHighVersionJava))
        {
            wanted = wanted switch
            {
                21 => 17,
                17 => 8,
                _ => 8,
            };
        }

        return wanted;
    }

    // ==================== 模组 ====================

    private static RepairAction BuildDisableModRepair(string jarPath)
    {
        var fileName = Path.GetFileName(jarPath);

        return new RepairAction
        {
            Kind = RepairKind.DisableMod,
            Title = $"疑似问题模组：{fileName}",
            Detail = "这个模组出现在崩溃堆栈里。点击后会把它改名为 " + fileName + DisabledSuffix
                     + "（不删除），游戏下次启动就不会加载它；确认没问题后把后缀去掉即可恢复。",
            ActionLabel = "禁用这个模组",
            CanExecute = true,
            Execute = _ =>
            {
                try
                {
                    var target = jarPath + DisabledSuffix;
                    if (File.Exists(target))
                        return Task.FromResult(RepairOutcome.Fail($"已存在 {Path.GetFileName(target)}，请先手动清理"));

                    File.Move(jarPath, target);
                    return Task.FromResult(RepairOutcome.Ok($"已禁用 {fileName}（改名为 {Path.GetFileName(target)}）"));
                }
                catch (Exception ex)
                {
                    return Task.FromResult(RepairOutcome.Fail($"禁用失败：{ex.Message}"));
                }
            },
        };
    }

    private static ModConflict? TryFindDuplicate(string instanceId)
    {
        try
        {
            return ModDependencyResolver.Analyze(instanceId).Conflicts.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "查找重复模组失败：{Id}", instanceId);
            return null;
        }
    }

    private static RepairAction BuildDuplicateRepair(ModConflict conflict)
    {
        // 保留版本号最高的那个，其余禁用
        var ordered = conflict.Files
            .OrderByDescending(f => f.Version, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var keep = ordered[0];
        var disable = ordered.Skip(1).ToList();

        return new RepairAction
        {
            Kind = RepairKind.DisableDuplicate,
            Title = $"重复安装：{conflict.ModId}",
            Detail = $"这个模组装了 {conflict.Files.Count} 份："
                     + string.Join("、", conflict.Files.Select(f => f.FileName))
                     + $"。游戏加载两份同名模组会直接崩。点击后保留 {keep.FileName}，其余改名禁用。",
            ActionLabel = $"保留 {keep.FileName}，禁用其余 {disable.Count} 份",
            CanExecute = true,
            Execute = _ =>
            {
                var done = 0;
                foreach (var mod in disable)
                {
                    try
                    {
                        var target = mod.FilePath + DisabledSuffix;
                        if (File.Exists(target))
                            continue;
                        File.Move(mod.FilePath, target);
                        done++;
                    }
                    catch (Exception ex)
                    {
                        Serilog.Log.Warning(ex, "禁用重复模组失败：{File}", mod.FilePath);
                    }
                }

                return Task.FromResult(done > 0
                    ? RepairOutcome.Ok($"已禁用 {done} 份重复的 {conflict.ModId}，保留 {keep.FileName}")
                    : RepairOutcome.Fail("没有文件被改动，请手动检查 mods 目录"));
            },
        };
    }

    /// <summary>
    /// 把「可疑模组」这个名字对应到 mods 目录里的实际文件。
    /// 日志分析给出来的可能是文件名、也可能是模组 id / 显示名，三种都试。
    /// </summary>
    private static string? TryLocateModFile(string modsFolder, string suspicious)
    {
        if (string.IsNullOrWhiteSpace(modsFolder) || string.IsNullOrWhiteSpace(suspicious)
            || !Directory.Exists(modsFolder))
            return null;

        string[] jars;
        try
        {
            jars = Directory.GetFiles(modsFolder, "*.jar", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return null;
        }

        var needle = suspicious.Trim();

        // 1) 日志直接给了文件名
        var byName = jars.FirstOrDefault(j =>
            string.Equals(Path.GetFileName(j), needle, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileNameWithoutExtension(j), needle, StringComparison.OrdinalIgnoreCase));
        if (byName is not null)
            return byName;

        // 2) 文件名里含这个名字（日志常写 "sodium" 而文件是 "sodium-fabric-0.5.8.jar"）
        var byContains = jars.FirstOrDefault(j =>
            Path.GetFileNameWithoutExtension(j).Contains(needle, StringComparison.OrdinalIgnoreCase));
        if (byContains is not null)
            return byContains;

        // 3) 拿模组元数据里的 id / 显示名比对
        foreach (var jar in jars)
        {
            var meta = ModMetadataReader.TryRead(jar);
            if (meta is null)
                continue;

            if (string.Equals(meta.ModId, needle, StringComparison.OrdinalIgnoreCase)
                || string.Equals(meta.Name, needle, StringComparison.OrdinalIgnoreCase)
                || ModMetadataReader.NormalizeId(meta.ModId) == ModMetadataReader.NormalizeId(needle))
                return jar;
        }

        return null;
    }

    // ==================== 文本补充信号 ====================

    private static IEnumerable<CrashReasons> ScanText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        if (text.Contains("OutOfMemoryError", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Could not reserve enough space for object heap", StringComparison.OrdinalIgnoreCase)
            || text.Contains("java.lang.OutOfMemoryError: Java heap space", StringComparison.OrdinalIgnoreCase))
            yield return CrashReasons.InsufficientMemory;

        if (text.Contains("UnsupportedClassVersionError", StringComparison.OrdinalIgnoreCase)
            || text.Contains("class file version", StringComparison.OrdinalIgnoreCase))
            yield return CrashReasons.UnsupportedJavaClassVersionError;

        if (text.Contains("Pixel format not accelerated", StringComparison.OrdinalIgnoreCase)
            || text.Contains("The driver does not appear to support OpenGL", StringComparison.OrdinalIgnoreCase)
            || text.Contains("OpenGL 1282", StringComparison.OrdinalIgnoreCase)
            || text.Contains("GL_INVALID_OPERATION", StringComparison.OrdinalIgnoreCase))
            yield return CrashReasons.GraphicsCardDoesNotSupportOpenGL;

        if (text.Contains("Duplicate mods", StringComparison.OrdinalIgnoreCase)
            || text.Contains("duplicate mod", StringComparison.OrdinalIgnoreCase))
            yield return CrashReasons.ModInstalledRepeatedly;
    }

    // ==================== 目录 ====================

    private static RepairOutcome OpenCrashFolder()
    {
        try
        {
            var folder = Path.Combine(GameInstanceHelper.GetMinecraftFolder(), "crash-reports");
            if (!Directory.Exists(folder))
                return RepairOutcome.Fail("崩溃报告目录不存在：" + folder);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", folder)
            {
                UseShellExecute = true
            });
            return RepairOutcome.Ok("已打开崩溃报告目录");
        }
        catch (Exception ex)
        {
            return RepairOutcome.Fail("无法打开目录：" + ex.Message);
        }
    }
}
