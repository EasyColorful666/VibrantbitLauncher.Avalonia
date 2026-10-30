namespace VibrantbitLauncher.Localization.Translations;

/// <summary>词表第四块之三：整合包安装、下载引擎提示、皮肤、更新检查、启动与退出。</summary>
internal static class UiMessages3
{
    public static void AddTo(Dictionary<string, string> d)
    {
        // ===== 整合包 / 配装码安装 =====
        d["安装完成（部分文件缺失）"] = "Install complete (some files missing)";
        d["安装完成：{0}"] = "Install complete: {0}";
        d["{0}「{1}」已装好，但有 {2} 个文件没能下载："] =
            "{0} “{1}” is installed, but {2} file(s) couldn't be downloaded: ";
        d["。可在「下载中心」手动补齐。"] = ". You can fill them in from the Download center.";
        d["{0}「{1}」已安装为实例 {2}"] = "{0} “{1}” is installed as instance {2}";
        d["（{0} 个文件"] = "({0} files";
        d[" + {0} 个随包文件"] = " + {0} bundled files";
        d["），可在「启动」页选择它开始游戏"] = "), pick it on the Launch page to play";
        d["{0}「{1}」安装已取消"] = "{0} “{1}” install cancelled";
        d["{0}安装失败"] = "{0} install failed";
        d["还没有进行过安装"] = "No install has run yet";
        d["{0}下载失败：服务器返回 {1}，请稍后重试"] =
            "{0} download failed: the server returned {1}. Try again later.";
        d["{0}安装超时，请检查网络（国内网络访问境外资源通常需要代理）后重试"] =
            "{0} install timed out. Check your network — reaching overseas services from mainland China usually needs a proxy.";
        d["{0}安装时网络中断，请检查网络或代理后重试"] =
            "The network dropped during the {0} install. Check your connection or proxy and try again.";
        d["{0}安装失败：{1}"] = "{0} install failed: {1}";
        d["无法识别的整合包格式。支持 Modrinth 的 .mrpack，以及 CurseForge 导出的 .zip。"] =
            "Unrecognised modpack format. Supported: Modrinth .mrpack and CurseForge-exported .zip.";
        d["（.zip 里需要有 manifest.json）"] = " (the .zip has to contain manifest.json)";
        d["整合包里没有找到 modrinth.index.json，可能不是有效的 Modrinth 整合包"] =
            "No modrinth.index.json in the pack — it may not be a valid Modrinth modpack";
        d["整合包「{0}」没有声明 Minecraft 版本，无法安装"] =
            "The modpack “{0}” doesn't declare a Minecraft version, so it can't be installed";
        d["有 {0} 个文件在清单里没有下载地址，已跳过"] =
            "{0} files in the manifest have no download URL and were skipped";
        d["这个整合包清单里没有需要下载的模组文件（可能全靠 overrides 随包携带）"] =
            "This pack's manifest lists no mod files to download (everything may be bundled in overrides)";
        d["整合包里没有找到 manifest.json，可能不是有效的 CurseForge 整合包"] =
            "No manifest.json in the pack — it may not be a valid CurseForge modpack";
        d["CurseForge 整合包里的 {0} 个模组需要登录 CurseForge 才能自动下载，"] =
            "The {0} mods in this CurseForge pack need a CurseForge login to download automatically,";
        d["本次安装会先建好实例与加载器，这些模组请手动补齐"] =
            "so the instance and loader are set up first — add those mods yourself";
        d["整合包文件不存在或不可读"] = "The modpack file is missing or unreadable";
        d["还没有配置 .minecraft 目录，请先到「设置 → 主页」选择游戏目录"] =
            "No .minecraft folder is set yet — choose one under Settings → Home";
        d["准备安装 {0}"] = "Preparing to install {0}";
        d["安装 Minecraft {0}"] = "Installing Minecraft {0}";
        d["安装 {0}"] = "Installing {0}";
        d["安装 Fabric {0}"] = "Installing Fabric {0}";
        d["安装 Quilt"] = "Installing Quilt";
        d["安装 Quilt {0}"] = "Installing Quilt {0}";
        d["安装 Forge {0}"] = "Installing Forge {0}";
        d["安装 NeoForge {0}"] = "Installing NeoForge {0}";
        d["暂不支持自动安装 {0} 加载器，已只安装原版，请手动补齐"] =
            "Automatic install of the {0} loader isn't supported yet — only vanilla was installed, add the loader yourself";
        d["这个整合包的加载器（{0}）没能识别出来，已只安装原版 Minecraft "] =
            "This pack's loader ({0}) couldn't be identified, so only vanilla Minecraft was installed ";
        d["并建好实例「{0}」。请自行补装加载器，否则包里的模组不会生效"] =
            "and the instance “{0}” was created. Install the loader yourself, or the pack's mods won't load";
        d["写入整合包自带文件"] = "Writing the pack's bundled files";
        d["校验实例"] = "Verifying the instance";
        d["实例 {0} 安装完成但解析失败，可能无法启动"] =
            "Instance {0} installed but couldn't be parsed — it may not launch";
        d["找不到 Minecraft {0} 可用的 Fabric 加载器版本"] =
            "No Fabric loader build available for Minecraft {0}";
        d["安装 {0} 需要 Java，但没有找到可用的 Java 运行环境 —— "] =
            "Installing {0} needs Java, but no usable Java runtime was found — ";
        d["请到「设置 → 启动」配置 Java 路径后重试"] =
            "set the Java path under Settings → Game and try again";

        // ===== 路径校验 =====
        d["路径为空"] = "The path is empty";
        d["是 UNC 路径"] = "It's a UNC path";
        d["包含 .. 上跳"] = "It contains a .. segment";
        d["是绝对路径"] = "It's an absolute path";
        d["路径逃出了实例目录"] = "The path escapes the instance folder";

        // ===== 安装进度文案 =====
        d["超时"] = "Timed out";
        d["实例根目录"] = "Instance root";
        d["未命名配装"] = "Untitled loadout";
        d["未命名整合包"] = "Untitled modpack";
        d["配装分享码"] = "Loadout share code";
        d["未知版本"] = "Unknown version";
        d["原版（无加载器）"] = "Vanilla (no loader)";
        d["配装码安装"] = "Loadout install";
        d["整合包安装"] = "Modpack install";
        d["这个{0}没有需要在线下载的文件，内容全部随包携带"] =
            "This {0} has no files to download — everything is bundled";
        d["尚未创建"] = "Not created yet";
        d["无待下载文件（全靠随包文件）"] = "Nothing to download (all bundled)";
        d["正在取消…"] = "Cancelling…";
        d["正在安装{0}"] = "Installing {0}";
        d["安装失败"] = "Install failed";
        d["安装已取消"] = "Install cancelled";
        d["下载失败"] = "Download failed";
        d["未创建"] = "Not created";
        d["已创建实例 {0}"] = "Created instance {0}";
        d["下载 {0} 个文件"] = "Downloaded {0} files";
        d["写入 {0} 个随包文件"] = "Wrote {0} bundled files";
        d["{0} 个文件未成功"] = "{0} files failed";
        d["准备安装"] = "Preparing install";
        d["解析清单、确认目录"] = "Reading the manifest and checking folders";
        d["安装游戏版本"] = "Installing the game version";
        d["最新版"] = "Latest";
        d["下载配装模组"] = "Downloading loadout mods";
        d["下载整合包文件"] = "Downloading modpack files";
        d["{0} 个文件"] = "{0} files";
        d["写入随包文件"] = "Writing bundled files";
        d["确认可被启动器识别"] = "Checking the launcher can see it";

        // ===== Modrinth =====
        d["连接 Modrinth 超时"] = "Timed out connecting to Modrinth";
        d["请求过于频繁（429），请稍后重试"] = "Too many requests (429) — try again shortly";
        d["Modrinth 服务器返回 {0}（服务端故障，稍后重试）"] =
            "Modrinth returned {0} (their side is having trouble; try later)";
        d["Modrinth 拒绝了本次请求（403），可能是所在网络被限制"] =
            "Modrinth rejected the request (403) — your network may be blocked";
        d["Modrinth 返回 {0}"] = "Modrinth returned {0}";
        d["无法连接 Modrinth"] = "Couldn't connect to Modrinth";
        d["无法访问 Modrinth（{0}）。"] = "Couldn't reach Modrinth ({0}).";
        d["Modrinth 服务器在境外，国内网络通常需要开启代理，"] =
            "Modrinth is hosted overseas, so from mainland China you usually need a proxy,";
        d["或在「设置 → 下载」中确认代理配置后重试。"] =
            "or check the proxy settings under Settings → Download and try again.";
        d["连接 Modrinth 超时。Modrinth 服务器在境外，国内网络通常需要开启代理后重试。"] =
            "Timed out connecting to Modrinth. It's hosted overseas; from mainland China you usually need a proxy.";
        d["无法连接 Modrinth。请检查网络或开启代理后重试。"] =
            "Couldn't connect to Modrinth. Check your network or turn on a proxy and try again.";
        d["Modrinth 返回的数据不完整（响应被中途截断），通常是网络不稳，请重试。"] =
            "Modrinth's response was cut off — usually an unstable connection. Try again.";
        d["加载资源失败：{0}"] = "Couldn't load content: {0}";
        d["已加载 {0} / 共 {1} 条"] = "Loaded {0} of {1}";
        d["下载量"] = "Downloads";
        d["关注数"] = "Followers";
        d["最近更新"] = "Recently updated";
        d["最新发布"] = "Newest";
        d["任意"] = "Any";
        d["默认"] = "Default";

        // ===== 皮肤 =====
        d["皮肤文件不存在。"] = "That skin file doesn't exist.";
        d["皮肤必须是 PNG 图片。"] = "A skin has to be a PNG image.";
        d["无法读取皮肤文件：不是有效的图片。"] = "Couldn't read the skin file — it isn't a valid image.";
        d["皮肤尺寸必须是 64×64 或 64×32，当前为 {0}×{1}。"] =
            "A skin must be 64×64 or 64×32 — this one is {0}×{1}.";
        d["无法读取皮肤文件：{0}"] = "Couldn't read the skin file: {0}";
        d["皮肤已保存，但从皮肤裁剪头像失败。"] =
            "The skin was saved, but cutting the head out of it failed.";
        d["账户缺少有效的访问令牌，请重新登录该微软账户。"] =
            "This account has no valid access token — sign in to the Microsoft account again.";
        d["访问令牌已失效，请重新登录该微软账户。"] =
            "The access token expired — sign in to the Microsoft account again.";
        d["该账户没有更换皮肤的权限（可能需要拥有 Minecraft Java 版，或账号受家长控制限制）。"] =
            "This account can't change skins (it may not own Minecraft Java, or it's under parental controls).";
        d["该微软账户下没有找到 Minecraft 档案（可能尚未购买 Java 版）。"] =
            "No Minecraft profile found for this Microsoft account (Java Edition may not be owned).";
        d["请求过于频繁，请稍后再试。"] = "Too many requests — try again later.";
        d["服务器返回 {0}。"] = "The server returned {0}.";

        // ===== 工具箱 / 热键 =====
        d["打不开工具箱：{0}"] = "Couldn't open the toolbox: {0}";
        d["无法打开工具箱：{0}"] = "Couldn't open the toolbox: {0}";
        d["游戏运行时按 {0} 可以唤出工具箱，但这几个组合键都被别的软件占用了"] =
            "In-game you're meant to press {0} to open the toolbox, but every one of those shortcuts is taken by other software";
        d["（{0}）。可以先从托盘菜单打开工具箱，或者关掉占用它的软件。"] =
            " ({0}). You can open the toolbox from the tray menu, or close whatever is holding them.";
        d["快捷键不可用"] = "Shortcut unavailable";
        d["{0} 被别的软件占用了，本局游戏改用 {1} 唤出工具箱。"] =
            "{0} is taken by other software, so this session uses {1} to open the toolbox.";
        d["快捷键已调整"] = "Shortcut changed";

        // ===== 更新检查 =====
        d["没有取到任何发布记录"] = "No releases came back";
        d["远端版本号无法识别（{0}）"] = "Couldn't read the remote version number ({0})";
        d["当前已是最新版本（{0}）"] = "You're on the latest version ({0})";
        d["发现新版本 {0}（当前 {1}）"] = "New version {0} is available (you have {1})";
        d["检查已取消"] = "Check cancelled";
        d["网络不通，暂时无法检查更新"] = "No network right now, so updates can't be checked";

        // ===== 版本下载 =====
        d["未配置 .minecraft 目录"] = "No .minecraft folder configured";
        d["版本信息为空"] = "The version info is empty";
        d["下载版本信息"] = "Downloading version info";
        d["写入版本文件失败（{0}）：{1}"] = "Couldn't write the version file ({0}): {1}";
        d["版本文件已下载，但解析失败：{0}（版本信息可能不完整，请重试或换一个版本）"] =
            "The version file downloaded but couldn't be parsed: {0} (the data may be incomplete — try again or pick another version)";
        d["准备下载依赖"] = "Preparing to download dependencies";
        d["下载文件 {0}/{1}"] = "Files {0}/{1}";
        d["有 {0} 个依赖文件下载失败（可能是网络波动、磁盘空间不足，"] =
            "{0} dependency files failed to download — network hiccups, low disk space,";
        d["或文件被正在运行的游戏占用）。请在「设置 → 下载」调整下载源 / 线程数后重试。"] =
            "or the files being held open by the running game. Adjust the source or concurrency under Settings → Download and try again.";
        d["下载依赖文件失败（{0}）"] = "Couldn't download dependency files ({0})";
        d["版本清单里没有可用的下载地址"] = "The version manifest has no usable download URL";

        // ===== 下载引擎 =====
        d["服务器返回 404（该版本在下载源上不存在）"] =
            "The server returned 404 (that version doesn't exist on the source)";
        d["服务器返回 {0}（下载源端故障，通常稍后重试即可）"] =
            "The server returned {0} (the source is having trouble; usually worth retrying later)";
        d["下载到的文件是空的（服务器可能返回了错误页面）"] =
            "The downloaded file is empty (the server probably sent an error page)";
        d["文件大小不符（期望 {0} 字节，实际 {1} 字节），可能被服务器截断"] =
            "File size mismatch (expected {0} bytes, got {1}) — it was probably cut off";
        d["文件校验失败（SHA1 不匹配），下载内容已损坏"] =
            "File verification failed (SHA1 mismatch) — the download is corrupt";
        d["不到 1 秒"] = "under a second";
        d["约 {0} 秒"] = "about {0} s";
        d["约 {0} 分 {1} 秒"] = "about {0} min {1} s";
        d["下载目标路径为空"] = "The download target path is empty";
        d["没有可用的下载地址：{0}"] = "No usable download URL: {0}";
        d["{0} 失败，改用 {1}"] = "{0} failed, switching to {1}";
        d["下载失败（{0}）：{1}。"] = "Download failed ({0}): {1}.";
        d["可在「设置 → 下载」切换下载源或增大重试次数后重试。"] =
            "Switch the download source or raise the retry count under Settings → Download, then try again.";
        d["连接后 {0} 秒内没有收到任何数据"] = "No data arrived within {0} s of connecting";
        d["已有 {0} 秒没有再收到数据"] = "No data for {0} s";
        d["多连接分块"] = "Chunked, multi-connection";
        d["单连接"] = "Single connection";
        d["{0} 无响应："] = "{0} isn't responding: ";
        d["下载被中断（连接提前关闭）"] = "The download was interrupted (the connection closed early)";
        d["下载到的 jar/zip 不完整（文件被截断，或下载源返回了错误页面）"] =
            "The downloaded jar/zip is incomplete — cut off, or the source sent an error page";
        d["下载源限流（HTTP {0}）"] = "The source is rate-limiting (HTTP {0})";
        d["文件在该下载源上不存在（404）"] = "The file doesn't exist on that source (404)";
        d["下载源端故障（HTTP {0}）"] = "The source is having trouble (HTTP {0})";
        d["服务器返回 HTTP {0}"] = "The server returned HTTP {0}";
        d["没有可用的下载地址"] = "No usable download URL";
        d["固定镜像"] = "Fixed mirror";
        d["官方优先"] = "Official first";
        d["自动测速"] = "Auto";
        d["不限"] = "Unlimited";
        d["下载设置已迁移：旧「线程数 {0}」→ 并发文件数 {1} + 单文件分块数 {2}"] =
            "Download settings migrated: old “{0} threads” → {1} files at once + {2} chunks per file";

        // ===== 账户 =====
        d["未选择账户"] = "No account selected";
        d["{0} 个账户"] = "{0} accounts";
        d["微软登录失败：{0}"] = "Microsoft sign-in failed: {0}";
        d["用户登录失败"] = "Sign-in failed";
        d["离线用户{0}已创建"] = "Offline user {0} created";

        // ===== 时间与问候 =====
        d["刚刚"] = "just now";
        d["{0} 分钟前"] = "{0} min ago";
        d["{0} 小时前"] = "{0} h ago";
        d["{0} 天前"] = "{0} d ago";
        d["{0} 个月前"] = "{0} mo ago";
        d["{0} 年前"] = "{0} y ago";
        d["凌晨好"] = "Good early morning";
        d["早上好"] = "Good morning";
        d["中午好"] = "Good afternoon";
        d["下午好"] = "Good afternoon";
        d["晚上好"] = "Good evening";
        d["{0}，先登录一个账户再开始"] = "{0} — sign in to an account to get started";
        d["从未启动"] = "Never launched";

        // ===== 安装向导 =====
        d["{0}(查询超时)"] = "{0} (timed out)";
        d["未找到 Java，请先在设置中选择 Java"] = "Java not found — pick one in Settings first";
        d["同一个实例只能安装一个模组加载器，请只勾选其中一个"] =
            "An instance takes only one mod loader — tick just one of them";
        d["正在安装原版 {0}..."] = "Installing vanilla {0}…";
        d["未找到原版版本: {0}"] = "Vanilla version not found: {0}";
        d["安装原版 {0}"] = "Installing vanilla {0}";
        d["原版 {0} 安装完成"] = "Vanilla {0} installed";
        d["未检测到可用的 Java 运行环境，请先在设置里配置 Java 路径"] =
            "No usable Java runtime found — set the Java path in Settings first";
        d["补全原版 {0} 文件"] = "Filling in vanilla {0} files";
        d["安装完成: {0}"] = "Install complete: {0}";
        d["安装中…"] = "Installing…";
        d["重新安装"] = "Reinstall";
        d["一键安装"] = "Install with one click";
        d["没有可用的实例"] = "No instances available";
        d["版本隔离开"] = "Version isolation on";
        d["版本隔离关"] = "Version isolation off";
        d["处理中…"] = "Working…";

        // ===== 联机页状态 =====
        d["未找到 EasyTier 核心，请先下载"] = "EasyTier core not found — download it first";
        d["联机服务未启动"] = "Multiplayer service is off";
        d["虚拟网络已就绪（房主），等待好友加入"] =
            "Virtual network ready (host) — waiting for friends";
        d["已加入虚拟网络（房客），可以在游戏里直接连接"] =
            "Joined the virtual network (guest) — you can connect in-game now";
        d["还没有安装任何 Minecraft 实例，请先在「下载」页装一个版本"] =
            "No Minecraft instance yet — install a version from the Download page";
        d["已安装 {0}，开房后按下面的说明操作即可"] =
            "{0} is installed — open the room and follow the steps below";
        d["已安装 {0}（旧方案），点一键安装会换成 LAN World Plug-n-Play"] =
            "{0} is installed (older approach); one-click install swaps it for LAN World Plug-n-Play";
        d["这个实例是原版，没有加载器 —— 模组装进去也不会被加载"] =
            "This instance is vanilla with no loader, so mods wouldn't be loaded anyway";
        d["读不出这个实例的加载器（版本 JSON 可能损坏），建议先做一次体检"] =
            "Couldn't read this instance's loader (the version JSON may be damaged) — run a check first";
        d["LiteLoader 没有可用的局域网联机模组"] = "No LAN multiplayer mod for LiteLoader";
        d["尚未安装（{0} 实例，可以一键装好）"] =
            "Not installed ({0} instance — one click sorts it out)";
        d["请先从下拉框选择一个实例"] = "Pick an instance from the drop-down first";
        d["正在准备…"] = "Preparing…";
        d["{0} 已安装"] = "{0} installed";
        d["装不了联机模组"] = "Can't install the multiplayer mod";
        d["已取消安装"] = "Install cancelled";
        d["安装联机模组出错：{0}"] = "Error installing the multiplayer mod: {0}";
        d["已生成新联机码：{0}"] = "New room code generated: {0}";
        d["打开下载窗口失败：{0}"] = "Couldn't open the download window: {0}";
        d["EasyTier 核心已就绪，现在可以启动服务了"] =
            "The EasyTier core is ready — you can start the service now";
        d["当前已经是管理员身份"] = "Already running as administrator";
        d["定位不到启动器程序，请手动右键启动器选择「以管理员身份运行」"] =
            "Couldn't locate the launcher executable — right-click it and choose Run as administrator";
        d["没有获得管理员权限，已取消。也可以手动右键启动器选择「以管理员身份运行」"] =
            "Administrator rights weren't granted, so it was cancelled. You can also right-click the launcher and choose Run as administrator.";
        d["请先填写或生成联机码"] = "Fill in or generate a room code first";
        d["请填写房主给你的联机码"] = "Enter the room code your host gave you";
        d["联机码无效。"] = "That room code isn't valid.";
        d["联机码不对"] = "Wrong room code";
        d["启动失败"] = "Start-up failed";
        d["{0}（暂时没取到真实地址，已交给核心自己去取）"] =
            "{0} (couldn't resolve the real address, so the core will fetch it)";
        d["本次实际使用 {0}，另有 {1} 个备用节点"] =
            "Using {0} this time, with {1} backup node(s)";
        d["本次实际使用 {0}"] = "Using {0} this time";
        d["虚拟网络已启动。请进游戏执行「对局域网开放」，再把联机码和你的虚拟 IP 发给好友"] =
            "The virtual network is up. Open to LAN in-game, then send friends the room code and your virtual IP.";
        d["已加入虚拟网络。进游戏后在「多人游戏 → 直接连接」里填房主的虚拟 IP"] =
            "Joined the virtual network. In-game, use Multiplayer → Direct Connect with the host's virtual IP.";
        d["联机服务启动出错：{0}"] = "Error starting the multiplayer service: {0}";
        d["联机服务已停止"] = "Multiplayer service stopped";
        d["停止联机服务出错：{0}"] = "Error stopping the multiplayer service: {0}";
        d["停止失败"] = "Couldn't stop it";
        d["还没有可复制的内容"] = "Nothing to copy yet";
        d["已复制：{0}"] = "Copied: {0}";
        d["已粘贴联机码"] = "Room code pasted";
        d["读取剪贴板失败：{0}"] = "Couldn't read the clipboard: {0}";
        d["复制失败：{0}"] = "Copy failed: {0}";

        // ===== 启动页 / 启动过程 =====
        d["{0} 个实例"] = "{0} instances";
        d["正在统计…"] = "Counting…";
        d["占用 {0}"] = "Using {0}";
        d["无法获取本地版本: {0}"] = "Couldn't load local versions: {0}";
        d["无法刷新本地版本: {0}"] = "Couldn't refresh local versions: {0}";
        d["请选择一个版本"] = "Pick a version";
        d["{0} 正在启动中，请稍候..."] = "{0} is starting — hold on…";
        d["解析版本失败: {0}"] = "Couldn't parse the version: {0}";
        d["未找到版本: {0}"] = "Version not found: {0}";
        d["未找到匹配的 Java 版本，请在设置中选择 Java"] = "No matching Java found — pick one in Settings";
        d["Java 不可用: {0}（该目录下没有 java.dll，请在设置里重新选择 Java）"] =
            "Java unusable at {0} (no java.dll there — pick Java again in Settings)";
        d["请先登录一个账户"] = "Sign in to an account first";
        d["令牌刷新失败，请重新登录微软账户: {0}"] =
            "Token refresh failed — sign in to the Microsoft account again: {0}";
        d["正在检查游戏文件完整性…"] = "Checking game files…";
        d["{0}，已打开「补全资源」窗口"] = "{0} — the Repair files window is open";
        d["无法打开补全资源窗口：{0}"] = "Couldn't open the repair window: {0}";
        d["正在启动 {0}，请稍等..."] = "Starting {0} — one moment…";
        d["启动失败：进程未创建"] = "Launch failed: the process wasn't created";
        d["Minecraft 异常退出（代码 {0}）"] = "Minecraft exited unexpectedly (code {0})";
        d["Minecraft 已正常退出"] = "Minecraft closed normally";
        d["Minecraft 已启动"] = "Minecraft is running";
        d["启动失败: 请登录一个用户"] = "Launch failed: sign in to an account";
        d["启动失败: {0}"] = "Launch failed: {0}";
        d["客户端文件损坏或未下载完整：{0}（{1} 字节）。"] =
            "The client jar is damaged or incomplete: {0} ({1} bytes).";
        d["请在「下载中心」重新安装该版本，或把下载源切换为 BMCLAPI 镜像后重试。"] =
            "Reinstall that version from the Download center, or switch to the BMCLAPI mirror and try again.";
        d["缺少客户端文件：{0}。"] = "The client jar is missing: {0}.";
        d["该版本没有下载完整，请在「下载中心」重新安装（或切换下载源后重试）。"] =
            "That version didn't download completely — reinstall it from the Download center (or switch the source and try again).";
        d["缺少资源索引文件：{0}。"] = "The asset index is missing: {0}.";
        d["说明该版本的资源没下载完整，请在「下载中心」重新安装该版本，"] =
            "That means the version's assets didn't finish downloading — reinstall it from the Download center,";
        d["或把下载源切换为 BMCLAPI 镜像后重试。"] =
            "or switch to the BMCLAPI mirror and try again.";
        d["已删除 {0} 个日志文件，并从此刻重新记录"] =
            "Deleted {0} log files and started logging fresh";
    }
}
