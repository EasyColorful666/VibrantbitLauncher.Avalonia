using System;
using System.Collections.Generic;
using System.Linq;

namespace VibrantbitLauncher.Helpers
{
    /// <summary>
    /// Minecraft 版本号的搜索匹配。
    ///
    /// 版本号的写法很杂：正式版 <c>1.21.4</c> / <c>26.3</c>，预发布 <c>1.21.4-pre2</c> 或
    /// <c>26.3-pre-1</c>，候选 <c>1.21.4-rc1</c> / <c>26.3-rc-3</c>，周快照 <c>25w45a</c>，
    /// 开发快照 <c>26.3-snapshot-10</c>，远古版本 <c>a1.2.6</c> / <c>b1.7.3</c> / <c>rd-132211</c>。
    ///
    /// 单纯用 <c>Contains</c> 的问题：
    /// <list type="bullet">
    /// <item>搜 <c>1.21</c> 会连 <c>1.210</c>（若存在）一起命中 —— 版本号里 <c>.</c> 应该当作分隔边界。</item>
    /// <item>搜 <c>rc1</c> 匹配不到 <c>rc-1</c>，搜 <c>pre2</c> 匹配不到 <c>pre-2</c> —— 连接符写法不统一。</item>
    /// <item>命中结果没有排序，精确等于某个版本号的那条往往被淹没。</item>
    /// </list>
    ///
    /// 这里把版本号和查询词都做一次 <see cref="Normalize"/>（统一小写、连接符归一），
    /// 再按「精确 → 前缀 → 段前缀 → 子串」分层打分，既能容忍写法差异，又能把最相关的结果排在前面。
    /// </summary>
    public static class VersionSearchMatcher
    {
        /// <summary>
        /// 打分：0 表示不匹配，分数越大越相关。
        /// </summary>
        public static int Score(string version, string query)
        {
            if (string.IsNullOrEmpty(version))
                return 0;

            if (string.IsNullOrWhiteSpace(query))
                return 1;

            var v = Normalize(version);
            var q = Normalize(query);
            if (q.Length == 0)
                return 1;

            // ① 完全相同
            if (v == q)
                return 1000;

            // ② 整体前缀（"1.21" → "1.21.4"）
            if (v.StartsWith(q, StringComparison.Ordinal))
                return 800 - Math.Min(v.Length - q.Length, 80);

            // ③ 命中在「段边界」上（"21" → "1.21.4" 里的 .21 段）
            if (StartsAtSegmentBoundary(v, q))
                return 600 - Math.Min(v.IndexOf(q, StringComparison.Ordinal), 60);

            // ④ 普通子串
            var index = v.IndexOf(q, StringComparison.Ordinal);
            if (index >= 0)
                return 400 - Math.Min(index, 80);

            // ⑤ 查询词是多段（如 "26.3 rc"）时的「全部包含」兜底
            var tokens = q.Split(new[] { '.', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length > 1 && tokens.All(t => v.Contains(t, StringComparison.Ordinal)))
                return 200;

            return 0;
        }

        public static bool IsMatch(string version, string query) => Score(version, query) > 0;

        /// <summary>
        /// 归一化版本号/查询词：
        /// <list type="bullet">
        /// <item>统一小写；</item>
        /// <item>去掉空格；</item>
        /// <item>把 <c>_</c> 视为 <c>.</c>（远古版本 <c>c0.30_01c</c>）；</item>
        /// <item>把「字母段 + 分隔符 + 数字」里的分隔符去掉（<c>pre-2</c>/<c>pre2</c> 等价，<c>rc-1</c>/<c>rc1</c> 等价，<c>snapshot-10</c>/<c>snapshot10</c> 等价）。</item>
        /// </list>
        /// 这样 "rc1" 和 "rc-1"、"pre2" 和 "pre-2" 都能互相匹配。
        /// </summary>
        public static string Normalize(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            var chars = new List<char>(value.Length);
            foreach (var raw in value)
            {
                var c = char.ToLowerInvariant(raw);
                if (c == ' ' || c == '\t')
                    continue;
                if (c == '_')
                    c = '.';
                chars.Add(c);
            }

            var s = new string(chars.ToArray());

            // "pre-2" → "pre2"，"rc-1" → "rc1"，"snapshot-10" → "snapshot10"
            // 规则：连接符前后，前面是字母、后面是数字时，把这个连接符吃掉。
            var result = new System.Text.StringBuilder(s.Length);
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if ((c == '-' || c == '.')
                    && i > 0 && i + 1 < s.Length
                    && char.IsLetter(s[i - 1])
                    && char.IsDigit(s[i + 1]))
                {
                    continue;
                }
                result.Append(c);
            }

            return result.ToString();
        }

        /// <summary>查询词是否恰好从某个「点分段的开头」开始（"21" 命中 "1.21.4" 的第二个段）。</summary>
        private static bool StartsAtSegmentBoundary(string normalizedVersion, string normalizedQuery)
        {
            var start = 0;
            while (true)
            {
                var index = normalizedVersion.IndexOf(normalizedQuery, start, StringComparison.Ordinal);
                if (index < 0)
                    return false;

                if (index == 0 || normalizedVersion[index - 1] == '.')
                    return true;

                start = index + 1;
            }
        }
    }
}
