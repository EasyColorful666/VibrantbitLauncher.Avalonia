namespace VibrantbitLauncher.Models
{
    /// <summary>
    /// 一次「重装实例」的来源类型。
    ///
    /// <para>
    /// 配装码（<see cref="Loadout"/>）在内部就是打成临时 <c>.mrpack</c> 交给整合包安装器 ——
    /// 两条路径共用同一套代码。但对用户来说这是两件事，所以凡是会显示给用户的地方
    /// （任务列表的类型、进度窗口的标题、来源说明）都必须按这个枚举分开，
    /// 不能笼统地写成「整合包」。
    /// </para>
    /// </summary>
    public enum InstallKind
    {
        /// <summary>用户拖入 / 从下载中心安装的整合包。</summary>
        Modpack = 0,

        /// <summary>从「配装码」粘贴导入的分享码。</summary>
        Loadout
    }
}
