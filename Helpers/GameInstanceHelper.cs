using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using VibrantbitLauncher.Services;

namespace VibrantbitLauncher.Helpers
{
    /// <summary>实例使用的模组加载器。</summary>
    public enum ModLoaderKind
    {
        /// <summary>没能识别（版本 JSON 缺失或损坏，这种版本本来也启动不了）。</summary>
        Unknown = 0,

        /// <summary>原版：没有装任何加载器，装不了模组。</summary>
        Vanilla,

        Fabric,
        Quilt,
        Forge,
        NeoForge,
        LiteLoader
    }

    /// <summary>一个本地可启动实例：版本 id + 它装的加载器。</summary>
    public sealed class GameInstance
    {
        public string Id { get; init; } = string.Empty;
        public ModLoaderKind Loader { get; init; } = ModLoaderKind.Unknown;

        /// <summary>加载器的展示名（原版 / Fabric / Forge …）。</summary>
        public string LoaderName => ModLoaderHelper.DisplayNameOf(Loader);

        /// <summary>下拉框里显示的文字，如 <c>1.21.4_fabric_0.16.5（Fabric）</c>。</summary>
        public string DisplayName => $"{Id}（{LoaderName}）";
    }

    /// <summary>
    /// 本地 Minecraft 实例（已安装版本）枚举、「下载到实例」的落点解析，以及加载器判定。
    ///
    /// <para>
    /// <b>实例口径</b>：<c>.minecraft/versions/&lt;id&gt;/&lt;id&gt;.json</c> 存在，且该 id
    /// 不是被其它版本用 <c>inheritsFrom</c> 引用的「基础版本」—— 基础版本是加载器版本的一部分
    /// （例如 Fabric 版本继承的 1.21.4），单独列出来只会让用户困惑。
    /// 这与启动页列出可启动版本的口径完全一致（<c>RunPageViewModel.GetLocalVersionIds</c>）。
    /// </para>
    ///
    /// <para>
    /// <b>落点跟随「版本隔离」</b>：开启 → <c>versions/&lt;id&gt;/&lt;子目录&gt;</c>；
    /// 关闭 → <c>.minecraft/&lt;子目录&gt;</c>。与启动时 gameDir 的算法是同一个条件
    /// （<c>RunPageViewModel</c> 里 <c>cfg.VersionIsolation ? versions/&lt;id&gt; : 根目录</c>），
    /// 改一处必须改另一处，否则「下到实例里的模组游戏读不到」。
    /// </para>
    /// </summary>
    public static class GameInstanceHelper
    {
        /// <summary>当前的 .minecraft 目录（绝对路径）。</summary>
        public static string GetMinecraftFolder() => SettingsService.ResolveMinecraftFolder();

        /// <summary>是否开启「版本隔离」。它决定下载是落到版本子目录还是根目录。</summary>
        public static bool IsVersionIsolated => SettingsService.Current.VersionIsolation;

        /// <summary>沿 <c>inheritsFrom</c> 往回找加载器的最大层数，防止环形继承把程序吊死。</summary>
        private const int MaxInheritDepth = 4;

        /// <summary>
        /// 枚举本地已安装实例（含各自的加载器），去掉了被继承的基础版本，按 id 排序。
        /// 目录不存在或读取失败时返回空表，绝不抛异常（调用方都在 UI 路径上）。
        /// </summary>
        public static List<GameInstance> GetInstances()
        {
            var result = new List<GameInstance>();

            try
            {
                var versionsDir = Path.Combine(GetMinecraftFolder(), "versions");
                if (!Directory.Exists(versionsDir))
                    return result;

                string[] dirs;
                try
                {
                    dirs = Directory.GetDirectories(versionsDir);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, "枚举 versions 目录失败：{Dir}", versionsDir);
                    return result;
                }

                // 第一遍：收集「被继承的基础版本」
                var inheritedBaseIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var dir in dirs)
                {
                    var id = Path.GetFileName(dir);
                    var jsonPath = Path.Combine(dir, id + ".json");
                    if (!File.Exists(jsonPath))
                        continue;

                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
                        if (doc.RootElement.TryGetProperty("inheritsFrom", out var inheritsEl))
                        {
                            var baseId = inheritsEl.GetString();
                            if (!string.IsNullOrEmpty(baseId))
                                inheritedBaseIds.Add(baseId);
                        }
                    }
                    catch
                    {
                        // 单个 JSON 坏掉不影响其它版本
                    }
                }

                // 第二遍：只保留能独立启动的版本，并识别各自的加载器
                foreach (var dir in dirs)
                {
                    var id = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(id))
                        continue;
                    if (!File.Exists(Path.Combine(dir, id + ".json")))
                        continue;
                    if (inheritedBaseIds.Contains(id))
                        continue;

