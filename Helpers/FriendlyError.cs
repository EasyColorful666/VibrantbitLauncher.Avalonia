using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace VibrantbitLauncher.Helpers;

/// <summary>
/// 把异常翻成一句中文，避免把 .NET 的英文原文（最典型的是
/// <c>The operation was canceled.</c>）直接弹给用户。
///
/// 为什么要单独收口：项目里大量地方是「catch (Exception ex) → 弹 ex.Message」，
/// 而弱网/超时时抛出的 <see cref="TaskCanceledException"/> 的 Message 恰是那句英文，
/// 用户完全看不出是「网络超时」，只会以为程序坏了（2026-10-17 实测）。
/// </summary>
public static class FriendlyError
{
    /// <summary>取一句可读的中文原因；已经是我们自己包装过的异常则原样返回它的消息。</summary>
    public static string Describe(Exception? ex, string fallback = "未知错误")
    {
        if (ex is null)
            return fallback;

        // 递归取内层：很多地方会把真实原因塞进 InnerException
        if (ex is AggregateException agg && agg.InnerException is not null)
            return Describe(agg.InnerException, fallback);

        // 我们自己抛的异常，文案本来就是给人看的
        if (ex is Services.DownloadFailedException || ex is Services.ModpackException)
            return ex.Message;

        // 超时 / 取消：TaskCanceledException 也是 OperationCanceledException 的子类
        if (ex is OperationCanceledException or TimeoutException
            || ex.InnerException is TimeoutException or OperationCanceledException)
        {
            return "请求超时（网络太慢或代理不通），请检查网络后重试";
        }

        if (ex is JsonException)
            return "服务器返回的数据不完整（响应被中途截断），通常是网络不稳，请稍后重试";

        if (ex is HttpRequestException http)
        {
            if (http.StatusCode.HasValue)
            {
                var code = (int)http.StatusCode.Value;
                return code switch
                {
                    404 => "服务器返回 404（资源不存在）",
                    >= 500 => $"服务器返回 {code}（对方服务故障，通常稍后重试即可）",
                    _ => $"服务器返回 {code}",
                };
            }

            if (http.InnerException is not null
                && Describe(http.InnerException, string.Empty) is { Length: > 0 } inner
                && inner != fallback)
            {
                return $"网络错误：{inner}";
            }

            return "网络错误（连接被拒绝或中断），请检查网络 / 代理后重试";
        }

        return string.IsNullOrWhiteSpace(ex.Message) ? fallback : ex.Message;
    }
}
