namespace VibrantbitLauncher.Models
{
    /// <summary>
    /// 通用的「下拉选项」：<see cref="Key"/> 是落盘到 settings.json 的值，
    /// <see cref="Display"/> 是界面上给用户看的名字。
    ///
    /// 让 Key 与 Display 分离，是为了以后改文案 / 做本地化时不至于把配置里已有的值写坏。
    /// </summary>
    public record SettingOption(string Key, string Display);

    /// <summary>内存档位选项（单位 MB）。</summary>
    public record MemoryOption(int Mb, string Display);
}
