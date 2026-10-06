using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MinecraftLaunch.Base.Models.Network;
using MinecraftLaunch.Components.Installer;

namespace VibrantbitLauncher.Services
{
    /// <summary>
    /// Minecraft 版本清单的进程内缓存。
    ///
    /// Mojang 的版本清单每次访问都要走一次 HTTPS，而且启动器里有多处（下载页、模组页的版本筛选、
    /// 安装器）都要用它。原先每个页面各自请求一次，导致点「下载中心」要等几秒才出列表。
    /// 这里合并成一次请求、全进程共享，并由 <see cref="Preload"/> 在主窗口显示后预热。
    /// </summary>
    public static class MinecraftVersionCache
    {
        private static readonly object SyncRoot = new();
        private static Task<List<VersionManifestEntry>>? _work;

        /// <summary>获取版本清单（首次调用发起请求，之后复用同一结果）。</summary>
        public static Task<List<VersionManifestEntry>> GetAsync()
        {
            lock (SyncRoot)
            {
                _work ??= Task.Run(async () =>
                {
                    var entries = await VanillaInstaller.EnumerableMinecraftAsync();
                    return entries.ToList();
                });
            }

            return _work;
        }

        /// <summary>后台预热，失败不影响后续调用（下次会重新发起）。</summary>
        public static void Preload()
        {
            _ = GetAsync().ContinueWith(
                t => { if (t.IsFaulted) Reset(); },
                TaskScheduler.Default);
        }

        private static void Reset()
        {
            lock (SyncRoot)
            {
                _work = null;
            }
        }
    }
}
