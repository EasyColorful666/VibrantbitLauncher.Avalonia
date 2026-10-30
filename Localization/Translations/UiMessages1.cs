namespace VibrantbitLauncher.Localization.Translations;

/// <summary>
/// 词表第四块之一：错误提示、崩溃分析的结论与建议、联机状态。
///
/// <para>
/// 代码里带插值的地方（<c>$"...{x}..."</c>）在这里一律写成 <c>{0}</c> / <c>{1}</c>，
/// 因为运行时的整句才会被拿去匹配 —— 写成源码里的 <c>{x}</c> 永远对不上。
/// </para>
/// </summary>
internal static class UiMessages1
{
    public static void AddTo(Dictionary<string, string> d)
    {
        // ===== 通用状态词 =====
        d["容器尚未初始化"] = "The service container isn't initialised yet";
        d["(未设置)"] = "(not set)";
        d["未探测"] = "Not probed";
        d["探测失败"] = "Probe failed";
        d["未知"] = "Unknown";
        d["未知错误"] = "Unknown error";
        d["未知体积"] = "Unknown size";
        d["其他"] = "Other";
        d["失败"] = "Failed";
        d["已取消"] = "Cancelled";
        d["已完成"] = "Done";
        d["等待中"] = "Waiting";
        d["下载中"] = "Downloading";
        d["准备中..."] = "Preparing…";
        d["下载完成"] = "Download complete";
        d["未启动"] = "Not started";
        d["本机"] = "This device";
        d["{0}（我）"] = "{0} (me)";
        d["P2P 直连"] = "Direct P2P";
        d["中继转发"] = "Relayed";
        d["开启"] = "On";
        d["原版"] = "Vanilla";
        d["未标注加载器"] = "Loader not labelled";
        d["愚人节"] = "April Fools";
        d["MSI 安装包"] = "MSI package";

        // ===== 文件 / 网络错误 =====
        d["正由另一进程使用"] = "is in use by another process";
        d["远程主机强迫关闭"] = "the remote host closed the connection";
        d["文件正被其他程序占用（常见原因：游戏还在运行、杀毒软件正在扫描该文件，"] =
            "The file is locked by another program — usually the game still running, an antivirus scanning it,";
        d["或同时开了两个启动器）。请关闭后重试。"] =
            "or two launchers open at once. Close it and try again.";
        d["磁盘空间不足，请清理磁盘后重试。"] =
            "Not enough disk space. Free some up and try again.";
        d["目标目录不存在或已被删除，请检查游戏目录设置。"] =
            "The target folder is missing or was deleted. Check the game folder setting.";
        d["没有写入权限（目标目录可能是只读，或被杀毒软件拦截）。请更换目录，或以管理员身份运行后重试。"] =
            "No write permission — the folder may be read-only or blocked by antivirus. Pick another folder, or run as administrator and try again.";
        d["磁盘读写失败："] = "Disk read/write failed: ";
        d["请求超时（网络太慢或代理不通），请检查网络后重试"] =
            "The request timed out — slow connection or a proxy in the way. Check your network and try again.";
        d["服务器返回的数据不完整（响应被中途截断），通常是网络不稳，请稍后重试"] =
            "The server's response was cut off, which usually means an unstable connection. Try again in a moment.";
        d["服务器返回 404（资源不存在）"] = "The server returned 404 (resource not found)";
        d["服务器返回 {0}（对方服务故障，通常稍后重试即可）"] =
            "The server returned {0} — their service is having trouble, usually worth retrying later";
        d["服务器返回 {0}"] = "The server returned {0}";
        d["网络错误：{0}"] = "Network error: {0}";
        d["网络错误（连接被拒绝或中断），请检查网络 / 代理后重试"] =
            "Network error — connection refused or dropped. Check your network or proxy and try again.";

        // ===== 全局热键 =====
        d["全局热键目前只在 Windows 上可用。"] = "Global hotkeys are only available on Windows right now.";
        d["这个快捷键已被其它程序占用，请先关掉占用它的软件。"] =
            "That shortcut is taken by another program — close it first.";
        d["系统拒绝了快捷键注册（错误码 {0}）。"] =
            "The system refused to register the shortcut (error {0}).";
        d["快捷键监听线程异常：{0}"] = "The hotkey listener failed: {0}";

        // ===== 联机码校验 =====
        d["联机码太短了。完整格式是 U/ 加 16 位字符，例如 U/ABCD-EFGH-JKLM-NPQR。"] =
            "That room code is too short. The full form is U/ plus 16 characters, e.g. U/ABCD-EFGH-JKLM-NPQR.";
        d["联机码核对不通过，可能抄错了一位。请让对方重新发一次，或直接从聊天记录里复制。"] =
            "The room code failed its check digit, so a character was probably mistyped. Ask for it again, or copy it straight from the chat.";
        d["没找到联机码（应当以 U/ 开头）。如果对方给的是旧版启动器的密钥，请让对方升级后用新联机码开房。"] =
            "No room code found — it should start with U/. If they sent a key from an older launcher, ask them to update and open the room again.";

        // ===== 单实例 / 托盘 =====
        d["已送达"] = "delivered";
        d["未送达（主实例可能仍在启动）"] = "not delivered (the main instance may still be starting)";
        d["显示主界面"] = "Show the main window";
        d["断开联机（停止虚拟网络）"] = "Disconnect (stop the virtual network)";
        d["退出启动器"] = "Quit launcher";

        // ===== 启动失败 =====
        d["启动器遇到未处理的错误"] = "The launcher hit an unexpected error";
        d["启动器启动失败"] = "The launcher failed to start";
        d["VibrantbitLauncher 启动失败"] = "VibrantbitLauncher failed to start";
        d["启动器启动失败。"] = "The launcher failed to start.";
        d["完整日志已保存到："] = "The full log was saved to:";
        d["（无法写入日志文件，请检查磁盘权限）"] = "(couldn't write the log file — check disk permissions)";

        // ===== 性能模式 / 兼容渲染 =====
        d["检测到性能优化建议"] = "A performance tweak is available";
        d["是否开启「性能模式」？开启后将会关闭云母材质、壁纸、动效与阴影，"] =
            "Turn on performance mode? It switches off mica, wallpaper, motion and shadows —";
        d["之后随时可以在「设置 → 个性化」里改回来。"] =
            "you can change it back anytime under Settings → Appearance.";
        d["暂不开启"] = "Not now";
        d["已开启性能模式"] = "Performance mode is on";
        d["如果希望连渲染方式也一起切换（老显卡上效果更明显），请重启一次启动器。"] =
            "To switch the renderer as well (more noticeable on older GPUs), restart the launcher once.";
        d["上次启动没有成功，已自动切换为软件渲染。若显示正常可忽略；"] =
            "The last start-up didn't succeed, so software rendering was turned on automatically. Ignore this if everything looks fine;";
        d["如需恢复显卡加速，请在「设置 → 个性化」里关闭性能模式后重启。"] =
            "to get GPU acceleration back, turn off performance mode under Settings → Appearance and restart.";
        d["已启用兼容渲染"] = "Compatibility rendering enabled";

        // ===== 新闻 / 图片 =====
        d["新闻数据为空或格式不符"] = "News data is empty or malformed";
        d["获取新闻列表失败（{0}）"] = "Couldn't fetch the news list ({0})";
        d["位图尺寸必须为正数。"] = "Bitmap size must be positive.";
        d["图片数据为空。"] = "The image data is empty.";
        d["无法识别的图片格式。"] = "Unrecognised image format.";
        d["图片尺寸无效。"] = "Invalid image size.";
        d["图片解码失败（{0}）。"] = "Couldn't decode the image ({0}).";

        // ===== 补全资源 =====
        d["准备补全资源"] = "Preparing repair";
        d["重新生成加载器补丁文件"] = "Rebuild loader patch files";
        d["补全完成"] = "Repair complete";
        d["缺失的资源已补全（加载器补丁文件已重新生成），现在可以重新启动游戏了"] =
            "Missing files are restored and the loader patches rebuilt — you can launch the game again.";
        d["需要重新安装加载器"] = "The loader needs reinstalling";
        d["原版资源已补全，但 {0} 需要重新安装加载器才能生成"] =
            "Vanilla files are restored, but {0} can only be produced by reinstalling the loader";
        d["（该文件由安装处理器本地打补丁产出，网上下载不到）"] =
            "(that file is generated locally while installing and can't be downloaded)";
        d["补全加载器依赖"] = "Repair loader dependencies";
        d["缺失的资源已补全，现在可以重新启动游戏了"] =
            "Missing files are restored — you can launch the game again.";
        d["原版资源已补全（部分加载器库未能补全）"] =
            "Vanilla files are restored (some loader libraries couldn't be)";
        d["原版资源已补全（加载器依赖未能补全）"] =
            "Vanilla files are restored (loader dependencies couldn't be)";

        // ===== 崩溃分析 · 分类 =====
        d["内存"] = "Memory";
        d["冲突"] = "Conflict";
        d["排查"] = "Investigate";
        d["建议"] = "Suggestion";
        d["需要手动处理"] = "Needs manual fixing";
        d["显卡 / 图形设置问题"] = "GPU / graphics issue";
        d["游戏报告 OpenGL 相关错误。请先更新显卡驱动（核显也要更新）；"] =
            "The game hit an OpenGL error. Update your graphics driver first (integrated graphics included);";
        d["若装了光影或高清材质包，先把它们移出实例再试。"] =
            "if you have shaders or HD texture packs, move them out of the instance and try again.";
        d["OptiFine 与 Forge 不兼容"] = "OptiFine isn't compatible with Forge";
        d["这个 Forge 版本不能直接用 OptiFine。请改用 OptiFine 的 Forge 兼容版本，"] =
            "This Forge build can't use OptiFine directly. Use OptiFine's Forge-compatible build,";
        d["或改用量钠（Sodium）/ Iris 这类加载器专用的性能模组。"] =
            "or switch to loader-native performance mods such as Sodium / Iris.";
        d["查看完整崩溃报告"] = "View the full crash report";
        d["日志分析只能覆盖常见原因。若上面的建议都没解决，请把崩溃报告发给整合包作者。"] =
            "Log analysis only covers common causes. If none of these helped, send the crash report to the modpack author.";

        // ===== 崩溃分析 · 结论 =====
        d["游戏文件校验失败（文件被改动或下载不完整）"] = "Game file check failed (modified or incompletely downloaded)";
        d["某个方块导致了崩溃"] = "A block caused the crash";
        d["某个实体导致了崩溃"] = "An entity caused the crash";
        d["材质过大，或显卡设置不足"] = "Textures too large, or graphics settings too low";
        d["光影 / 资源包引发 OpenGL 错误"] = "Shaders or resource packs triggered an OpenGL error";
        d["无法加载纹理"] = "Couldn't load textures";
        d["某个模组的配置导致崩溃"] = "A mod's config caused the crash";
        d["模组 Mixin 注入失败（通常是与其它模组冲突）"] = "A mod's Mixin injection failed (usually a conflict with another mod)";
        d["模组加载器报错"] = "The mod loader reported an error";
        d["模组初始化失败"] = "Mod initialisation failed";
        d["模组文件被解压过（应保持 .jar 原样）"] = "A mod file was unpacked (it has to stay a .jar)";
        d["模组太多，超出了 ID 上限"] = "Too many mods — the ID limit was exceeded";
        d["某个模组导致游戏崩溃"] = "A mod crashed the game";
        d["同一个模组装了多份（重复）"] = "The same mod is installed more than once";
        d["Fabric 相关错误"] = "Fabric-related error";
        d["Fabric 相关错误（有已知解决方案）"] = "Fabric-related error (a known fix exists)";
        d["Forge 相关错误"] = "Forge-related error";
        d["旧版 Forge 不兼容当前高版本 Java"] = "This old Forge build doesn't work with your newer Java";
        d["版本 JSON 里重复声明了 Forge"] = "Forge is declared twice in the version JSON";
        d["OptiFine 导致世界加载失败"] = "OptiFine made the world fail to load";
        d["崩溃堆栈里出现了已知问题关键字"] = "The crash stack contains a known problem keyword";
        d["崩溃堆栈里出现了模组名"] = "The crash stack mentions a mod name";
        d["日志里出现了已知问题关键字"] = "The log contains a known problem keyword";
        d["内存不足（Java 堆太小或分配失败）"] = "Out of memory (heap too small or allocation failed)";
        d["正在使用 JDK 而不是 JRE（可能引发兼容问题）"] = "Running a JDK instead of a JRE (can cause compatibility trouble)";
        d["显卡不支持所需的 OpenGL 版本"] = "Your GPU doesn't support the required OpenGL version";
        d["正在使用 OpenJ9 虚拟机（部分模组不兼容）"] = "Running on OpenJ9 (some mods aren't compatible)";
        d["Java 版本过高"] = "Java version too new";
        d["Java 版本与模组不匹配（类文件版本错误）"] = "Java doesn't match the mods (class file version error)";
        d["32 位 Java 导致可用内存不足"] = "32-bit Java caps the available memory";
        d["玩家手动触发了调试崩溃"] = "The crash was triggered manually for debugging";

        // ===== 崩溃分析 · 修复动作 =====
        d["内存不足"] = "Out of memory";
        d["32 位 Java"] = "32-bit Java";
        d["需要换成 64 位 Java。"] = "Switch to 64-bit Java.";
        d["Java 版本问题"] = "Java version issue";
        d["日志里出现了内存不足的信号，当前最大堆内存是 {0} MB。"] =
            "The log shows an out-of-memory signal. The maximum heap is currently {0} MB.";
        d["本机可用内存约 {0} MB，建议提高到 {1} MB。"] =
            "This machine has roughly {0} MB available; raising it to {1} MB is recommended.";
        d["把最大内存调到 {0} MB"] = "Raise maximum memory to {0} MB";
        d["已把最大内存从 {0} MB 提高到 {1} MB（初始堆 {2} MB），重新启动游戏即可生效"] =
            "Maximum memory raised from {0} MB to {1} MB (initial heap {2} MB). Relaunch the game and it takes effect.";
        d["日志显示是 32 位 Java 导致的内存不足 —— 32 位虚拟机最多只能用到约 1.5 GB，"] =
            "The log points to 32-bit Java running out of memory — a 32-bit VM can use about 1.5 GB at most,";
        d["即使把 -Xmx 调大也没用。建议换成 64 位 Java（本机可用内存约 "] =
            "so raising -Xmx won't help. Switch to 64-bit Java (this machine has about ";
        d["日志显示当前 Java 与这个游戏 / 模组不匹配。这个版本建议使用 Java {0}"] =
            "The log shows your Java doesn't match this game or these mods. Java {0} is recommended for this version.";
        d["（当前未指定 Java）"] = "(no Java set right now)";
        d["（当前：{0}）"] = "(current: {0})";
        d["。点击后会扫描本机已安装的 Java 并自动切换到匹配的那一个。"] =
            ". Clicking scans the Java runtimes installed here and switches to the matching one.";
        d["自动选择 Java {0}"] = "Pick Java {0} automatically";
        d["扫描 Java 失败：{0}"] = "Couldn't scan for Java: {0}";
        d["本机没有检测到任何可用的 Java"] = "No usable Java found on this computer";
        d["本机已安装："] = "Installed here: ";
        d["没有找到 Java {0}（{1}）。请先安装 Java {0} 后重试，"] =
            "Java {0} wasn't found ({1}). Install Java {0} and try again,";
        d["或到「设置 → 启动」手动指定 Java 路径"] = "or point to a Java path under Settings → Game.";
        d["已切换到这个 Java：{0}（{1}）"] = "Switched to this Java: {0} ({1})";

        // ===== 模组禁用 / 重复 =====
        d["疑似问题模组：{0}"] = "Suspect mod: {0}";
        d["这个模组出现在崩溃堆栈里。点击后会把它改名为 "] =
            "This mod shows up in the crash stack. Clicking renames it to ";
        d["（不删除），游戏下次启动就不会加载它；确认没问题后把后缀去掉即可恢复。"] =
            " (it isn't deleted), so the next launch skips it; remove the suffix to bring it back.";
        d["禁用这个模组"] = "Disable this mod";
        d["已存在 {0}，请先手动清理"] = "{0} already exists — clean it up manually first";
        d["已禁用 {0}（改名为 {1}）"] = "Disabled {0} (renamed to {1})";
        d["禁用失败：{0}"] = "Couldn't disable it: {0}";
        d["重复安装：{0}"] = "Installed twice: {0}";
        d["这个模组装了 {0} 份："] = "This mod is installed {0} times: ";
        d["。游戏加载两份同名模组会直接崩。点击后保留 {0}，其余改名禁用。"] =
            ". Loading two copies of one mod crashes the game. Clicking keeps {0} and disables the rest.";
        d["保留 {0}，禁用其余 {1} 份"] = "Keep {0} and disable the other {1}";
        d["已禁用 {0} 份重复的 {1}，保留 {2}"] = "Disabled {0} duplicate(s) of {1}, kept {2}";
        d["没有文件被改动，请手动检查 mods 目录"] = "No files were changed — check the mods folder yourself";

        // ===== 崩溃报告目录 =====
        d["崩溃报告目录不存在："] = "The crash report folder doesn't exist: ";
        d["已打开崩溃报告目录"] = "Opened the crash report folder";
        d["无法打开目录："] = "Couldn't open the folder: ";

        // ===== 联机状态 =====
        d["联机未启动"] = "Multiplayer off";
        d["联机中（正在获取虚拟 IP）"] = "Online (fetching virtual IP)";
        d["房客"] = "Guest";
        d["联机：未启动"] = "Multiplayer: off";
        d["未找到 EasyTier 核心程序，请先点击「下载 EasyTier 核心」。"] =
            "The EasyTier core isn't here yet — click Download EasyTier core first.";
        d["联机码解出来的网络名太短，无法组网。"] =
            "The room code yields a network name too short to join.";
        d["联机码解出来的网络密码太短，无法组网。"] =
            "The room code yields a password too short to join.";
        d["启动 EasyTier 失败：{0}"] = "Couldn't start EasyTier: {0}";
        d["启动 EasyTier 失败：无法创建核心进程。"] =
            "Couldn't start EasyTier: the core process wasn't created.";
        d["正在加入虚拟网络…"] = "Joining the virtual network…";
    }
}
