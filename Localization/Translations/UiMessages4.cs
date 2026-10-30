namespace VibrantbitLauncher.Localization.Translations;

/// <summary>词表第四块之四：设置页下拉文案、模组与世界管理、Java 下载、配装导入、皮肤、浮窗状态。</summary>
internal static class UiMessages4
{
    public static void AddTo(Dictionary<string, string> d)
    {
        // ===== 日志与诊断 =====
        d["没有找到日志文件，已从此刻重新记录"] = "No log files found — logging starts fresh from now";
        d["已导出到 {0}"] = "Exported to {0}";
        d["导出失败：没有可导出的日志文件，或目标与源文件相同"] =
            "Export failed: there are no log files to export, or the target is the same as the source";
        d["{0} 个文件 · 共 {1} · 界面显示 {2} 条（最多 {3} 条）"] =
            "{0} files · {1} total · {2} shown here (up to {3})";
        d["已导出日志包到 {0}"] = "Log bundle exported to {0}";
        d["日志包已导出"] = "Log bundle exported";
        d["导出失败："] = "Export failed: ";
        d["导出日志包失败"] = "Couldn't export the log bundle";
        d["设置已恢复默认"] = "Settings restored to defaults";
        d["全部等级"] = "All levels";
        d["信息及以上"] = "Information and above";
        d["警告及以上"] = "Warning and above";
        d["错误及以上"] = "Error and above";
        d["已删除 {0} 个日志文件"] = "Deleted {0} log files";

        // ===== 设置页选项 =====
        d["未设置"] = "Not set";
        d["自动（JVM 默认）"] = "Auto (JVM default)";
        d["G1GC · 通用推荐"] = "G1GC · recommended in general";
        d["ZGC · 大内存低停顿"] = "ZGC · big heaps, low pauses";
        d["ParallelGC · 老版本友好"] = "ParallelGC · friendly to older versions";
        d["SerialGC · 单核 / 小内存"] = "SerialGC · single core / small memory";
        d["不做任何操作"] = "Do nothing";
        d["最小化启动器"] = "Minimize the launcher";
        d["关闭启动器"] = "Close the launcher";
        d["本机内存约 {0} GB"] = "This machine has about {0} GB";
        d["初始内存高于最大内存，启动时会自动按最大内存处理"] =
            "Initial memory is above the maximum, so the maximum gets used at launch";
        d["启动参数：-Xmx{0}m -Xms{1}m"] = "Launch arguments: -Xmx{0}m -Xms{1}m";
        d["BMCLAPI 镜像：国内节点，速度通常更快，但更新可能略滞后于官方"] =
            "BMCLAPI mirror: China-based nodes, usually faster, but updates can lag slightly behind official";
        d["官方源：直接连 Mojang，版本信息最新，国内访问可能较慢"] =
            "Official: connects straight to Mojang with the freshest data; can be slow from China";
        d["BMCLAPI 镜像"] = "BMCLAPI mirror";
        d["官方源"] = "Official";
        d["自动测速（镜像优先，可回退官方）"] = "Auto (mirror first, official as fallback)";
        d["固定镜像（只走镜像）"] = "Fixed mirror (mirror only)";
        d["跟随系统"] = "Follow system";
        d["系统默认"] = "System default";
        d["完整"] = "Full";
        d["精简"] = "Reduced";
        d["无（只保留光斑）"] = "None (glow only)";
        d["纯色"] = "Solid color";
        d["图片 / 内置壁纸"] = "Image / built-in wallpaper";
        d["滑动（上浮淡入）"] = "Slide (float up and fade in)";
        d["渐变（只改透明度）"] = "Fade (opacity only)";
        d["无（直接切换）"] = "None (instant)";
        d["所有过渡与入场动画停止，界面变成「点了就变」。"] =
            "All transitions and entrances stop — the interface just changes when you click.";
        d["保留颜色与明暗反馈，去掉位移和缩放，老机器上更跟手。"] =
            "Keeps colour and light/dark feedback but drops movement and scaling, which feels snappier on old machines.";
        d["所有动效开启。低配电脑可切到「精简」或「关闭」。"] =
            "Everything animates. On slower machines switch to Reduced or Off.";
        d["已开启：动效 / 阴影 / 壁纸已关闭。重启启动器后渲染方式也会切到软件渲染。"] =
            "On: motion, shadows and wallpaper are off. After a restart the renderer switches to software as well.";
        d["为低配电脑（尤其 Windows 7 老机器）关闭动效、阴影与壁纸，并改用软件渲染。"] =
            "Turns off motion, shadows and wallpaper and switches to software rendering, for low-end machines (older Windows 7 PCs especially).";
        d["未设置（使用系统 Mica 质感底）"] = "Not set (uses the system Mica backdrop)";
        d["内置壁纸 · {0}"] = "Built-in wallpaper · {0}";
        d["默认蓝"] = "Default blue";
        d["海洋青"] = "Ocean teal";
        d["森林绿"] = "Forest green";
        d["紫罗兰"] = "Violet";
        d["玫瑰粉"] = "Rose pink";
        d["落日橙"] = "Sunset orange";
        d["石榴红"] = "Garnet red";
        d["靛青蓝"] = "Indigo";
        d["草地"] = "Meadow";
        d["暮色"] = "Dusk";
        d["峡谷"] = "Ravine";
        d["雪原"] = "Snowfield";
        d["主题色已应用 {0}"] = "Accent color {0} applied";
        d["未登录"] = "Not signed in";
        d["在「账户」页登录后即可启动游戏"] = "Sign in on the Accounts page to launch the game";
        d["默认：{0}"] = "Default: {0}";
        d["已登出，可随时在「账户」页切回来"] = "Signed out — switch back anytime on the Accounts page";
        d["未知账户"] = "Unknown account";
        d["第三方认证（Yggdrasil）"] = "Third-party auth (Yggdrasil)";

        // ===== 更新检查 =====
        d["正在检查…"] = "Checking…";
        d["发现新版本"] = "New version available";
        d["稍后"] = "Later";
        d["打不开这个链接"] = "Couldn't open that link";
        d["打不开许可页面"] = "Couldn't open the licence page";
        d["协议全文为空，请点「在线查看」或到仓库根目录阅读 LICENSE.txt。"] =
            "The licence text came back empty — click View online, or read LICENSE.txt in the repository root.";
        d["协议全文没能读出来。可以点下面的「在线查看」，或到仓库根目录打开 LICENSE.txt。"] =
            "The licence text couldn't be read. Use View online below, or open LICENSE.txt in the repository root.";

        // ===== 模组管理 =====
        d["还没有模组"] = "No mods yet";
        d["还没有存档"] = "No worlds yet";
        d["无法创建模组 / 存档目录：{0}"] = "Couldn't create the mods / worlds folder: {0}";
        d["筛选出 {0} 个 · 共 {1} 个（已启用 {2}）"] = "{0} shown · {1} total ({2} enabled)";
        d["共 {0} 个模组 · 已启用 {1}"] = "{0} mods · {1} enabled";
        d["切换模组状态失败：{0}"] = "Couldn't change the mod state: {0}";
        d["所有模组都已启用"] = "All mods are enabled";
        d["所有模组都已禁用"] = "All mods are disabled";
        d["禁用"] = "Disabled";
        d["选择模组文件（可多选）"] = "Choose mod files (multiple allowed)";
        d["已导入 {0} 个模组"] = "Imported {0} mods";
        d["已导入 {0} 个，{1} 个失败"] = "Imported {0}, {1} failed";
        d["没有导入任何模组"] = "No mods were imported";
        d["确认删除"] = "Confirm delete";
        d[" 吗？此操作不可撤销。"] = "? This can't be undone.";
        d["已删除模组 {0}"] = "Deleted mod {0}";
        d["删除失败: {0}"] = "Delete failed: {0}";

        // ===== 存档管理 =====
        d["共 {0} 个存档"] = "{0} worlds";
        d["正在处理上一个存档操作，请稍候"] = "The previous world action is still running — one moment";
        d["选择存档文件夹（内含 level.dat），也可选择包含多个存档的父目录"] =
            "Choose a world folder (the one holding level.dat), or a parent folder with several worlds";
        d["选中的文件夹不是有效的存档（缺少 level.dat）"] =
            "That folder isn't a valid world (no level.dat)";
        d["已导入 {0} 个存档"] = "Imported {0} worlds";
        d["导入失败"] = "Import failed";
        d["选择存档压缩包（可多选）"] = "Choose world archives (multiple allowed)";
        d["导入 {0} 失败：{1}"] = "Couldn't import {0}: {1}";
        d["不是有效的存档压缩包（缺少 level.dat）"] = "That archive isn't a valid world (no level.dat)";
        d["备份存档"] = "Back up world";
        d["已备份到 {0}"] = "Backed up to {0}";
        d["备份失败: {0}"] = "Backup failed: {0}";
        d["名称不能为空"] = "The name can't be empty";
        d["名称包含非法字符"] = "The name contains invalid characters";
        d["已存在名为 {0} 的存档"] = "A world named {0} already exists";
        d["已重命名为 {0}"] = "Renamed to {0}";
        d["重命名失败: {0}"] = "Rename failed: {0}";
        d["已删除存档 {0}"] = "Deleted world {0}";
        d["打开目录失败: {0}"] = "Couldn't open the folder: {0}";
        d["打开目录失败：{0}"] = "Couldn't open the folder: {0}";
        d["没有找到存档"] = "No worlds found";

        // ===== 版本列表 / Java 下载 =====
        d["匹配 {0} / {1} 个版本"] = "{0} of {1} versions match";
        d["共 {0} 个版本"] = "{0} versions";
        d["显示 {0} / 共 {1} 个版本"] = "Showing {0} of {1} versions";
        d["当前筛选条件下没有版本"] = "No versions match the current filter";
        d["资源「{0}」没有有效的项目 ID"] = "“{0}” has no valid project ID";
        d["导航到资源详情页失败"] = "Couldn't open the project page";
        d["正在加载中"] = "Loading";
        d["正在安装版本 {0}"] = "Installing version {0}";
        d["当前平台　{0}"] = "Current platform  {0}";
        d["获取 Java 版本列表失败，请检查网络后点「刷新」重试"] =
            "Couldn't fetch the Java version list — check your network and hit Refresh";
        d["正在获取 Java {0} 的版本…"] = "Fetching Java {0} builds…";
        d["没有找到适用于 {0} 的 Java {1} 发行版"] = "No Java {1} distribution found for {0}";
        d["获取版本列表失败（Adoptium 接口不可达），请稍后点「刷新」重试"] =
            "Couldn't fetch the version list (Adoptium is unreachable) — hit Refresh in a moment";
        d["选择 Java 安装目录"] = "Choose a Java install folder";
        d["准备下载 Java {0}"] = "Preparing to download Java {0}";
        d["Java {0} 安装完成"] = "Java {0} installed";
        d["安装失败：{0}"] = "Install failed: {0}";
        d["Java 安装失败"] = "Java install failed";
        d["已设为启动 Java：{0}"] = "Set as the launch Java: {0}";
        d["已设为启动 Java"] = "Set as the launch Java";
        d["正在扫描本地版本…"] = "Scanning local versions…";
        d["请先选择一个版本"] = "Pick a version first";
        d["无法打开配装码窗口："] = "Couldn't open the loadout code window: ";
        d["（手动指定）"] = "(chosen manually)";
        d["未设置，启动时自动选择"] = "Not set — picked automatically at launch";
        d["选择 Minecraft 文件夹"] = "Choose the Minecraft folder";
        d["已切换到新的 Minecraft 文件夹"] = "Switched to the new Minecraft folder";
        d["切换文件夹失败："] = "Couldn't switch folders: ";
        d["选择 java.exe"] = "Choose java.exe";
        d["这个文件不是可用的 Java（同目录下应当有 java.dll）"] =
            "That file isn't a usable Java (there should be a java.dll next to it)";
        d["选择 Java 失败："] = "Couldn't select Java: ";
        d["已切换 Java：{0}"] = "Switched Java: {0}";
        d["选择字体文件"] = "Choose a font file";
        d["字体文件"] = "Font files";
        d["这个字体文件里读不到字体名，换一个试试"] =
            "Couldn't read a font name from that file — try another";
        d["已切换到 {0}"] = "Switched to {0}";
        d["选择背景图片"] = "Choose a background image";
        d["图片文件"] = "Image files";
        d["选择资源包目录的新位置"] = "Choose a new location for the resource packs folder";
        d["选择存档目录的新位置"] = "Choose a new location for the worlds folder";
        d["已迁移到 {0}"] = "Moved to {0}";
        d["导出日志"] = "Export logs";
        d["日志文件"] = "Log files";
        d["选择 java 可执行文件"] = "Choose the java executable";
        d["Java 可执行文件"] = "Java executables";

        // ===== 资源文件类型与提示 =====
        d["整合包文件"] = "Modpack files";
        d["选择一个版本下载安装；整合包会新建一个实例，自动装好 Minecraft、加载器与包内模组"] =
            "Pick a version to download and install. A modpack creates a new instance and sets up Minecraft, the loader and the pack's mods for you.";
        d["资源包文件"] = "Resource pack files";
        d["选择游戏版本查看可下载的资源包；可添加到任意实例（资源包与加载器无关）"] =
            "Pick a game version to browse downloadable resource packs. They fit any instance (packs don't depend on the loader).";
        d["光影文件"] = "Shader files";
        d["选择游戏版本查看可下载的光影；可添加到任意实例（光影与加载器无关，需 Iris / OptiFine）"] =
            "Pick a game version to browse downloadable shaders. They fit any instance (shaders don't depend on the loader, but need Iris / OptiFine).";
        d["数据包文件"] = "Data pack files";
        d["选择游戏版本查看可下载的数据包；数据包要放进具体存档，请使用「另存为」"] =
            "Pick a game version to browse downloadable data packs. They belong inside a specific world, so use Save as.";
        d["尚未选择实例"] = "No instance selected yet";
        d["版本隔离：开"] = "Version isolation: on";
        d["版本隔离：关"] = "Version isolation: off";
        d["未找到模组文件"] = "No mod files found";
        d["该项目暂无可用的模组文件"] = "This project has no mod files yet";
        d["加载模组文件列表失败: {0}"] = "Couldn't load the mod file list: {0}";
        d["游戏版本：未标注"] = "Game versions: not labelled";
        d["Modrinth 没有给出这个文件支持的游戏版本"] =
            "Modrinth didn't list any game versions for this file";
        d["游戏版本："] = "Game versions: ";
        d[" 等 {0} 个"] = " and {0} more";
        d["支持的游戏版本（共 {0} 个）："] = "Supported game versions ({0}): ";
        d["无法获取文件下载链接"] = "Couldn't get the file's download link";
        d["该文件没有有效的下载链接"] = "That file has no valid download link";
        d["已有整合包正在安装，请等它完成后再试"] = "A modpack is already installing — wait for it to finish";
        d["无法创建临时目录：{0}"] = "Couldn't create the temp folder: {0}";
        d["已有下载任务正在进行，请稍候"] = "A download is already running — one moment";
        d["下载整合包：{0}"] = "Downloading modpack: {0}";
        d["正在下载「{0}」…"] = "Downloading “{0}”…";
        d["下载完成，开始安装"] = "Download finished — starting the install";
        d["整合包下载失败"] = "Modpack download failed";
        d["保存模组文件"] = "Save mod file";
        d["该类型的资源不能直接添加到实例，请使用「另存为」"] =
            "This kind of content can't be added to an instance directly — use Save as";
        d["没有可用的 Minecraft 实例，请先在「下载」页安装一个版本"] =
            "No Minecraft instance available — install a version from the Download page first";
        d["{0} 是{1}实例，装不了需要「{2}」的文件，"] =
            "{0} is a {1} instance and can't take files that need “{2}”,";
        d["请换一个加载器匹配的实例，或使用「另存为」"] =
            "so pick an instance with a matching loader, or use Save as";
        d["无法定位实例目录：{0}"] = "Couldn't locate the instance folder: {0}";
        d["已添加到 {0}：{1}"] = "Added to {0}: {1}";
        d["已保存到：{0}"] = "Saved to: {0}";
        d["文件已保存到：{0}"] = "File saved to: {0}";
        d["下载失败：服务器返回 {0}，请稍后重试"] =
            "Download failed: the server returned {0}. Try again later.";
        d["下载超时，请检查网络后重试"] = "Download timed out — check your network and try again";
        d["网络错误，请检查网络或代理设置后重试"] =
            "Network error — check your network or proxy settings and try again";
        d["下载失败：{0}"] = "Download failed: {0}";
        d["压缩包"] = "Archives";
        d["LiteLoader 模组"] = "LiteLoader mods";
        d["文本文件"] = "Text files";
        d["JSON 文件"] = "JSON files";
        d["YAML 文件"] = "YAML files";
        d["基岩版资源包"] = "Bedrock resource packs";
        d["基岩版世界"] = "Bedrock worlds";
        d["文件 (*{0})"] = "Files (*{0})";

        // ===== 补全资源 / 崩溃修复 =====
        d["检测到当前实例的资源不完整"] = "This instance's files are incomplete";
        d["实例 {0} · 游戏目录 {1}"] = "Instance {0} · game folder {1}";
        d["正在补全…"] = "Repairing…";
        d["（重新体检：资源完整）"] = "(re-checked: files complete)";
        d["已补全"] = "Repaired";
        d["补全未完全成功：{0}，可再点一次「开始补全」重试"] =
            "Repair didn't fully succeed: {0} — hit Start repair again to retry";
        d["补全已取消"] = "Repair cancelled";
        d["补全失败："] = "Repair failed: ";
        d["未能自动识别出明确原因，请查看崩溃报告文件。"] =
            "No clear cause could be identified — check the crash report file.";
        d["修复完成"] = "Fix applied";
        d["已执行 {0} 项修复。请重新启动游戏验证；如果问题依旧，可再次点「一键修复」查看剩余建议。"] =
            "Applied {0} fix(es). Relaunch the game to check; if the problem persists, hit Fix all again for the remaining suggestions.";
        d["部分修复未能完成"] = "Some fixes couldn't be applied";
        d["这条只是建议，需要手动处理"] = "This one is only a suggestion and needs doing by hand";
        d["正在下载 EasyTier 核心…"] = "Downloading the EasyTier core…";
        d["正在解压…"] = "Extracting…";

        // ===== 依赖体检 / 配装分享 =====
        d["还没有安装任何实例"] = "No instances installed yet";
        d["正在读取模组元数据…"] = "Reading mod metadata…";
        d["待补全"] = "To fill in";
        d[" · 需要版本 {0}"] = " · needs version {0}";
        d["{0}：装了 {1} 份 —— "] = "{0}: installed {1} times — ";
        d["{0}　⚠ {1} 模组，与当前实例不匹配，不会加载"] =
            "{0}  ⚠ a {1} mod that doesn't match this instance and won't load";
        d["{0}（没有模组元数据，可能是普通资源包或已损坏）"] =
            "{0} (no mod metadata — it may be a plain resource pack or damaged)";
        d["共 {0} 个可识别模组"] = "{0} recognisable mods";
        d["（其中 {0} 个加载器不匹配）"] = " ({0} don't match the loader)";
        d["，游戏版本 {0}"] = ", game version {0}";
        d["体检失败："] = "Check failed: ";
        d["正在查询 Modrinth（{0}/{1}）：{2}"] = "Querying Modrinth ({0}/{1}): {2}";
        d["可补全 → {0}"] = "Can be filled in → {0}";
        d["查不到"] = "Not found";
        d["没有找到可以自动补齐的依赖 —— 这些模组可能没发布到 Modrinth，需要手动安装。"] =
            "No dependency could be filled in automatically — those mods may not be on Modrinth and have to be installed by hand.";
        d["正在下载（{0}/{1}）：{2}"] = "Downloading ({0}/{1}): {2}";
        d["已补齐 {0}/{1} 个依赖。"] = "Filled in {0}/{1} dependencies.";
        d["建议重新点「开始体检」复核，然后启动游戏验证。"] =
            "Run the check again to confirm, then launch the game.";
        d["已补齐 {0} 个缺失依赖（{1}）"] = "Filled in {0} missing dependencies ({1})";
        d["补齐失败："] = "Couldn't fill them in: ";
        d["补齐依赖失败："] = "Couldn't fill in the dependencies: ";
        d["正在识别模组…"] = "Identifying mods…";
        d["正在识别（{0}/{1}）：{2}"] = "Identifying ({0}/{1}): {2}";
        d["完成：{0} 个模组可复现，分享码 {1} 字符"] =
            "Done: {0} mods can be reproduced, the code is {1} characters";
        d["；另有 {0} 个未收录的模组（对方需自行准备）"] =
            "; {0} more aren't listed (the other side has to supply those)";
        d["生成失败："] = "Generation failed: ";
        d["分享码已复制到剪贴板，发给好友粘贴即可。"] =
            "The share code is on your clipboard — send it to a friend to paste.";
        d["复制失败："] = "Copy failed: ";
        d["保存配装分享码"] = "Save loadout share code";
        d["Vibrantbit 配装分享码"] = "Vibrantbit loadout share code";
        d["保存失败："] = "Save failed: ";
        d["请先把分享码粘贴到上面的文本框。"] = "Paste a share code into the box above first.";
        d["，另有 {0} 个未收录（需自行准备）"] =
            ", plus {0} not listed (you'll have to supply those)";
        d["正在新建实例并安装（进度见弹出的安装详情页）…"] =
            "Creating an instance and installing (watch the progress window)…";
        d["已导入为实例：{0}。可在「启动」页选择它开始游戏。"] =
            "Imported as instance {0}. Pick it on the Launch page to play.";
        d["导入未完成，请查看提示信息。"] = "The import didn't finish — check the message.";
        d["导入失败："] = "Import failed: ";
        d["已把 {0}/{1} 个模组补进实例 {2}。"] = "Added {0}/{1} mods to instance {2}.";
        d["若分享者的加载器 / 游戏版本与这个实例不同，请自行确认兼容性。"] =
            "If the sender's loader or game version differs from this instance, check compatibility yourself.";
        d["已补入 {0} 个模组（{1}）"] = "Added {0} mods ({1})";
        d["游戏版本：分享码里没有记录"] = "Game version: not recorded in the share code";
        d["可自动下载 {0} 个模组"] = "{0} mods can be downloaded automatically";
        d["；另有 {0} 个未收录（自制品，需向分享者索取）"] =
            "; {0} more aren't listed (custom ones — ask the sender for them)";
        d["分享者设置的最大内存：{0} MB"] = "Maximum memory set by the sender: {0} MB";
        d["这个分享码里没有记录 Minecraft 版本，无法自动安装。"] =
            "This share code doesn't record a Minecraft version, so it can't be installed automatically.";
        d["正在取消：当前文件下载完就会停下（已下载的文件会保留）。"] =
            "Cancelling: it stops once the current file finishes (downloaded files are kept).";
        d["取消添加"] = "Cancel adding";
        d["正在生成安装包…"] = "Building the install package…";
        d["已添加"] = "Added";
        d["已添加为实例「{0}」。关闭本窗口后在「启动」页选中它就能开始游戏。"] =
            "Added as instance “{0}”. Close this window, select it on the Launch page and play.";
        d["已取消这次添加。已经下载的文件会保留，重新粘贴配装码可以再来一次。"] =
            "Cancelled. Files already downloaded are kept — paste the code again to start over.";
        d["添加未完成，请查看提示信息后重试。"] = "Adding didn't finish — check the message and try again.";
        d["添加失败："] = "Adding failed: ";
        d["已有整合包正在安装"] = "A modpack is already installing";
        d["请等待当前安装完成后再拖入新的整合包"] =
            "Wait for the current install to finish before dropping in another modpack";
        d["拖入的文件不是整合包。支持 Modrinth 的 .mrpack，以及带 manifest.json 的 CurseForge .zip"] =
            "That file isn't a modpack. Supported: Modrinth .mrpack and CurseForge .zip containing manifest.json";
        d["拖入的文件"] = "The dropped file";
        d["无法打开浏览器"] = "Couldn't open the browser";
        d["安装进度"] = "Install progress";
        d["安装仍在后台继续，可在「下载任务」里查看进度"] =
            "The install keeps running in the background — follow it under Downloads";
        d["「{0}」还没装完。取消后已经下载的文件会保留，但实例可能不完整，需要重新安装。"] =
            "“{0}” hasn't finished. Cancelling keeps what's downloaded, but the instance may be incomplete and need reinstalling.";
        d["确定取消"] = "Yes, cancel";
        d["继续安装"] = "Keep installing";
        d["还没有配置 .minecraft 目录"] = "No .minecraft folder configured yet";
        d["实例目录还不存在，可能安装尚未走到创建实例这一步"] =
            "The instance folder doesn't exist yet — the install may not have reached that step";

        // ===== 皮肤 =====
        d["皮肤管理 - {0}"] = "Skins - {0}";
        d["暂无皮肤预览"] = "No skin preview yet";
        d["预览加载失败"] = "Couldn't load the preview";
        d["已设置自定义皮肤"] = "Custom skin applied";
        d["使用默认皮肤"] = "Use the default skin";
        d["当前没有可用的窗口"] = "No window is available right now";
        d["选择皮肤文件"] = "Choose a skin file";
        d["PNG 图片"] = "PNG images";
        d["已选择：{0}，点击「保存」应用"] = "Selected: {0} — click Save to apply";
        d["已从网址加载皮肤，点击「保存」应用"] = "Skin loaded from the URL — click Save to apply";
        d["加载失败：{0}"] = "Load failed: {0}";
        d["重置皮肤"] = "Reset skin";
        d["确定要将该微软账户的皮肤重置为官方默认皮肤吗？"] =
            "Reset this Microsoft account's skin to the official default?";
        d["确定要清除该账户的本地自定义皮肤并恢复默认皮肤吗？"] =
            "Clear this account's local custom skin and go back to the default?";
        d["重置"] = "Reset";
        d["已重置为官方默认皮肤"] = "Reset to the official default skin";
        d["重置失败：{0}"] = "Reset failed: {0}";
        d["已恢复默认皮肤"] = "Default skin restored";
        d["皮肤已保存"] = "Skin saved";
        d["保存失败：{0}"] = "Save failed: {0}";

        // ===== 游戏内浮窗 =====
        d["{0} · {1} · 正在运行"] = "{0} · {1} · running";
        d["游戏运行时按 {0} 呼出"] = "Press {0} while playing";
        d["{0} 随时呼出"] = "{0} anytime";
        d["关闭（{0} 可再次唤出）"] = "Close ({0} to reopen)";
        d["已换新码。旧码对应的房间不会再有人进来。"] =
            "New code set. Nobody can join the old room any more.";
        d["联机码已复制，发到群里等好友进来。"] =
            "Room code copied — drop it in the group chat and wait for friends.";
        d["复制失败，请手动选中后复制。"] = "Copy failed — select the text and copy it yourself.";
        d["读不到剪贴板内容，请手动粘贴。"] = "Couldn't read the clipboard — paste manually.";
        d["联机服务未就绪。"] = "The multiplayer service isn't ready.";
        d["还没有房间码。"] = "No room code yet.";
        d["请先填入房主给你的联机码。"] = "Enter the room code your host gave you first.";
        d["正在连接虚拟网络…"] = "Connecting to the virtual network…";
        d["下一步：进游戏「对局域网开放」，让好友在「多人游戏 → 直接连接」里填你的虚拟 IP。"] =
            "Next: open to LAN in-game, then have friends enter your virtual IP under Multiplayer → Direct Connect.";
        d["下一步：进游戏「多人游戏 → 直接连接」，填房主的虚拟 IP。"] =
            "Next: in-game, use Multiplayer → Direct Connect with the host's virtual IP.";
        d["启动联机出错：{0}"] = "Error starting multiplayer: {0}";
        d["已断开虚拟网络。"] = "Disconnected from the virtual network.";
        d["断开联机出错：{0}"] = "Error disconnecting: {0}";
        d["虚拟网络已启动（{0}），正在获取虚拟 IP…"] =
            "Virtual network up ({0}), fetching the virtual IP…";
        d["虚拟网络已启动（{0}）· 你的虚拟 IP：{1}"] =
            "Virtual network up ({0}) · your virtual IP: {1}";
    }
}
