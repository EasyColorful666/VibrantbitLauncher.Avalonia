namespace VibrantbitLauncher.Localization.Translations;

/// <summary>
/// 词表第一块：窗口按钮、账户卡片、下载中心与版本列表。
///
/// <para>
/// 键是界面上的<b>中文原文</b>，必须逐字一致（包括全角标点、「」和结尾的省略号）。
/// 带 <c>{0}</c> 的键按模式匹配，用来翻译运行时才拼出来的句子（如「安装版本 - 1.21」）。
/// </para>
/// </summary>
internal static class UiShell
{
    public static void AddTo(Dictionary<string, string> d)
    {
        // ===== 标题栏按钮 =====
        d["最小化"] = "Minimize";
        d["最大化"] = "Maximize";
        d["关闭"] = "Close";

        // ===== 账户卡片 =====
        d["账户"] = "Accounts";
        d["还没有添加账户"] = "No accounts yet";
        d["从右侧选一种登录方式，添加后即可启动游戏"] = "Pick a sign-in method on the right — once added, you can launch the game";
        d["当前身份"] = "Current identity";
        d["启动游戏时使用这个身份"] = "Used when launching the game";
        d["更换皮肤"] = "Change skin";
        d["尚未选择启动身份"] = "No identity selected yet";
        d["在下方账户里点「切换」，右侧还可以再添加一个"] = "Hit \u201cSwitch\u201d on an account below, or add another one on the right";
        d["全部账户"] = "All accounts";
        d["换肤"] = "Skin";
        d["从列表中移除这个账户"] = "Remove this account from the list";
        d["切换"] = "Switch";
        d["使用中"] = "In use";
        d["添加身份"] = "Add identity";
        d["选择一种登录方式"] = "Choose a sign-in method";
        d["使用微软账号正版登录"] = "Sign in with a Microsoft account";
        d["微软账户"] = "Microsoft account";
        d["正版登录 · 需联网"] = "Official sign-in · needs internet";
        d["连接第三方 Yggdrasil 验证服务器"] = "Connect to a third-party Yggdrasil auth server";
        d["外置账户"] = "External account";
        d["第三方验证服务器"] = "Third-party auth server";
        d["不联网，只在本机用一个名字"] = "No internet — just a name on this computer";
        d["离线账户"] = "Offline account";
        d["无需联网"] = "No internet needed";
        d["自动登录上次账户"] = "Sign in to the last account automatically";
        d["关闭后每次打开启动器都要先来这里选一次"] = "When off, you pick an account here every time the launcher starts";
        d["账户凭据只保存在本机；正版登录由微软官方服务完成，启动器不接触密码。"] =
            "Credentials stay on this computer. Official sign-in is handled by Microsoft — the launcher never sees your password.";

        // ===== 新闻与下载任务 =====
        d["新闻"] = "News";
        d["下载任务"] = "Downloads";
        d["下载游戏版本"] = "Download a game version";
        d["下载模组资源"] = "Download mods and resources";
        d["清空已完成"] = "Clear finished";
        d["暂无下载任务"] = "No download tasks";
        d["查看详情"] = "View details";
        d["移除"] = "Remove";

        // ===== 下载中心 =====
        d["下载中心"] = "Download center";
        d["社区资源"] = "Community content";
        d["模组"] = "Mods";
        d["整合包"] = "Modpacks";
        d["数据包"] = "Data packs";
        d["资源包"] = "Resource packs";
        d["光影"] = "Shaders";
        d["运行环境"] = "Runtimes";
        d["搜索游戏版本，输完按回车或点「搜索」"] = "Search game versions, then press Enter or hit Search";
        d["搜索"] = "Search";
        d["共 {0} 个版本"] = "{0} versions";
        d["共 0 个版本"] = "0 versions";
        d["正式版"] = "Release";
        d["测试版"] = "Snapshot";
        d["远古版"] = "Legacy";
        d["愚人节版"] = "April Fools";
        d["没有匹配的版本，换个关键词试试"] = "No matching versions — try another keyword";
        d["搜索模组"] = "Search mods";
        d["来源"] = "Source";
        d["来源：{0}"] = "Source: {0}";
        d["标签"] = "Tags";
        d["排序方式"] = "Sort by";
        d["刷新"] = "Refresh";
        d["版本"] = "Version";
        d["加载器"] = "Loader";
        d["正在加载..."] = "Loading…";
        d["正在加载更多…"] = "Loading more…";
        d["没有匹配的资源，换个关键词或筛选条件试试"] = "Nothing matched — try another keyword or filter";
        d["重试"] = "Retry";

        // ===== 安装向导（版本 / 加载器 / Java） =====
        d["下一步"] = "Next";
        d["返回上一页"] = "Back one page";
        d["返回"] = "Back";
        d["安装版本 - {0}"] = "Install {0}";
        d["安装"] = "Install";
        d["{0} 个版本"] = "{0} versions";
        d["该版本暂无可用版本"] = "No builds available for this version";
        d["Java 运行环境"] = "Java runtime";
        d["主版本"] = "Major version";
        d["平台检测中…"] = "Detecting platform…";
        d["默认 %ProgramFiles%\\Eclipse Adoptium —— 与 Java 官方安装器使用同一路径；写入需要管理员权限，也可点「浏览…」换目录"] =
            "Defaults to %ProgramFiles%\\Eclipse Adoptium — the same path the official Java installer uses. Writing there needs admin rights; you can also click Browse… to pick another folder.";
        d["安装目录"] = "Install folder";
        d["浏览…"] = "Browse…";
        d["可下载的版本"] = "Available versions";
        d["正在获取版本列表…"] = "Fetching version list…";
        d["下载"] = "Download";
        d["已安装"] = "Installed";
        d["还没有安装过 Java"] = "Java has never been installed here";
        d["设为启动 Java"] = "Use for launching";
        d["打开目录"] = "Open folder";
        d["准备中…"] = "Preparing…";
        d["取消"] = "Cancel";
        d["启动"] = "Launch";

        // ===== 启动页 =====
        d["Minecraft · 点击右侧按钮管理这个实例"] = "Minecraft · use the buttons on the right to manage this instance";
        d["工具箱"] = "Tools";
        d["设置"] = "Settings";
        d["还没有可用的游戏版本"] = "No game versions yet";
        d["到「下载中心」安装一个版本，或用配装码一键添加"] = "Install one from the Download center, or add one with a loadout code";
        d["用配装码添加游戏"] = "Add a game with a loadout code";
        d["启动游戏"] = "Launch game";
        d["版本设置"] = "Version settings";
        d["配装码"] = "Loadout code";
        d["快捷设置"] = "Quick settings";
        d["最近游玩"] = "Recently played";
        d["按版本目录的修改时间排序"] = "Sorted by when each version's folder last changed";
        d["实例概览"] = "Instances";
        d["快捷入口"] = "Shortcuts";
        d["装模组"] = "Install mods";
        d["装整合包"] = "Install modpacks";
        d["显示游戏卡片"] = "Show the game card";
        d["关掉后只留右侧的模块"] = "When off, only the panels on the right remain";
        d["Minecraft 文件夹"] = "Minecraft folder";
        d["更改"] = "Change";
        d["未检测到 Java"] = "Java not found";
        d["浏览"] = "Browse";
        d["外观"] = "Appearance";
        d["亮色"] = "Light";
        d["暗色"] = "Dark";

        // ===== 模组文件页 =====
        d["模组文件"] = "Mod files";
        d["选择游戏版本与加载器，查看可下载的模组文件；只有加载器匹配的实例才能直接添加"] =
            "Pick a game version and loader to browse downloadable mod files. Only instances with a matching loader can be added to directly.";
        d["游戏版本"] = "Game version";
        d["模组加载器"] = "Mod loader";
        d["全部"] = "All";
        d["目标实例"] = "Target instance";
        d["安装整合包"] = "Install modpack";
        d["添加到实例"] = "Add to instance";
        d["另存为"] = "Save as";
        d["正在加载模组文件..."] = "Loading mod files…";
    }
}
