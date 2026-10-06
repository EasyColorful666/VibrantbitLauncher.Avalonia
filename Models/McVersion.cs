namespace VibrantbitLauncher.Models
{
    /// <summary>
    /// 下载页展示用的版本条目（从安装清单映射而来）。
    /// </summary>
    public class McVersion
    {
        public string Version { get; set; } = string.Empty;
        public string Date { get; set; } = string.Empty;

        /// <summary>原始清单里的次序（版本清单是新的在前）。搜索排序时用作稳定的次级键。</summary>
        public int Index { get; set; }
    }
}
