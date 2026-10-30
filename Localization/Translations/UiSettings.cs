namespace VibrantbitLauncher.Localization.Translations;

/// <summary>词表第二块：多人联机页与设置页全部文案。</summary>
internal static class UiSettings
{
    public static void AddTo(Dictionary<string, string> d)
    {
        // ===== 多人联机 =====
        d["多人联机"] = "Multiplayer";
        d["联机服务"] = "Multiplayer service";
        d["默认使用与陶瓦联机相同的公共节点牵线，无需自建服务器；房主与房客填同一个地址即可，只有这样两边才碰得到一起。公共节点填的是「发地址的接口」，启动时会先问接口拿到当前真正的服务器地址，再连过去。"] =
            "By default this uses the same public rendezvous nodes as Taowa multiplayer, so you don't need to host anything. Host and guest only find each other if they use the same address. The public node entry is really an address lookup endpoint: at start-up the launcher asks it for the current server address, then connects there.";
        d["没有找到 EasyTier 核心程序，需要先下载一次（约 10 MB）。"] =
            "The EasyTier core isn't here yet — it has to be downloaded once (about 10 MB).";
        d["下载核心"] = "Download core";
        d["当前未以管理员身份运行。EasyTier 需要管理员权限创建虚拟网卡，否则无法拿到虚拟 IP。"] =
            "Not running as administrator. EasyTier needs admin rights to create a virtual adapter; without them you won't get a virtual IP.";
        d["以管理员身份重启"] = "Restart as administrator";
        d["中继服务器"] = "Relay server";
        d["可以直接填 tcp:// 开头的服务器地址；填 https:// 开头的接口地址时，启动前会先访问它换取当前真正的服务器地址"] =
            "You can enter a server address starting with tcp://. If you enter an https:// endpoint instead, it is queried before start-up to look up the current server address.";
        d["启动服务"] = "Start service";
        d["停止服务"] = "Stop service";
        d["我的虚拟 IP：{0}"] = "My virtual IP: {0}";
        d["复制我的虚拟 IP"] = "Copy my virtual IP";
        d["退出启动器后联机继续运行"] = "Keep multiplayer running after the launcher closes";
        d["退出启动器时断开联机"] = "Disconnect when the launcher closes";
        d["开启后可以直接关掉启动器（或收进系统托盘），虚拟网络会保留，房客不会掉线；下次打开启动器会自动接管这个会话。"] =
            "With this on you can close the launcher (or send it to the tray) and the virtual network stays up, so guests don't drop. The next time the launcher starts it takes the session back over.";
        d["选择身份"] = "Choose your role";
        d["我要当房主"] = "I'm the host";
        d["联机服务运行期间不能切换身份，先点「停止服务」"] =
            "You can't switch roles while the service is running — hit Stop service first";
        d["我要当房客"] = "I'm a guest";
        d["联机码里存着这个房间的虚拟网络名和密码。把「联机码 + 你的虚拟 IP」一起发给好友，他们填进来就能进同一个网络。这串码与陶瓦联机的码通用，两边互相都认。"] =
            "A room code carries this room's virtual network name and password. Send friends the code together with your virtual IP and they join the same network. These codes are interchangeable with Taowa multiplayer codes — each side accepts the other's.";
        d["联机码"] = "Room code";
        d["自动生成，比如 U/ABCD-EFGH-JKLM-NPQR"] = "Generated for you, e.g. U/ABCD-EFGH-JKLM-NPQR";
        d["重新生成"] = "Generate new";
        d["复制联机码"] = "Copy room code";
        d["启动服务后，进游戏按 Esc → 对局域网开放，好友就能通过虚拟 IP 连进你的世界。"] =
            "Once the service is running, press Esc in-game → Open to LAN and friends can join your world through your virtual IP.";
        d["把房主给你的联机码填进来（整句粘过来也行，会自动认出 U/ 开头的那一段），启动服务后你们就处在同一个虚拟局域网里。"] =
            "Paste the room code the host gave you (pasting the whole sentence works too — the part starting with U/ is picked out for you). After the service starts you share one virtual LAN.";
        d["粘贴房主给你的联机码"] = "Paste the room code from your host";
        d["粘贴"] = "Paste";
        d["房主的虚拟 IP：{0}"] = "Host's virtual IP: {0}";
        d["在游戏里进入「多人游戏 → 直接连接」，把这个地址粘进去即可。"] =
            "In-game, open Multiplayer → Direct Connect and paste this address.";
        d["复制地址"] = "Copy address";
        d["如果列表里还没看到房主，多等十几秒；组网完成后「网络成员」里就会出现对方的虚拟 IP。"] =
            "If the host isn't listed yet, give it another ten seconds — once the network is up their virtual IP appears under Members.";
        d["局域网联机模组"] = "LAN multiplayer mod";
        d["原版「对局域网开放」会强制正版验证，离线账户会被直接踢掉 —— 这也是组网成功后仍然连不进来的原因。这里装的是 LAN World Plug-n-Play（mcwifipnp）：它给开房界面加上「正版验证」三档开关、端口修改和 UPnP 映射，Fabric / Quilt / Forge / NeoForge 都支持。"] =
            "Vanilla's Open to LAN forces online-mode authentication, so offline accounts are kicked — which is why you can still fail to connect after the network is up. This installs LAN World Plug-n-Play (mcwifipnp): it adds a three-way online-mode switch, port editing and UPnP mapping to the LAN screen, and works on Fabric / Quilt / Forge / NeoForge.";
        d["还没有安装任何 Minecraft 实例，请先在「下载」页装一个带加载器的版本（Fabric / Forge / NeoForge）。"] =
            "No Minecraft instance installed yet. Install a version with a loader (Fabric / Forge / NeoForge) from the Download page first.";
        d["怎么用"] = "How it works";
        d["网络成员"] = "Members";
        d["同一个联机码下的所有设备。每三秒自动刷新一次。"] =
            "Every device on the same room code. Refreshes every three seconds.";
        d["还没读到的成员信息。启动服务并完成组网后，这里会列出本机和好友的虚拟 IP。"] =
            "No member info yet. Once the service is running and the network is up, your own and your friends' virtual IPs are listed here.";
        d["复制 IP"] = "Copy IP";
        d["提示：房主和房客都点「启动服务」后，双方各拿到一个 10 开头的虚拟 IP；房主在游戏里开放局域网，房客用房主的虚拟 IP 直连即可。若连不上，先确认防火墙没有拦截 EasyTier。"] =
            "Tip: once both host and guest have hit Start service, each side gets a virtual IP starting with 10. The host opens the world to LAN and guests connect straight to the host's virtual IP. If that fails, check your firewall isn't blocking EasyTier.";
        d["运行"] = "Play";
        d["选择已安装的版本直接启动游戏"] = "Pick an installed version and launch right away";

        // ===== 设置 · 分节标题 =====
        d["通用"] = "General";
        d["游戏"] = "Game";
        d["网络"] = "Network";
        d["高级"] = "Advanced";
        d["关于"] = "About";
        d["主页"] = "Home";
        d["控制「启动」页显示哪些模块。关掉的模块只是不在这里出现，功能本身在别的页面仍然可达。（游戏卡片的开关在启动页的快捷设置里，账户相关的在「账户」页。）"] =
            "Choose which panels show up on the Launch page. Turning one off only hides it here — the feature stays reachable elsewhere. (The game card toggle is in the Launch page's quick settings; account options live on the Accounts page.)";
        d["显示问候语"] = "Show the greeting";
        d["显示最近游玩"] = "Show recently played";
        d["显示实例概览"] = "Show instances";
        d["显示快捷入口"] = "Show shortcuts";
        d["启动与更新"] = "Startup and updates";
        d["启动器自身的行为，以及版本更新的检查方式。"] =
            "How the launcher itself behaves, and how it looks for updates.";
        d["关闭窗口时最小化到托盘"] = "Minimize to tray when the window closes";
        d["参与测试版"] = "Opt into beta builds";
        d["立即检查更新"] = "Check for updates now";
        d["检查更新"] = "Check for updates";

        // ===== 设置 · Java 与内存 =====
        d["Java 与内存"] = "Java and memory";
        d["决定游戏用哪个 Java 运行、能用多少内存。改动在下次启动游戏时生效。"] =
            "Which Java runs the game, and how much memory it may use. Changes take effect the next time you launch.";
        d["JAVA 路径"] = "JAVA path";
        d["重新检测"] = "Detect again";
        d["手动指定 java 可执行文件"] = "Point to the java executable manually";
        d["按实例独立绑定 Java"] = "Bind Java per instance";
        d["去版本管理"] = "Go to versions";
        d["内存分配"] = "Memory";
        d["自动推荐"] = "Auto (recommended)";
        d["初始内存（-Xms）"] = "Initial memory (-Xms)";
        d["JVM 参数"] = "JVM arguments";
        d["一行一条，例如 -Dfile.encoding=UTF-8；以 # 开头的行会被忽略"] =
            "One per line, e.g. -Dfile.encoding=UTF-8. Lines starting with # are ignored.";

        // ===== 设置 · 目录 =====
        d["目录"] = "Folders";
        d["版本、存档、模组都放在游戏根目录下。"] =
            "Versions, worlds and mods all live under the game folder.";
        d["游戏根目录"] = "Game folder";
        d["选择"] = "Choose";
        d["打开"] = "Open";
        d["版本隔离"] = "Version isolation";
        d["恢复默认"] = "Restore defaults";
        d["换到别的盘会建立目录链接，游戏仍然按原位置读取，不会丢资源包。"] =
            "Moving to another drive creates a folder link, so the game still reads from its original location and you don't lose resource packs.";
        d["存档"] = "Worlds";

        // ===== 设置 · 启动参数 =====
        d["游戏窗口的大小，以及启动游戏时额外附加的参数。"] =
            "The game window size, plus any extra arguments passed at launch.";
        d["全屏启动"] = "Launch fullscreen";
        d["Minecraft 窗体大小"] = "Minecraft window size";
        d["像素"] = "px";
        d["高级启动参数"] = "Advanced launch arguments";
        d["禁用 IPv6"] = "Disable IPv6";
        d["Java 垃圾回收器"] = "Java garbage collector";
        d["追加自定义游戏参数"] = "Append custom game arguments";

        // ===== 设置 · 主题与背景 =====
        d["主题"] = "Theme";
        d["明暗"] = "Light or dark";
        d["主题色"] = "Accent color";
        d["取色器"] = "Color picker";
        d["应用这个颜色"] = "Use this color";
        d["主题渐变色"] = "Accent gradient";
        d["启用渐变"] = "Enable gradient";
        d["渐变终点色"] = "Gradient end color";
        d["恢复默认外观"] = "Restore default look";
        d["背景"] = "Background";
        d["背景类型"] = "Background type";
        d["内置壁纸"] = "Built-in wallpaper";
        d["选择本地图片"] = "Pick a local image";
        d["移除背景"] = "Remove background";
        d["毛玻璃强度"] = "Frost strength";
        d["不透明度"] = "Opacity";
        d["暗角与可读性"] = "Vignette and readability";
        d["暗角"] = "Vignette";
        d["向四角压暗，把视线收拢到中间。深色壁纸上效果最明显"] =
            "Darkens the corners to pull your eye to the middle. Most noticeable on dark wallpapers.";
        d["动态背景"] = "Animated background";

        // ===== 设置 · 动效 =====
        d["动效"] = "Motion";
        d["界面里那些「浮起来」「滑进来」的效果。低配电脑建议切到精简或关闭。"] =
            "The float and slide effects used across the interface. On slower machines, switch to Reduced or Off.";
        d["动画档位"] = "Motion level";
        d["页面切换方式"] = "Page transition";
        d["动画速度"] = "Animation speed";
        d["性能模式"] = "Performance mode";
        d["一键关掉所有好看但费性能的东西：壁纸、动效、卡片阴影、光斑，并改用软件渲染。适合 Windows 7 老机器。"] =
            "Turns off everything pretty but costly in one go — wallpaper, motion, card shadows, glow — and switches to software rendering. Made for older Windows 7 machines.";
        d["开启性能模式"] = "Turn on performance mode";

        // ===== 设置 · 语言与字体 =====
        d["语言与字体"] = "Language and fonts";
        d["界面文字用什么语言、什么字体、多大。"] =
            "Which language the interface uses, in what font, at what size.";
        d["界面语言"] = "Language";
        d["切换后立即生效，无需重启。"] = "Takes effect immediately — no restart needed.";
        d["界面字体"] = "Interface font";
        d["从文件…"] = "From file…";
        d["字体缩放"] = "Font scale";
        d["高对比度模式"] = "High contrast mode";

        // ===== 设置 · 下载 =====
        d["下载源"] = "Download sources";
        d["「自动测速」下，同一个文件的不同分片会从多个源并行取，某个源被限流只会降权，不会永久禁用。"] =
            "On Auto, chunks of one file are fetched from several mirrors in parallel. A rate-limited mirror is only deprioritised, never banned.";
        d["速度限制"] = "Speed limit";
        d["拖到最左为不限速。限的是整个文件的总速度，分块下载时不会被放大。"] =
            "Drag all the way left for no limit. The cap applies to the whole file, so it isn't multiplied by chunked downloading.";
        d["下载引擎参数"] = "Download engine";
        d["并发度默认按 CPU 核数与实测带宽自适应，一般不需要动。只有镜像频繁报错时才需要调低。"] =
            "Concurrency adapts to your CPU cores and measured bandwidth, so usually leave it alone. Only lower it if mirrors keep erroring.";
        d["同时下载文件数"] = "Files at once";
        d["{0} 个"] = "{0}";
        d["单文件分块数"] = "Chunks per file";
        d["{0} 块"] = "{0} chunks";
        d["失败重试次数"] = "Retries";
        d["{0} 次"] = "{0}";
        d["连接超时"] = "Connection timeout";
        d["{0} 秒"] = "{0} s";
        d["联机"] = "Multiplayer";
        d["局域网联机模组、虚拟局域网这些功能目前放在「多人」页。P2P 直连与中继服务器设置会在后续版本并入这里。"] =
            "The LAN mod and virtual LAN features currently live on the Multiplayer page. P2P and relay settings will move here in a later version.";

        // ===== 设置 · 日志与关于 =====
        d["日志与诊断"] = "Logs and diagnostics";
        d["启动失败、下载出错时，把等级调到「调试」再复现一次，然后导出日志即可提供给开发者定位。"] =
            "If launching fails or a download errors, set the level to Debug, reproduce it once, then export the log and hand it to the developer.";
        d["记录等级"] = "Log level";
        d["保留天数"] = "Keep for";
        d["{0} 天"] = "{0} days";
        d["输出到调试器"] = "Write to debugger";
        d["日志目录"] = "Log folder";
        d["导出日志包"] = "Export log bundle";
        d["导出单个文件"] = "Export a single file";
        d["清空日志"] = "Clear logs";
        d["最近日志"] = "Recent log";
        d["崩溃与重置"] = "Crashes and reset";
        d["崩溃日志自动上传"] = "Upload crash logs automatically";
        d["重置设置"] = "Reset settings";
        d["恢复默认设置"] = "Restore default settings";
        d["版本与更新"] = "Version and updates";
        d["一款跨平台的 Minecraft Java 版启动器：多账户登录、版本下载与启动、模组浏览、一键联机。"] =
            "A cross-platform Minecraft Java launcher: multiple accounts, version download and launch, mod browsing, one-click multiplayer.";
        d["立即检查"] = "Check now";
        d["更新日志"] = "Changelog";
        d["开源与致谢"] = "Open source and credits";
        d["本项目以 GNU Affero 通用公共许可证第 3 版（AGPL-3.0）开源：可以自由使用、修改、分发，修改后的版本也必须以同样的协议开放源代码。界面框架、下载引擎、认证与版本解析分别来自不同的开源项目，字体版权归各字体作者所有。"] =
            "This project is open source under the GNU Affero General Public License v3 (AGPL-3.0): you may use, modify and redistribute it freely, and modified versions must also be released under the same licence. The UI framework, download engine, authentication and version parsing each come from separate open-source projects; fonts remain the copyright of their authors.";
        d["开源协议"] = "Licence";
        d["查看协议"] = "View licence";
        d["获取源代码"] = "Get the source code";
        d["致谢"] = "Credits";
        d["谢谢"] = "Thank you";
        d["链接"] = "Links";
        d["官网"] = "Website";
        d["用户文档"] = "Documentation";
        d["问题反馈"] = "Report an issue";
        d["赞助支持"] = "Sponsor";

        // ===== 设置页：搜索 / 分区重置 / 备份 / 磁盘占用 =====
        d["搜索设置"] = "Search settings";
        d["没找到匹配的设置，换个更短的词试试"] = "No matching setting — try a shorter word";
        d["没找到匹配的设置。相近的有：{0}"] = "No exact match. Similar: {0}";

        d["下次启动游戏生效"] = "Applies on next launch";
        d["恢复本节默认"] = "Restore this section";
        d["本区恢复默认"] = "Restore section";
        d["只把「{0}」这一节的选项恢复默认，其它节不受影响"] =
            "Only {0} goes back to defaults; the other sections keep your choices";
        d["只把「外观」这一节的选项恢复默认，其它节不受影响。背景图、字体文件、语言与性能模式会保留"] =
            "Only Appearance goes back to defaults; the other sections keep your choices. Your wallpaper, font file, language and performance mode stay as they are";
        d["只把「高级」这一节的选项恢复默认，日志文件与磁盘上的东西不会被删"] =
            "Only Advanced goes back to defaults; nothing on disk is deleted";
        d["恢复「{0}」的默认设置"] = "Restore defaults for {0}";
        d["只把「{0}」这一节的选项恢复默认，其它节和账户、游戏目录都不受影响。"] =
            "Only {0} goes back to defaults. Other sections, your accounts and the game folder are untouched.";
        d["「{0}」已恢复默认（改动 {1} 项）"] = "{0} restored to defaults ({1} changed)";
        d["「{0}」本来就是默认值"] = "{0} was already at defaults";

        d["磁盘占用"] = "Disk usage";
        d["游戏目录、日志与联机内核各占多少。一个装满模组和存档的游戏目录有几万个小文件，统计要几秒钟，慢是正常的。"] =
            "How much the game folder, the logs and the multiplayer core take up. A game folder with mods and worlds holds tens of thousands of small files, so a few seconds is normal.";
        d["重新统计"] = "Recount";
        d["正在统计…"] = "Measuring…";
        d["统计失败，可以点「重新统计」再试一次"] = "Measuring failed — press Recount to try again";
        d["合计 {0}"] = "Total {0}";
        d["清理超期日志"] = "Clean old logs";
        d["打开游戏目录"] = "Open game folder";
        d["已清理 {0} 个超期日志文件"] = "Deleted {0} old log files";
        d["没有超期日志需要清理"] = "No old log files to clean";
        d["未生成"] = "Not created yet";
        d["日志"] = "Logs";
        d["联机内核"] = "Multiplayer core";

        d["配置备份"] = "Settings backup";
        d["导出一份设置文件，换电脑、给朋友抄一份、或者改坏了想退回去时用。账户、联机密钥和本机路径不会写进文件，导入时也不会覆盖这些东西。"] =
            "Save your settings to a file — for a new PC, to share with a friend, or to roll back after a change you regret. Accounts, the multiplayer key and local paths are never written to it, and importing won't overwrite them.";
        d["导出配置"] = "Export settings";
        d["导入配置"] = "Import settings";
        d["导出设置备份"] = "Export settings backup";
        d["导入设置备份"] = "Import settings backup";
        d["设置备份"] = "Settings backup";
        d["已导出 {0} 项设置到 {1}"] = "Exported {0} settings to {1}";
        d["设置备份已导出"] = "Settings backup exported";
        d["导出失败：{0}"] = "Export failed: {0}";
        d["导出失败"] = "Export failed";
        d["会用文件里的设置覆盖你现在的选择，账户、游戏目录、Java 路径和语言不受影响。"] =
            "Your current choices will be replaced by the ones in the file. Accounts, the game folder, Java paths and language stay as they are.";
        d["导入"] = "Import";
        d["已导入 {0} 项设置"] = "Imported {0} settings";
        d["文件里的设置和你现在的完全一致"] = "The file matches your current settings exactly";
        d["这个文件导入不了"] = "That file can't be imported";
        d["设置已更新"] = "Settings updated";
        d["没有需要改的项"] = "Nothing needed changing";
        d["读不到这个文件"] = "Couldn't read that file";
        d["这个文件不是设置备份"] = "That file isn't a settings backup";
        d["这个文件不是本启动器的设置备份"] = "That file isn't a settings backup from this launcher";
    }
}
