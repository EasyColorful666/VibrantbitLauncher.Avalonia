namespace VibrantbitLauncher.Models
{
    /// <summary>
    /// 请求下载中心切换到指定 Tab（0 = 游戏版本，1 = 模组资源）。
    /// CommunityToolkit.Mvvm 的消息必须是引用类型，因此不再用裸 int 传递。
    /// </summary>
    public sealed record DownloadHubTabMessage(int Index);

    /// <summary>
    /// 背景图变更通知（设置页 → 主窗口）。
    /// ImagePath 为空表示恢复纯 Mica 底；可以是本地绝对路径，也可以是内置壁纸的资源 URI。
    /// Opacity 为 0.05 ~ 1.0 的显示不透明度。
    /// </summary>
    public sealed record BackgroundChangedMessage(string? ImagePath, double Opacity);
}
