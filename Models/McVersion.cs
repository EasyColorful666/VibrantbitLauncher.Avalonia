namespace VibrantbitLauncher.Models
{
    /// <summary>
    /// 下载页展示用的版本条目（从安装清单映射而来）。
    /// </summary>
    public class McVersion
    {
        public string Version { get; set; } = string.Empty;
        public string Date { get; set; } = string.Empty;

        /// <summary>
        /// 原始清单里的 <c>type</c> 字段（release / snapshot / old_beta / old_alpha）。
        /// 保留原文，是为了让分类只在一个地方做（<see cref="McVersionKindClassifier"/>），
        /// 而不是把判断散到各页面。
        /// </summary>
        public string Type { get; set; } = "release";

        /// <summary>页面筛选用的大类（正式 / 测试 / 远古 / 愚人节）。</summary>
        public McVersionKind Kind { get; set; } = McVersionKind.Release;

        /// <summary>
        /// 类型角标文案。正式版返回空串 —— 正式版是常态，每行都标反而是噪音。
        /// </summary>
        public string KindLabel => Kind switch
        {
            McVersionKind.Snapshot => "测试版",
            McVersionKind.Legacy => "远古版",
            McVersionKind.AprilFools => "愚人节",
            _ => string.Empty,
        };

        /// <summary>是否显示类型角标（正式版不显示）。</summary>
        public bool ShowKindLabel => Kind != McVersionKind.Release;

        /// <summary>原始清单里的次序（版本清单是新的在前）。搜索排序时用作稳定的次级键。</summary>
        public int Index { get; set; }
    }
}
