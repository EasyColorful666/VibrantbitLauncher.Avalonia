using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
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
            Serilog.Log.Information("{Loader} 安装器：{Url}", loaderName, installerUrl);

            onProgress(5, $"下载 {loaderName} 安装器");
            // 上一次失败留下的半截安装器会被内核的「文件已存在」判据当成下好了，
            // 随后崩在 ZipFile.OpenRead —— 先清掉，9 MB 重下比排错便宜。
            TryDelete(installerJar);
            // 安装器有 8~9 MB，慢速链路（代理/镜像限速）下整体下完可能远超 5 分钟，
            // 但只要一直在收字节就不该判失败 —— 用空闲超时兜底「彻底卡死」的那次。
            await FabricDownloader.DownloadFileAsync(
                installerUrl, installerJar, TimeSpan.FromMinutes(5), ct,
                idleTimeout: TimeSpan.FromSeconds(90));

            if (!File.Exists(installerJar) || new FileInfo(installerJar).Length == 0)
                throw new ModpackException($"{loaderName} 安装器下载失败（文件为空）");

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

            // 合并 install_profile.libraries：这些是跑 processor 需要的工具库，
            // 官方 installer 也会把它们写进版本 JSON，缺了会在启动/处理器阶段报 ClassNotFound。
            var allLibraries = new List<JsonNode>();
            CollectLibraries(versionJson["libraries"], allLibraries);
            CollectLibraries(profileJson["libraries"], allLibraries);
            // 同 maven 坐标去重（版本 JSON 和 install_profile 常有重叠）
            var deduped = DedupLibraries(allLibraries);
            versionJson["libraries"] = new JsonArray(deduped.Select(x => x.DeepClone()).ToArray());

            File.WriteAllText(clientJsonPath, versionJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Serilog.Log.Information("{Loader} 版本文件已写入 {Path}", loaderName, clientJsonPath);

            // ---------- 5. 下载依赖库 ----------
            onProgress(22, $"下载 {loaderName} 依赖库");
            await DownloadLibrariesAsync(mcFolder, deduped, p =>
                onProgress(22 + (int)(p * 0.33), $"下载 {loaderName} 依赖库"), ct);

            // ---------- 6. 落 client.lzma（processor 要用） ----------
            var forgeVerDir = Path.Combine(mcFolder, "libraries", "net", "minecraftforge", "forge", $"{mcVersion}-{picked}");
            Directory.CreateDirectory(forgeVerDir);
            var clientData = zip.GetEntry("data/client.lzma");
            if (clientData is not null)
                clientData.ExtractToFile(Path.Combine(forgeVerDir, $"forge-{mcVersion}-{picked}-clientdata.lzma"), overwrite: true);

            // ---------- 7. 跑 processors ----------
            var processors = profileJson["processors"] as JsonArray;
            if (processors is { Count: > 0 })
            {
                onProgress(56, $"运行 {loaderName} 安装处理器");
                await RunProcessorsAsync(
                    mcFolder, javaPath, installerJar, mcVersion, picked, neoForge,
                    profileJson, processors, forgeVerDir,
                    p => onProgress(56 + (int)(p * 0.42), $"运行 {loaderName} 安装处理器"), ct);
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

            var json = await FabricDownloader.GetStringAsync(
                url, TimeSpan.FromSeconds(20), 2, "NeoForge 版本列表（官方）", null, ct);

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

        // ==================== 库下载 ====================

        private static void CollectLibraries(JsonNode? source, List<JsonNode> into)
        {
            if (source is not JsonArray arr) return;
            foreach (var item in arr)
                if (item is not null) into.Add(item);
        }

        /// <summary>按 maven 坐标去重，保留先出现的（版本 JSON 的优先）。</summary>
        private static List<JsonNode> DedupLibraries(List<JsonNode> libs)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<JsonNode>();
            foreach (var lib in libs)
            {
                var name = lib["name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(name)) continue;
                if (seen.Add(name!)) result.Add(lib);
            }
            return result;
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
            string mcFolder, List<JsonNode> libraries, Action<double> onProgress, CancellationToken ct)
        {
            var libsDir = Path.Combine(mcFolder, "libraries");
            var items = new List<DownloadItem>(libraries.Count);

            foreach (var lib in libraries)
            {
                ct.ThrowIfCancellationRequested();

                var name = lib["name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(name))
                    continue;

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
                parallelism: 4,
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
            string forgeVerDir,
            Action<double> onProgress,
            CancellationToken ct)
        {
            // data 段：把 {KEY} 解析成真实值（processor 的 args 会引用它们）
            var data = ResolveData(profile["data"], mcFolder, mcVersion, forgeVersion);

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
                        onProgress((double)index / total);
                        continue;
                    }
                }

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
                        args.Add(Substitute(raw, data, replacements));
                    }
                }

                var mainClass = proc["mainClass"]?.GetValue<string>();
                var cpString = string.Join(Path.PathSeparator, classpath);

                Serilog.Log.Information("运行 {Loader} 处理器 {Index}/{Total}：{Jar} 参数 {Args}",
                    neoForge ? "NeoForge" : "Forge", index + 1, total, jarName, string.Join(" ", args.Take(6)));

                await RunJavaAsync(javaPath, mainClass, cpString, args, mcFolder, ct);

                index++;
                onProgress((double)index / total);
            }
        }

        /// <summary>把 <c>data</c> 段解析成「KEY → 真实值」。<c>[maven]</c> 转成本地路径，<c>'literal'</c> 去掉引号。</summary>
        private static Dictionary<string, string> ResolveData(
            JsonNode? data, string mcFolder, string mcVersion, string forgeVersion)
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
                // BINPATCH 这类以 / 开头的归档内路径 —— 指向安装器 jar 内的条目，
                // 官方 installer 会先解出来；这里保留原样，由 processor 的 --clean 参数处理。
                else
                {
                    result[key] = t;
                }
            }

            return result;
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

        /// <summary>占位符替换：先换 <c>{VAR}</c>（来自 data 与固定表），再对结果里出现的 <c>[maven]</c>/<c>'literal'</c> 兜一遍。</summary>
        private static string Substitute(string raw, Dictionary<string, string> data, Dictionary<string, string> replacements)
        {
            var s = raw;

            // 第一轮：{VAR}
            foreach (var (k, v) in replacements)
                s = s.Replace(k, v, StringComparison.Ordinal);
            foreach (var (k, v) in data)
                s = s.Replace("{" + k + "}", v, StringComparison.Ordinal);

            // 第二轮：args 里直接写死了 [maven] / 'literal'
            if (s.Length > 1 && s[0] == '\'' && s[^1] == '\'')
                s = s[1..^1];
            else if (s.Length > 1 && s[0] == '[' && s[^1] == ']')
            {
                // 此时没法再拿 mcFolder，只能靠调用方保证 args 里以 [ 开头的已被 data 覆盖。
                // Forge 的客户端 processor 不会把裸 [maven] 写进 args（都走 {KEY}），
                // 所以走到这里基本都是解析异常 —— 保留原样让 java 报错更直观。
            }

            return s;
        }

        /// <summary>
        /// 跑一个 processor：<c>java [-cp classpath] mainClass args…</c>。
        /// 部分 processor 用 <c>jar</c> 作为可执行 jar（无 mainClass），退化成 <c>java -jar</c>。
        /// </summary>
        private static async Task RunJavaAsync(
            string javaPath, string? mainClass, string classpath, List<string> args, string workingDir, CancellationToken ct)
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
                // 没有 mainClass：把 classpath 第一项当成可执行 jar
                var first = classpath.Split(Path.PathSeparator).FirstOrDefault() ?? "";
                psi.ArgumentList.Add("-jar");
                psi.ArgumentList.Add(first);
            }

            foreach (var a in args)
                psi.ArgumentList.Add(a);

            using var proc = new Process { StartInfo = psi };
            var stdout = new System.Text.StringBuilder();
            var stderr = new System.Text.StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (stdout) stdout.AppendLine(e.Data); } };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (stderr) stderr.AppendLine(e.Data); } };

            if (!proc.Start())
                throw new ModpackException("无法启动 Java 进程（检查「设置 → 启动」里的 Java 路径）");

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            try
            {
                await proc.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                TryKill(proc);
                throw;
            }

            if (proc.ExitCode != 0)
            {
                var tail = stderr.ToString().Trim();
                if (tail.Length > 600) tail = tail[^600..];
                Serilog.Log.Error("加载器安装处理器失败（退出码 {Code}）：{Err}", proc.ExitCode, tail);
                throw new ModpackException(
                    $"加载器安装处理器执行失败（退出码 {proc.ExitCode}）。"
                    + (string.IsNullOrWhiteSpace(tail) ? "" : $"Java 输出：{tail}"));
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
