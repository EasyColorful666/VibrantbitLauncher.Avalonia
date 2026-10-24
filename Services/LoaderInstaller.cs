using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using VibrantbitLauncher.Helpers;
using VibrantbitLauncher.Services.Download;

namespace VibrantbitLauncher.Services
{
    /// <summary>
    /// 自实现 Forge / NeoForge 安装器，绕过 MinecraftLaunch 库的 <c>ForgeInstaller</c>。
    ///
    /// <para><b>为什么不用库的安装器（1.0.6.9 实测结论）</b></para>
    /// <list type="number">
    /// <item><description>
    /// <b>硬编码 <c>maven.neoforged.net</c>，国内基本连不上。</b>
    /// <c>ForgeInstaller.DownloadForgePackageAsync</c> 里 NeoForge 的下载地址是
    /// <c>https://maven.neoforged.net/releases/net/neoforged/{forge|neoforge}/…-installer.jar</c>，
    /// 既不走 <c>DownloadManager.BmclApi</c> 改写，也没有任何兜底。实测该主机在本机直接
    /// 连接失败（curl 退出码 7），而 BMCLAPI 上其实**有**镜像
    /// （<c>https://bmclapi2.bangbang93.com/maven/net/neoforged/forge/&lt;mc&gt;-&lt;ver&gt;/…-installer.jar</c>
    /// 返回 200 且是完整包）—— 库版本用不上它。
    /// </description></item>
    /// <item><description>
    /// <b>BMCLAPI 的 NeoForge 列表里有「带 MC 前缀」的版本号。</b>
    /// 列表前 55 条是 <c>version = "47.1.5"</c>，最后 5 条却是
    /// <c>version = "1.20.1-47.1.105"</c>（见条目里的 <c>rawVersion</c> / <c>installerPath</c>）。
    /// 库原样把它塞进下载 URL，拼出 <c>…/forge/1.20.1-47.1.105/forge-1.20.1-47.1.105-installer.jar</c>
    /// —— 该路径**恰好就是这个包的正确镜像路径**，但库请求的是官方域名，于是 404 → NRE。
    /// 我们在选版本时统一做归一化（见 <see cref="NormalizeLoaderVersion"/>），
    /// 并且**自己拼下载地址**，不受库的 URL 拼接影响。
    /// </description></item>
    /// <item><description>
    /// <b>失败信息会被吞成一句 NullReferenceException。</b>
    /// 库的 <c>DownloadForgePackageAsync</c> 在「HEAD/GET 期间拿到空响应」时直接 NRE，
    /// 用户看到的是「对象引用未设置」，完全无法定位。我们自己下载，失败一律抛
    /// 中文可操作的 <see cref="DownloadFailedException"/>。
    /// </description></item>
    /// </list>
    ///
    /// <para><b>实现流程（对齐 Forge 官方 installer 的 client 侧行为）</b></para>
    /// <list type="number">
    /// <item><description>下载 installer jar（官方源 ↔ BMCLAPI 镜像互为兜底，带重试）。</description></item>
    /// <item><description>解出 <c>install_profile.json</c> 与 <c>version.json</c>。</description></item>
    /// <item><description>把 <c>version.json</c> 铺成实例版本 JSON：改 <c>id</c>、补 <c>inheritsFrom</c>、合并 <c>install_profile.libraries</c>。</description></item>
    /// <item><description>下载全部依赖库（版本 JSON 的 + install_profile 的），走同一套镜像兜底。</description></item>
    /// <item><description>按 <c>install_profile.processors</c> 依次跑 java 处理器（只跑 client 侧），
    ///     把 <c>data</c> 里的 <c>[maven]</c> / <c>'literal'</c> 占位符解析成实际值。</description></item>
    /// <item><description>把 <c>data/client.lzma</c> 落盘到 <c>libraries/net/minecraftforge/forge/…</c>（processor 会引用它）。</description></item>
    /// </list>
    /// </summary>
    internal static class LoaderInstaller
    {
        // ==================== 公开入口 ====================

