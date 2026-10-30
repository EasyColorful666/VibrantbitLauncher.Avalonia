namespace VibrantbitLauncher.Localization.Translations;

/// <summary>词表第四块之二：联机内核状态、加载器安装、补全资源、依赖体检、配装码。</summary>
internal static class UiMessages2
{
    public static void AddTo(Dictionary<string, string> d)
    {
        // ===== 联机内核 =====
        d["房主"] = "Host";
        d["EasyTier 核心进程已退出。"] = "The EasyTier core process exited.";
        d["已脱钩（启动器退出后仍在运行）"] = "Detached (still running after the launcher quit)";
        d["已接管上次会话（启动器重启后仍在联机）"] = "Took over the previous session (still online after a restart)";
        d["已加入虚拟网络"] = "Joined the virtual network";
        d["核心进程已退出"] = "The core process exited";
        d["EasyTier 核心启动后立即退出了。"] = "The EasyTier core exited right after starting.";
        d["（退出码 {0}）"] = "(exit code {0})";
        d[" 核心输出：{0}"] = " Core output: {0}";
        d[" 提示：EasyTier 需要管理员权限创建虚拟网卡，请关闭启动器后以管理员身份重新打开。"] =
            " Tip: EasyTier needs admin rights to create a virtual adapter. Close the launcher and reopen it as administrator.";
        d["虚拟网络已就绪（房主）"] = "Virtual network ready (host)";
        d["虚拟网络已就绪（房客）"] = "Virtual network ready (guest)";

        // ===== 加载器安装 · 通用步骤 =====
        d["安装完成"] = "Install complete";
        d["解析版本"] = "Parsing version";
        d["解析依赖清单"] = "Reading dependency list";
        d["生成实例版本文件"] = "Writing the instance version file";
        d["安装处理器"] = "Install processor";
        d["运行 {0}（{1}/{2}）"] = "Running {0} ({1}/{2})";
        d["{0}（{1} 安装处理器）"] = "{0} ({1} install processor)";
        d["{0}（已耗时 {1}）"] = "{0} ({1} elapsed)";
        d["{0} 秒"] = "{0} s";
        d["{0} 分 {1} 秒"] = "{0} min {1} s";

        // ===== 加载器安装 · Fabric / Quilt =====
        d["Fabric 版本列表"] = "Fabric versions";
        d["下载 Fabric 版本信息"] = "Download Fabric version info";
        d["Fabric 版本信息"] = "Fabric version info";
        d["下载库 {0}/{1}"] = "Libraries {0}/{1}";
        d["下载库 {0}/{1} · {2}"] = "Libraries {0}/{1} · {2}";
        d["Fabric 版本信息里没有任何可下载的依赖库，安装信息可能已损坏，请重新选择加载器版本"] =
            "The Fabric version info lists no downloadable libraries, so the install data may be damaged. Pick the loader version again.";
        d["找不到 Minecraft {0} 可用的 Quilt 版本"] = "No Quilt build available for Minecraft {0}";
        d["准备 Quilt {0}"] = "Preparing Quilt {0}";
        d["获取 Quilt 版本信息"] = "Fetching Quilt version info";
        d["Quilt 版本信息"] = "Quilt version info";
        d["Quilt 版本信息解析失败"] = "Couldn't parse the Quilt version info";
        d["下载 Quilt 依赖库"] = "Downloading Quilt libraries";
        d["Quilt 安装完成"] = "Quilt installed";
        d["Quilt 版本列表"] = "Quilt versions";

        // ===== 加载器安装 · 通用流程 =====
        d["找不到 Minecraft {0} 可用的 {1} 版本"] = "No {1} build available for Minecraft {0}";
        d["Minecraft {0} 没有可用的 {1} 版本"] = "No {1} build is available for Minecraft {0}";
        d["准备 {0} {1}"] = "Preparing {0} {1}";
        d["下载 {0} 安装器"] = "Downloading the {0} installer";
        d["下载 {0} 安装器 {1}%"] = "Downloading the {0} installer {1}%";
        d["下载 {0} 安装器 {1} MB"] = "Downloading the {0} installer {1} MB";
        d["{0} 安装器下载不完整（文件被服务器多次截断）。"] =
            "The {0} installer download is incomplete (the server cut it off repeatedly).";
        d["已尝试官方源与 BMCLAPI 镜像。"] = "Both the official source and the BMCLAPI mirror were tried.";
        d["请检查网络稳定性，或在「设置 → 下载」切换下载源后重试"] =
            "Check how stable your connection is, or switch the download source under Settings → Download and try again";
        d["{0} 安装器下载失败：{1}。"] = "Couldn't download the {0} installer: {1}.";
        d["请在「设置 → 下载」切换下载源（或开启镜像）后重试"] =
            "Switch the download source (or turn on the mirror) under Settings → Download and try again";
        d["解析 {0} 安装清单"] = "Reading the {0} install manifest";
        d["{0} 安装器里没有 version.json"] = "The {0} installer has no version.json";
        d["{0} 的 version.json 解析失败"] = "Couldn't parse {0}'s version.json";
        d["{0} 安装器里没有 install_profile.json"] = "The {0} installer has no install_profile.json";
        d["{0} 的 install_profile.json 解析失败"] = "Couldn't parse {0}'s install_profile.json";
        d["{0} {1} 是 1.12 及以前的老版本，安装结构不同，暂不支持自动安装。"] =
            "{0} {1} is a legacy build (1.12 or earlier) with a different layout, so automatic install isn't supported yet.";
        d["请在「下载中心 → 版本」里手动安装该加载器，或换一个较新的游戏版本"] =
            "Install that loader manually from Download center → Versions, or pick a newer game version";
        d["下载 {0} 依赖库"] = "Downloading {0} libraries";
        d["运行 {0} 安装处理器"] = "Running the {0} install processor";
        d["{0} 安装完成"] = "{0} installed";
        d["{0} 版本列表"] = "{0} versions";
        d["Forge 版本列表（官方）"] = "Forge versions (official)";
        d["NeoForge 版本列表（官方）"] = "NeoForge versions (official)";
        d["获取 {0} 版本列表失败（官方源与 BMCLAPI 都不可用）。"] =
            "Couldn't fetch the {0} version list — neither the official source nor BMCLAPI responded.";
        d["请检查网络或代理后重试"] = "Check your network or proxy and try again";
        d["有 {0} 个加载器依赖库下载失败：{1}。"] = "{0} loader libraries failed to download: {1}.";
        d["可在「设置 → 下载」切换下载源或增大重试次数后重试"] =
            "Switch the download source or raise the retry count under Settings → Download, then try again";
        d["{0} 安装处理器没有产出 {1}。"] = "The {0} install processor didn't produce {1}.";
        d["这个文件是处理器把原版 client.jar 打补丁后生成的，缺失它游戏会报"] =
            "That file is patched out of the vanilla client.jar by the processor; without it the game reports";
        d["请确认磁盘空间充足（需要约 100 MB 临时空间）后重试安装。"] =
            "Make sure there's enough disk space (about 100 MB free) and install again.";
        d["加载器安装处理器缺少 jar 声明"] = "The loader install processor is missing its jar declaration";
        d["加载器安装处理器缺少依赖：{0}。"] = "The loader install processor is missing a dependency: {0}.";
        d["多为依赖库下载不完整，请在「设置 → 下载」切换下载源后重试"] =
            "That usually means an incomplete library download — switch the source under Settings → Download and retry";
        d["加载器安装包里缺少 {0}（安装包可能不完整，请重新安装）"] =
            "The loader package is missing {0} (it may be incomplete — install again)";
        d["无法启动 Java 进程（检查「设置 → 启动」里的 Java 路径）"] =
            "Couldn't start the Java process — check the Java path under Settings → Game";
        d["加载器安装处理器 {0} 连续 {1} 分钟无输出，判定为僵死，已终止"] =
            "The {0} install processor said nothing for {1} minutes and was terminated as stalled";
        d["处理器连续 {0} 分钟没有任何输出，已判定为卡死并终止。"] =
            "The processor went {0} minutes without any output and was terminated as stalled.";
        d["多为依赖库不完整或网络被墙导致处理器内部下载挂起，请切换下载源后重试。"] =
            "Usually an incomplete library or a blocked network hung its internal download — switch the source and try again.";
        d["加载器安装处理器执行失败（退出码 {0}）。"] = "The loader install processor failed (exit code {0}).";
        d["Java 输出：{0}"] = "Java output: {0}";
        d["压缩包里没有 version.json / install_profile.json，不是有效的加载器安装器"] =
            "The archive has neither version.json nor install_profile.json, so it isn't a valid loader installer";
        d["文件不完整（下载被中途截断，或下载源返回了错误页面）"] =
            "The file is incomplete — the download was cut off, or the source returned an error page";
        d["文件无法读取（{0}）"] = "The file can't be read ({0})";

        // ===== 下载错误 =====
        d["{0}：{1} 失败，改用 {2}"] = "{0}: {1} failed, switching to {2}";
        d["可在「设置 → 下载」把下载源切换为 BMCLAPI 镜像，或检查网络与代理后重试。"] =
            "Switch the source to the BMCLAPI mirror under Settings → Download, or check your network and proxy and try again.";
        d["可在「设置 → 下载」切换下载源、增大重试次数，或关闭正在运行的游戏后重试。"] =
            "Switch the download source or raise the retry count under Settings → Download, or close the running game and try again.";
        d["服务器返回 404（该版本在 Fabric 源上不存在）"] =
            "The server returned 404 (that version doesn't exist on the Fabric source)";
        d["服务器返回 400（请求的 Fabric 版本标识不被接受）"] =
            "The server returned 400 (the requested Fabric version was rejected)";
        d["服务器限流（{0}）—— 镜像站在并发过高时会这样，稍后重试即可"] =
            "The server is rate-limiting ({0}) — mirrors do this under heavy load. Try again shortly.";
        d["服务器返回 {0}（Fabric 源端故障，通常稍后重试即可）"] =
            "The server returned {0} (Fabric's side is having trouble; usually worth retrying later)";
        d["网络错误"] = "Network error";
        d["网络错误（{0}）"] = "Network error ({0})";
        d["服务器返回的数据不完整（响应被中途截断），通常是网络不稳"] =
            "The server's response was cut off — usually an unstable connection";
        d["获取 Minecraft 版本清单失败（{0}）。"] = "Couldn't fetch the Minecraft version manifest ({0}).";
        d["每个源 {0} 次。请检查网络或代理后重试。"] =
            "{0} attempts per source. Check your network or proxy and try again.";
        d["清单里没有任何版本记录"] = "The manifest has no version entries";

        // ===== 目录链接 =====
        d["新位置和原位置是同一个目录，不需要移动"] =
            "The new location is the same folder as the old one — nothing to move.";
        d["原位置已有内容。请先把里面的文件移动到新位置，再回来重新选择"] =
            "The new location already has files in it. Move them out first, then come back and pick again.";
        d["建立目录链接失败："] = "Couldn't create the folder link: ";
        d["拆除目录链接失败："] = "Couldn't remove the folder link: ";
        d["无法启动 mklink"] = "Couldn't start mklink";
        d["mklink 返回失败"] = "mklink returned a failure";

        // ===== 资源完整性 =====
        d["支持的文件"] = "Supported files";
        d["客户端"] = "Client";
        d["资源索引"] = "Asset index";
        d["依赖库"] = "Libraries";
        d["资源文件"] = "Assets";
        d["资源完整"] = "Files complete";
        d["检测到 {0} 类资源缺失（{1}）"] = "Found {0} kind(s) of missing files ({1})";
        d["客户端文件缺失"] = "Client jar missing";
        d["{0}.jar 不存在（{1}）"] = "{0}.jar is missing ({1})";
        d["客户端文件损坏"] = "Client jar damaged";
        d["{0}.jar 只有 {1} 字节，或不是有效的 jar（多半是被限速 / 代理截断）"] =
            "{0}.jar is only {1} bytes, or isn't a valid jar — usually throttling or a proxy cutting it off";
        d["资源索引缺失"] = "Asset index missing";
        d["assets/indexes/{0}.json 不存在（缺了它游戏会在加载资源阶段直接退出）"] =
            "assets/indexes/{0}.json is missing; without it the game quits while loading assets";
        d["资源文件缺失"] = "Assets missing";
        d["assets/objects 下缺 {0} 个资源文件（音效 / 语言 / 贴图等）"] =
            "{0} asset files are missing under assets/objects (sounds, languages, textures…)";
        d[" 等 {0} 个"] = " and {0} more";
        d["依赖库缺失"] = "Libraries missing";
        d["缺 {0} 个库文件：{1}{2}"] = "{0} library files are missing: {1}{2}";
        d["请检查磁盘空间，或点「浏览…」换一个目录。"] =
            "Check your disk space, or click Browse… to pick another folder.";
        d["该版本已存在于 {0}"] = "That version already exists at {0}";

        // ===== Java 安装 =====
        d["安装包方式失败，改用压缩包部署…"] = "The installer method failed — falling back to the archive…";
        d["检查安装目录权限"] = "Checking install folder permissions";
        d["准备下载 JDK {0} 安装包（{1}）"] = "Preparing to download the JDK {0} installer ({1})";
        d["下载 JDK {0} 安装包　{1}"] = "Downloading the JDK {0} installer  {1}";
        d["JDK 安装包下载不完整，请重试或换个网络环境"] =
            "The JDK installer download is incomplete. Try again, or on another network.";
        d["JDK 安装包大小不符（期望 {0} 字节，实际 {1} 字节）"] =
            "The JDK installer size doesn't match (expected {0} bytes, got {1})";
        d["校验 SHA256"] = "Verifying SHA256";
        d["JDK 安装包校验失败（SHA256 不匹配），请重试"] =
            "The JDK installer failed verification (SHA256 mismatch). Try again.";
        d["正在静默安装…（安装期间请勿关闭启动器）"] =
            "Installing silently… (don't close the launcher while it runs)";
        d["安装程序返回 {0}（{1}），"] = "The installer returned {0} ({1}),";
        d["JDK {0} 安装完成"] = "JDK {0} installed";
        d["无法启动 msiexec.exe"] = "Couldn't start msiexec.exe";
        d["安装超时（超过 10 分钟）。可能是杀毒软件拦截了安装程序，或系统里另有安装任务在跑。"] =
            "The install timed out (over 10 minutes). An antivirus may be blocking the installer, or another install is running.";
        d["安装成功"] = "Installed";
        d["用户取消了安装"] = "The install was cancelled";
        d["系统拒绝安装（通常是权限不足，或目标目录被占用）"] =
            "Windows refused the install (usually insufficient rights, or the target folder is in use)";
        d["另一个安装程序正在运行，请稍后重试"] = "Another installer is running — try again shortly";
        d["安装包无法打开（下载不完整或被杀毒软件隔离）"] =
            "The installer can't be opened (incomplete download or quarantined by antivirus)";
        d["安装被系统策略禁止"] = "Blocked by system policy";
        d["已经安装过同一版本"] = "This version is already installed";
        d["安装成功，需要重启系统后生效"] = "Installed — restart for it to take effect";
        d["未知结果"] = "Unknown result";
        d["这个版本没有可用的压缩包，安装失败"] = "No archive is available for this version — install failed";
        d["准备下载 JDK {0}（{1}）"] = "Preparing to download JDK {0} ({1})";
        d["下载 JDK {0}　{1}"] = "Downloading JDK {0}  {1}";
        d["JDK 压缩包下载不完整，请重试或换个网络环境"] =
            "The JDK archive download is incomplete. Try again, or on another network.";
        d["JDK 压缩包大小不符（期望 {0} 字节，实际 {1} 字节）"] =
            "The JDK archive size doesn't match (expected {0} bytes, got {1})";
        d["JDK 压缩包校验失败（SHA256 不匹配），请重试"] =
            "The JDK archive failed verification (SHA256 mismatch). Try again.";
        d["解压 JDK"] = "Extracting JDK";
        d["解压 JDK　{0}%"] = "Extracting JDK  {0}%";
        d["解压完成但找不到 {0}，目录结构可能不符合预期：{1}"] =
            "Extracted, but {0} wasn't found — the folder layout may differ: {1}";
        d["JDK 压缩包是空的"] = "The JDK archive is empty";

        // ===== 局域网联机模组 =====
        d["进游戏 → Esc → 对局域网开放。新出现的「正版验证」按钮选「禁用 + 修复 UUID」"] =
            "In-game → Esc → Open to LAN. On the new “Online mode” button choose “Disable + fix UUID”";
        d["（离线玩家能进，同时尽量保留老玩家的背包和 UUID）；端口可以自己改。"] =
            " (offline players can join while existing inventories and UUIDs are kept); the port is yours to change.";
        d["朋友用你的 EasyTier 虚拟 IP + 这个端口连接；路由器支持 UPnP 的话，"] =
            "Friends connect with your EasyTier virtual IP plus this port. If your router supports UPnP,";
        d["在同一个界面把 UPnP 打开就能走公网直连。"] =
            "turn UPnP on in the same screen to allow direct internet connections.";
        d["未安装"] = "Not installed";
        d["没有选择实例"] = "No instance selected";
        d["请先在「下载」页给这个版本装上 Fabric / Forge / NeoForge，再回来点这个按钮。"] =
            "Install Fabric / Forge / NeoForge for this version on the Download page first, then come back to this button.";
        d["建议先用「版本管理」里的体检/修复功能检查一下这个实例。"] =
            "It's worth running the check/repair in version management on this instance first.";
        d["LiteLoader 没有可用的局域网联机模组，请改用 Fabric 或 Forge 实例。"] =
            "There's no LAN multiplayer mod for LiteLoader — use a Fabric or Forge instance instead.";
        d["暂不支持 {0} 实例。"] = "{0} instances aren't supported yet.";
        d["读不出 {0} 对应的 Minecraft 版本号。"] = "Couldn't read the Minecraft version for {0}.";
        d["无法创建模组目录：{0}"] = "Couldn't create the mods folder: {0}";
        d["正在查询 {0}（{1} / MC {2}）…"] = "Looking up {0} ({1} / MC {2})…";
        d["联机模组：{0} 没有适配 {1} / MC {2} 的版本"] = "LAN mod: {0} has no build for {1} / MC {2}";
        d["装 {0} 需要 Fabric API，"] = "Installing {0} needs Fabric API,";
        d["但在 MC {0} 上没找到可用的 Fabric API 版本。"] =
            "but no usable Fabric API build was found for MC {0}.";
        d["正在下载 {0}…"] = "Downloading {0}…";
        d["下载 {0} 失败，请检查网络后重试。"] = "Couldn't download {0}. Check your network and try again.";
        d["已安装到 {0}"] = "Installed to {0}";
        d["（它们都接管「对局域网开放」，装两份会互相抢界面）。"] =
            " (they all take over Open to LAN, so two of them would fight over the same screen).";
        d["没有适配 {0} 的局域网联机模组"] = "No LAN mod fits {0}";
        d["可以换一个加载器（Fabric 通常支持得最全）或更新的游戏版本试试，"] =
            "Try another loader (Fabric usually has the widest coverage) or a newer game version,";
        d["也可以到 Modrinth 上手动挑一个联机模组放进 mods 目录。"] =
            "or pick a LAN mod on Modrinth yourself and drop it into the mods folder.";
        d["正在补装 Fabric API（{0} 的必需依赖）…"] =
            "Installing Fabric API ({0}'s required dependency)…";

        // ===== 配装码 =====
        d["这个实例是原版（没有装模组加载器），没有模组可以分享。请先给实例装一个加载器"] =
            "This instance is vanilla (no mod loader), so there's nothing to share. Install a loader for it first.";
        d["无法读取模组目录：{0}"] = "Couldn't read the mods folder: {0}";
        d["这个实例的模组目录是空的：{0}"] = "This instance's mods folder is empty: {0}";
        d["分享码是空的"] = "The share code is empty";
        d["分享码格式不对（应以 VBL1- 开头），请检查是否复制完整"] =
            "That share code has the wrong format — it should start with VBL1-. Check you copied all of it.";
        d["分享码内容已损坏（Base64 解码失败）："] = "The share code is damaged (Base64 decode failed): ";
        d["分享码内容已损坏（解压失败）："] = "The share code is damaged (unpacking failed): ";
        d["分享码里的数据读不出来："] = "The data in the share code can't be read: ";
        d["分享码里没有有效的配装数据"] = "The share code carries no valid loadout data";
        d["不认识的分享码格式：{0}"] = "Unrecognised share code format: {0}";
        d["分享码里没有 Minecraft 版本信息"] = "The share code has no Minecraft version info";
        d["这个分享码里既没有可下载的模组，也没有记录游戏版本，无法自动安装"] =
            "This share code has neither downloadable mods nor a game version, so it can't be installed automatically";
        d["生成安装包失败：{0}"] = "Couldn't build the install package: {0}";
        d["没有选择要导入的实例"] = "No instance selected for import";
        d["这个分享码里没有任何可自动下载的模组"] =
            "This share code has no mods that can be downloaded automatically";
        d["下载失败：{0}（{1}）"] = "Download failed: {0} ({1})";
        d["Base64 长度非法"] = "Invalid Base64 length";
        d["配装"] = "Loadout";
        d["已有{0}正在安装，请等它完成后再试"] = "A {0} install is already running — wait for it to finish";
        d["找不到{0}文件，可能已被移动或删除"] = "The {0} file is missing — it may have been moved or deleted";
        d["已取消这次{0}安装"] = "Cancelled this {0} install";
        d["无法安装{0}"] = "Couldn't install {0}";
        d["解析{0}失败：{1}"] = "Couldn't parse {0}: {1}";

        // ===== 日志等级 =====
        d["详细 (Verbose)"] = "Verbose";
        d["调试 (Debug)"] = "Debug";
        d["信息 (Information)"] = "Information";
        d["警告 (Warning)"] = "Warning";
        d["错误 (Error)"] = "Error";
        d["致命 (Fatal)"] = "Fatal";

        // ===== 依赖体检 =====
        d["这个实例还没有装任何模组"] = "This instance has no mods yet";
        d["体检通过：{0} 个模组，依赖完整"] = "Check passed: {0} mods, dependencies complete";
        d["缺 {0} 个依赖"] = "{0} dependencies missing";
        d["{0} 处重复/冲突"] = "{0} duplicates / conflicts";
        d["{0} 个模组加载器不匹配"] = "{0} mods don't match the loader";
        d["{0} 个模组，"] = "{0} mods, ";
        d["这个实例是原版（没装模组加载器），装进来的模组不会生效"] =
            "This instance is vanilla (no mod loader) — mods added to it won't take effect";
        d["{0} 个 {1}"] = "{0} × {1}";
        d["未知加载器"] = "Unknown loader";

        // ===== Modrinth 查询 =====
        d["在 Modrinth 上没有找到这个模组（可能是自制库或纯服务端模组），请手动安装"] =
            "This mod isn't on Modrinth (it may be a homemade library or a server-only mod) — install it manually";
        d["Modrinth 上「{0}」没有可用版本"] = "“{0}” has no available versions on Modrinth";
        d["「{0}」的版本里没有可下载文件"] = "No downloadable file in the versions of “{0}”";
        d["查询 Modrinth 时出错："] = "Error while querying Modrinth: ";
    }
}
