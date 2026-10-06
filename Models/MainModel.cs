using MinecraftLaunch.Base.Models.Authentication;
using MinecraftLaunch.Base.Models.Game;

namespace VibrantbitLauncher.Models
{
    /// <summary>
    /// 应用运行时共享状态（当前选中的账户、Java、版本等）。
    /// </summary>
    public class MainModel
    {
        public string? MinecraftFolder { get; set; } = @"./.minecraft";
        public string? JavaPath { get; set; }
        public JavaEntry? Java { get; set; }
        public string? McVersion { get; set; }
        public Account? Account { get; set; }
        public bool IsMicrosoftAccount { get; set; }
        public List<MinecraftEntry> minecrafts { get; set; } = new List<MinecraftEntry>();
        public List<string> minecraftVersions { get; set; } = new List<string>();
    }
}
