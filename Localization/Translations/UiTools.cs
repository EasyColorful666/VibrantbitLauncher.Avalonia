namespace VibrantbitLauncher.Localization.Translations;

/// <summary>词表第三块：实例工具、首次向导、补全资源、崩溃分析、配装码、皮肤与浮窗工具箱。</summary>
internal static class UiTools
{
    public static void AddTo(Dictionary<string, string> d)
    {
        // ===== 实例管理 =====
        d["返回运行页面"] = "Back to Launch";
        d["模组管理"] = "Mods";
        d["存档管理"] = "Worlds";
        d["打开游戏目录"] = "Open game folder";
        d["导入模组"] = "Import mods";
        d["全部启用"] = "Enable all";
        d["全部禁用"] = "Disable all";
        d["打开模组目录"] = "Open mods folder";
        d["搜索模组…"] = "Search mods…";
        d["从其他游戏目录导入存档文件夹（也可选择含多个存档的父目录）"] =
            "Import a world folder from another game directory (you can also pick a parent folder holding several worlds)";
        d["导入存档"] = "Import world";
        d["从 .zip 压缩包导入存档（地图下载常见格式）"] =
            "Import a world from a .zip (the usual format for downloaded maps)";
        d["导入压缩包"] = "Import .zip";
        d["打开存档目录"] = "Open worlds folder";
        d["勾选启用 / 取消勾选禁用"] = "Tick to enable, untick to disable";
        d["删除"] = "Delete";
        d["备份"] = "Back up";
        d["导出为 .zip 压缩包"] = "Export as .zip";
        d["重命名"] = "Rename";
        d["确定"] = "OK";
        d["没有找到模组"] = "No mods found";

        // ===== 首次使用向导 =====
        d["首次使用向导"] = "First-run wizard";
        d["登录账户"] = "Sign in";
        d["登录后才能联机游玩；也可以先跳过，之后随时在「账户」里添加"] =
            "You need an account to play online. You can also skip this and add one later from Accounts.";
        d["还没有登录账户"] = "No account signed in";
        d["选一种方式登录，正版账户可以联机"] =
            "Choose a sign-in method — an official account can play online";
        d["上一步"] = "Back";
        d["跳过"] = "Skip";
        d["一切就绪"] = "All set";
        d["Vibrantbit Launcher 已准备好为您服务"] = "Vibrantbit Launcher is ready for you";
        d["开始使用"] = "Get started";
        d["欢迎使用 Vibrantbit Launcher"] = "Welcome to Vibrantbit Launcher";
        d["轻量、快速、现代的 Minecraft 启动器"] = "A lightweight, fast, modern Minecraft launcher";
        d["开始"] = "Start";
        d["快速设置"] = "Quick setup";
        d["确认游戏目录和 Java 运行环境，之后随时能在「设置」里修改"] =
            "Confirm the game folder and Java runtime — you can change them anytime in Settings";
        d["游戏目录"] = "Game folder";
        d["版本、存档和模组都会放在这个目录下"] =
            "Versions, worlds and mods all go in this folder";
        d["没有自动检测到 Java 时，可以手动指定 java.exe"] =
            "If Java isn't detected automatically, point to java.exe yourself";

        // ===== 补全资源 =====
        d["补全资源"] = "Repair files";
        d["只需补齐下面列出的缺失部分，不会重装整个版本。补全过程会联网下载，国内网络建议在「设置 → 下载」里启用 BMCLAPI 镜像。"] =
            "Only the missing pieces listed below are fetched — nothing is reinstalled. Repair downloads from the network; in mainland China, turn on the BMCLAPI mirror under Settings → Download.";
        d["缺失的资源"] = "Missing files";
        d["尚未开始"] = "Not started";
        d["开始补全"] = "Start repair";

        // ===== 崩溃分析 =====
        d["崩溃分析"] = "Crash analysis";
        d["Minecraft 崩溃分析"] = "Minecraft crash analysis";
        d["游戏进程异常退出。下面是自动分析出的原因与可以直接执行的修复动作。"] =
            "The game exited unexpectedly. Below are the causes found for you and the fixes you can apply right away.";
        d["崩溃原因"] = "Cause";
        d["可疑模组"] = "Suspect mods";
        d["一键修复"] = "Fix all";
        d["全部修复"] = "Repair everything";
        d["没有检测到可以自动修复的问题。请参考上面的原因，或打开崩溃报告进一步排查。"] =
            "Nothing fixable was detected. Check the causes above, or open the crash report to dig further.";
        d["打开崩溃报告目录"] = "Open crash report folder";

        // ===== 联机模块安装 =====
        d["安装联机模块"] = "Install multiplayer module";
        d["将从 GitHub 下载 EasyTier 核心（约 10 MB），解压到程序的 VBL 目录。"] =
            "Downloads the EasyTier core from GitHub (about 10 MB) and unpacks it into the launcher's VBL folder.";

        // ===== 实例工具箱 · 依赖体检 =====
        d["实例工具箱"] = "Instance tools";
        d["依赖体检"] = "Dependency check";
        d["开始体检"] = "Run check";
        d["一键补齐依赖"] = "Fill in missing dependencies";
        d["缺失的依赖"] = "Missing dependencies";
        d["没有缺失依赖。"] = "Nothing missing.";
        d["重复 / 冲突"] = "Duplicates / conflicts";
        d["已安装模组"] = "Installed mods";

        // ===== 配装分享 =====
        d["配装分享"] = "Share loadout";
        d["把当前实例的模组清单压成一段分享码。对方粘贴后可以一键复原同样的配装（模组会从 Modrinth 重新下载）。"] =
            "Packs this instance's mod list into a share code. Whoever pastes it gets the same setup in one click (mods are re-downloaded from Modrinth).";
        d["生成分享码"] = "Generate code";
        d["复制分享码"] = "Copy code";
        d["保存为文件"] = "Save to file";
        d["生成的分享码会显示在这里；也可以把别人的分享码粘贴进来，再点下面的按钮导入"] =
            "The generated code shows up here. You can also paste someone else's code and use the buttons below to import it.";
        d["识别"] = "Read code";
        d["新建实例并导入"] = "Create a new instance and import";
        d["只补模组到选中实例"] = "Only add mods to the selected instance";

        // ===== 开源许可 =====
        d["开源许可"] = "Open-source licence";
        d["GNU Affero 通用公共许可证"] = "GNU Affero General Public License";
        d["第 3 版，2007 年 11 月 19 日 · 本启动器以 AGPL-3.0 开源：可以自由使用、修改、分发，修改后的版本也必须以同样的协议开放源代码。"] =
            "Version 3, 19 November 2007 · This launcher is open source under AGPL-3.0: you may use, modify and redistribute it freely, and modified versions must be released under the same licence.";
        d["在线查看"] = "View online";

        // ===== 配装码导入 =====
        d["朋友发来一串配装码？粘贴到下面，会自动下载 Minecraft 本体、对应的模组加载器和他的模组，装成一个完整实例。本机还没有 Minecraft 也能直接用。"] =
            "Got a loadout code from a friend? Paste it below and it downloads Minecraft, the matching mod loader and their mods, then builds a complete instance. Works even if Minecraft isn't installed yet.";
        d["在这里粘贴配装码（VBL1- 开头，一长串字母数字），也支持直接粘贴分享码里的 JSON"] =
            "Paste the loadout code here (starts with VBL1-, a long run of letters and digits). Pasting the raw JSON from a code works too.";
        d["安装需要联网下载（Minecraft 本体 + 加载器 + 模组），国内网络建议先在「设置 → 下载」启用 BMCLAPI 镜像。下载过程会弹出进度详情页，可在「下载任务」里随时查看。"] =
            "Installing downloads from the network (Minecraft + loader + mods); in mainland China, turn on the BMCLAPI mirror under Settings → Download first. A progress window opens and you can revisit it anytime from Downloads.";
        d["支持 VBL1- 前缀的分享码；粘贴后会自动识别。"] =
            "Supports share codes with the VBL1- prefix; they're recognised as soon as you paste them.";
        d["开始添加"] = "Start adding";
        d["多人"] = "Multiplayer";
        d["松开即可安装整合包"] = "Drop to install the modpack";
        d["支持 Modrinth (.mrpack) 与 CurseForge (.zip) 整合包"] =
            "Supports Modrinth (.mrpack) and CurseForge (.zip) modpacks";

        // ===== 微软登录验证 =====
        d["微软账户验证"] = "Microsoft account verification";
        d["微软账户登录验证"] = "Microsoft sign-in verification";
        d["请在浏览器中打开验证页面，输入以下一次性代码完成登录。代码已自动复制到剪贴板。"] =
            "Open the verification page in your browser and enter the one-time code below. It's already on your clipboard.";
        d["代码已复制到剪贴板，点击下方按钮打开浏览器"] =
            "The code is on your clipboard — click the button below to open your browser";
        d["打开浏览器"] = "Open browser";

        // ===== 整合包安装进度 =====
        d["文件清单"] = "File list";
        d["在资源管理器中打开新实例目录"] = "Show the new instance folder in File Explorer";
        d["打开实例目录"] = "Open instance folder";
        d["取消安装"] = "Cancel install";
        d["完成"] = "Done";

        // ===== 离线账户 =====
        d["添加离线账户"] = "Add offline account";
        d["添加外置账户"] = "Add external account";
        d["填入 Yggdrasil 认证服务器地址（如 LittleSkin）"] =
            "Enter the Yggdrasil auth server address (e.g. LittleSkin)";
        d["服务器地址"] = "Server address";
        d["用户名"] = "Username";
        d["请输入游戏内用户名"] = "Enter the in-game username";
        d["邮箱"] = "Email";
        d["密码"] = "Password";
        d["登录"] = "Sign in";

        // ===== 皮肤管理 =====
        d["皮肤管理"] = "Skins";
        d["角色正面预览"] = "Front view";
        d["手臂模型"] = "Arm model";
        d["经典（粗手臂）"] = "Classic (wide)";
        d["纤细（细手臂）"] = "Slim (narrow)";
        d["选择皮肤"] = "Choose a skin";
        d["支持 64×64 或 64×32 的 PNG 文件"] = "PNG files at 64×64 or 64×32";
        d["从文件选择"] = "Choose a file";
        d["从网址加载"] = "Load from URL";
        d["重置为默认皮肤"] = "Reset to the default skin";
        d["保存"] = "Save";
        d["从网址加载皮肤"] = "Load a skin from a URL";
        d["皮肤文件直链（PNG）"] = "Direct link to the skin file (PNG)";

        // ===== 游戏内浮窗 =====
        d["Minecraft 工具箱"] = "Minecraft toolbox";
        d["快速联机"] = "Quick multiplayer";
        d["未检测到正在运行的游戏"] = "No game is running";
        d["我是房主"] = "I'm the host";
        d["我是房客"] = "I'm a guest";
        d["把这个码发给好友"] = "Send this code to your friends";
        d["换一个码"] = "New code";
        d["粘贴房主给你的联机码，例如 U/ABCD-EFGH-JKLM-NPQR"] =
            "Paste the room code from your host, e.g. U/ABCD-EFGH-JKLM-NPQR";
        d["从剪贴板粘贴"] = "Paste from clipboard";
        d["开启联机"] = "Start multiplayer";
        d["断开"] = "Disconnect";
        d["尚未开启虚拟网络。"] = "Virtual network is off.";
        d["Esc 关闭"] = "Esc closes";
        d["游戏中按 Alt+Shift+T 呼出"] = "Alt+Shift+T in-game";

        // ===== 实例工具箱 · 导出整合包 =====
        d["导出整合包"] = "Export modpack";
        d["把当前实例打包成一个能发给别人的整合包。对方用启动器导入后会自动装好同样的游戏版本与加载器，模组按清单补齐。"] =
            "Packs this instance into a modpack you can hand to someone else. Their launcher will set up the same game version and loader, then fill in the mods from the manifest.";
        d["基本信息"] = "Basics";
        d["名称"] = "Name";
        d["留空就用实例名"] = "Leave blank to use the instance name";
        d["版本号"] = "Version";
        d["简介"] = "Summary";
        d["一句话说明这个整合包是干什么的（可留空）"] = "One line about what this pack is for (optional)";
        d["格式"] = "Format";
        d["Modrinth 整合包（.mrpack）"] = "Modrinth modpack (.mrpack)";
        d["体积小：清单里只记「哪个模组、哪一版、从哪下」，对方启动器自己去下。没发布到 Modrinth 的模组会一起打进包里。"] =
            "Small: the manifest only records which mod, which version, and where to get it, and the other launcher downloads them. Mods that aren't on Modrinth get bundled into the pack.";
        d["普通压缩包（.zip）"] = "Plain archive (.zip)";
        d["不联网：所有模组原样装进包里，谁都能装，代价是体积大。解压后也可以直接当一份游戏目录用。"] =
            "Offline: every mod is packed as-is, so anyone can install it — the trade-off is size. Unzipped, it also works as a plain game folder.";
        d["打包内容"] = "What to include";
        d["配置（config / defaultconfigs / kubejs / scripts）"] =
            "Config (config / defaultconfigs / kubejs / scripts)";
        d["光影包"] = "Shader packs";
        d["选项文件（键位与视频设置，会覆盖对方的）"] =
            "options.txt (keybinds and video settings — overwrites theirs)";
        d["存档（含你的建筑与坐标，默认不带）"] =
            "Worlds (contains your builds and coordinates — off by default)";
        d["Modrinth 上查不到的模组一并打包"] = "Bundle mods that aren't on Modrinth";

        d["Modrinth 整合包"] = "Modrinth modpack";
        d["整合包压缩包"] = "Modpack archive";
        d["正在准备…"] = "Preparing…";
        d["打开保存对话框失败：{0}"] = "Could not open the save dialog: {0}";
        d["已导出 {0}（{1}）：模组 {2} 个，自动下载 {3} 个，随包 {4} 个，未包含 {5} 个"] =
            "Exported {0} ({1}): {2} mods — {3} auto-downloaded, {4} bundled, {5} left out";
        d["整合包已导出：{0}"] = "Modpack exported: {0}";
        d["导出失败：{0}"] = "Export failed: {0}";

        d["没有选择实例"] = "No instance selected";
        d["没有选择保存位置"] = "No save location chosen";
        d["读不出这个实例的游戏版本，导出的整合包对方会不知道装哪个版本。请确认这个实例能正常启动一次"] =
            "Can't read this instance's game version, so the pack wouldn't tell anyone what to install. Try launching the instance once first.";
        d["无法读取模组目录：{0}"] = "Could not read the mods folder: {0}";
        d["正在校验模组（{0}/{1}）：{2}"] = "Checking mods ({0}/{1}): {2}";
        d["{0}：Modrinth 上查不到，又没有选择一并打包，对方会缺这个模组"] =
            "{0}: not found on Modrinth and not bundled, so the other side will be missing this mod";
        d["没读到这个实例的加载器版本号，清单里写的是 latest，对方启动器可能需要自己选一下版本"] =
            "Couldn't read the loader version for this instance — the manifest says latest, so the other launcher may ask them to pick one";
        d["这是原版实例，导出包里不含加载器，对方装上就能直接玩"] =
            "This is a vanilla instance, so the pack ships without a loader — it will just work for them";
        d["正在写入整合包清单…"] = "Writing the modpack manifest…";
        d["正在打包模组：{0}"] = "Packing mod: {0}";
        d["正在打包 {0}…"] = "Packing {0}…";
        d["打包失败：{0}"] = "Packing failed: {0}";
        d["Minecraft 整合包"] = "Minecraft modpack";
    }
}
