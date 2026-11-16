namespace VibrantbitLauncher.Localization.Translations;

/// <summary>
/// 词表：更新窗口（选下载线路 / 进度 / 自动替换）。
///
/// <para>
/// 键必须与源码里的中文**逐字一致**：全角标点、「」、省略号「…」、行首空格都不能改，
/// 否则查不到就退回中文。带数字的句子写成 <c>{0}</c> 占位。
/// </para>
///
/// <para>
/// 「发现新版本」「稍后」「完成」「下载失败：{0}」这些键已经在别的词表里，
/// 这里不重复定义 —— 重复会互相覆盖，看不出哪份才是有效的。
/// </para>
/// </summary>
internal static class UiUpdate
{
    public static void AddTo(Dictionary<string, string> d)
    {
        // ===== 窗口正文 =====
        d["更新内容"] = "What's new";
        d["下载线路"] = "Download route";
        d["发现新版本 {0}"] = "Version {0} is available";
        d["当前版本 {0} · 发布于 {1} · 安装包 {2}"] =
            "Current {0} · released {1} · package {2}";
        d["这次发布没有写更新说明。"] = "This release has no notes.";
        d["这次发布没有适配当前系统的安装包，请到发布页手动下载。"] =
            "This release has no package for your system — download it from the release page.";
        d["打开下载页面"] = "Open download page";

        // ===== 三条线路 =====
        d["IPv4 加速（v4.gh-proxy.com）"] = "IPv4 accelerator (v4.gh-proxy.com)";
        d["经 IPv4 加速站转发 GitHub 链接，多数网络下最稳"] =
            "Routes the GitHub link through an IPv4 accelerator — usually the most stable";
        d["IPv4 / IPv6 加速（v6.gh-proxy.com）"] = "IPv4 / IPv6 accelerator (v6.gh-proxy.com)";
        d["需要网络支持 IPv6；走 IPv6 时通常更快"] =
            "Needs IPv6 on your network; usually faster when it works";
        d["直连 GitHub（github.com）"] = "GitHub directly (github.com)";
        d["不经第三方，网络能直连 GitHub 时用它"] =
            "No third party — use it when GitHub is reachable on its own";

        // ===== 状态 / 进度 =====
        d["点「下载并更新」开始。安装包 {0}，支持多线程分块与断点续传。"] =
            "Press “Download and update” to start. Package is {0}; multi-threaded chunks and resume are supported.";
        d["正在连接…"] = "Connecting…";
        d["正在校验安装包…"] = "Verifying the package…";
        d["正在解压安装包…"] = "Extracting the package…";
        d["正在重启启动器，几秒后会自动打开新版本…"] =
            "Restarting the launcher — the new version opens in a few seconds…";
        d["更新包已保存在 {0}"] = "The package is kept at {0}";
        d["{0}%（{1} / {2}）"] = "{0}% ({1} / {2})";
        d["已下载 {0}"] = "Downloaded {0}";
        d["已取消，已下载的部分会保留，下次接着下。"] =
            "Cancelled. What was downloaded is kept and resumes next time.";
        d["安装包校验不通过，可能在下载链路中被替换，已删除"] =
            "The package failed verification — it may have been swapped in transit and has been deleted";

        // ===== 自动替换 =====
        d["当前不是以启动器本体运行（例如开发环境），无法自动替换，请手动解压安装包"] =
            "Not running as the launcher itself (a dev build, for example), so it can't replace itself — extract the package manually";
        d["无法定位程序所在目录"] = "Couldn't locate the program folder";
        d["安装包里没有找到 {0}，更新已中止"] =
            "The package doesn't contain {0}; the update was stopped";

        // ===== 按钮 =====
        d["下载并更新"] = "Download and update";
        d["取消下载"] = "Cancel download";
        d["正在安装…"] = "Installing…";

        // ===== 下载内核的换源提示（{0}、{1} 是主机名）=====
        d["{0} 失败，改用 {1}"] = "{0} failed, switching to {1}";
    }
}