                    result.Add(new GameInstance
                    {
                        Id = id,
                        Loader = DetectLoader(versionsDir, id)
                    });
                }

                result.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "枚举本地实例失败");
            }

            return result;
        }

        /// <summary>只取实例 id（按名称排序）。兼容旧调用点。</summary>
        public static List<string> GetInstanceIds() => GetInstances().Select(i => i.Id).ToList();

        /// <summary>
        /// 识别某个版本的加载器：先看自己的 version JSON，没线索再沿 <c>inheritsFrom</c> 往上找
        /// （有人会把模组配置版继承在 Fabric 版上，此时加载器库记在父版本里）。
        /// </summary>
        public static ModLoaderKind DetectLoader(string versionsDir, string versionId)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = versionId;
            var anyJsonRead = false;

            for (var depth = 0; depth < MaxInheritDepth && !string.IsNullOrWhiteSpace(current); depth++)
            {
                if (!visited.Add(current))
                    break;

                var jsonPath = Path.Combine(versionsDir, current, current + ".json");
                if (!File.Exists(jsonPath))
                    break;

                JsonElement root;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
                    root = doc.RootElement.Clone();
                }
                catch
                {
                    break;
                }

                anyJsonRead = true;

                var kind = ReadLoaderFromJson(root);
                if (kind.HasValue)
                    return kind.Value;

                current = root.TryGetProperty("inheritsFrom", out var inheritsEl)
                    ? inheritsEl.GetString()
                    : null;
            }

            return anyJsonRead ? ModLoaderKind.Vanilla : ModLoaderKind.Unknown;
        }

        /// <summary>
        /// 从单个 version JSON 里找加载器线索，找不到返回 null（注意：返回 null ≠ 原版，
        /// 「全都没有线索」才判定为原版）。
        /// </summary>
        private static ModLoaderKind? ReadLoaderFromJson(JsonElement root)
        {
            var optifineOnly = false;

            if (root.TryGetProperty("libraries", out var libs) && libs.ValueKind == JsonValueKind.Array)
            {
                foreach (var lib in libs.EnumerateArray())
                {
                    var name = lib.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    // OptiFine 单独安装时也挂在 launchwrapper 上启动，但它不是模组加载器，
                    // 不能因为 mainClass 里出现 launchwrapper 就把「原版 + OptiFine」认成 Forge。
                    if (name!.StartsWith("optifine:", StringComparison.OrdinalIgnoreCase))
                    {
                        optifineOnly = true;
                        continue;
                    }

                    var kind = ClassifyMavenName(name);
                    if (kind.HasValue)
                        return kind;
                }
            }

            if (optifineOnly)
                return null;

            var mainClass = root.TryGetProperty("mainClass", out var mainEl) ? mainEl.GetString() : null;
            if (!string.IsNullOrWhiteSpace(mainClass))
            {
                var m = mainClass!.ToLowerInvariant();
                if (m.Contains("fabricmc"))
                    return ModLoaderKind.Fabric;
                if (m.Contains("quiltmc"))
                    return ModLoaderKind.Quilt;
                if (m.Contains("neoforged"))
                    return ModLoaderKind.NeoForge;

                // modlauncher / launchwrapper / bootstraplauncher 是 Forge 系（含旧版）的启动器类。
                // NeoForge 也用 cpw.mods 的启动器，但它的库坐标带 net.neoforged，上面就返回了。
                if (m.Contains("minecraftforge") || m.Contains("modlauncher")
                    || m.Contains("launchwrapper") || m.Contains("bootstraplauncher"))
                    return ModLoaderKind.Forge;
            }

            return null;
        }

        /// <summary>
        /// Maven 坐标 → 加载器。
        /// <b>NeoForge 必须先于 Forge 判断</b>：NeoForge 1.20.1 是发布在
        /// <c>net.neoforged:forge</c> 下的，与 Forge 自己的 <c>net.minecraftforge:forge</c> 只差 groupId，
        /// 顺序写反会导致 NeoForge 实例被判成 Forge。
        /// </summary>
        private static ModLoaderKind? ClassifyMavenName(string mavenName)
        {
            var n = mavenName.ToLowerInvariant();

            if (n.StartsWith("net.fabricmc:"))
                return ModLoaderKind.Fabric;
            if (n.StartsWith("org.quiltmc:"))
                return ModLoaderKind.Quilt;
            if (n.StartsWith("net.neoforged"))
                return ModLoaderKind.NeoForge;
            if (n.StartsWith("net.minecraftforge:"))
                return ModLoaderKind.Forge;
            if (n.StartsWith("com.mumfrey:"))
                return ModLoaderKind.LiteLoader;

            return null;
        }

        /// <summary>
        /// 解析「下载到实例」的落点目录（绝对路径）。
        /// <paramref name="versionId"/> 为空时落到根目录下的子目录，避免拼出不存在的版本路径。
        /// </summary>
        public static string ResolveInstanceFolder(string? versionId, string subfolder)
        {
            var mcFolder = GetMinecraftFolder();

            var baseDir = IsVersionIsolated && !string.IsNullOrWhiteSpace(versionId)
                ? Path.Combine(mcFolder, "versions", versionId)
                : mcFolder;

            return Path.Combine(baseDir, subfolder);
        }

        /// <summary>
        /// 落点的「给人看」写法（相对 .minecraft，用正斜杠），用于界面提示。
        /// 例如 <c>.minecraft/versions/1.21.4/mods</c> 或 <c>.minecraft/mods</c>。
        /// </summary>
        public static string DescribeInstanceFolder(string? versionId, string subfolder)
        {
            try
            {
                var full = Path.GetFullPath(ResolveInstanceFolder(versionId, subfolder));
                var mc = Path.GetFullPath(GetMinecraftFolder());
                var rel = Path.GetRelativePath(mc, full).Replace('\\', '/');

                return rel.StartsWith("..", StringComparison.Ordinal)
                    ? full
                    : $".minecraft/{rel}";
            }
            catch
            {
                return ResolveInstanceFolder(versionId, subfolder);
            }
        }
    }

    /// <summary>
    /// 加载器名称与兼容规则。抽成独立类是因为「实例侧的加载器」和「Modrinth 侧的加载器标签」
    /// 是两个来源（枚举 / 字符串），必须走同一张映射表才不会出现两边判断不一致。
    /// </summary>
    public static class ModLoaderHelper
    {
        /// <summary>加载器的展示名。</summary>
        public static string DisplayNameOf(ModLoaderKind kind) => kind switch
        {
            ModLoaderKind.Fabric => "Fabric",
            ModLoaderKind.Quilt => "Quilt",
            ModLoaderKind.Forge => "Forge",
            ModLoaderKind.NeoForge => "NeoForge",
            ModLoaderKind.LiteLoader => "LiteLoader",
            ModLoaderKind.Unknown => "未知",
            _ => "原版"
        };

        /// <summary>
        /// 名称 → 加载器。同时接受两类输入：Modrinth 的标签（<c>fabric</c> / <c>neoforge</c> …）
        /// 和本工程的展示名（<c>NeoForge</c>）。先剥掉非字母数字再比对，大小写与连字符都不敏感。
        /// 认不出来（<c>iris</c> / <c>optifine</c> / <c>datapack</c> 之类）返回 null。
        /// </summary>
        public static ModLoaderKind? ParseName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            var chars = name.Where(char.IsLetterOrDigit).ToArray();
            if (chars.Length == 0)
                return null;

            return new string(chars).ToLowerInvariant() switch
            {
                "fabric" => ModLoaderKind.Fabric,
                "quilt" => ModLoaderKind.Quilt,
                "forge" => ModLoaderKind.Forge,
                "neoforge" => ModLoaderKind.NeoForge,
                "liteloader" => ModLoaderKind.LiteLoader,
                "minecraft" or "vanilla" => ModLoaderKind.Vanilla,
                _ => null
            };
        }

        /// <summary>
        /// 兼容矩阵（单向）：
        /// <list type="bullet">
        /// <item>Fabric 模组 → Fabric / Quilt 实例（Quilt Loader 向下兼容 Fabric 模组，反之不成立）</item>
        /// <item>Quilt 模组 → 仅 Quilt 实例</item>
        /// <item>Forge / NeoForge / LiteLoader 模组 → 各自的实例（互不通用）</item>
        /// <item>原版标签 → 仅原版实例</item>
        /// </list>
        /// </summary>
        public static bool IsCompatible(ModLoaderKind instanceLoader, ModLoaderKind required)
        {
            if (required == ModLoaderKind.Unknown)
                return false;

            if (required == ModLoaderKind.Vanilla)
                return instanceLoader == ModLoaderKind.Vanilla;

            if (required == ModLoaderKind.Fabric)
                return instanceLoader is ModLoaderKind.Fabric or ModLoaderKind.Quilt;

            return instanceLoader == required;
        }

        /// <summary>
        /// 这个文件能不能装进这个实例。
        /// <paramref name="requiredLoaders"/> 是 Modrinth 给该文件标注的加载器标签。
        /// 一个都认不出来时返回 true（Modrinth 没标注 ≠ 不能装，拦下来会变成「功能看起来坏了」）；
        /// 实例加载器未识别则返回 false（宁可只给「另存为」，也别往一个读不懂的版本里塞模组）。
        /// </summary>
        public static bool CanLoad(GameInstance? instance, IEnumerable<string>? requiredLoaders)
        {
            if (instance is null || instance.Loader == ModLoaderKind.Unknown)
                return false;

            var required = ParseAll(requiredLoaders);
            if (required.Count == 0)
                return true;

            return required.Any(k => IsCompatible(instance.Loader, k));
        }

        /// <summary>把一批标签解析成加载器集合（去重、丢弃认不出来的）。</summary>
        public static List<ModLoaderKind> ParseAll(IEnumerable<string>? names)
            => (names ?? Enumerable.Empty<string>())
                .Select(ParseName)
                .Where(k => k.HasValue)
                .Select(k => k!.Value)
                .Distinct()
                .ToList();

        /// <summary>给界面用的一句「需要什么加载器」，例如 <c>Fabric / Quilt</c>、<c>未标注加载器</c>。</summary>
        public static string DescribeRequired(IEnumerable<string>? names)
        {
            var kinds = ParseAll(names);
            return kinds.Count == 0
                ? "未标注加载器"
                : string.Join(" / ", kinds.Select(DisplayNameOf));
        }
    }
}