        /// <summary>
        /// 安装 Forge / NeoForge。
        /// </summary>
        /// <param name="mcFolder">.minecraft 根目录。</param>
        /// <param name="mcVersion">Minecraft 版本，如 1.20.1。</param>
        /// <param name="loaderVersion">加载器版本（已归一化，如 47.4.26），为空则用最新的稳定版。</param>
        /// <param name="instanceId">实例 id（目录名 / 版本 JSON 的 id）。</param>
        /// <param name="neoForge">true 装 NeoForge，false 装 Forge。</param>
        /// <param name="javaPath">java.exe 路径（跑 processor 用）。</param>
        /// <param name="onProgress">进度回调 (0-100, 阶段文案)。可能在后台线程调用。</param>
        /// <param name="ct">取消令牌。</param>
        public static async Task InstallForgeLikeAsync(
            string mcFolder,
            string mcVersion,
            string loaderVersion,
            string instanceId,
            bool neoForge,
            string javaPath,
            Action<int, string> onProgress,
            CancellationToken ct = default)
        {
            var loaderName = neoForge ? "NeoForge" : "Forge";

            // ---------- 1. 选版本 ----------
            var picked = await PickForgeVersionAsync(mcVersion, loaderVersion, neoForge, ct)
                ?? throw new ModpackException($"找不到 Minecraft {mcVersion} 可用的 {loaderName} 版本");
            onProgress(2, $"准备 {loaderName} {picked}");

            // ---------- 2. 下载 installer jar ----------
            var installerJar = Path.Combine(mcFolder, $"vbl-{loaderName.ToLowerInvariant()}-{picked}-installer.jar");
            var installerUrl = BuildInstallerUrl(mcVersion, picked, neoForge);
            var mirrorUrl = BuildMirrorInstallerUrl(mcVersion, picked, neoForge);

            // 官方源的实际可用性，两种加载器完全不同（2026-10-18 实测）：
            //
            //   Forge     maven.minecraftforge.net  → 可用（约 60 KB/s，6.0 MB 能下完）
            //             bmclapi 镜像             → HTTP 200 但**经常只给 89 KB / 171 KB 就断开**
            //   NeoForge  maven.neoforged.net       → 完全连不上（TLS 都建不起来）
            //             bmclapi 镜像             → 6.7 MB 数秒下完，稳定
            //
            // 所以顺序按加载器分开定，而不是一律「官方优先」：
            // Forge 走官方（镜像容易给截断文件），NeoForge 走镜像（官方压根不通）。
            // 用户显式开启「使用 BMCLAPI 镜像」时，两者都以镜像为先。
            var preferMirror = SettingsService.Current.UseBmclMirror || neoForge;

            var primary = preferMirror ? mirrorUrl : installerUrl;
            var fallback = preferMirror ? installerUrl : mirrorUrl;

            Serilog.Log.Information("{Loader} 安装器：主 {Primary}，备 {Fallback}",
                loaderName, primary, fallback);

            onProgress(5, $"下载 {loaderName} 安装器");
            // 上一次失败留下的半截安装器会被内核的「文件已存在」判据当成下好了，
            // 随后崩在 ZipFile.OpenRead —— 先清掉，重下比排错便宜。
            // （续传残片 .vblresume 不在清理范围内：那正是我们要复用的进度。）
            TryDelete(installerJar);

            // 用「跨连接续传」的下载器，而不是分块内核。
            //
            // 原因见 ResilientFileDownloader 的注释：这里实测的瓶颈是**单条连接约 1 MB 配额**，
            // 一条连接下完 6 MB 的安装器根本不可能完成 —— 无论重试多少次，每次都会停在同一个位置。
            // 续传下载器每轮从「已有字节数」处重新开一条连接，把配额一段段拼起来，
            // 这才是「Forge 每次都装不上」的真正解法。
            var installerSources = new[] { primary, fallback }
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // 进度条 5% → 12% 这一段留给安装器下载；总长未知时退化成「已下载多少 MB」
            var downloaded = await ResilientFileDownloader.DownloadAsync(
                installerSources,
                installerJar,
                (have, total) => onProgress(5, total > 0
                    ? $"下载 {loaderName} 安装器 {have * 100 / Math.Max(1, total)}%"
                    : $"下载 {loaderName} 安装器 {have / 1024 / 1024.0:0.0} MB"),
                ct);

            if (!downloaded)
            {
                TryDelete(installerJar);
                throw new ModpackException(
                    $"{loaderName} 安装器下载不完整（文件被服务器多次截断）。"
                    + "已尝试官方源与 BMCLAPI 镜像。"
                    + "请检查网络稳定性，或在「设置 → 下载」切换下载源后重试");
            }

            var invalid = ValidateInstaller(installerJar);
            if (invalid is not null)
            {
                TryDelete(installerJar);
                throw new ModpackException(
                    $"{loaderName} 安装器下载失败：{invalid}。"
                    + "请在「设置 → 下载」切换下载源（或开启镜像）后重试");
            }

            // ---------- 3. 解包 install_profile / version.json ----------
            onProgress(12, $"解析 {loaderName} 安装清单");
            using var zip = ZipFile.OpenRead(installerJar);

            var versionEntry = zip.GetEntry("version.json")
                ?? throw new ModpackException($"{loaderName} 安装器里没有 version.json");
            var versionJson = await ReadNodeAsync(versionEntry, ct)
                ?? throw new ModpackException($"{loaderName} 的 version.json 解析失败");

            var profileEntry = zip.GetEntry("install_profile.json")
                ?? throw new ModpackException($"{loaderName} 安装器里没有 install_profile.json");
            var profileJson = await ReadNodeAsync(profileEntry, ct)
                ?? throw new ModpackException($"{loaderName} 的 install_profile.json 解析失败");

            // 老版本 Forge（1.12 及以前）的 install_profile 结构完全不同，
            // 落点里带 install/filePath、没有 processors。这里明确拒绝并给可操作提示，
            // 而不是跑一半崩在莫名其妙的 null 上。
            if (profileJson["install"] is not null && profileJson["processors"] is null)
            {
                throw new ModpackException(
                    $"{loaderName} {picked} 是 1.12 及以前的老版本，安装结构不同，暂不支持自动安装。" +
                    "请在「下载中心 → 版本」里手动安装该加载器，或换一个较新的游戏版本");
            }

            // ---------- 4. 写实例版本 JSON ----------
            onProgress(18, "生成实例版本文件");
            var versionsDir = Path.Combine(mcFolder, "versions", instanceId);
            Directory.CreateDirectory(versionsDir);
            var clientJsonPath = Path.Combine(versionsDir, instanceId + ".json");

            // id 改成实例名，并把 inheritsFrom 指向基础版本（库解析器靠它找 parent）
            versionJson["id"] = instanceId;
            if (versionJson["inheritsFrom"] is null)
                versionJson["inheritsFrom"] = mcVersion;

            // 库要分成两份，绝不能混在一起：
            //
            //   · 启动库 = 加载器 version.json 自己声明的那些（官方 installer 写进版本 JSON 的也只有这些）
            //   · 工具库 = install_profile.libraries，只给 processors 用（installertools、
            //     ForgeAutoRenamingTool、maven-artifact、jopt-simple 6.0-alpha-3 …）
            //
            // 以前把两份都塞进版本 JSON，于是 tools 跟着进了启动 classpath，模块层直接爆炸。
            // 实测两组症状：
            //   Forge 1.20.1：Modules org.objectweb.asm.tree.analysis and ForgeAutoRenamingTool
            //                 export package org.objectweb.asm.tree.analysis to module installertools
            //   Forge 26.2：  Modules joptsimple and jopt.simple export package joptsimple.util
            //                 / Module jopt.simple not found, required by cpw.mods.modlauncher
            // 两份分开之后两者都能正常启动。
            var launchLibraries = new List<JsonNode>();
            CollectLibraries(versionJson["libraries"], launchLibraries);
            launchLibraries = DedupLibraries(launchLibraries, ignoreVersion: true);
            versionJson["libraries"] = new JsonArray(launchLibraries.Select(x => x.DeepClone()).ToArray());

            var toolLibraries = new List<JsonNode>();
            CollectLibraries(profileJson["libraries"], toolLibraries);
            // 工具库按完整坐标去重：不同 processor 点名不同版本（asm 9.3 / 9.5），
            // 按 artifact 去重会把高版本丢掉，处理器随即报「缺少依赖」。
            toolLibraries = DedupLibraries(toolLibraries, ignoreVersion: false);

            // 下载清单 = 启动库 + 工具库（processor 缺一个都跑不动）。
            // 两份之间还要再按完整坐标查一次重：forge/fmlloader 这类两边都声明的库
            // 若各下一份，会被并行下载同时写同一个文件（体积对了内容不对）。
            var downloadLibraries = new List<JsonNode>(launchLibraries.Count + toolLibraries.Count);
            var seenCoordinates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var lib in launchLibraries.Concat(toolLibraries))
            {
                var name = lib["name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(name))
                    continue;

                if (!seenCoordinates.Add(NormalizeCoordinate(name!)))
                    continue;

                downloadLibraries.Add(lib);
            }

            File.WriteAllText(clientJsonPath, versionJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Serilog.Log.Information("{Loader} 版本文件已写入 {Path}", loaderName, clientJsonPath);

            // ---------- 5. 下载依赖库 ----------
            //
            // ⚠️ 必须排除「处理器产物」。install_profile.data 里的 PATCHED / MC_UNPACKED
            // 是 [group:artifact:version:classifier] 形式的 maven 坐标，但**本地生成**、
            // maven 上根本不存在（实测 https://maven.minecraftforge.net/.../forge-<mc>-<ver>-client.jar
            // 返回 404）。把它们当依赖去下，结果就是：下 4 轮全 404 / 超时 →
            // 安装直接失败，或者下载被跳过、装完缺 client.jar → 启动时 FML 报
            // 「The patched Minecraft jar is missing」。
            onProgress(22, $"下载 {loaderName} 依赖库");
            var processorOutputs = CollectProcessorOutputs(profileJson);
            await DownloadLibrariesAsync(mcFolder, downloadLibraries, p =>
                onProgress(22 + (int)(p * 0.33), $"下载 {loaderName} 依赖库"), ct, processorOutputs);

            // ---------- 6. 已删除：手工落 client.lzma ----------
            //
            // 老实现把 data/client.lzma 解到 libraries/net/minecraftforge/forge/<mc>-<ver>/ 下，
            // 但处理器的 BINPATCH 参数指向的**不是**那个路径（它要的是安装包内条目解出来的临时文件），
            // 而且 NeoForge 的落点根本不在 net/minecraftforge 下 —— 这段对两种加载器都是无效动作。
            // 现在由 ResolveData 统一处理「/ 开头的归档内路径」，见 ExtractInstallerEntry。

            // ---------- 7. 跑 processors ----------
            var processors = profileJson["processors"] as JsonArray;
            if (processors is { Count: > 0 })
            {
                onProgress(56, $"运行 {loaderName} 安装处理器");
                var clientProcessorCount = processors.Count(p =>
                {
                    var sides = p?["sides"] as JsonArray;
                    return sides is null || sides.Count == 0
                           || sides.Any(s => string.Equals(s?.GetValue<string>(), "client",
                               StringComparison.OrdinalIgnoreCase));
                });

                await RunProcessorsAsync(
                    mcFolder, javaPath, installerJar, mcVersion, picked, neoForge,
                    profileJson, processors,
                    // 进度 + 文案一起报：处理器阶段是全程最长的静默期，
                    // 文案里带上「第 N/M 个」用户才知道它真的在走。
                    (p, step) => onProgress(56 + (int)(p * 0.42),
                        clientProcessorCount > 1 ? $"{step}（{loaderName} 安装处理器）" : step),
                    ct);

                // ---------- 7.5 把 patched jar 注册进 classpath ----------
                //
                // 处理器会把打补丁后的 client jar 写到 data.PATCHED 指向的位置，
                // 但 install_profile 的 libraries 里**没有**这一条 —— 官方安装器在结束时自己补上。
                // 我们不补的话，FML 启动时在 classpath 上找不到 Minecraft 类，报
                //   「The patched Minecraft jar is missing. Please try to reinstall NeoForge.」
                // 于是「安装成功、启动必崩」—— 这也是历史上被反复反馈的一条。
                EnsurePatchedLibrary(mcFolder, profileJson, versionJson, clientJsonPath, loaderName, processors);
            }

            onProgress(100, $"{loaderName} 安装完成");
            Serilog.Log.Information("{Loader} {Ver} 安装完成：{Id}", loaderName, picked, instanceId);

            // 安装器 jar 用完即弃（几十 MB，留在 .minecraft 根目录很脏）
            TryDelete(installerJar);
        }

        // ==================== Quilt ====================

        /// <summary>
        /// 安装 Quilt。
        ///
        /// Quilt 的 loader profile（<c>meta.quiltmc.org/v3/versions/loader/&lt;mc&gt;/&lt;ver&gt;/profile/json</c>）
        /// 就是一份现成的版本 JSON：<c>inheritsFrom</c> + <c>libraries</c> + <c>mainClass</c>，**没有 processor**，
        /// 所以流程比 Forge 简单得多 —— 拿 profile → 改写 id → 下库 → 落盘。
        ///
        /// <para><b>为什么不用库的 QuiltInstaller</b>：它会调
        /// <c>MinecraftResourceDownloader.VerifyAndDownloadDependenciesAsync</c> →
        /// <c>MinecraftEntry.GetRequiredLibraries()</c> →
        /// <c>MinecraftLibrary.ParseJsonNode</c>，而 Quilt profile 的 14 个库声明
        /// （<c>url = https://maven.fabricmc.net/</c>，无 <c>sha1</c> / <c>size</c> / <c>downloads</c>）
        /// 会命中 ParseJsonNode 里的 <c>Sha1 = libNode.Sha1.Value</c> 分支 —— 对 null 的
        /// <c>Nullable&lt;string&gt;</c> 取 <c>.Value</c>，抛
        /// <c>InvalidOperationException: Nullable object must have a value</c>。
        /// 实测 MC 1.20.1 + Quilt 0.20.0 100% 复现。这是上游库的 bug，
        /// 我们自己下库就绕开了。</para>
        /// </summary>
        public static async Task InstallQuiltAsync(
            string mcFolder,
            string mcVersion,
            string loaderVersion,
            string instanceId,
            Action<int, string> onProgress,
            CancellationToken ct = default)
        {
            // ---------- 1. 选版本 ----------
            var picked = await PickQuiltVersionAsync(mcVersion, loaderVersion, ct)
                ?? throw new ModpackException($"找不到 Minecraft {mcVersion} 可用的 Quilt 版本");
            onProgress(5, $"准备 Quilt {picked}");

            // ---------- 2. 拉 profile JSON ----------
            var profileUrl = $"https://meta.quiltmc.org/v3/versions/loader/{mcVersion}/{picked}/profile/json";
            onProgress(15, "获取 Quilt 版本信息");
            var profileText = await FabricDownloader.GetStringAsync(
                profileUrl, TimeSpan.FromSeconds(15), 3, "Quilt 版本信息", null, ct);

            var profile = JsonNode.Parse(profileText)
                ?? throw new ModpackException("Quilt 版本信息解析失败");

            // ---------- 3. 写实例版本 JSON ----------
            onProgress(25, "生成实例版本文件");
            var versionsDir = Path.Combine(mcFolder, "versions", instanceId);
            Directory.CreateDirectory(versionsDir);
            var clientJsonPath = Path.Combine(versionsDir, instanceId + ".json");

            profile["id"] = instanceId;
            if (profile["inheritsFrom"] is null)
                profile["inheritsFrom"] = mcVersion;

            File.WriteAllText(clientJsonPath, profile.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Serilog.Log.Information("Quilt 版本文件已写入 {Path}", clientJsonPath);

            // ---------- 4. 下载库 ----------
            var libs = new List<JsonNode>();
            CollectLibraries(profile["libraries"], libs);
            onProgress(30, "下载 Quilt 依赖库");
            await DownloadLibrariesAsync(mcFolder, libs, p =>
                onProgress(30 + (int)(p * 0.65), "下载 Quilt 依赖库"), ct);

            onProgress(100, "Quilt 安装完成");
            Serilog.Log.Information("Quilt {Ver} 安装完成：{Id}", picked, instanceId);
        }

        private static async Task<string?> PickQuiltVersionAsync(
            string mcVersion, string wanted, CancellationToken ct)
        {
            var url = $"https://meta.quiltmc.org/v3/versions/loader/{mcVersion}";
            var json = await FabricDownloader.GetStringAsync(
                url, TimeSpan.FromSeconds(20), 3, "Quilt 版本列表", null, ct);

            var versions = new List<string>();
            using (var doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var elem in doc.RootElement.EnumerateArray())
                    {
                        // 每条形如 { "loader": { "version": "0.20.0", ... }, "intermediary": {...} }
                        var v = elem.TryGetProperty("loader", out var l) && l.TryGetProperty("version", out var ve)
                            ? ve.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(v))
                            versions.Add(v!);
                    }
                }
            }

