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

    /// <summary>
    /// 本地实例列表发生变化（装了整合包 / 装了新版本 / 删了版本）。
    /// 启动页收到后重新枚举实例，保证新实例立刻能选。
    /// </summary>
    public sealed record InstancesChangedMessage;

    /// <summary>
    /// 请求刷新下载页数据：社区资源列表（模组 / 整合包 / 数据包 / 资源包 / 光影）
    /// 与 Minecraft 原版版本列表。
    ///
    /// <para>
    /// 由主窗口在**程序运行后 1.3 秒**广播一次（让数据在用户点进来之前就先就绪）；
    /// 已经创建出来的下载页收到后各自重新拉一遍。页面还没创建也没关系 ——
    /// 进入页面时还有一次刷新兜底（见 <see cref="DownloadResourcesPage"/> /
    /// <see cref="DownloadPage"/> 的导航与分类切换处理）。
    /// </para>
    /// </summary>
    public sealed record RefreshDownloadDataMessage;
}
