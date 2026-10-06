using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace VibrantbitLauncher.Helpers
{
    /// <summary>
    /// 远程图片加载器：把 http(s) 图片 URL 拉成 Avalonia 的 <see cref="Bitmap"/>。
    ///
    /// <para>
    /// 为什么不能直接把 URL 字符串绑给 <c>Image.Source</c>：
    /// Avalonia 的 <c>Image.Source</c> 需要 <see cref="IImage"/>，字符串会被默认转换器当作
    /// **本地路径 / avares 资源**去解析；http(s) 这类网络地址根本走不到网络那一层，
    /// 解析失败后静默返回 null —— 界面表现就是「图片不显示」（模组图标、新闻配图均是此因）。
    /// </para>
    ///
    /// <para>
    /// 这里在后台线程用 <see cref="HttpClient"/> 取字节流再解码成 <see cref="Bitmap"/>，
    /// 解码结果缓存在内存里（同一个 URL 只下一次），并保证 <c>Bitmap</c> 在 UI 线程上创建
    /// （Avalonia 的部分后端要求位图在 UI 线程构造）。
    /// </para>
    /// </summary>
    public static class RemoteImageLoader
    {
        private static readonly HttpClient Http = new()
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        /// <summary>URL → 已解码位图的缓存。加载失败记 null，避免对坏链接反复重试。</summary>
        private static readonly ConcurrentDictionary<string, Bitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>正在进行中的加载，避免同一 URL 并发重复下载。</summary>
        private static readonly ConcurrentDictionary<string, Task<Bitmap?>> InFlight = new(StringComparer.OrdinalIgnoreCase);

        static RemoteImageLoader()
        {
            // 很多 CDN（含 cdn.modrinth.com）会校验 Referer / UA，带一个常规 UA 更稳。
            Http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "VibrantbitLauncher/1.0 (+https://github.com/vibrantbit)");
        }

        /// <summary>
        /// 异步取图。返回 null 表示地址为空 / 下载或解码失败（调用方直接忽略即可）。
        /// </summary>
        public static async Task<Bitmap?> LoadAsync(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;

            if (Cache.TryGetValue(url, out var cached))
                return cached;

            var task = InFlight.GetOrAdd(url, u => FetchAsync(u));
            try
            {
                return await task.ConfigureAwait(false);
            }
            finally
            {
                InFlight.TryRemove(url, out _);
            }
        }

        private static async Task<Bitmap?> FetchAsync(string url)
        {
            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    Cache[url] = null;
                    return null;
                }

                var bytes = await Http.GetByteArrayAsync(uri).ConfigureAwait(false);
                if (bytes.Length == 0)
                {
                    Cache[url] = null;
                    return null;
                }

                // Bitmap 的创建放到 UI 线程：部分后端（如 Windows 的 Direct2D）要求如此
                var bitmap = await Dispatcher.UIThread.InvokeAsync<Bitmap?>(() =>
                {
                    try
                    {
                        using var ms = new MemoryStream(bytes);
                        return new Bitmap(ms);
                    }
                    catch
                    {
                        return null;
                    }
                });

                Cache[url] = bitmap;
                return bitmap;
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "远程图片加载失败：{Url}", url);
                Cache[url] = null;
                return null;
            }
        }
    }
}