            if (versions.Count == 0) return null;

            if (!string.IsNullOrWhiteSpace(wanted))
            {
                var target = NormalizeLoaderVersion(wanted!, mcVersion);
                var hit = versions.FirstOrDefault(v => string.Equals(v, target, StringComparison.OrdinalIgnoreCase));
                if (hit is not null) return hit;

                Serilog.Log.Warning("Quilt 清单里指定的版本 {Wanted} 找不到，改用最新版", wanted);
            }

            return versions[0];
        }

        // ==================== 版本挑选 ====================
        /// <summary>
        /// 拉取 Forge / NeoForge 的可用版本列表，挑出与 <paramref name="wanted"/> 匹配的那个。
        ///
        /// 这里**不用**库的 <c>ForgeInstaller.EnumerableForgeAsync</c>，
        /// 因为它返回的 <c>ForgeVersion</c> 对 BMCLAPI 的部分条目是带 MC 前缀的
        /// （<c>1.20.1-47.1.105</c>），拿来做 URL 会踩坑；我们自己解析 JSON，
        /// 只取纯版本号，并且把下载地址自己也拼一份。
        /// </summary>
        /// <summary>
        /// 选一个 Forge / NeoForge 版本。
        ///
        /// <para>
        /// <b>1.0.8.0 起取源顺序改为「官方优先、镜像兜底」</b>（用户要求：加载器获取源统一用官方）。
        /// 原来直接读 BMCLAPI 的 <c>/forge/minecraft/&lt;mc&gt;</c> / <c>/neoforge/list/&lt;mc&gt;</c>，
        /// 那是镜像源 —— 版本集合偶尔落后于官方，而安装页上展示的版本来自官方枚举，
        /// 两边对不上就会出现「选了 51.0.33 却装成别的版本」。
        /// </para>
        ///
        /// <para>
        /// 但官方 maven 在国内经常连不上，所以官方失败时**仍然回退 BMCLAPI** ——
        /// 只有「官方拿不到任何版本」时才用镜像，不会因为官方慢就白等到超时。
        /// </para>
        /// </summary>
        private static async Task<string> PickForgeVersionAsync(
            string mcVersion, string? wanted, bool neoForge, CancellationToken ct)
        {
            var loaderName = neoForge ? "NeoForge" : "Forge";
            var versions = await FetchForgeVersionsAsync(mcVersion, neoForge, ct);

            if (versions.Count == 0)
                throw new ModpackException($"Minecraft {mcVersion} 没有可用的 {loaderName} 版本");

            // 有指定版本就精确匹配（归一化后），否则取最新的一个
            if (!string.IsNullOrWhiteSpace(wanted))
            {
                var target = NormalizeLoaderVersion(wanted!, mcVersion);
                var hit = versions.FirstOrDefault(v => string.Equals(v, target, StringComparison.OrdinalIgnoreCase));
                if (hit is not null)
                    return hit;

                Serilog.Log.Warning(
                    "{Loader} 清单里指定的版本 {Wanted} 在 {Mc} 的列表里找不到，改用最新版",
                    loaderName, wanted, mcVersion);
            }

            return versions[0];
        }

        /// <summary>
        /// 拉版本列表：官方 maven 优先，失败回退 BMCLAPI。返回值按「新 → 旧」排序。
        /// </summary>
        private static async Task<List<string>> FetchForgeVersionsAsync(
            string mcVersion, bool neoForge, CancellationToken ct)
        {
            var loaderName = neoForge ? "NeoForge" : "Forge";

            try
            {
                var official = neoForge
                    ? await FetchNeoForgeVersionsFromMavenAsync(mcVersion, ct)
                    : await FetchForgeVersionsFromMavenAsync(mcVersion, ct);

                if (official.Count > 0)
                {
                    Serilog.Log.Information("{Loader} 版本列表来自官方 maven：{Count} 个", loaderName, official.Count);
                    return official;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "{Loader} 官方源取版本列表失败，回退 BMCLAPI", loaderName);
            }

            // 镜像兜底：官方拿不到（被墙 / 超时 / 5xx）时才走到这里
            var listUrl = neoForge
                ? $"https://bmclapi2.bangbang93.com/neoforge/list/{mcVersion}"
                : $"https://bmclapi2.bangbang93.com/forge/minecraft/{mcVersion}";

            string json;
            try
            {
                json = await FabricDownloader.GetStringAsync(
                    listUrl, TimeSpan.FromSeconds(20), 3, $"{loaderName} 版本列表", null, ct);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "{Loader} 镜像源取版本列表也失败", loaderName);
                throw new ModpackException(
                    $"获取 {loaderName} 版本列表失败（官方源与 BMCLAPI 都不可用）。"
                    + "请检查网络或代理后重试");
            }

