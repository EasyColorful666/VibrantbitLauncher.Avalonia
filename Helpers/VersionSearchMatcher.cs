using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

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
    /// <item>搜 <c>21</c> 会命中 <c>1.210</c> —— 数字段被切断，属于假命中。</item>
    /// <item>搜 <c>1.2</c> 时 <c>1.21.4</c> 排在 <c>1.2.5</c> 前面 —— 段对齐的应优先。</item>
    /// <item>搜 <c>rc1</c> 匹配不到 <c>rc-1</c>，搜 <c>pre2</c> 匹配不到 <c>pre-2</c> —— 连接符写法不统一。</item>
    /// <item>多关键词只能整串比对，<c>1.21 pre</c> 一个都搜不到。</item>
    /// </list>
    ///
    /// 这里把版本号和查询词都做一次 <see cref="Normalize"/>（统一小写、连接符归一），
    /// 再按「精确 → 段对齐前缀 → 前缀 → 段对齐子串 → 子串」分层打分，
    /// 既能容忍写法差异，又能把最相关的结果排在前面。
    ///
    /// <para>
    /// <b>数字段边界</b>：命中结束的位置如果后面还紧跟数字，说明这个数字段被拦腰截断了
    /// （<c>21</c> 之于 <c>1.210</c>），只给降级分而不是满分，避免假命中挤掉真命中。
    /// </para>
    /// </summary>
    public static class VersionSearchMatcher
    {
        /// <summary>查询词的分隔符（多关键词之间用空白隔开，AND 语义）。</summary>
        private static readonly char[] QuerySeparators = { ' ', '\t', '\u3000' };

        /// <summary>
        /// 打分：0 表示不匹配，分数越大越相关。
        /// 多关键词时取各词分数的平均值，避免词多导致分数虚高。
        /// </summary>
        public static int Score(string version, string query)
        {
            if (string.IsNullOrEmpty(version))
                return 0;

            if (string.IsNullOrWhiteSpace(query))
                return 1;

            var v = Normalize(version);
            if (v.Length == 0)
                return 1;

            var tokens = Tokenize(query);
            if (tokens.Count == 0)
                return 1;

            if (tokens.Count == 1)
                return ScoreToken(v, tokens[0]);

            // 多关键词：全部命中才算命中（AND），分数取平均
            var sum = 0;
            foreach (var token in tokens)
            {
                var s = ScoreToken(v, token);
                if (s == 0)
                    return 0;
                sum += s;
            }

            return sum / tokens.Count;
        }

        public static bool IsMatch(string version, string query) => Score(version, query) > 0;

        /// <summary>单个关键词的打分。</summary>
        private static int ScoreToken(string v, string q)
        {
            // ① 完全相同
            if (v == q)
                return 1000;

            // ② 整体前缀：段对齐（"1.2" → "1.2.5"）优于拦腰截断（"1.2" → "1.21.4"）
            if (v.StartsWith(q, StringComparison.Ordinal))
            {
                var aligned = EndsAtDigitBoundary(v, q.Length);
                return (aligned ? 850 : 720) - LengthPenalty(v, q);
            }

            // ③ 命中在段边界上（"21" → "1.21.4" 里的 .21 段）
            var boundaryIndex = IndexAtSegmentBoundary(v, q);
            if (boundaryIndex >= 0)
            {
                var aligned = EndsAtDigitBoundary(v, boundaryIndex + q.Length);
                var tier = aligned ? 640 : 560;
                return tier - Math.Min(boundaryIndex, 40);
            }

            // ④ 普通子串
            var index = v.IndexOf(q, StringComparison.Ordinal);
            if (index >= 0)
                return 400 - Math.Min(index, 80);

            return 0;
        }

        /// <summary>
        /// 把查询词按空白切成若干关键词。
        /// 逗号 / 分号不切 —— 版本号里不会出现，但玩家偶尔会连写，切开反而更困惑。
        /// </summary>
        private static List<string> Tokenize(string query)
        {
            var tokens = new List<string>();
            foreach (var raw in query.Split(QuerySeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                var normalized = Normalize(raw);
                if (normalized.Length > 0)
                    tokens.Add(normalized);
            }

            return tokens;
        }

        /// <summary>长度差惩罚：在**同一档内**让更贴近查询词的版本号排前面。</summary>
        private static int LengthPenalty(string v, string q)
            => Math.Min(Math.Abs(v.Length - q.Length), 40);

        /// <summary>
        /// 命中结束在 <paramref name="endIndex"/> 时，后面是否已经不是一个数字段
        /// （即结尾或后面是分隔符）。
        /// </summary>
        private static bool EndsAtDigitBoundary(string normalizedVersion, int endIndex)
        {
            if (endIndex >= normalizedVersion.Length)
                return true;

            return !char.IsDigit(normalizedVersion[endIndex]);
        }

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
                if (c == ' ' || c == '\t' || c == '\u3000')
                    continue;
                if (c == '_')
                    c = '.';
                chars.Add(c);
            }

            var s = new string(chars.ToArray());

            // "pre-2" → "pre2"，"rc-1" → "rc1"，"snapshot-10" → "snapshot10"
            // 规则：连接符前后，前面是字母、后面是数字时，把这个连接符吃掉。
            var result = new StringBuilder(s.Length);
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

        /// <summary>
        /// 查询词是否从某个「点分段的开头」开始（"21" 命中 "1.21.4" 的第二个段）。
        /// 返回该位置的字符下标，未命中返回 -1。
        /// </summary>
        private static int IndexAtSegmentBoundary(string normalizedVersion, string normalizedQuery)
        {
            var start = 0;
            while (true)
            {
                var index = normalizedVersion.IndexOf(normalizedQuery, start, StringComparison.Ordinal);
                if (index < 0)
                    return -1;

                if (index == 0 || normalizedVersion[index - 1] == '.'
                    || normalizedVersion[index - 1] == '-')
                    return index;

                start = index + 1;
            }
        }
    }
}
