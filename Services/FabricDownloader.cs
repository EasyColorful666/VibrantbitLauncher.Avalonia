using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MinecraftLaunch.Base.Models.Game;
using MinecraftLaunch.Components.Parser;

namespace VibrantbitLauncher.Services
{
    /// <summary>
    /// 自实现 Fabric 下载安装器，绕过 MinecraftLaunch 库的 FabricInstaller / MinecraftResourceDownloader 的 bug
    /// </summary>
    public class FabricDownloader
    {
        private static readonly HttpClient _http = new();

        /// <summary>
        /// 获取指定 MC 版本可用的 Fabric Loader 版本列表
        /// </summary>
        public static async Task<List<FabricLoaderInfo>> EnumerableFabricAsync(string mcVersion)
        {
            var url = $"https://meta.fabricmc.net/v2/versions/loader/{mcVersion}";
            var json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);

            var result = new List<FabricLoaderInfo>();
            foreach (var elem in doc.RootElement.EnumerateArray())
            {
                var loader = elem.GetProperty("loader");
                result.Add(new FabricLoaderInfo
                {
                    Version = loader.GetProperty("version").GetString(),
                    Maven = loader.GetProperty("maven").GetString(),
                    Stable = loader.TryGetProperty("stable", out var s) && s.GetBoolean()
                });
            }
            return result;
        }

        /// <summary>
        /// 完整安装 Fabric：下载 profile JSON → 修复格式 → 保存 → 下载所有库
        /// </summary>
        /// <param name="mcFolder">.minecraft 根目录</param>
        /// <param name="mcVersion">Minecraft 版本（如 1.20.1）</param>
        /// <param name="fabricVersion">Fabric Loader 版本（如 0.15.0）</param>
        /// <param name="customId">版本实例 ID（如 1.20.1_fabric_0.15.0）</param>
        /// <param name="onProgress">进度回调 (percent, stepText)</param>
        /// <returns>安装后的 MinecraftEntry</returns>
        public static async Task<MinecraftEntry> InstallAsync(
            string mcFolder,
            string mcVersion,
            string fabricVersion,
            string customId,
            Action<int, string> onProgress = null)
        {
            onProgress?.Invoke(5, "下载 Fabric 版本信息");

            // 1. 下载 Fabric profile JSON
            var profileUrl = $"https://meta.fabricmc.net/v2/versions/loader/{mcVersion}/{fabricVersion}/profile/json";
            var json = await _http.GetStringAsync(profileUrl);

            // 2. 修复 JSON：DateTime 时区格式 +0000 → +00:00，id 改成 customId
            json = Regex.Replace(json, @"([+-]\d{2})(\d{2})""", "$1:$2\"");
            json = Regex.Replace(json, @"""id""\s*:\s*""[^""]*""", $"\"id\": \"{customId}\"");

            // 3. 保存 version JSON
            var versionDir = Path.Combine(mcFolder, "versions", customId);
            var jsonPath = Path.Combine(versionDir, customId + ".json");
            if (!Directory.Exists(versionDir))
                Directory.CreateDirectory(versionDir);
            await File.WriteAllTextAsync(jsonPath, json);

            onProgress?.Invoke(20, "版本信息已保存");

            // 4. 从 JSON 提取所有库的下载地址和路径
            var libraries = ExtractLibraries(json);

            // 5. 下载所有库到 .minecraft/libraries/
            var libsDir = Path.Combine(mcFolder, "libraries");
            if (!Directory.Exists(libsDir))
                Directory.CreateDirectory(libsDir);

            int done = 0;
            foreach (var lib in libraries)
            {
                var targetPath = Path.Combine(libsDir, lib.Path);
                var targetDir = Path.GetDirectoryName(targetPath);
                if (!Directory.Exists(targetDir))
                    Directory.CreateDirectory(targetDir);

                if (!File.Exists(targetPath))
                {
                    var data = await _http.GetByteArrayAsync(lib.Url);
                    await File.WriteAllBytesAsync(targetPath, data);
                }
                done++;
                int percent = 20 + (int)((double)done / libraries.Count * 75);
                onProgress?.Invoke(percent, $"下载库 {done}/{libraries.Count}");
            }

            onProgress?.Invoke(96, "解析版本");

            // 6. 用 MinecraftParser 解析已安装的版本
            var parser = new MinecraftParser(mcFolder);
            var entry = parser.GetMinecraft(customId);
            if (entry == null)
            {
                var all = parser.GetMinecrafts();
                entry = all.FirstOrDefault(v => v.Id == customId)
                    ?? all.FirstOrDefault(v => v.Id.Contains("fabric") && v.Id.Contains(mcVersion));
            }

            onProgress?.Invoke(100, "安装完成");
            return entry ?? throw new InvalidOperationException($"Fabric 安装完成但解析版本失败: {customId}");
        }

        /// <summary>
        /// 从 version JSON 中提取所有库的下载 URL 和相对路径
        /// </summary>
        private static List<(string Url, string Path)> ExtractLibraries(string json)
        {
            var result = new List<(string, string)>();
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("libraries", out var libsElem) ||
                libsElem.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var lib in libsElem.EnumerateArray())
            {
                string url = null;
                string path = null;

                // 优先用 downloads.artifact（vanilla 格式）
                if (lib.TryGetProperty("downloads", out var dl) &&
                    dl.TryGetProperty("artifact", out var art))
                {
                    if (art.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                        url = u.GetString();
                    if (art.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
                        path = p.GetString();
                }

                // 回退：maven 格式（url + name 拼接）
                if (url == null &&
                    lib.TryGetProperty("url", out var baseUrl) &&
                    lib.TryGetProperty("name", out var name))
                {
                    var parts = name.GetString().Split(':');
                    if (parts.Length >= 3)
                    {
                        var group = parts[0].Replace('.', '/');
                        var artifact = parts[1];
                        var ver = parts[2];
                        path = $"{group}/{artifact}/{ver}/{artifact}-{ver}.jar";
                        var b = baseUrl.GetString();
                        url = (b.EndsWith("/") ? b : b + "/") + path;
                    }
                }

                if (!string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(path))
                    result.Add((url, path));
            }

            return result;
        }
    }

    public class FabricLoaderInfo
    {
        public string Version { get; set; }
        public string Maven { get; set; }
        public bool Stable { get; set; }
        public string DisplayVersion => Version;
    }
}