            var versions = new List<string>();
            using (var doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var elem in doc.RootElement.EnumerateArray())
                    {
                        var v = elem.TryGetProperty("version", out var ve) ? ve.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(v))
                            versions.Add(NormalizeLoaderVersion(v!, mcVersion));
                    }
                }
            }

            return SortVersionsDescending(versions);
        }

        /// <summary>
        /// Forge 官方 maven 的版本列表：<c>maven-metadata.xml</c> 里是一串
        /// <c>&lt;version&gt;1.21-51.0.33&lt;/version&gt;</c>，先按 mc 版本前缀过滤再剥掉前缀。
        /// </summary>
        private static async Task<List<string>> FetchForgeVersionsFromMavenAsync(
            string mcVersion, CancellationToken ct)
        {
            const string url = "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml";

            var xml = await FabricDownloader.GetStringAsync(
                url, TimeSpan.FromSeconds(20), 2, "Forge 版本列表（官方）", null, ct);

            var prefix = mcVersion + "-";
            var result = new List<string>();

            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(xml, @"<version>([^<]+)</version>"))
            {
                var raw = m.Groups[1].Value.Trim();
                if (!raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var ver = raw[prefix.Length..];
                // 只保留纯版本号（跳过 1.21-51.0.33-mdk 这类附属制品）
                if (ver.Length > 0 && char.IsDigit(ver[0]))
                    result.Add(ver);
            }

            return SortVersionsDescending(result);
        }

        /// <summary>
        /// NeoForge 官方 maven 的版本列表（JSON）。
        ///
        /// <para>
        /// 端点返回的是**全部**版本，需要自己按 mc 版本过滤：
        /// 1.21.x 对应 <c>21.x.y</c>、1.20.4 对应 <c>20.4.y</c>；
        /// 1.20.1 是特例 —— 它的制品走 Forge 的老版本号（47.x），放在 <c>net/neoforged/forge</c> 下。
        /// </para>
        /// </summary>
        private static async Task<List<string>> FetchNeoForgeVersionsFromMavenAsync(
            string mcVersion, CancellationToken ct)
        {
            var artifact = mcVersion == "1.20.1" ? "forge" : "neoforge";
            var url = $"https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/{artifact}";

            // 官方 REST 端点在国内实测**完全连不上**（curl 直接超时，连 TLS 都建不起来），
            // 而它又是「唯一能给出完整版本集合」的地方，所以主路径之外必须有兜底。
            // 抓不到就直接抛，让上层走 BMCLAPI 的 /neoforge/list/<mc>。
            var json = await FabricDownloader.GetStringAsync(
                url, TimeSpan.FromSeconds(12), 1, "NeoForge 版本列表（官方）", null, ct);

            // 1.20.1 走 Forge 版本号（47.x），不按前缀过滤
            var prefix = mcVersion == "1.20.1" ? null : mcVersion.StartsWith("1.", StringComparison.Ordinal)
                ? mcVersion[2..] + "."
                : null;

            var result = new List<string>();
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("versions", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var elem in arr.EnumerateArray())
                {
                    var v = elem.GetString();
                    if (string.IsNullOrWhiteSpace(v))
                        continue;

                    if (prefix is not null && !v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    result.Add(v);
                }
            }

            return SortVersionsDescending(result);
        }

        /// <summary>把版本号按「新 → 旧」排序：先比前三段数字，再按后缀（正式版优先于 beta）。</summary>
        private static List<string> SortVersionsDescending(IEnumerable<string> versions)
        {
            static (int, int, int, int) Key(string v)
            {
                var dash = v.IndexOf('-');
                var core = dash >= 0 ? v[..dash] : v;
                var parts = core.Split('.');
                int P(int i) => i < parts.Length && int.TryParse(parts[i], out var n) ? n : 0;

                // 有后缀（beta / pre）的排在无后缀之后
                var suffixPenalty = dash >= 0 ? 1 : 0;
                return (P(0), P(1), P(2), suffixPenalty);
            }

            return versions
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(Key)
                .ThenByDescending(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// 归一化加载器版本号，处理这些形态：<c>47.4.10</c> / <c>forge-47.4.10</c> /
        /// <c>neoforge-21.1.0</c> / <c>1.20.1-47.4.10</c> / <c>1.20.1-0.27.1</c> /
        /// <c>1.20.1-forge-47.1.105</c>（BMCLAPI 的 <c>rawVersion</c> 形态）。
        ///
        /// <para>前缀的顺序不固定 —— 可能是 <c>mc-</c> 在前，也可能 <c>loader-</c> 在前，
        /// 甚至两个都有（<c>1.20.1-forge-47.1.105</c>）。所以这里**反复剥前缀直到稳定**，
        /// 而不是只剥一次。实测 BMCLAPI 的 NeoForge 列表最后 5 条就返回这种双前缀。</para>
        /// </summary>
        internal static string NormalizeLoaderVersion(string raw, string? mcVersion = null)
        {
            var s = (raw ?? string.Empty).Trim().Replace('_', '-');

            // 反复剥：每一轮尝试剥掉一个前导的「加载器名-」或「MC 版本-」，
            // 只要这一轮剥成功就再来一轮，直到剥不动为止。
            // 最多 3 轮（正常最多两段前缀），多余的轮次不会执行。
            for (var guard = 0; guard < 4; guard++)
            {
                var dash = s.IndexOf('-');
                if (dash <= 0) break;

                var head = s[..dash];

                // 已知 mc 版本时只剥「等于该版本」的前缀，不再靠「看起来像 1.2.3」泛化判断 ——
                // NeoForge 的 21.0.1-beta 会让泛化判断把 "21.0.1" 当成 MC 版本吃掉，
                // 最后剩下的版本号变成 "beta"，拼出来的安装器 URL 404（1.0.7.16 修）。
                var isMcPrefix = mcVersion is not null
                    ? string.Equals(head, mcVersion, StringComparison.OrdinalIgnoreCase)
                    : LooksLikeMcVersion(head);

                if (IsLoaderWord(head) || isMcPrefix)
                {
                    s = s[(dash + 1)..];
                    continue;
                }

                break;
            }

            return s;
        }

        private static bool IsLoaderWord(string s) =>
            s.Equals("forge", StringComparison.OrdinalIgnoreCase)
            || s.Equals("neoforge", StringComparison.OrdinalIgnoreCase)
            || s.Equals("fabric", StringComparison.OrdinalIgnoreCase)
            || s.Equals("quilt", StringComparison.OrdinalIgnoreCase);

        /// <summary>形如 <c>1.20.1</c>：含至少两个点、全是数字与点。</summary>
        private static bool LooksLikeMcVersion(string s)
            => s.Count(c => c == '.') >= 2 && s.All(c => char.IsDigit(c) || c == '.');

        /// <summary>
        /// 拼 installer jar 的**官方**地址。
        ///
        /// Forge：<c>maven.minecraftforge.net/net/minecraftforge/forge/&lt;mc&gt;-&lt;ver&gt;/forge-&lt;mc&gt;-&lt;ver&gt;-installer.jar</c>
        /// NeoForge 1.20.1 特殊：它的制品放在 <c>net/neoforged/forge/</c> 下（1.20.2+ 才是 <c>net/neoforged/neoforge/</c>）。
        /// 归一化后的版本是纯版本号，拼出来的 URL 才是对的。
        /// </summary>
        private static string BuildInstallerUrl(string mcVersion, string forgeVersion, bool neoForge)
        {
            if (!neoForge)
                return $"https://maven.minecraftforge.net/net/minecraftforge/forge/{mcVersion}-{forgeVersion}/forge-{mcVersion}-{forgeVersion}-installer.jar";

            // NeoForge 的下载同时收录到 <see cref="Helpers.DownloadSources"/> 的镜像映射里
            var artifact = mcVersion == "1.20.1" ? "forge" : "neoforge";
            var ver = mcVersion == "1.20.1" ? $"{mcVersion}-{forgeVersion}" : forgeVersion;
            return $"https://maven.neoforged.net/releases/net/neoforged/{artifact}/{ver}/{artifact}-{ver}-installer.jar";
        }

        /// <summary>
        /// 安装器的 BMCLAPI 镜像地址。
        ///
        /// <para>
        /// 不复用 <see cref="Helpers.DownloadSources"/> 的自动改写，是因为那里对
        /// <c>maven.neoforged.net/releases/</c> 的前缀映射没问题，但它把「用户偏好」放在
        /// 第一位 —— 这里我们要的是「**镜像必进候选、且优先**」，需要显式拿到它，
        /// 而不是赌用户设置里开了 <c>UseBmclMirror</c>。
        /// </para>
        ///
        /// <para>
        /// 实测（2026-10-18）：官方 <c>maven.neoforged.net</c> 完全连不上；
        /// <c>maven.minecraftforge.net</c> 能连但响应体不发。两者的镜像路径都是
        /// <c>/maven/&lt;groupId 路径&gt;/…</c>，且返回 200 + 完整文件。
        /// </para>
        /// </summary>
        private static string BuildMirrorInstallerUrl(string mcVersion, string forgeVersion, bool neoForge)
        {
            const string mirrorRoot = "https://bmclapi2.bangbang93.com/maven";

            if (!neoForge)
                return $"{mirrorRoot}/net/minecraftforge/forge/{mcVersion}-{forgeVersion}/forge-{mcVersion}-{forgeVersion}-installer.jar";

            var artifact = mcVersion == "1.20.1" ? "forge" : "neoforge";
            var ver = mcVersion == "1.20.1" ? $"{mcVersion}-{forgeVersion}" : forgeVersion;
            return $"{mirrorRoot}/net/neoforged/{artifact}/{ver}/{artifact}-{ver}-installer.jar";
        }

        // ==================== 库下载 ====================

        private static void CollectLibraries(JsonNode? source, List<JsonNode> into)
        {
            if (source is not JsonArray arr) return;
            foreach (var item in arr)
                if (item is not null) into.Add(item);
        }

        /// <summary>
        /// 去重，保留先出现的。
        ///
        /// <para>
        /// <paramref name="ignoreVersion"/> = true：按「group:artifact:classifier」去重，版本号忽略。
        /// 只适用于**启动库** —— 同一个 artifact 装两个版本进 classpath 会撞模块。
        /// </para>
        /// <para>
        /// <paramref name="ignoreVersion"/> = false：按完整坐标（含版本号）去重。
        /// **处理器工具库必须用这个**：不同 processor 的 classpath 会点名不同版本，
        /// 例如 1.20.2 的 installertools 要 asm-commons <c>9.3</c>，ForgeAutoRenamingTool 要 <c>9.5</c>。
        /// 按 artifact 去重会把 9.5 当成「重复项」丢掉，处理器随即报
        /// 「加载器安装处理器缺少依赖：asm-commons-9.5.jar」—— 装上的是半个实例，
        /// 启动时必然缺类崩溃。
        /// </para>
        /// </summary>
        private static List<JsonNode> DedupLibraries(List<JsonNode> libs, bool ignoreVersion)
        {
            // 按「group:artifact:classifier」去重，而不是整条 name。
            //
            // 整条 name 去重等于「同一个库的不同版本全都留着」。加载器恰好会在这一点上翻车：
            // Forge 26.2 的 install_profile 带 jopt-simple 6.0-alpha-3，原版 26.2 声明 5.0.4，
            // 两份都进 classpath 之后 ForgeBootstrap 建模块层直接报
            //   Modules joptsimple and jopt.simple export package joptsimple.util to module JarJarMetadata
            // —— 游戏连窗口都不出，用户只看到一闪而过。
            //
            // 保留先出现的：调用方先铺 versionJson（加载器自己声明的版本，权威），
            // 再铺 install_profile（processor 工具库），冲突时听加载器的。
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<JsonNode>();

            foreach (var lib in libs)
            {
                var name = lib["name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(name))
                    continue;

                var key = ignoreVersion ? ArtifactKey(name!) : NormalizeCoordinate(name!);
                if (key.Length == 0)
                {
                    result.Add(lib);
                    continue;
                }

                if (seen.Add(key))
                    result.Add(lib);
            }

            return result;
        }

        /// <summary>
        /// 从 maven 坐标里取「group:artifact:classifier」当作同一干活的标识。
        /// 版本号故意忽略：同一 artifact 装两个版本只会让模块层打架。
        /// </summary>
        private static string ArtifactKey(string coordinate)
        {
            var trimmed = coordinate.Contains('@') ? coordinate[..coordinate.IndexOf('@')] : coordinate;
            var parts = trimmed.Split(':');
            if (parts.Length < 2)
                return string.Empty;

            var classifier = parts.Length > 3 ? parts[3] : string.Empty;
            return string.Join(':', parts[0], parts[1], classifier);
        }

        /// <summary>
        /// 下载加载器的依赖库。
        ///
        /// <para>
        /// 1.0.7.15 起换成 <see cref="BatchDownloader"/>：Forge / NeoForge 的库有几十个，
        /// 原来串行一个个下，装一次能等好几分钟；现在 4 个文件并行、每个文件内部再分块，
        /// 总连接数约 4×8，既压满带宽又不至于把 BMCLAPI 打成 403。
        /// </para>
        ///
        /// <para>
        /// 已经存在的库由内核按体积判断跳过（不必在这里自己 <c>File.Exists</c>）；
        /// 失败**不中断**整批 —— 先把能下的下完，最后一次性报出「哪些失败、为什么」。
        /// </para>
        /// </summary>
        private static async Task DownloadLibrariesAsync(
            string mcFolder, List<JsonNode> libraries, Action<double> onProgress, CancellationToken ct,
            IReadOnlySet<string>? skipCoordinates = null)
        {
            var libsDir = Path.Combine(mcFolder, "libraries");
            var items = new List<DownloadItem>(libraries.Count);

            foreach (var lib in libraries)
            {
                ct.ThrowIfCancellationRequested();

                var name = lib["name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(name))
                    continue;

                // 处理器产物：maven 上没有这个文件，只能由 processor 在本地写出来。
                // 这里必须跳过，否则会连着 4 轮 404 / 超时，最后把安装判死。
                if (skipCoordinates is not null
                    && skipCoordinates.Contains(NormalizeCoordinate(name)))
                {
                    Serilog.Log.Debug("跳过处理器产物（不下载）：{Name}", name);
                    continue;
                }

                var (url, relativePath, size, sha1) = ResolveArtifact(lib, name!);
                if (url is null || relativePath is null)
                    continue;   // 纯声明型依赖（如 net.minecraft:launchwrapper 的某些变体），跳过

                var target = Path.Combine(libsDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
                items.Add(new DownloadItem(url, target, size, sha1, DisplayName: ShortName(name!)));
            }

            if (items.Count == 0)
            {
                onProgress(1);
                return;
            }

            onProgress(0);

            var result = await BatchDownloader.DownloadAsync(
                items,
                parallelism: SettingsService.ResolveDownloadConcurrency(8),
                onProgress: p => onProgress(Math.Clamp(p.Percent / 100d, 0, 1)),
                ct: ct).ConfigureAwait(false);

            // processor 的工具库缺一个都跑不起来，必须整批成功
            if (!result.IsSuccess)
            {
                var listed = string.Join(" / ", result.Failures.Take(3)
                    .Select(f => $"{Path.GetFileName(f.File)}（{FabricDownloader.Describe(f.Error)}）"));

                throw new ModpackException(
                    $"有 {result.Failures.Count} 个加载器依赖库下载失败：{listed}。"
                    + "可在「设置 → 下载」切换下载源或增大重试次数后重试");
            }
        }

        /// <summary>
        /// 解析一条库声明，得到「下载地址」与「相对 libraries/ 的落点路径」。
        ///
        /// 优先用 <c>downloads.artifact</c>（官方清单里通常都带 url + path）；
        /// 缺失时退回 <c>name</c> + <c>url</c> 自己拼（Forge 的 install_profile 常见这种）。
        /// </summary>
        /// <summary>
        /// 解析一条库声明，得到「下载地址 / 相对 libraries/ 的落点 / 期望体积 / sha1」。
        ///
        /// <para>
        /// 后两个是 1.0.7.16 加的：只靠「文件存在」判断会导致「上次下到一半的坏 jar」
        /// 被当成已下载（Forge 的 processor 随后莫名其妙 ClassNotFound）。
        /// 有 size/sha1 就能在下载前判跳过、下载后判完整。
        /// </para>
        ///
        /// 优先用 <c>downloads.artifact</c>（官方清单里通常都带 url + path）；
        /// 缺失时退回 <c>name</c> + <c>url</c> 自己拼（Forge 的 install_profile 常见这种）。
        /// </summary>
        private static (string? Url, string? RelativePath, long Size, string? Sha1) ResolveArtifact(
            JsonNode lib, string name)
        {
            // downloads.artifact
            var artifact = lib["downloads"]?["artifact"];
            if (artifact is not null)
            {
                var url = artifact["url"]?.GetValue<string>();
                var path = artifact["path"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(path))
                {
                    // 体积 / sha1 缺失时给 0 / null：内核会退化成「存在即跳过」，
                    // 与改动前的行为一致，不会因为清单不全就把已经下好的库重下一遍。
                    var size = 0L;
                    if (artifact["size"] is JsonNode sizeNode)
                        long.TryParse(sizeNode.ToJsonString(), out size);

                    var sha1 = artifact["sha1"] is JsonNode shaNode
                        ? shaNode.GetValue<string>()
                        : null;

                    // artifact 地址可能是 libraries.minecraft.net / maven.minecraftforge.net，
                    // 都已在 DownloadSources 的镜像表里；maven.neoforged.net 也补进去了。
                    return (url, path, size, sha1);
                }
            }

            // name + (maven url)
            var coords = name.Split(':');
            if (coords.Length < 3) return (null, null, 0, null);

            var group = coords[0];
            var artifactId = coords[1];
            var version = coords[2];
            var classifier = coords.Length > 3 ? coords[3] : null;

            var fileName = classifier is null
                ? $"{artifactId}-{version}.jar"
                : $"{artifactId}-{version}-{classifier}.jar";
            var relative = $"{group.Replace('.', '/')}/{artifactId}/{version}/{fileName}";

            var mavenBase = lib["url"]?.GetValue<string>()?.TrimEnd('/')
                            ?? DefaultMavenFor(name);
            if (mavenBase is null) return (null, null, 0, null);

            return ($"{mavenBase}/{relative}", relative, 0, null);
        }

        /// <summary>没有显式 url 时，按 groupId 猜一个默认 maven 根。</summary>
        private static string? DefaultMavenFor(string name)
        {
            if (name.StartsWith("net.neoforged", StringComparison.OrdinalIgnoreCase))
                return "https://maven.neoforged.net/releases/";
            if (name.StartsWith("net.minecraftforge", StringComparison.OrdinalIgnoreCase))
                return "https://maven.minecraftforge.net/";
            if (name.StartsWith("net.fabricmc", StringComparison.OrdinalIgnoreCase))
                return "https://maven.fabricmc.net/";
            if (name.StartsWith("org.quiltmc", StringComparison.OrdinalIgnoreCase))
                return "https://maven.quiltmc.org/repository/release/";
            if (name.StartsWith("net.minecraft", StringComparison.OrdinalIgnoreCase))
                return "https://libraries.minecraft.net/";
            // 其余（asm / gson / jopt 等）Forge 会把它们镜像在自家 maven
            return "https://maven.minecraftforge.net/";
        }

        /// <summary>
        /// 规范化 maven 坐标：去掉 <c>@jar</c> 之类的扩展后缀，供比较用。
        /// </summary>
        private static string NormalizeCoordinate(string coordinate)
        {
            var value = coordinate.Trim();
            var at = value.IndexOf('@');
            return at > 0 ? value[..at] : value;
        }

        /// <summary>
        /// 收集 <c>install_profile.data</c> 里所有「maven 坐标形式」的值。
        ///
        /// <para>
        /// 形如 <c>[net.minecraftforge:forge:26.2-65.1.5:client]</c> 的条目是
        /// <b>处理器本地生成</b>的产物（<c>PATCHED</c> / <c>MC_UNPACKED</c>），
        /// maven 上并不存在同名文件：实测 Forge 26.2 的 client jar 在官方 maven 返回 404
        /// （BMCLAPI 镜像同样 404）。它们只该由 processor 写出来，绝不能进下载队列。
        /// </para>
        ///
        /// <para>
        /// 反过来的 <c>/data/client.lzma</c>（BINPATCH）是安装包内条目，以 <c>/</c> 开头，
        /// 不是坐标，这里自然被过滤掉。
        /// </para>
        /// </summary>
        private static HashSet<string> CollectProcessorOutputs(JsonNode profile)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (profile["data"] is not JsonObject data)
                return result;

            foreach (var pair in data)
            {
                var raw = ReadSideValue(pair.Value);
                if (raw is null)
                    continue;

                raw = raw.Trim();
                if (raw.Length > 2 && raw[0] == '[' && raw[^1] == ']')
                    result.Add(NormalizeCoordinate(raw[1..^1]));
            }

            return result;
        }

        /// <summary>这个处理器是否会在 client 侧执行（没写 sides 视为两端通用）。</summary>
        private static bool IsClientSideProcessor(JsonNode proc)
        {
            if (proc["sides"] is not JsonArray sides || sides.Count == 0)
                return true;

            return sides.Any(s => string.Equals(
                s?.GetValue<string>(), "client", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 把处理器产出的 patched client jar 作为一条 library 写进版本 JSON。
        ///
        /// <para>
        /// <b>为什么必须做</b>：<c>install_profile.data.PATCHED</c> 只说明了「补丁后的产物放哪」，
        /// 并没有在 <c>libraries</c> 里声明它。classpath 完全由版本 JSON 的 <c>libraries</c> 决定，
        /// 所以少了这一条，游戏启动时 FML 就在 classpath 上找不到 Minecraft 类，直接报
        /// <c>The patched Minecraft jar is missing</c>。官方安装器跑完处理器后会自己补，我们也得补。
        /// </para>
        ///
        /// <para>
        /// 不锁坐标前缀：Forge 是 <c>net.minecraftforge:forge:&lt;mc&gt;-&lt;ver&gt;:client</c>，
        /// NeoForge 是 <c>net.neoforged:neoforge:&lt;ver&gt;:client</c>，
        /// 老的 NeoForge 还出现过 <c>minecraft-client-patched</c>。统一按 maven 规则算路径即可。
        /// </para>
        /// </summary>
        private static void EnsurePatchedLibrary(
            string mcFolder, JsonNode profile, JsonNode versionJson, string clientJsonPath,
            string loaderName, JsonArray? processors = null)
        {
            var raw = ReadSideValue(profile["data"]?["PATCHED"]);
            if (string.IsNullOrWhiteSpace(raw))
                return;

            raw = raw.Trim();
            if (raw.Length < 3 || raw[0] != '[' || raw[^1] != ']')
                return;   // 不是 maven 坐标，无从推断路径

            var coordinate = raw[1..^1];
            var (_, relative) = MavenToRelative(coordinate);
            var jarPath = Path.Combine(mcFolder, "libraries",
                relative.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(jarPath))
            {
                // 这个产物是**必须**的：client 侧的 binarypatcher 明确声明了它的输出。
                // 缺失就意味着安装没真正完成（游戏启动会报
                // 「The patched Minecraft jar is missing」），
                // 不能再像以前那样只打一条 warning 就当安装成功 ——
                // 用户随后会拿着一个「装好了但起不来」的实例来问为什么。
                var expectsClientPatch = processors is { Count: > 0 } && processors.Any(p =>
                    p is not null
                    && IsClientSideProcessor(p)
                    && p["outputs"] is JsonObject outputs
                    && outputs.ContainsKey("{PATCHED}"));

                if (expectsClientPatch)
                {
                    throw new ModpackException(
                        $"{loaderName} 安装处理器没有产出 {Path.GetFileName(jarPath)}。"
                        + "这个文件是处理器把原版 client.jar 打补丁后生成的，缺失它游戏会报"
                        + "「The patched Minecraft jar is missing」。"
                        + "请确认磁盘空间充足（需要约 100 MB 临时空间）后重试安装。");
                }

                // 没有 client 侧 patcher 声明（例如服务端专用布局）：记一笔即可
                Serilog.Log.Warning("{Loader} 的 patched jar 不存在，跳过注册：{Path}", loaderName, jarPath);
                return;
            }

            // 补丁 jar 的清单属性必须先修：FML 12（Forge 65 / NeoForge 26.x）在
            // GameLocator 里无条件读游戏 jar 的 Minecraft-Dists 主属性，读不到直接 Fatal。
            // 详见 EnsureDistsManifest 的注释。
            var manifestPatched = EnsureDistsManifest(jarPath);

            var libraries = versionJson["libraries"] as JsonArray;
            if (libraries is null)
            {
                libraries = new JsonArray();
                versionJson["libraries"] = libraries;
            }

            var info = new FileInfo(jarPath);
            var sha1 = ComputeSha1(jarPath);

            // 已经声明过就只校正 size/sha1（补清单属性会让文件内容变化，旧值就过期了）
            foreach (var lib in libraries)
            {
                var name = lib?["name"]?.GetValue<string>();
                if (name is null)
                    continue;

                // name 可能带 @jar 后缀，比较时去掉
                var normalized = name.Contains('@') ? name[..name.IndexOf('@')] : name;
                if (!string.Equals(normalized, coordinate, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!manifestPatched)
                    return;

                if (lib is not JsonObject obj)
                    return;

                obj["downloads"] = new JsonObject
                {
                    ["artifact"] = new JsonObject
                    {
                        ["path"] = relative,
                        ["size"] = info.Length,
                        ["sha1"] = sha1,
                    },
                };

                File.WriteAllText(clientJsonPath,
                    versionJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                Serilog.Log.Information("已更新 {Loader} 补丁 jar 的 size/sha1（补了清单属性）：{Path}",
                    loaderName, relative);
                return;
            }

            libraries.Add(new JsonObject
            {
                ["name"] = coordinate,
                ["downloads"] = new JsonObject
                {
                    ["artifact"] = new JsonObject
                    {
                        ["path"] = relative,
                        ["size"] = info.Length,
                        ["sha1"] = sha1,
                    },
                },
            });

            File.WriteAllText(clientJsonPath,
                versionJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            Serilog.Log.Information("已把 {Loader} 的 patched jar 注册进 classpath：{Coord} → {Path}",
                loaderName, coordinate, relative);
        }

        /// <summary>
        /// 给补丁客户端 jar 的主清单补一条 <c>Minecraft-Dists: client</c>。
        ///
        /// <para>
        /// <b>为什么必须补</b>：FML 12（Forge 65 / NeoForge 26.x）启动时会走
        /// <c>NeoForgeDevDistCleaner.getMaskedFiles(jar, dist)</c>，第一件事就是读游戏 jar
        /// 的主清单属性 <c>Minecraft-Dists</c>；读不到就抛
        /// <c>fml.modloadingissue.neodev_missing_dists_attribute</c>：
        /// </para>
        /// <code>
        /// NeoForge dev environment Minecraft jar does not have a Minecraft-Dists
        /// attribute in its manifest; this may be because you have an out-of-date gradle plugin
        /// </code>
        /// <para>
        /// 而官方 installertools 的 <c>PROCESS_MINECRAFT_JAR</c> 只会**照抄**原版 jar 的清单
        /// （原版只有 <c>Manifest-Version</c> + <c>Main-Class</c>），于是官方安装思路产出的补丁 jar
        /// 天生缺这一条 —— 报错信息里那句「gradle plugin 太旧」完全是误导，跟 gradle 无关。
        /// </para>
        ///
        /// <para>
        /// 实测：给 <c>minecraft-client-patched-26.3.0.0-beta.jar</c> 补上这一条之后，
        /// NeoForge 26.3 能一路走到主界面（Setting user → 资源加载 → 声音引擎）。
        /// </para>
        ///
        /// <para>
        /// 只有本来就有 <c>META-INF/MANIFEST.MF</c> 的 jar 才动它：1.20.1 那代 Forge 的补丁 jar
        /// 压根没有清单，FML 1.x 也不看这个属性，硬塞一份清单属于无谓风险。
        /// </para>
        /// </summary>
        /// <returns>是否真的改了文件（改了就要重算 size/sha1）。</returns>
        private static bool EnsureDistsManifest(string jarPath)
        {
            try
            {
                using var zip = ZipFile.Open(jarPath, ZipArchiveMode.Update);
                var entry = zip.GetEntry("META-INF/MANIFEST.MF");
                if (entry is null)
                    return false;

                string text;
                using (var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
                           leaveOpen: false))
                {
                    text = reader.ReadToEnd();
                }

                if (text.Contains("Minecraft-Dists", StringComparison.OrdinalIgnoreCase))
                    return false;

                var nl = text.Contains("\r\n") ? "\r\n" : "\n";
                var blank = text.IndexOf(nl + nl, StringComparison.Ordinal);
                var head = blank < 0 ? text : text[..blank];
                var tail = blank < 0 ? string.Empty : text[blank..];
                if (!head.EndsWith(nl, StringComparison.Ordinal))
                    head += nl;

                var updated = head + "Minecraft-Dists: client" + nl + tail;

                entry.Delete();
                var replaced = zip.CreateEntry("META-INF/MANIFEST.MF", CompressionLevel.Optimal);
                using (var writer = new StreamWriter(replaced.Open(), new UTF8Encoding(false)))
                {
                    writer.Write(updated);
                }

                Serilog.Log.Information("已给补丁 jar 补 Minecraft-Dists 清单属性：{Path}", jarPath);
                return true;
            }
            catch (Exception ex)
            {
                // 补不了也不能让安装失败：老版本加载器根本不需要这条属性，
                // 真正需要它的新版本会在启动时报错，届时用户重装即可（安装日志里有这条 Warning）。
                Serilog.Log.Warning(ex, "补 Minecraft-Dists 清单属性失败（不影响非 FML12 加载器）：{Path}", jarPath);
                return false;
            }
        }

        /// <summary>读 data 里某一项的 client 值（可能是字符串，也可能是 {client,server} 对象）。</summary>
        private static string? ReadSideValue(JsonNode? node)
        {
            if (node is JsonObject sideObj)
                return sideObj["client"]?.GetValue<string>() ?? sideObj["server"]?.GetValue<string>();

            if (node is JsonValue value && value.TryGetValue<string>(out var s))
                return s;

            return null;
        }

        /// <summary>算文件的 SHA1（大写十六进制，与官方清单一致）。失败返回 null，不阻断安装。</summary>
        private static string? ComputeSha1(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                var hash = System.Security.Cryptography.SHA1.HashData(stream);
                return Convert.ToHexString(hash);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "计算 SHA1 失败：{Path}", path);
                return null;
            }
        }

        // ==================== processors ====================
        private static async Task RunProcessorsAsync(
            string mcFolder,
            string javaPath,
            string installerJar,
            string mcVersion,
            string forgeVersion,
            bool neoForge,
            JsonNode profile,
            JsonArray processors,
            Action<double, string> onProgress,
            CancellationToken ct)
        {
            // 安装包内的条目（data/client.lzma 等）要解到临时目录再交给处理器。
            // 放在 temp 而不是 .minecraft 里：这些是安装期的一次性中间物，
            // 留在游戏目录会被下次「完整性检查」当成垃圾文件。
            var extractDir = Path.Combine(
                Path.GetTempPath(), "vbl-loader-" + Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(extractDir);

                // data 段：把 {KEY} 解析成真实值（processor 的 args 会引用它们）
                var data = ResolveData(profile["data"], mcFolder, mcVersion, forgeVersion,
                    installerJar, extractDir);

            // 变量替换表 —— 官方 installer 的同一套
            var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["{SIDE}"] = "client",
                ["{MINECRAFT_JAR}"] = Path.Combine(mcFolder, "versions", mcVersion, mcVersion + ".jar"),
                ["{MINECRAFT_VERSION}"] = mcVersion,
                ["{ROOT}"] = mcFolder,
                ["{INSTALLER}"] = installerJar,
                ["{LIBRARY_DIR}"] = Path.Combine(mcFolder, "libraries"),
            };

            var total = processors.Count;
            var index = 0;

            foreach (var proc in processors)
            {
                ct.ThrowIfCancellationRequested();

                // 只跑 client（或没写 sides 的，表示两端通用）
                var sides = proc["sides"] as JsonArray;
                if (sides is not null && sides.Count > 0)
                {
                    var list = sides.Select(x => x?.GetValue<string>()).ToList();
                    if (!list.Contains("client", StringComparer.OrdinalIgnoreCase))
                    {
                        index++;
                        onProgress((double)index / total,
                            $"跳过 {(neoForge ? "NeoForge" : "Forge")} 服务端专用处理器");
                        continue;
                    }
                }

                // 每个处理器开跑前先把「当前是第几个」推给界面。
                // 处理器（尤其 Forge 的 installer-tools）单个可能跑几十秒到几分钟，
                // 期间没有任何字节流可上报 —— 没有这一句，用户看到的就是进度条
                // 停在 56% 一动不动，误判成「卡死」然后强杀进程（1.0.8.x 反馈最多的一条）。
                onProgress((double)index / total, DescribeProcessor(proc, index + 1, total));
                var jarName = proc["jar"]?.GetValue<string>()
                    ?? throw new ModpackException("加载器安装处理器缺少 jar 声明");

                // -cp：处理器 jar + 它声明的 classpath
                var classpath = new List<string>();
                classpath.Add(LibraryPath(mcFolder, jarName));
                if (proc["classpath"] is JsonArray cpArr)
                    foreach (var c in cpArr)
                        if (c?.GetValue<string>() is { } cn)
                            classpath.Add(LibraryPath(mcFolder, cn));

                var missing = classpath.Where(p => !File.Exists(p)).ToList();
                if (missing.Count > 0)
                {
                    throw new ModpackException(
                        $"加载器安装处理器缺少依赖：{ShortName(Path.GetFileName(missing[0]))}。" +
                        "多为依赖库下载不完整，请在「设置 → 下载」切换下载源后重试");
                }

                // args：做三轮替换 —— {VAR} → data 值 → 再替换一遍（data 值本身可能含占位符）
                var args = new List<string>();
                if (proc["args"] is JsonArray argArr)
                {
                    foreach (var a in argArr)
                    {
                        var raw = a?.GetValue<string>();
                        if (raw is null) continue;
                        args.Add(Substitute(raw, data, replacements, mcFolder));
                    }
                }

                var mainClass = proc["mainClass"]?.GetValue<string>();
                var cpString = string.Join(Path.PathSeparator, classpath);

                Serilog.Log.Information("运行 {Loader} 处理器 {Index}/{Total}：{Jar} 参数 {Args}",
                    neoForge ? "NeoForge" : "Forge", index + 1, total, jarName, string.Join(" ", args.Take(6)));

                // 心跳：处理器跑起来之后可能几十秒不产出任何一行输出（尤其 installer-tools
                // 在解 lzma / 写 jar 的时候）。这里每 2 秒把「还在跑、已耗时多久」推给界面，
                // 进度条不会走，但文案会跳 —— 用户能区分「在干活」和「真卡死」。
                var stepLabel = DescribeProcessor(proc, index + 1, total);
                var baseFraction = (double)index / total;
                var slice = 1d / Math.Max(1, total);

                await RunJavaAsync(javaPath, mainClass, cpString, args, mcFolder, ct,
                    onHeartbeat: elapsed => onProgress(
                        baseFraction + slice * 0.9,
                        $"{stepLabel}（已耗时 {FormatElapsed(elapsed)}）"));

                index++;
                onProgress((double)index / total, stepLabel);
            }
            }
            finally
            {
                // 临时解压目录用完即删：里面是 client.lzma 之类的中间物，几十 MB
                try { Directory.Delete(extractDir, recursive: true); }
                catch { /* 被占用就留给系统清理，不影响安装结果 */ }
            }
        }

        /// <summary>
        /// 处理器的短描述，给界面显示用。
        ///
        /// 用 jar 名而不是 mainClass：jar 名短、可读性好（<c>installertools</c> / <c>jarsplitter</c>），
        /// mainClass 是一长串 <c>net.minecraftforge.installertools.ConsoleTool</c>，
        /// 塞进一行步骤文案里会被截断。
        /// </summary>
        private static string DescribeProcessor(JsonNode proc, int ordinal, int total)
        {
            var jar = proc["jar"]?.GetValue<string>();
            var name = string.IsNullOrWhiteSpace(jar) ? "安装处理器" : ShortName(jar!);

            // 去掉版本号后缀，只留构件名（installertools-1.4.2.jar → installertools）
            if (name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                name = name[..^4];
            var dash = name.LastIndexOf('-');
            if (dash > 0 && name[(dash + 1)..].All(char.IsDigit))
                name = name[..dash];

            return $"运行 {name}（{ordinal}/{total}）";
        }

        /// <summary>
        /// 从 jar 的 <c>META-INF/MANIFEST.MF</c> 里读 <c>Main-Class</c>。
        ///
        /// <para>
        /// 读不到就返回 null，调用方退回 <c>-jar</c>。只认第一处 <c>Main-Class:</c>，
        /// 不处理续行 —— manifest 的 Main-Class 极少折行，真折行了那也不是我们该猜的。
        /// </para>
        /// </summary>
        private static string? ReadMainClassFromJar(string jarPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(jarPath) || !File.Exists(jarPath))
                    return null;

                using var zip = ZipFile.OpenRead(jarPath);
                var entry = zip.GetEntry("META-INF/MANIFEST.MF");
                if (entry is null)
                    return null;

                using var reader = new StreamReader(entry.Open());
                while (reader.ReadLine() is { } line)
                {
                    if (line.StartsWith("Main-Class:", StringComparison.OrdinalIgnoreCase))
                    {
                        var value = line["Main-Class:".Length..].Trim();
                        return string.IsNullOrWhiteSpace(value) ? null : value;
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "读取 jar manifest 失败：{Path}", jarPath);
                return null;
            }
        }

        /// <summary>
        /// 安全地问「进程退出了吗」。
        ///
        /// <para>
        /// <c>Process</c> 被 Dispose 之后再读 <c>HasExited</c> 会抛
        /// <c>InvalidOperationException</c>（"No process is associated with this object"）。
        /// 而心跳和僵死看门狗都是没人 await 的后台 Task，异常最终会在 finalizer 线程
        /// 变成 AggregateException 打进日志 —— 每个 Forge 安装完都留一条红色「未观察到的任务异常」，
        /// 看起来像安装失败，其实是噪音。
        /// </para>
        /// </summary>
        private static bool HasExitedSafe(Process proc)
        {
            try
            {
                return proc.HasExited;
            }
            catch (InvalidOperationException)
            {
                // ObjectDisposedException 是它的子类，会一并走到这里
                return true;
            }
        }

        /// <summary>把耗时格式化成「12 秒」/「1 分 20 秒」，避免界面上出现 83.4s 这种读数。</summary>
        private static string FormatElapsed(TimeSpan elapsed)
        {
            if (elapsed.TotalSeconds < 60)
                return $"{elapsed.TotalSeconds:0} 秒";

            return $"{(int)elapsed.TotalMinutes} 分 {elapsed.Seconds} 秒";
        }

        /// <summary>
        /// 把 <c>data</c> 段解析成「KEY → 真实值」。
        ///
        /// <para>
        /// 三种写法，缺一不可（对齐官方 installer 的 <c>Install.java</c>）：
        /// </para>
        /// <list type="number">
        /// <item><description><c>'literal'</c> —— 去掉两侧单引号，直接用。</description></item>
        /// <item><description><c>[group:artifact:version:classifier]</c> —— 换成 <c>libraries/</c> 下的绝对路径。</description></item>
        /// <item><description>
        /// <c>/data/client.lzma</c> —— <b>安装包内部条目</b>，必须先从 installer jar 解压到磁盘，
        /// 再把**解压后的绝对路径**交给处理器。
        /// </description></item>
        /// </list>
        ///
        /// <para>
        /// <b>第三条是「Forge / NeoForge 每次安装都失败」的直接原因。</b>
        /// binarypatcher 的最后一个参数是 <c>--apply {BINPATCH}</c>，
        /// 以前这里把 <c>/data/client.lzma</c> 原样传下去 —— 在 Windows 上会被当成
        /// <c>C:\data\client.lzma</c>，处理器必然报「找不到文件」，
        /// 表现为装到 90% 突然失败，或者（更糟）Java 卡在那里等输入。
        /// 官方安装器从不这样传：它解压到临时目录，参数里给的是临时文件路径。
        /// </para>
        /// </summary>
        private static Dictionary<string, string> ResolveData(
            JsonNode? data, string mcFolder, string mcVersion, string forgeVersion,
            string installerJar, string extractDir)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (data is not JsonObject obj) return result;

            foreach (var (key, value) in obj)
            {
                string? v = null;
                if (value is JsonObject sideObj)
                    v = sideObj["client"]?.GetValue<string>() ?? sideObj["server"]?.GetValue<string>();
                else if (value is JsonValue jv && jv.TryGetValue<string>(out var s))
                    v = s;

                if (string.IsNullOrEmpty(v)) continue;

                var t = v.Trim();

                // 'literal' —— 去掉两侧单引号
                if (t.Length > 1 && t[0] == '\'' && t[^1] == '\'')
                {
                    result[key] = t[1..^1];
                }
                // [maven] —— 转成本地 libraries 路径
                else if (t.Length > 1 && t[0] == '[' && t[^1] == ']')
                {
                    result[key] = LibraryPath(mcFolder, t[1..^1]);
                }
                // /path/in/installer.jar —— 从安装包里解出来（client.lzma 等）
                else if (t.Length > 1 && t[0] == '/' && !t.StartsWith("//", StringComparison.Ordinal))
                {
                    result[key] = ExtractInstallerEntry(installerJar, t[1..], extractDir);
                }
                else
                {
                    result[key] = t;
                }
            }

            return result;
        }

        /// <summary>
        /// 把安装包里的某个条目解压到 <paramref name="extractDir"/>，返回解压后的绝对路径。
        ///
        /// 目录结构与归档内保持一致（<c>data/client.lzma</c> → <c>&lt;tmp&gt;/data/client.lzma</c>），
        /// 这样处理器如果对相对路径有假设也不会错位。
        /// </summary>
        private static string ExtractInstallerEntry(string installerJar, string entryName, string extractDir)
        {
            var target = Path.Combine(extractDir, entryName.Replace('/', Path.DirectorySeparatorChar));

            using var zip = ZipFile.OpenRead(installerJar);
            var entry = zip.GetEntry(entryName);

            if (entry is null)
            {
                throw new ModpackException(
                    $"加载器安装包里缺少 {entryName}（安装包可能不完整，请重新安装）");
            }

            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            entry.ExtractToFile(target, overwrite: true);
            Serilog.Log.Debug("已从安装包解出 {Entry} → {Path}", entryName, target);

            return target;
        }

        /// <summary>把 <c>group:artifact:version[:classifier][@ext]</c> 转成 libraries/ 下的绝对路径。</summary>
        private static string LibraryPath(string mcFolder, string maven)
        {
            var (_, relative) = MavenToRelative(maven);
            return Path.Combine(mcFolder, "libraries", relative.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>
        /// maven 坐标 → libraries/ 下的相对路径。
        /// 支持 <c>g:a:v</c>、<c>g:a:v:classifier</c>、<c>g:a:v@ext</c>、<c>g:a:v:cls@ext</c>。
        /// </summary>
        private static (string Name, string Relative) MavenToRelative(string maven)
        {
            var ext = "jar";
            var at = maven.IndexOf('@');
            if (at >= 0)
            {
                ext = maven[(at + 1)..];
                maven = maven[..at];
            }

            var coords = maven.Split(':');
            var group = coords.Length > 0 ? coords[0] : "";
            var artifact = coords.Length > 1 ? coords[1] : "";
            var version = coords.Length > 2 ? coords[2] : "";
            var classifier = coords.Length > 3 ? coords[3] : null;

            var file = classifier is null
                ? $"{artifact}-{version}.{ext}"
                : $"{artifact}-{version}-{classifier}.{ext}";
            var relative = $"{group.Replace('.', '/')}/{artifact}/{version}/{file}";
            return (maven, relative);
        }

        /// <summary>
        /// 占位符替换：先换 <c>{VAR}</c>（来自 data 与固定表），再把结果里的
        /// <c>[maven]</c> / <c>'literal'</c> 归一化。
        ///
        /// <para>
        /// <b>裸 <c>[maven]</c> 必须解析</b>（1.0.8.10）：NeoForge 1.21.1 的清单里有这样一条
        /// <c>args</c>，注意它是**直接写死在 args 里**的，不是走 <c>{KEY}</c>：
        /// </para>
        /// <code>
        /// --task MCP_DATA --input [net.neoforged:neoform:1.21.1-20240808.144430@zip] ...
        /// </code>
        /// <para>
        /// 以前这里刻意「保留原样，让 java 报错更直观」—— 结果就是 java 收到一个字面量
        /// <c>[net.neoforged:...]</c>，当成文件名打开、报找不到文件，MCP_DATA 这一步直接失败。
        /// NeoForge 安装因此**必然**卡在第 4 个处理器上。官方 installer 的规则很明确：
        /// 参数整段是 <c>[a:b:c]</c> 就换成 <c>libraries/</c> 下的绝对路径。
        /// </para>
        /// </summary>
        private static string Substitute(
            string raw, Dictionary<string, string> data, Dictionary<string, string> replacements, string mcFolder)
        {
            var s = raw;

            // 第一轮：{VAR}
            foreach (var (k, v) in replacements)
                s = s.Replace(k, v, StringComparison.Ordinal);
            foreach (var (k, v) in data)
                s = s.Replace("{" + k + "}", v, StringComparison.Ordinal);

            // 第二轮：args 里直接写死的 [maven] / 'literal'
            if (s.Length > 1 && s[0] == '\'' && s[^1] == '\'')
            {
                s = s[1..^1];
            }
            else if (s.Length > 2 && s[0] == '[' && s[^1] == ']')
            {
                // 整段都是 maven 坐标才替换；带空格的参数（如 "--input [x]"）不在本分支，
                // 因为 JSON 里每个参数是独立一项，不会混在一起。
                var coordinate = s[1..^1];
                if (coordinate.Contains(':'))
                    s = LibraryPath(mcFolder, coordinate);
            }

            return s;
        }

        /// <summary>
        /// 跑一个 processor：<c>java [-cp classpath] mainClass args…</c>。
        /// 部分 processor 用 <c>jar</c> 作为可执行 jar（无 mainClass），退化成 <c>java -jar</c>。
        /// </summary>
        /// <param name="onHeartbeat">
        /// 每 2 秒回调一次「已运行时长」。处理器本身可能长时间不产出输出，
        /// 界面靠它来区分「在算」和「死了」。
        /// </param>
        private static async Task RunJavaAsync(
            string javaPath, string? mainClass, string classpath, List<string> args, string workingDir,
            CancellationToken ct, Action<TimeSpan>? onHeartbeat = null)
        {
            var psi = new ProcessStartInfo
            {
                FileName = javaPath,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            if (!string.IsNullOrEmpty(mainClass))
            {
                psi.ArgumentList.Add("-cp");
                psi.ArgumentList.Add(classpath);
                psi.ArgumentList.Add(mainClass);
            }
            else
            {
                // 没有 mainClass：Main-Class 写在 jar 的 manifest 里，但**不能简单用 -jar**。
                //
                // ⚠️ 这是 Forge 26.x 安装失败的真正根因（2026-10-20 实测）：
                // 新版 install_profile 的二进制打补丁 processor 不再显式给 mainClass，
                // 而它的 jar 不是 fat jar —— jopt-simple / lzma-java / javaxdelta 等
                // 全在 processor.classpath 里。用 `-jar` 启动等于把整个 classpath 丢掉，
                // 第一行就报：
                //     NoClassDefFoundError: joptsimple/OptionException
                // 结果 client.jar 永远生成不出来，安装要么失败，要么留下一个
                // 「装好了但缺 forge-<mc>-<ver>-client.jar」的实例（启动时 FML 报
                // The patched Minecraft jar is missing）。
                //
                // 正确姿势与官方 installer 一致：从 manifest 读 Main-Class，
                // 用 `-cp <jar + classpath> <Main-Class>` 启动。
                var first = classpath.Split(Path.PathSeparator).FirstOrDefault() ?? string.Empty;
                var mainFromManifest = ReadMainClassFromJar(first);

                if (string.IsNullOrWhiteSpace(mainFromManifest))
                {
                    // 连 manifest 都没有 Main-Class：只能退回 -jar（真正可执行的 fat jar）
                    psi.ArgumentList.Add("-jar");
                    psi.ArgumentList.Add(first);
                }
                else
                {
                    psi.ArgumentList.Add("-cp");
                    psi.ArgumentList.Add(classpath);   // classpath 第一项就是 jar 自身
                    psi.ArgumentList.Add(mainFromManifest);
                }
            }

            foreach (var a in args)
                psi.ArgumentList.Add(a);

            using var proc = new Process { StartInfo = psi };
            var stdout = new System.Text.StringBuilder();
            var stderr = new System.Text.StringBuilder();

            // 最后一次「有输出」的时刻 —— 挂死判据看的是它，不是总时长：
            // 有的处理器确实要跑几分钟，只要还在打日志就不该被杀。
            var lastOutputTicks = Environment.TickCount64;

            void Append(System.Text.StringBuilder sb, string? line)
            {
                if (line is null) return;
                lock (sb) sb.AppendLine(line);
                Interlocked.Exchange(ref lastOutputTicks, Environment.TickCount64);
            }

            proc.OutputDataReceived += (_, e) => Append(stdout, e.Data);
            proc.ErrorDataReceived += (_, e) => Append(stderr, e.Data);

            if (!proc.Start())
                throw new ModpackException("无法启动 Java 进程（检查「设置 → 启动」里的 Java 路径）");

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            var clock = System.Diagnostics.Stopwatch.StartNew();

            if (onHeartbeat is not null)
            {
                _ = Task.Run(async () =>
                {
                    while (!HasExitedSafe(proc) && !ct.IsCancellationRequested)
                    {
                        try
                        {
                            await Task.Delay(2000, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }

                        if (HasExitedSafe(proc)) return;

                        try { onHeartbeat(clock.Elapsed); }
                        catch { /* 上报失败不影响安装 */ }
                    }
                }, ct);
            }

            // 挂死兜底：连续 12 分钟一行输出都没有 → 判定这个处理器僵死。
            // 官方 installer 遇到这种情况也是干等，用户只能杀进程；我们主动收尾并给出可操作提示。
            // 阈值放得很宽，是因为 installer-tools 处理大 jar 时确实会安静一阵。
            var stallTimeout = TimeSpan.FromMinutes(12);
            _ = Task.Run(async () =>
            {
                while (!HasExitedSafe(proc) && !ct.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(5000, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    if (HasExitedSafe(proc)) return;

                    if (Environment.TickCount64 - Interlocked.Read(ref lastOutputTicks)
                        > (long)stallTimeout.TotalMilliseconds)
                    {
                        Serilog.Log.Error(
                            "加载器安装处理器 {Main} 连续 {Min} 分钟无输出，判定为僵死，已终止",
                            mainClass ?? Path.GetFileName(classpath), stallTimeout.TotalMinutes);
                        TryKill(proc);
                        return;
                    }
                }
            }, ct);

            try
            {
                await proc.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                TryKill(proc);
                throw;
            }
            finally
            {
                clock.Stop();
                // 不 await 这两个后台任务：它们靠 ct / HasExited 自行退出，
                // 在这里等待只会给「取消」路径添一段无谓的延迟。
            }

            if (proc.ExitCode != 0)
            {
                var tail = stderr.ToString().Trim();
                if (tail.Length == 0)
                    lock (stdout) tail = stdout.ToString().Trim();
                if (tail.Length > 600) tail = tail[^600..];

                // 被杀掉的情况单独给一句话，避免用户看到 -1 退出码一头雾水
                var hint = Environment.TickCount64 - Interlocked.Read(ref lastOutputTicks)
                           > (long)stallTimeout.TotalMilliseconds
                    ? $"处理器连续 {stallTimeout.TotalMinutes} 分钟没有任何输出，已判定为卡死并终止。"
                      + "多为依赖库不完整或网络被墙导致处理器内部下载挂起，请切换下载源后重试。"
                    : $"加载器安装处理器执行失败（退出码 {proc.ExitCode}）。";

                Serilog.Log.Error("加载器安装处理器失败（退出码 {Code}）：{Err}", proc.ExitCode, tail);
                throw new ModpackException(
                    hint + (string.IsNullOrWhiteSpace(tail) ? "" : $"Java 输出：{tail}"));
            }
        }

        private static async Task<JsonNode?> ReadNodeAsync(ZipArchiveEntry entry, CancellationToken ct)
        {
            await using var stream = entry.Open();
            return await JsonNode.ParseAsync(stream, cancellationToken: ct);
        }

        // ==================== 小工具 ====================

        private static string ShortName(string name)
        {
            var slash = name.LastIndexOf('/');
            return slash >= 0 ? name[(slash + 1)..] : name;
        }

        /// <summary>
        /// 校验「这个文件真的是一份加载器安装器」。
        ///
        /// <para>
        /// <b>不能只看魔数</b>：实测从 BMCLAPI 的 302 目标站下载时，连接会在 450 KB 左右
        /// 悄悄断掉（服务端不报错、HTTP 也已经是 200），文件**开头仍是 <c>PK\x03\x04</c>**
        /// —— 只看头 4 字节会把它当成合法 jar，然后崩在 <c>ZipFile.OpenRead</c> 上。
        /// 必须让 ZipArchive 真正去读**中央目录**（在文件末尾）才能识别截断。
        /// </para>
        ///
        /// <para>
        /// 返回失败原因（null 表示通过），由调用方拼成一句可操作的提示。
        /// </para>
        /// </summary>
        private static string? ValidateInstaller(string path)
        {
            try
            {
                using var zip = ZipFile.OpenRead(path);

                var hasVersion = zip.GetEntry("version.json") is not null;
                var hasProfile = zip.GetEntry("install_profile.json") is not null;

                // 1.12 及以前的老 Forge 只有 install_profile.json，没有 version.json，
                // 那种情况在后面会给出「暂不支持」的专门提示，这里不算「文件坏了」。
                if (!hasProfile && !hasVersion)
                    return "压缩包里没有 version.json / install_profile.json，不是有效的加载器安装器";

                return null;
            }
            catch (InvalidDataException)
            {
                // 中央目录读不到 —— 截断、或者是 HTML 错误页
                return "文件不完整（下载被中途截断，或下载源返回了错误页面）";
            }
            catch (IOException ex)
            {
                return $"文件无法读取（{ex.Message}）";
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* 留着也无妨 */ }
        }

        private static void TryKill(Process proc)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
        }
    }
}
