using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace VibrantbitLauncher.Services;

/// <summary>
/// 资源简介的英译中。
///
/// <para>
/// Modrinth 的 <c>summary</c> 基本都是英文（"A mod that adds …"），一屏二十张卡片全是英文
/// 对中文用户没什么信息量。这里在列表加载后**后台逐条翻译**并回填，
/// 翻不出来就保留原文，不阻塞列表、不弹错误。
/// </para>
///
/// <para>
/// <b>为什么不用大模型 / 需要密钥的 API</b>：这是个纯展示功能，用户没配置任何 key 也应该能用。
/// 所以走两个免密钥端点（MyMemory → Google 的 gtx 端点），失败就放弃这一条。
/// 两个端点都在公网，国内可达性不保证 —— 所以翻译永远是「尽力而为」，不是功能依赖。
/// </para>
///
/// <para>
/// 缓存按原文做 key：翻页来回滚动时不会重复打同一个请求。
/// </para>
/// </summary>
public static class TranslateService
{
    /// <summary>单段最大字符数。两个端点对长文本都不友好，切句后分别翻再拼。</summary>
    private const int MaxChunkLength = 400;

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(12),
    };

    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);

    /// <summary>同时在飞的请求数上限：免费端点会对并发限流，打太猛只会全部失败。</summary>
    private static readonly SemaphoreSlim Gate = new(3, 3);

    /// <summary>
    /// 这段文本「像英文吗」——即：一个汉字都没有，且至少有 3 个拉丁字母。
    /// 已经含中文的简介不去翻（避免把中文条目翻译成另一种中文）。
    /// </summary>
    public static bool LooksEnglish(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var letters = 0;
        foreach (var c in text)
        {
            if (c is >= '\u4e00' and <= '\u9fff')
                return false;
            if (c < 128 && char.IsLetter(c))
                letters++;
        }

        return letters >= 3;
    }

    /// <summary>
    /// 翻译成简体中文。返回 <c>null</c> 表示「不用翻 / 翻不出来」，调用方保留原文。
    /// </summary>
    public static async Task<string?> TranslateAsync(string? text, CancellationToken ct = default)
    {
        if (!LooksEnglish(text))
            return null;

        var source = text!.Trim();
        if (Cache.TryGetValue(source, out var cached))
            return cached;

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var translated = await TranslateChunkedAsync(source, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(translated))
                return null;

            Cache[source] = translated;
            return translated;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 翻译失败不影响任何主流程，记一条 Debug 就够
            Log.Debug(ex, "简介翻译失败：{Text}", source.Length > 40 ? source[..40] : source);
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<string?> TranslateChunkedAsync(string text, CancellationToken ct)
    {
        var chunks = SplitIntoChunks(text);
        if (chunks.Count == 0)
            return null;

        // 单段：直接返回，省一次拼接
        if (chunks.Count == 1)
            return await TranslateOneAsync(chunks[0], ct).ConfigureAwait(false);

        var builder = new StringBuilder();
        foreach (var chunk in chunks)
        {
            var part = await TranslateOneAsync(chunk, ct).ConfigureAwait(false);
            if (part is null)
                return null;   // 有一段失败就整条放弃，避免出现「半中文半英文」

            builder.Append(part);
        }

        return builder.ToString();
    }

    /// <summary>先试 MyMemory，再试 Google 的免密钥端点。</summary>
    private static async Task<string?> TranslateOneAsync(string chunk, CancellationToken ct)
    {
        var byMemory = await TryMyMemoryAsync(chunk, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(byMemory))
            return byMemory;

        return await TryGoogleAsync(chunk, ct).ConfigureAwait(false);
    }

    private static async Task<string?> TryMyMemoryAsync(string chunk, CancellationToken ct)
    {
        try
        {
            var url = "https://api.mymemory.translated.net/get?langpair=en|zh-CN&q="
                      + Uri.EscapeDataString(chunk);

            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("responseData", out var data) ||
                !data.TryGetProperty("translatedText", out var translated))
            {
                return null;
            }

            var value = translated.GetString();
            // 配额用完时它会把原文原样返回，或返回一段 "MYMEMORY WARNING" 提示
            if (string.IsNullOrWhiteSpace(value) ||
                value.Contains("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, chunk, StringComparison.Ordinal))
            {
                return null;
            }

            return value;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> TryGoogleAsync(string chunk, CancellationToken ct)
    {
        try
        {
            var url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=en&tl=zh-CN&dt=t&q="
                      + Uri.EscapeDataString(chunk);

            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            // 形如 [[["译文","原文",null,null,10]],null,"en",...]
            if (doc.RootElement.ValueKind != JsonValueKind.Array ||
                doc.RootElement.GetArrayLength() == 0 ||
                doc.RootElement[0].ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var builder = new StringBuilder();
            foreach (var segment in doc.RootElement[0].EnumerateArray())
            {
                if (segment.ValueKind == JsonValueKind.Array &&
                    segment.GetArrayLength() > 0 &&
                    segment[0].ValueKind == JsonValueKind.String)
                {
                    builder.Append(segment[0].GetString());
                }
            }

            var result = builder.ToString();
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>按句子切块（尽量在句号 / 换行处断开），保证每块不超过 <see cref="MaxChunkLength"/>。</summary>
    private static List<string> SplitIntoChunks(string text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return result;

        if (text.Length <= MaxChunkLength)
        {
            result.Add(text);
            return result;
        }

        var current = new StringBuilder();

        foreach (var piece in SplitSentences(text))
        {
            // 单句本身超长：硬切
            if (piece.Length > MaxChunkLength)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }

                for (var i = 0; i < piece.Length; i += MaxChunkLength)
                    result.Add(piece.Substring(i, Math.Min(MaxChunkLength, piece.Length - i)));

                continue;
            }

            if (current.Length + piece.Length > MaxChunkLength)
            {
                result.Add(current.ToString());
                current.Clear();
            }

            current.Append(piece);
        }

        if (current.Length > 0)
            result.Add(current.ToString());

        return result.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
    }

    /// <summary>把文本切成句子（保留标点与空格），不追求语言学正确，只要切点合理。</summary>
    private static IEnumerable<string> SplitSentences(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var isBreak = c is '.' or '!' or '?' or ';' or '\n';
            if (!isBreak)
                continue;

            // 句号后面紧跟数字（版本号、小数）时不算断句
            if (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1]))
                continue;

            var end = i + 1;
            while (end < text.Length && char.IsWhiteSpace(text[end]) && text[end] != '\n')
                end++;

            yield return text[start..end];
            start = end;
            i = end - 1;
        }

        if (start < text.Length)
            yield return text[start..];
    }
}
