# 更新日志（Avalonia 版）

本文件记录 VibrantbitLauncher.Avalonia 的所有重要变更。

> 本工程是 WPF 版 VibrantbitLauncher 的 Avalonia 重构分支，与之并存、互不影响。
> 版本号唯一来源是 `VibrantbitLauncher.Avalonia.csproj` 的 `<Version>`（四段式 `主.次.修订.补丁`）。


## [1.0.8.2] - 2026-10-18

### 修复

- **内嵌的子页被改造成了「页面」**。1.0.8.0 / 1.0.8.1 把子窗口拆成了 `UserControl` 之后，
  为了让内嵌页有个标题，把每个子页**自己那条标题栏隐藏了**，改由宿主顶栏显示
  「← 返回 / 标题 / ×」。这等于把「窗口」改成了「页面」—— 与 PCL2 的做法并不一样：
  PCL2 里那个「登录 Minecraft」是一整个窗口（有自己的标题栏和关闭按钮）浮在主界面上。

  现在改回「窗口原样嵌进来」：
  - 每个子页恢复**自己的标题栏**（`Title="补全资源"` 这类原生标题，不再是宿主顶栏）；
  - 宿主不再有任何自己的标题栏，只提供一层半透明遮罩 + 居中的边框与投影，
    看起来就是「原来那个窗口浮在主界面上」；
  - `TitleBar` 新增 `IsInline`：内嵌页的标题栏**不 Hook 主窗口**
    （否则点 × 会把整个启动器关掉）、隐藏最小化/最大化、× 改为关闭当前内嵌页；
  - 子页仍是原来的尺寸（380×330 / 860×700 …），一字未改。

## [1.0.8.1] - 2026-10-18

### 修复

- **内嵌子页被拉满整个内容区**。1.0.8.0 把子窗口改成内嵌时，只保留了「换成 UserControl」，
  却没保留原来的 `Width` / `Height` —— 于是 380×330 的「添加离线账户」表单被拉到 1000px 宽，
  输入框横成一整条，观感完全走样。这不是「内嵌」该有的样子：内嵌的是**原来那个窗口**，
  尺寸不该变。

  现在：
  - 11 个子页全部按**原窗口尺寸**（380×330 / 440×320 / 740×660 / 860×700 …）显示；
  - 宿主把它们**居中**放在内容区里，外面加一层圆角卡片与投影，
    视觉上就是「原来的窗口浮在主窗口里」，而不是「换了一个铺满的页面」；
  - 外面套了一层 `ScrollViewer`：主窗口被缩到比子页还小时出现滚动条，
    而不是把内容裁掉（860×700 的实例工具箱在 1180×720 的窗口里就能滚）。

## [1.0.8.0] - 2026-10-18

### 新增

- **所有子窗口改为内嵌页面（学 PCL2）**。此前登录、换肤、补全资源、崩溃分析、实例工具箱、
  配装码、联机模块、整合包进度……十几个功能都是独立 `Window`：会占用任务栏、
  会被 Alt+Tab 当成第二个程序、主窗口缩小时容易飞到屏幕外，观感也不统一。

  现在统一由 `Services/InlineHost` 显示在主窗口内容区：
  - 顶部一条「← 返回 / 标题 / ×」，下面是页面内容，标题由各页的 `IInlinePage.InlineTitle` 提供；
  - 子页内部原来的自定义标题栏一律隐藏（否则就是双层标题栏）；
  - 「添加离线账户」「微软账户验证」这类小表单会自动居中并限宽到 420~500px，不会在宽屏上被拉成一条；
  - 同一时刻只显示一个内嵌页（模态语义），退出主窗口时打开的页面会正常结束等待，不会挂住。
  - `AppLifetime.ShowDialogAsync(Control)` 作为统一入口，调用点只需把参数从 Window 换成页面。

- **资源简介自动翻译**（`Services/TranslateService`）：Modrinth 的 `summary` 基本都是英文，
  列表加载后**后台逐条翻成中文**并回填，翻不出来就保留原文。
  走免密钥端点（MyMemory → Google gtx）双路兜底，按句切块、串行请求（避免免费端点限流），
  结果按原文缓存，翻页来回滚动不重复请求。失败只记 Debug 日志，不影响任何主流程。

### 改进

- **账户页头像换成 Win11（太阳谷）风格的账户图标**：没换过皮肤的离线账户不再显示史蒂夫头像，
  改为圆底 + 人形轮廓的系统风格图标；一旦用户真的换过皮肤（`res/<uuid>.custom` 标记）就显示真实头像。
  史蒂夫皮肤仍会生成，游戏里照旧是默认皮肤，只是账户界面不再是一排一模一样的脸。
- **加载器版本列表改为「官方源优先、镜像兜底」**：
  - Forge → `maven.minecraftforge.net` 的 `maven-metadata.xml`；
  - NeoForge → `maven.neoforged.net` 的 versions API（1.20.1 走 `net/neoforged/forge`）；
  - Fabric / Quilt 本来就是官方（`meta.fabricmc.net` / `meta.quiltmc.org`），未改动。
  原来 Forge/NeoForge 直接读 BMCLAPI 的列表接口，版本集合可能落后于官方，
  而安装页展示的版本来自官方枚举 —— 两边对不上就会「选了某个版本却装成别的」。
  官方拿不到任何版本时才回退 BMCLAPI，避免官方被墙时彻底装不了。

### 说明

- 子窗口的**文件与类名没有改**（还叫 `XxxWindow`），只是基类从 `Window` 换成了 `UserControl`，
  这样全部调用点与 XAML 不用重写名字；新写子页请直接继承 `UserControl` 并实现 `IInlinePage`。

## [1.0.7.16] - 2026-10-18

### 修复

- **Forge / NeoForge / Quilt 在「安装版本」页卡住装不上**（进度停在 0/1、速度显示 N/A）。

  根因：这三家加载器在安装页走的是 **MinecraftLaunch 自带的 `ForgeInstaller` / `QuiltInstaller`**，
  而那条路径与我们自己写的 `LoaderInstaller` 是两套完全独立的东西 —— 它
  - **直连** `maven.minecraftforge.net` / `maven.neoforged.net`（国内几乎连不上，
    NeoForge 那边实测直接连接失败），不知道要改走 BMCLAPI 镜像；
  - **没有空闲超时**，TCP 连上但服务端不回数据时会一直挂着，进度条就死在那里；
  - **不报速度**（`IsStepSupportSpeed = false`），所以界面上只能显示 `N/A`；
  - NeoForge 还有额外的坑：库的解析器对缺 `sha1` 的库声明会抛 `Nullable.Value` 崩溃。

  修法：安装页的 Forge / NeoForge / Quilt **统一改走自实现的 `LoaderInstaller`**，
  与整合包安装共用同一条经过验证的路径：
  - 安装器 jar 与全部依赖库都带 BMCLAPI 镜像兜底（`maven.minecraftforge.net` →
    `bmclapi2.bangbang93.com/maven/`），并走 1.0.7.15 的多线程内核（分块并行 + 全局速度反馈）；
  - 依赖库 4 路并行下载，失败隔离；
  - 安装器 jar 下载前先删残留 —— 上一次失败留下的半截文件会被「文件已存在」判据当成下好了，
    随后崩在 `ZipFile.OpenRead`；
  - 装 Forge / NeoForge 前检查原版 `client.jar`，缺了先补齐（processor 的 `{MINECRAFT_JAR}` 要用它），
    否则会崩在 processor 阶段，用户只看到一段 Java 堆栈。

- **NeoForge 的 `21.0.1-beta` 这类版本号被截成 `beta`**：`NormalizeLoaderVersion` 原来靠
  「看起来像 1.2.3 就当前缀剥掉」的泛化判断，会把 `21.0.1` 当成 Minecraft 版本吃掉。
  现在改成「已知 mc 版本时只剥等于该版本的前缀」，拼出来的安装器 URL 才不会 404。

### 改进

- 加载器依赖库的下载声明现在会带上清单里的 `size` 与 `sha1`：上次下到一半的坏 jar
  不再被当成「已下载」，`ClassNotFound` 那类莫名其妙的启动崩溃少一类来源。

## [1.0.7.15] - 2026-10-18

### 新增

- **统一的高性能下载内核（`Services/Download/`）**，接管全部 Minecraft 资源 / 模组资源 /
  加载器资源的下载。原来每个下载点都是「开一条 TCP 流、`CopyToAsync` 到临时文件、
  失败就从头再来」，慢速链路上一个 8 MB 的 Forge 安装器能等几十秒，而且各处的超时口径、
  重试逻辑、速度算法各写一套。新内核基于第三方库 `Downloader` 5.10.1（MIT、约 1.3 MB、
  唯一依赖 `Microsoft.Extensions.Logging.Abstractions`）：

  - **分块多连接**：一个文件切成 2~8 块并行拉，块数取设置里的「下载线程数」；
    小于 1 MB 的文件不分块（握手开销大于收益）。
  - **断点续传**：块级失败自动续传 + 自动重试 3 次；`BlockTimeout` 30 秒收不到字节才算卡死
    —— 取代原来「单文件总时限」，慢而稳的下载不再被误杀。
  - **完整性校验**：先写 `<name>.<guid>.vblpart`，校验「非空 → 体积与清单一致 → sha1 一致」
    通过后才改名到目标位置。镜像端返回被截断的文件、CDN 返回错误页面，都会当场识别并自动换源，
    不会再在 `mods/` 里留半截 `.jar` 把游戏崩掉。
  - **双源自动换源**：复用既有的 `DownloadSources.BuildCandidates`（官方 ↔ BMCLAPI + 源健康度降级），
    每个候选源独立重试，403/429 视为限流、多给 3 轮并加长退避。

- **统一的速度反馈（`DownloadSpeedometer`）**：所有下载点把收到的字节报进同一张速度表，
  500 ms 采样 + 指数平滑 + 连续空窗归零。界面上的「速度」从此是**全局总速度**，
  而不是「某个文件自己的局部速度」——并行下 8 个文件时数字和进度条终于对得上。

### 改进

- **加载器依赖库改为批量下载**（`LoaderInstaller.DownloadLibrariesAsync`）：Forge / NeoForge
  的几十个库原来是串行一个个下，现在 4 个文件并行 × 每个文件内部分块，装一次加载器的时间
  显著缩短；单文件失败不再中断整批，最后一次性报出「哪几个失败、为什么」。
- **模组下载**（模组页「另存为 / 添加到实例」）改走新内核：Modrinth CDN 支持 range 请求，
  多连接后大模组的速度提升明显；任务中心的进度文案改用全局速度表。
- **EasyTier 联机核心下载**改走新内核（走 gh-proxy 的单连接经常只有几百 KB/s）。
- **配装码 / 整合包文件下载**改走新内核，顺带补上了体积与 sha1 校验。
- 设置页「下载线程数」说明改为「每个文件的分块并发连接数（2~8）」，与实际语义对齐。
- 新增 `DownloadIntegrityException`：「下到了但内容不对」单独成类，不再被
  `DownloadErrors.DescribeFileSystem` 误报成「磁盘读写失败」，把用户引向错误的排查方向。

### 修复

- **「补全资源」第二节（加载器依赖）点取消无效**：`AssetRepairService` 调用
  `VerifyAndDownloadDependenciesAsync(parallelism)` 时漏传了取消令牌，
  几个 GB 的补全会一直跑到底。现在令牌一路传进库的下载器。

### 说明

- 原版资源（client.jar / 原版库 / 资源索引与资源对象）这一段的**枚举与校验**仍由
  MinecraftLaunch 的 `MinecraftResourceDownloader` 完成 —— 它没有公开的下载器注入点
  （`DownloaderClient` 只读、构造函数不收 `IDownloader`），要换只能把「库解析 + natives 解析 +
  资源索引」整套重新实现，风险远大于收益。这一阶段的速度直接取它上报的
  `Speed`，一旦新内核有活动就优先显示全局速度表的值。

## [1.0.7.14] - 2026-10-17

### 修复

- **明暗主题在普通窗口里不生效（浅色底 + 浅色字，整窗发白）**。
  根因是两套主题来源打架：
  - `App.axaml` 里 `FluentAvaloniaTheme` 设了 `PreferSystemTheme="True"`，它会**自己**把
    `Application.RequestedThemeVariant` 改成操作系统的主题；
  - 而本工程的窗口底色、背景遮罩、拖放面板都是按**用户设置**算好手写进资源的。

  于是出现「变体 = 系统（暗色），底色 = 应用设置（亮色）」：文字用暗色主题的浅色字，
  衬在 #F3F3F3 的亮底上，正文几乎看不见。主窗口因为有 Mica 材质垫底、且会自己按
  `ActualThemeVariant` 重算底色，看起来是正常的 —— 这就是「只有 FAAppWindow 管用」的假象。

  修法：
  - `PreferSystemTheme` / `PreferUserAccentColor` 改为 `False`（应用自带主题开关，由自己说了算）。
  - `ApplyTheme()` 顺序调整为：**先关掉 FA 的两个跟随开关 → 再写主题色与手写色 → 最后才切主题变体**。
    以前第一步就切变体，而那一刻 `PreferSystemTheme` 还是 True，会被它改回系统值。
  - 启动流程里把 `ApplyTheme()` 提前到**第一个窗口出现之前**，避免窗口先按系统主题建出来。
  - 新增运行时守卫：`ActualThemeVariant` 一旦和用户设置不一致，记一条 Warning 并按设置纠正回来。

### 修复

- 应用级 `Style Selector="Window"` 的底色兜底原先引用 `SolidBackgroundFillColorBase` ——
  那是 **Color** 资源（同名 Brush 是 `*Brush` 版本），赋给 `IBrush` 类型的 `Background`
  会因类型不匹配**静默失效**，等于没有兜底。改用本工程自己的 `ApplicationBackgroundBrush`。

### 说明

- 排查手段：把截图当数据读（解 PNG 采样像素）。窗口底色实测 `#F3F3F3`（亮色手写值）、
  正文几乎没有深色像素、只有主按钮 `#1599FF` 饱和 —— 一眼判定「底色按设置、文字按变体」，
  而不是「整窗被降低透明度」或「字体颜色写错」。
- 日志新增：`主题已应用：{Variant}（强调色 …, 渐变 …）`；变体不一致时打 Warning。

## [1.0.7.13] - 2026-10-17

### 改进

- **账户页二次重构：从「卡片墙」改为「焦点卡 + 列表组」**。上一版是 232×268 的等高卡片整齐排布，
  每张卡的视觉音量相同 —— 没有主次，眼睛不知道先看哪，这正是「看着整齐但不高级」的根因。现在：
  - **整屏只有一个焦点**：当前启动身份单独做成 Hero 卡（92px 头像 + 主色渐变环 + 26 号名字 +
    7% 渐变洗底）。其余账户全部降一档，收成 64px 的行。
  - **不再是一排等宽卡片**：账户改为同一张卡里的一组行，靠"组"表达归属，
    而不是靠重复的卡片边框。行与行只差背景与一条选中环。
  - **行内操作分三档重量**：「换肤」文字按钮（次色）→ 删除只留图标（悬停才变危险色）→
    「切换」描边按钮（唯一的行内主操作）。使用中的行把「切换」换成主色「使用中」文字 +
    名字前的 6px 主色圆点，避免同一行出现两个互相竞争的动作。
- **强调色预算收紧到 3% 以内**：删掉三张卡片各一条的主色渐变条与常驻主色图标块，
  彩色只剩 Hero 的渐变环 / 渐变洗底 / 选中圆点。右栏三个「添加身份」图标块改成中性浅底，
  悬停才借 12% 主色。页面里实心主色按钮 0 个。
- 页头的「N 个账户」徽标移到「全部账户」区块标题右侧，与它修饰的对象放在一起。
- 空状态文案改为一步到位的引导（"从右侧选一种登录方式，添加后即可启动游戏"）。
- 有账户但一个都没选时，Hero 位置不再空着，而是给出「尚未选择启动身份」的引导态。

### 新增

- `FontSizeHero`（26）字号档：页标题 34、区块标题 22 之间原本缺一级，Hero 名字只能硬写尺寸。
- `AccountPageViewModel.CurrentUser` / `HasCurrentUser` / `HasUsers` / `AccountCountText`：
  空状态、计数、Hero 内容全部改由绑定驱动，页面 code-behind 不再逐个 `x:Name` 开关 `IsVisible`。

### 修复

- **键盘焦点在账户页不可见**：行内按钮的模板底色被自定义样式覆盖后，Fluent 默认焦点框也跟着丢了。
  现在每个可交互元素都有显式的 `:focus-visible`（1px 主色环 + 12% 浅底，不改 `BorderThickness`，
  避免文字跳动），列表行由 `ListBoxItem:focus-visible` 点亮行边框。
- `Users.CollectionChanged` 原先在 `Loaded` 里订阅，每次导航回账户页都会重复挂一次处理器；
  改为构造函数订阅一次。

### 说明

- 三级深度：L0 页面 / L1 Hero 卡与列表组（1px 内描边，无阴影）/ 仅悬停时一层浅影，
  取代了上一版卡片常驻阴影的"贴纸感"。
- 入场动效分三层：Hero（0ms）→ 右栏（80ms）→ 列表逐行（100ms 起，每行 +40ms），
  只在这三处动；`.performanceMode` 下全部关闭。

## [1.0.7.12] - 2026-10-17

### 改进

- **账户页重构为「左卡片墙 + 右操作栏」**。原来三个添加账户的大按钮横排在页面底部，
  既吃掉一整行高度，窗口变窄时还会换行错位。现在：
  - 三个入口收进右侧固定宽度的操作栏（300px），尺寸统一为紧凑的「图标 + 主副标题 + 箭头」行，
    纵向排列、始终可见，不再随卡片数量上下漂移。
  - 左侧卡片区拿到全部剩余宽度，卡片从 248×284 收到 232×268，环绕排布更紧凑，
    窄窗口下能多放一列。
  - 页头右侧增加「N 个账户」计数徽标，账户为空时自动隐藏。
- **账户卡片增加「当前使用」状态**。以前只靠一个 `RadioButton` 表达选中，而它和
  ViewModel 里的 `selectedAccount` 是两份状态，自动恢复上次账户时两边会对不上。
  现在由 `User.IsSelected` 单一数据源驱动三处视觉：主色边框、顶部渐变条、头像外环，
  外加右上角勾选徽章；`RadioButton` 换成普通按钮，文字在「使用该账户 / 当前使用」之间切换。
- 卡片上的账户类型改为中文（「微软账户 / 外置账户 / 离线账户」），「皮肤 / 删除」按钮改为小号描边样式，
  删除按钮保留危险色。

### 新增

- **账户页动效**：
  - 卡片入场：从下方 10px、97% 缩放、全透明浮到终态，多个卡片按 45ms 错落出现；
    已入场过的卡片不会在增删账户时重复播放。
  - 卡片悬停：整卡 1.8% 抬升 + 阴影 + 边框高亮，头像同时放大 5%。
  - 选中态：顶部渐变条淡入、头像外环浮现、勾选徽章从 60% 弹性放大。
  - 添加账户按钮：悬停时图标块放大 8%、背景加深，右侧箭头右移 3px、透明度提高。
  - 全部动效走 `Transitions` 声明式表达，并补充 `.performanceMode` 覆盖，
    性能模式下一次性关掉（缩放复位、过渡清空）。
- 新增两个语义化画刷键 `AppAccentSurfaceBrush`（主色 12%）、`AppAccentSurfaceStrongBrush`（20%），
  由 `SettingsService.ApplyAccentCore` 随主题色实时覆写，避免页面里硬编码 `#1F0078D4`
  这类换色后会失配的值。

## [1.0.7.11] - 2026-10-17

### 修复

- **配装码「准备阶段」点取消无效**。1.0.7.10 把取消令牌打通到了下载层，但漏了一段：
  从点「开始添加」到安装会话建立之间，还有「生成 .mrpack」「解析清单」两步，
  这段时间**还没有会话令牌可取消**。此时点「取消添加」，`RequestCancel` 打到
  `ModpackInstallSession.Current` 上——首次安装时它是 null，第二次安装时它是上一次的**已终态会话**，
  `IsInProgress` 判 false 直接 return。表现是按钮变成「正在取消…」，安装却照常开始。
  现在补上第二条通道 `_prepareCts`：`LoadoutImportWindow` 每次安装新建一个令牌，
  一路传到 `LoadoutShareService.ImportAsNewInstanceAsync(…, ct)` →
  `ModpackInstallCoordinator.InstallAsync(…, ct)`；
  - 生成安装包阶段取消 → `Task.Run(…, ct)` 立即中断，安静返回，界面显示「已取消这次添加」
  - 清单解析完、会话刚建好时发现已请求取消 → 立刻 `session.RequestCancel()`，
    让安装器第一行 `ThrowIfCancellationRequested` 直接收兵，不会「取消了却开始下载」
  - 已进入下载阶段 → 仍走会话令牌（1.0.7.10 已打通）
- `ModpackInstallCoordinator` 的清单解析增加 `OperationCanceledException` 分支：
  以前取消异常会落到通用 `catch (Exception)` 里被报成「解析整合包失败」。现在按取消处理，
  只给一句「已取消这次安装」，不弹错误。

### 说明

- 取消现在覆盖「生成安装包 → 解析清单 → 装原版 → 装加载器 → 下模组」全链路，
  任一阶段点取消都能停下；已下载的文件保留，重试不会从头再来。

## [1.0.7.10] - 2026-10-17

### 修复

- **配装码安装时取消不了**。根因：原版 Minecraft 那一大段下载（几百 MB）**完全没有接取消令牌** ——
  `ModpackInstaller.EnsureVanillaAsync` 调 `VanillaDownloader.InstallAsync` 时没传 `ct`，
  而后者内部也没接受令牌，`MinecraftResourceDownloader.VerifyAndDownloadDependenciesAsync`
  本来就有 `(parallelism, ct)` 重载却只传了一半。结果点「取消安装」后界面显示「正在取消…」，
  实际照样把整个原版下载完才停。
  现在令牌一路打通：版本 JSON 请求（`CreateLinkedTokenSource` + 单次超时）、写盘、
  库下载器全部可中断；「外层取消」与「内部超时」用 `when (ct.IsCancellationRequested)` 区分，
  取消时立即上抛、不再换源重试。
- **配装码安装被显示成「整合包」**。配装码内部是打成临时 `.mrpack` 交给整合包安装器的，
  以前没告诉协调器来源，于是任务名写「整合包：xxx」、类型显示「游戏版本」、
  进度窗口标题「整合包安装」。
  新增 `Models/InstallKind.cs`（`Modpack` / `Loadout`）贯穿
  `ModpackInstallCoordinator` → `ModpackInstallSession` → 界面：
  - 下载任务页：任务名「配装码：xxx」，类型列「配装码」（新增 `DownloadTaskType.LoadoutInstall`）
  - 进度窗口：标题「配装码安装」，格式标签「配装分享码」，步骤「下载配装模组」
  - 提示文案：开始 / 完成 / 取消 / 失败全部按「配装」措辞
- **取消后任务显示「失败」**：新增 `DownloadTaskService.CancelTask`，取消的任务在任务中心
  标为「已取消」，不再染成失败色。
- **配装码窗口安装期间无法取消**：安装中「开始添加」按钮变为「取消添加」，
  点击即向安装会话发取消请求并给出「正在取消…」反馈；关闭按钮不再被禁用
  （关窗只是收起界面，安装会在后台继续，可在「下载任务」里看）。

### 改进

- **纯原版配装码也能装了**：原来 `ShareableCount == 0` 一律拒绝，导致「想让它帮我装个原版本体」
  的码用不了。现在只有「既没有模组、也没有游戏版本」才拒绝；有版本无模组时照常装出本体 + 加载器。

## [1.0.7.9] - 2026-10-17

### 修复

- **启动页有版本时仍显示「还没有可用的游戏版本」空状态**。
  根因：`Refresh()` 是 fire-and-forget（`Task.Run` 后台扫盘），而空状态只在页面
  `Loaded` 那一刻判了一次 `MinecraftVersions.Count == 0` —— 那时集合肯定是空的，
  之后填进来的版本不会再把空状态关掉。
  改为**订阅集合变化 + 扫描状态变化**，每次都重算：有版本 → 整块隐藏；没版本 → 才显示。
- **首次进入会闪一下「没有版本」**：新增 `RunPageViewModel.IsRefreshing`，
  扫描期间空状态显示「正在扫描本地版本…」（不显示图标、提示与按钮），
  扫完仍为空才切换成真正的空态文案。

### 说明

- 底部工具栏 `启动游戏 | 版本设置 | 配装码 | 刷新` 里的**「配装码」按钮始终可见**，
  与空状态是否存在无关；空状态里的「用配装码添加游戏」按钮只属于空态。

## [1.0.7.8] - 2026-10-17

### 新增

- **启动页新增「配装码」入口**：在「版本设置」右侧加了「配装码」按钮，点开粘贴一串分享码，
  即可从零装出一个完整实例（Minecraft 本体 + 对应加载器 + 对方的所有模组）。
  面向「刚拿到新电脑、一个版本都没装」的玩家 —— 这条路径不依赖任何已存在的实例。
- **版本列表空状态也带入口**：没有可用版本时，空状态面板里直接给「用配装码添加游戏」按钮，
  不用在空页面上找按钮。

### 改进

- 新增 `Views/Windows/LoadoutImportWindow.axaml(.cs)`：粘贴即识别（`VBL1-` 分享码与裸 JSON 都吃），
  识别后展示配装名、游戏版本 + 加载器、可下载模组数、未收录模组数、分享者的内存设置；
  点「开始添加」后禁用输入，避免「界面显示的和实际在装的不是同一份清单」。
- 安装复用 `LoadoutShareService.ImportAsNewInstanceAsync`（内部走整合包协调器），
  因此与拖入 `.mrpack` 是同一条路径：进度详情页、任务中心、完成提示、重名加后缀全都一致。
- 只有真正装出实例才刷新启动页版本列表；打开看一眼就关掉不会白扫一遍磁盘。

### 说明

- 识别失败（粘贴到一半）只在窗口内提示，不弹通知 —— 边打边解析必然会短暂失败，弹窗会很吵。
- 分享码里没有记录 Minecraft 版本时不予安装（会明确说明原因），避免装出一个不知道装什么的空实例。

## [1.0.7.7] - 2026-10-17

### 新增

- **启动前「补全资源」窗口**（`Views/Windows/AssetRepairWindow.axaml`）：
  点「启动」时先做一次完整性体检，缺东西就弹出本窗口，列出**缺什么**、点一下**只补缺的那部分**，
  带进度条与速度显示。补完自动重新体检，确认干净后提示「可以重新启动游戏」。
  - 取代原来的行为：启动流程里直接抛 `FileNotFoundException`，
    弹一句「缺少资源索引文件：…assets\indexes\5.json。说明该版本的资源没下载完整，
    请在「下载中心」重新安装该版本」—— 无路可走，且重装整个版本要几百 MB。
- **`Services/InstanceIntegrityChecker.cs`**（体检，纯本地 IO）：
  沿 `inheritsFrom` 链检查四类资源 ——
  **客户端 jar**（缺失 / 小于 512 KB / 不是 ZIP 头都算坏）、
  **资源索引**（`assets/indexes/<id>.json`）、
  **依赖库**（按 `downloads.artifact.path`，退化按 maven 坐标拼；跳过被 `rules` 排除的非 Windows 库）、
  **资源对象**（按索引里的 `objects[].hash` 逐个查 `assets/objects/xx/<hash>`）。
  同类问题聚合为一条，避免几千个资源对象把列表刷爆。
- **`Services/AssetRepairService.cs`**（补全，复用既有链路）：
  阶段 ① 原版层走 `VanillaDownloader.InstallAsync` —— 库的 `MinecraftResourceDownloader`
  本身就是「校验 + 只下缺的」，超时 / 双源 / 中文报错都在里面；
  阶段 ② 加载器层对实例本身再跑一次 `VerifyAndDownloadDependenciesAsync`，
  失败只记日志（不拖垮原版补全）。进度条按 9:1 分配。

### 改进

- **体检结果不再「一缺就拦死」**：加载器实例的 `client.jar` / 资源索引在**父版本**目录里，
  体检与补全都按 `inheritsFrom` 链定位到原版根版本（`ResolveRootVersion`），
  不会把 Forge / Fabric 实例误判成「客户端文件缺失」。

### 说明

- 体检失败（例如版本 JSON 坏到读不出来）只记 Debug 并放行，不阻断启动 ——
  宁可让游戏自己报错，也不要因为体检器自己的问题挡路。
- 补全期间「关闭」按钮禁用：底层下载没有外部取消令牌，中途关窗会让下载在后台空跑。

## [1.0.7.6] - 2026-10-17

### 改进

- **安装页的模组加载器勾选框改为严格单选**：勾上 Fabric 就立刻取消勾选并禁用 Forge / NeoForge / Quilt，
  勾上 Forge 同理，其余依此类推。取消勾选后四个框恢复为「有货就能选」，可以换装别的加载器。
  - 之前沿用 WPF 版的**部分共存**规则（Forge 只挡 Fabric/Quilt，NeoForge 只挡 Fabric/Quilt），
    会出现「Forge 与 NeoForge 同时勾着」的组合。
  - 但安装逻辑本身是按 **Forge → Fabric → Quilt → NeoForge** 的顺序取**第一个**非空版本号，
    同时勾两个只会静默只装其中一个，玩家却以为两个都装了 —— UI 规则与安装逻辑口径不一致。
- **勾选状态与已选版本号强绑定**：未勾选的加载器，其 `Select*Version` 一律清空，
  避免「框没勾却还带着版本号」被安装流程当成要装。
- **安装前的兜底守卫**：`InstallPageViewModel` 里若检测到同时有多个加载器版本号非空，
  直接提示「同一个实例只能安装一个模组加载器，请只勾选其中一个」并中止，
  不再静默只装第一个（防止将来新增入口没守住单选规则）。

### 说明

- 卡片描边（`loaderCard.selected`）与勾选框状态保持同步，点哪个亮哪个。
- 版本列表加载中时四个框仍统一禁用。

## [1.0.7.5] - 2026-10-17

### 改进

- **版本清单改为「磁盘缓存优先」**：上次成功拉到的清单存到 `%LOCALAPPDATA%\VibrantbitLauncher\cache\version_manifest_v2.json`，
  下次启动**先秒出列表**，网络刷新改到后台做（成功则同时更新磁盘与进程内缓存）。
  首次运行 / 缓存缺失时才必须等网络。缓存损坏、目录不可写都只记 Debug，一律降级为「去网络拿」。
- **清单请求改为双源并行竞速**：官方 `launchermeta.mojang.com` 与镜像 `bmclapi2.bangbang93.com`
  **同时发**，谁先返回完整清单就用谁，另一个立刻取消。原来是串行「一个源 15 秒 × 3 次，全败才换源」，
  最坏要挂 90 秒 —— 用户随手关窗口就会撞出 `SocketException 995` / `TaskCanceledException` 的中断异常。
- **单次超时 15s → 8s，每源重试 3 → 2 次**：清单只有几百 KB，正常 1 秒内到；卡住的主机应尽快让位。
- **接入源健康度**：清单拉取成功回报 `Good`、失败回报 `Failed`，
  下次会自动把上次超时的主机排到后面（与下载层共用同一套降级记录）。

### 修复

- **关窗口时日志里出现 `TaskCanceledException` / `SocketException 995` 堆栈**：
  这类异常是「程序退出时仍在飞的 HTTPS 被 CLR 掐断」，属正常收尾。
  - `DownloadResourcesViewModel` 对 `OperationCanceledException` 单独处理，日志降为 Debug，
    不再以 Warning 带整段堆栈落盘。
  - 单源请求的取消判据改为 `catch (OperationCanceledException) when (ct.IsCancellationRequested)` ——
    用「外部令牌是否已请求取消」区分「外层取消」与「单次请求超时」，后者照常重试，
    不会再把超时当成用户取消直接放行（1.0.7.4 修的同一个坑，这次连外层兜底一起收口）。
  - 库的 `EnumerableMinecraftAsync()` 兜底只在「网络能通但结构解析不出来」时才调用，
    纯网络失败不再去白等它那 100 秒。

### 说明

- 磁盘缓存放用户目录而非程序目录：发布版 exe 可能被放在 Program Files 等不可写位置，
  写缓存失败不应影响任何功能。
- 清单结构解析仍是自实现（`versions[].id/type/url/releaseTime`），
  库接口仅作「官方改结构、库已升级」时的自愈兜底。

## [1.0.7.4] - 2026-10-17

### 修复

- **加载 Minecraft 时弹出英文错误 “The operation was canceled.”**。1.0.7.3 引入的双源清单拉取
  用 `CancellationTokenSource` 做单次 15 秒超时，而**超时抛出的正是
  `TaskCanceledException`（`OperationCanceledException` 的子类）**；重试的
  `catch (Exception ex) when (ex is not OperationCanceledException)` 把它当成「用户取消了」放过去，
  于是第一条请求超时就整条冒到界面上 —— 中文标题「错误」+ 英文正文。
  修法：
  - 清单拉取的 `catch` 改为**连取消异常一起接住**（这里没有外部取消令牌，取消即超时），
    照常重试、换源；
  - 最终失败抛的仍是中文的 `DownloadFailedException`（含已尝试的主机与次数）。
- **顺手把「英文异常原文直接弹窗」这个通病收口**。新增 `Helpers/FriendlyError.cs`：
  `OperationCanceledException / TaskCanceledException` → 「请求超时（网络太慢或代理不通）…」；
  `JsonException` → 「服务器返回的数据不完整…」；`HttpRequestException` 按状态码翻中文；
  已包装过的 `DownloadFailedException` / `ModpackException` 原样返回。
  已接入：下载页版本列表、安装页安装失败、实例工具箱「补齐依赖」、新闻拉取（同时让超时也参与重试）。

### 说明

- 这次不改变任何行为逻辑，只保证「失败时说人话」——超时仍然是失败，但用户能看懂是网络问题，
  并按提示重试或换源。

## [1.0.7.3] - 2026-10-17

### 修复

- **「工具箱」按钮放错了页面**。1.0.7.0 把入口加在了 `RunPage`（运行页），但导航里的「启动」
  实际是 `LaunchPage`，`RunPage` 早已没有导航入口 —— 用户根本看不到按钮。
  现在版本列表每一行按「工具箱 / 设置」排列，与启动页共用同一个 `RunPageViewModel`。

- **从启动页点「设置」后目录为空、列表不刷新**。两个原因叠加：
  1. `VersionManagePage` / `VersionManageViewModel` 在 DI 里是**懒加载单例**，
     而消息是先发 `McVersionForManage` 再发 `NavigateTo` —— 第一次点击时接收方还没被创建，
     版本号直接丢掉，页面打开后 `CurrentVersion` 为空、`GameDir` 也是空字符串；
  2. 页面的 `Load()` 是空实现，`Loaded` 里也没有刷新，所以之后每次进来都停留在旧内容。
  修法：**先导航（同步创建页面、注册接收方）再发版本号**；`Load()` 改成真正重读；
     页面 `Loaded` 触发一次刷新；并给 `LoadDataAsync` 加加载令牌，避免旧目录的结果覆盖新目录。

- **使用官方源时 Minecraft 版本列表加载不出来**。`MinecraftVersionCache` 原来直接调
  库的 `VanillaInstaller.EnumerableMinecraftAsync()`，它只看全局 `IsEnableMirror`：
  下载源选「官方源」时清单只走 Mojang，国内网络下整页为空。
  现在改为**自己发请求 + 双源兜底**（`launchermeta.mojang.com` 与
  `bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json` 各试 3 次，用户设置只决定先试谁），
  库的接口只作为结构变更时的最后兜底；错误文案会列出已尝试的主机。

- **官方源下「游戏起不来」缺少可读线索**。启动前补两道体检，把静默失败变成明确提示：
  - `client.jar` 缺失、**小于 512 KB 或不是 zip（PK 头）** → 直接报「客户端文件损坏或未下载完整」
    并提示重装 / 换源（官方源被限速或代理拦截时会出现「文件在、内容不对」）；
  - 版本参数里出现 `--assetIndex` 但 `assets/indexes/<id>.json` 不存在 → 报「资源索引缺失」，
    说明资源没下载完整（游戏会在加载资源阶段直接失败）；
  - 缺失的依赖库清单从 Debug 提到 **Warning** 级，默认日志级别就能看到。

### 说明

- 目录约定未变：仍然固定使用 `versions/<版本号>` 作为隔离目录，与启动参数 `--gameDir` 同源。
- 「设置」入口的行为改为「先切页、再定位版本」，因此从任意页面点进来都会带上正确的版本号。

## [1.0.7.2] - 2026-10-17

### 修复

- **Fabric 安装「卡在第 5 步：下载库」不动**。用户表现为进度停在「下载库」，既不报错也不结束。
  实测根因在**下载源本身**，不在安装流程：
  - 官方 `maven.fabricmc.net` 本机实测多个库 20s 超时（`asm-analysis` 20s 只收到 12.8KB、
    `sponge-mixin` 直接连接超时），基本不可用；
  - BMCLAPI `/maven/` 会 **302 跳到第三方免费节点**（open-bmcl-api.lunar.cc /
    bmclapi1.sakura.exp.dog / minio.749333.xyz / mirror.nyist.edu.cn），节点质量参差：
    同一批 8 个库，有 0.8s 下完的，也有 **29s** 的，`asm-util` 直接连接超时；
  - 而 `FileTimeout` 原来是 **90 秒**，再乘「2 个候选源 × 重试轮数（默认 4）」，
    **单个文件最坏能磨 12 分钟** —— 界面上就是「卡死」。

  改动（`Services/FabricDownloader.cs`）：
  - `FileTimeout` 90s → **30s**：Fabric 的库只有几十 KB ~ 1.5MB，30s 还不响应就该换节点；
    每次重试都会重新拿 302，大概率落到另一个健康节点上。
  - 新增 `FileIdleTimeout = 15s` **空闲超时**：只要还在持续收字节就顺延，
    「慢而稳」不会被 30s 砍掉，只有「彻底不动」才判失败。
  - 新增 `FileTotalTimeout = 90s` **单文件总时限**：含所有候选源与重试轮，
    彻底杜绝「候选源数 × 重试轮数 × 单次超时」叠乘出十几分钟的假死。
  - `DownloadFileAsync` 增加可选参数 `totalTimeout`（默认 `null` = 行为不变，
    Forge / NeoForge 等大文件调用方不受影响），超预算时错误文案会明确说「已用完总时限」。
  - 进度文案现在会带上失败原因，例如
    `下载库 3/8 · asm-util.jar open-bmcl-api.lunar.cc 失败，改用 maven.fabricmc.net`
    —— 让用户看得出它在重试换源，而不是界面死住。

### 说明

- 「设置页改了不保存」经核查**不成立**：`settings.json` 里确实存着非默认值
  （`UseBmclMirror: true`、`DownloadThreads: 8`、`EasyTierNetworkKey` 有值），
  设置页每个 setter 都调了 `SettingsService.Save()`，绑定名与 ViewModel 属性也一一对应。
  用户已确认是自己看错了。

## [1.0.7.1] - 2026-10-17

### 修复

- **「下载任务」页未完成任务点「查看详情」跳错页**：安装型任务原本一律切到「下载中心」
  的通用分类列表（原版 / 模组 Tab），那只是资源浏览的导航，看不到这一次安装到底装到哪了。
  现在改为跳到**对应版本的「安装版本」详情进度页**（`InstallPage`），并把版本号一并带过去：
  - `Models/DownloadTaskItem.cs` 新增 `TargetMcVersion`（该任务关联的 MC 版本号）；
    `ViewModels/Pages/InstallPageViewModel.cs` 在创建原版 / 加载器安装任务时把它填上。
  - `ViewModels/Pages/DownloadCenterViewModel.cs` 的 `ViewDetailCommand`：
    安装任务 → `AppNavigation.Navigate(typeof(InstallPage))` + 发送 `McVersion` 消息；
    升级前创建、没有版本号的旧任务退化为回下载中心（避免打开空白安装页）；
    模组文件下载任务没有独立进度页，仍回下载中心模组分类。
  - `Views/Pages/InstallPage.axaml.cs` 的 `SetMcVersion` 增加**同版本保护**：已经是这个版本的
    页面就早退，不重建 ViewModel —— 否则从任务中心跳回来时会把正在跑的那次安装的进度条重置掉。
- 整合包安装任务（`IsModpack`）维持原行为，直接打开整合包进度详情窗口。

## [1.0.7.0] - 2026-10-17

**三个围绕「实例内容」的新功能**：崩溃一键修复、模组依赖求解器、配装分享码。
共同点是把启动器从「能装能跑」推进到「装完能跑通、跑挂了能修、配好了能分享」。

### 新增

- **崩溃一键修复**（`Services/CrashRepairService.cs` + 改造 `Views/Windows/CrashAnalysisWindow`）。
  从「分析出原因」升级到「给出能点的动作」：
  - **调内存**：识别 `InsufficientMemory` / `OutOfMemoryError`（含**日志文本**补充信号，不只依赖库的枚举），
    按「当前值 ×2、不低于 4 GB、不超过物理内存 75%」算出新值，一键写回 `MaxMemoryMb` 并落盘。
  - **换 Java**：识别 `UnsupportedJavaClassVersionError` / `JavaVersionTooHigh` /
    `LowVersionForgeIncompatibleWithHighVersionJava` / `UsingJDK` / `UsingOpenJ9`，
    按实例的 MC 版本推断该用 Java 8 / 17 / 21（1.17 及更早 → 8，1.18~1.20 → 17，1.21+ → 21；
    明确「版本过高」时往下退一档），扫描本机 Java 并自动切到匹配的那一个；找不到就给出可操作提示。
  - **禁用模组**：可疑模组一律**改名**为 `*.jar.disabled`（不删除，随手可恢复）。
    名字匹配支持三种形态：精确文件名 / 文件名包含 / 读 jar 元数据比对 mod id 与显示名。
  - **处理重复安装**：检测同一 mod id 装了多份，保留版本号最高的那个、其余改名禁用。
  - **纯建议类**：显卡 OpenGL、OptiFine 与 Forge 不兼容等无法自动修的，给出明确指引而不是假动作。
  - 崩溃原因列表**翻成中文**（原来直接显示 `UnsupportedJavaClassVersionError` 这类枚举名，玩家看不懂）。
  - 界面：每条修复一张卡片 + 独立按钮，另有「全部修复」；执行后按钮收起并显示结果文案（✓/✗）。

- **模组依赖求解器**（`Services/ModMetadataReader.cs` + `Services/ModDependencyResolver.cs` +
  `Services/ModrinthApi.cs`）。解决最大痛点「装完打不开、报一堆 mod 缺失」：
  - **读 jar 元数据**：Fabric（`fabric.mod.json`，含 `depends` 的对象 / 数组两种写法）、
    Quilt（`quilt.mod.json`，含对象与裸字符串两种依赖写法）、Forge（`META-INF/mods.toml`）、
    NeoForge（`META-INF/neoforge.mods.toml`）。自带极简 TOML 扫描（表头 + 键值 + 引号感知的注释剥离）。
  - **体检**：构建「谁提供什么 id」与「谁需要什么 id」两张表，算出**必需但无人提供**的依赖
    （丢掉内置提供者 `minecraft` / `java` / `fabricloader` / `forge` / `neoforge` / `quilt_loader` …，
    以及 `side=SERVER` 的服务端专属依赖），并检出**同一 id 装了多份**的重复 / 冲突。
  - **一键补齐**：用 mod id 到 Modrinth 搜项目 → 取「本实例加载器 + 游戏版本」下可用的版本 →
    主文件地址，直接下进实例 `mods/`（已存在且哈希一致则跳过，重名自动加 (1)(2)）。
  - 拿不到下载地址的（没发到 Modrinth 的自制品 / 纯服务端库）如实标明原因，不假装成功。
  - 全部失败路径**降级为报告里的一条**，不抛异常（体检是 UI 路径，单个坏 jar 不该把整页带崩）。

- **配装分享码**（`Services/LoadoutShareService.cs`）。把实例的模组清单压成一段可复制的短码：
  - **导出**：扫实例 `mods/`，对每个 jar 算 SHA1，走 Modrinth 的
    `/v2/version_file/<sha1>` 反查它属于哪个项目 / 版本，取**哈希完全一致**的那个文件（退化取主文件）
    拿到官方下载地址；JSON → GZip → Base64URL，前缀 `VBL1-`。
  - **导入（新建实例）**：把清单还原成 Modrinth 规范的 `modrinth.index.json` 打成临时 `.mrpack`，
    交给现成的整合包安装流程（自动装 MC 基础版本 + 加载器 + 拉模组，带进度详情页）。
  - **导入（补进现有实例）**：只往所选实例的 `mods/` 补文件，不动版本与加载器。
  - 复制到剪贴板、保存为 `.vblshare` 文件（文件里同时写分享码与可读 JSON）、
    也接受直接粘贴导出的 JSON 文本。
  - **诚实地说明边界**：自制品 / 未收录的模组带不走内容，会在清单里列为「仅本地」，
    导入方能看到还差哪几个需要自己找。
  - 反向对照：前缀不认识、Base64 损坏、内容不是本格式、空字符串 —— 一律抛
    `LoadoutShareException` 并给出中文原因，绝不静默返回一个空配装。

- **实例工具箱窗口**（`Views/Windows/InstanceToolsWindow.axaml`）：顶部一个实例选择器，
  两个 Tab（依赖体检 / 配装分享）共用它；入口在「运行」页每个版本行的**「工具箱」**按钮上
  （点哪一行就预设哪个实例）。

### 说明

- **测试口子**：全部逻辑都在静态服务类里，UI 只做编排，因此可以直接反射验证。
  验证台（`C:\temp\vbl-tools`，反射加载发布用的产品程序集）共 **54 项断言全 PASS**：
  元数据解析 15 项（含非模组 jar / 损坏 jar / 不存在的文件都返回 null）、
  依赖求解 10 项（含「依赖已满足时不再报缺失」的反向对照 + 重复模组检出）、
  分享码 13 项（含往返一致性与 4 类损坏码必须被拒）、
  一键修复 16 项（含**真的执行**调内存修复并核对 `MaxMemoryMb` 与 `settings.json` 落盘、
  执行禁用模组并核对 `.jar` 已改名为 `.jar.disabled`）。
- `ModMetadataReader` 里 `ModEntry` 刻意用**引用类型**：值类型的 `FirstOrDefault()` 会静默给出
  全默认值，把「没有 `[[mods]]` 表」伪装成「有一个空 id 的模组」。
- 依赖解析与分享码导出**都要联网**（Modrinth），且逐个文件串行查询以避免触发限流；
  网络不可用时依赖体检仍能出结论（缺什么、重复什么），只是「一键补齐」拿不到下载地址。

## [1.0.6.12] - 2026-10-15

**找到「单文件发布后窗口全空白/透明」的真正根因**：`TrimPlatformPayload` 把
`Avalonia.Metal.dll` 当「macOS 后端」删掉了，但 **Avalonia 12 的 Windows 渲染管线实际依赖它**。

### 修复

- **`Avalonia.Metal` 不再被裁剪**（`VibrantbitLauncher.Avalonia.csproj` 的
  `TrimPlatformPayload`，win 与 linux 两个分支都去掉了对它的删除）。
  名字带「Metal」很容易被当成 macOS 专有，实测**删掉它之后窗口建得出来、但内容一片空白**，
  整窗透出桌面（在无 DWM 合成的机器上则是白屏）。

### 说明

- **为什么「调试正常、一发布就透明」**：`TrimPlatformPayload` / `TrimPublishPayload`
  只在 **publish** 阶段生效，`dotnet build` 与 Visual Studio 调试都不经过它们。
- **定位过程（同机对照，只差一个 dll）**：

  | 产物 | 窗口内容 |
  |---|---|
  | Debug（VS 调试） | 正常 |
  | Release `build`（不裁剪） | 正常 |
  | Release `publish` 单文件（裁剪） | **空白/透明** |
  | Release `publish` 非单文件（裁剪） | **空白/透明** |
  | Release `publish -p:SkipPlatformTrim=true` | 正常 |
  | 未裁剪目录里**只删 `Avalonia.Metal.dll`** | **空白/透明** |
  | 未裁剪目录里只删 `Avalonia.Native` / X11 / FreeDesktop / DBus / Designer 组 | 正常 |

- 判据用的是 `PrintWindow` 抓窗口自身内容：正常 ≈ 510 KB，空白 ≈ 3 KB
  （窗口样式位 `WS_EX_LAYERED` 两版一致，说明不是窗口样式问题，是**内容没渲染**）。
- ⚠️ **教训**：这轮之前把「白屏/透明」归因到主题资源（`ApplicationBackgroundBrush`）与 Mica 回退，
  那两处确实是真问题、修得没错，但**不是**「整窗空白」的主因。**遇到「窗口完全没内容」，
  先比较 `build` 与 `publish` 的产物差异**，别只在 XAML / 主题里找。
- 版本产物体积：裁剪后仍能省掉 X11 / FreeDesktop / DBus / Native / Designer 等约 2.5 MB。

## [1.0.6.11] - 2026-10-15

继续收敛「换台电脑窗口透明 / 白屏」一族问题。

### 修复

- **主窗口 Mica 回退时整窗穿透**：`TryEnableMicaEffect` 之前无条件把窗口背景设成透明 +
  请求 Mica 材质，**假设 Mica 一定生效**。但 Mica 能否渲染由 DWM 决定——系统关闭「透明效果」、
  远程桌面、部分虚拟机 / 显卡驱动下，请求 Mica 会**回退**到 Transparent 甚至 None，
  而主窗口内容区没有任何不透明背景（设计上「未设壁纸时保持纯 Mica 底」）→ 整窗连同标题栏
  一起穿透到桌面。修法：hint 照发，但**背景是否透明以实际达成的
  `ActualTransparencyLevel` 为准**（订阅其属性变化 + 开启时各校一次）；只有 Mica / AcrylicBlur
  真的生效才透明，否则清掉局部值让 `AppWindowBackgroundBrush`（不透明）回来，
  并把 `TransparencyBackgroundFallback` 设成不透明兜底。
- **9 个二级窗口加窗口级不透明背景兜底**：欢迎向导 / 登录 / 皮肤管理 / 崩溃分析等窗口
  之前只靠根 `Grid` 的 `ApplicationBackgroundBrush`（1.0.6.10 补的定义）着色，窗口自身
  `Background` 为空——一旦 Grid 层异常（资源解析失败、内容页未加载），窗口就是「无背景」，
  在 DWM 合成开启的机器上表现为**整窗透明透出桌面**（同一根因在无合成机器上表现为白屏）。
  现在给这 9 个窗口的 `Window` 元素直接挂 `Background="{DynamicResource ApplicationBackgroundBrush}"`，
  与根 Grid 双保险；`DynamicResource` 保证切主题时同步换色。
- **应用级窗口底色兜底**：`App.axaml` 加一条 `Style Selector="Window"`，把
  `Background` 兜到 FluentAvalonia 官方键 `SolidBackgroundFillColorBase`（必然存在、随主题换色）。
  局部值优先级高于应用样式，所以主窗口的 Mica 透明背景与二级窗口自写的背景都不会被压掉，
  这条只兜「XAML 里什么都没写」的窗口——属于防漏网，不是本次症状的直接原因。

### 说明

- **「白屏」与「透明」是同一根因的两种表现**：窗口无不透明背景时，DWM 合成开 → alpha 通道
  真透明（看到桌面）；合成关 / 老显卡 → 透出白底。1.0.6.10 修的
  `ApplicationBackgroundBrush`、本版的窗口级兜底与 Mica 级别校验，三层加起来才算闭环。
- 排查时请先确认对方机器跑的 exe 版本（关于页 / 文件属性 → 详细信息），
  1.0.6.10 之前的产物**必然**有这族问题。

## [1.0.6.10] - 2026-10-13

修掉一个**换台电脑就整窗白屏**的必现问题：`ApplicationBackgroundBrush` 是从 WPF 版
一路抄过来的键名，但 **Avalonia / FluentAvalonia 根本没有这个资源键**。

### 修复

- **所有二级窗口在别的电脑上全白（用户报：窗口出来了，内容一片空白）**。
  根因：登录 / 皮肤管理 / 欢迎向导 / 崩溃分析 / 整合包进度 等 **9 个窗口**的根
  `<Grid>` 都写着 `Background="{DynamicResource ApplicationBackgroundBrush}"`，
  而这个键在 Avalonia 侧**任何地方都没有定义**（连 Avalonia Flet / FluentAvalonia 主题里也没有）。
  `DynamicResource` 解析不到就落到 `null` → 根面板没有背景 → 直接透出窗口默认底色 ——
  在别的机器上就是**整窗全白**；主窗口用的是 `AppWindowBackgroundBrush`，所以本地看着正常，
  一直没暴露。这解释了「本机好、别的电脑全白、且日志一切都正常」的现场。
  **修法**：① 在 `Styles/AppTheme.axaml` 里补上该键的不透明兜底定义；
  ② 在 `SettingsService.ApplyTheme()` 里跟 `AppWindowBackgroundBrush` 一起按明暗主题覆写，
  保证任何主题下都是不透明色（Light `#F3F3F3` / Dark `#202020`）。
  ⚠️ 不要图省事把它放进 `ThemeDictionaries`：对「被 `ResourceInclude` 合并进来的字典」
  跨变体查询不生效，实测 Dark 主题仍会取到浅色，必须走运行时覆写。

### 说明

- **反向对照已验证**：反射探针直接查编译好的产品程序集资源字典 ——
  移除定义时 `ApplicationBackgroundBrush` 在 Light/Dark/Default 三个变体下**全部返回 null**（3 FAIL），
  补上定义后 **全部可解析**；再走 `SettingsService.Load()` + `ApplyTheme()` 验证运行时覆写，
  Light → `#FFF3F3F3`、Dark → `#FF202020`，且 Alpha 恒为 255（不会透出白底）—— 8 项断言全 PASS。
- 同一轮还修了 `FabricDownloader` 的**空闲超时**（见上一节），大文件在慢速链路上不再反复重下。


## [1.0.6.9] - 2026-10-11

给整合包安装**补齐 Fabric / Forge / NeoForge / Quilt 四家加载器的自动安装** ——
其中 Forge / NeoForge / Quilt 三家改为**自实现安装器**，不再走 MinecraftLaunch 的库安装器
（那三条路实测全都装不上，下面逐条说明根因）。

### 新增

- **`Services/LoaderInstaller.cs`**：自实现的加载器安装器，负责
  ①选版本（含版本号归一化）②下载 installer jar（官方源 ↔ BMCLAPI 镜像互为兜底）
  ③解 `install_profile.json` / `version.json` ④铺实例版本 JSON ⑤下载全部依赖库
  ⑥按 `processors` 跑 java 安装处理器。
  网络层复用 `FabricDownloader` 那套已经打磨过的短超时 + 双源兜底 + 重试 + 中文报错，
  不再受库的 `FlurlClient`（15 秒硬超时、无镜像）拖累。
- **Quilt 也走自实现**（`LoaderInstaller.InstallQuiltAsync`）：Quilt 的 profile 就是一份
  现成版本 JSON（无 processor），拉下来改 id → 下库即可。

### 修复

- **Forge / NeoForge 装不上（`NullReferenceException`）**。库的
  `ForgeInstaller.DownloadForgePackageAsync` 硬编码 `maven.neoforged.net`，
  既不参与 BMCLAPI 改写也无兜底；实测该主机直接连接失败（curl 退出码 7），
  而 BMCLAPI 上其实有完整镜像。异常又被库裹成一个没有上下文的 `NullReferenceException`。
  现在自己拼下载地址并走双源兜底，并且补了 `DownloadSources` 的
  `maven.neoforged.net/releases/ → bmclapi/maven/` 与
  `maven.quiltmc.org/repository/release/ → bmclapi/maven/` 两条映射。
- **Quilt 装不上（`Nullable object must have a value`）**。库的
  `QuiltInstaller` → `MinecraftEntry.GetRequiredLibraries()` →
  `MinecraftLibrary.ParseJsonNode` 里有一句 `Sha1 = libNode.Sha1.Value`，
  而 Quilt profile 的 14 个库声明（`url = https://maven.fabricmc.net/`）
  **都没有 `sha1` / `size` / `downloads` 字段** → 对 null 的 `Nullable<string>` 取 `.Value`，
  实测 MC 1.20.1 + Quilt 0.20.0 100% 复现。这是上游库的 bug，自实现下库即绕开。
- **BMCLAPI 的 NeoForge 版本号带 MC 前缀导致拼出 404**。列表前 55 条是
  `version = "47.1.5"`，最后 5 条却是 `version = "1.20.1-47.1.105"`
  （条目里另有 `rawVersion = "1.20.1-forge-47.1.105"` 与现成的 `installerPath`）。
  库原样把带前缀的版本号塞进下载 URL。新增
  `LoaderInstaller.NormalizeLoaderVersion`，**循环剥离**前导的加载器名与 MC 版本号，
  无论 `forge-47.4.10` / `1.20.1-47.4.10` / `1.20.1-forge-47.1.105` 哪种形态都归一化到纯版本号。
- **整合包安装会创建「空壳实例目录」，进而毒化之后的每次版本解析**。
  `ResolveGameRoot` 原本在安装一开始就 `Directory.CreateDirectory`，
  于是 `versions/<实例名>/` 在没有版本 JSON 的情况下就存在了 ——
  而库的 `MinecraftParser.GetMinecrafts()` 会遍历 `versions/` 下**每一个**子目录，
  撞上没有 `<目录名>.json` 的目录直接抛 `FileNotFoundException`。
  现在 `ResolveGameRoot` **只算路径不建目录**（目录推迟到真正要写文件时再建），
  并新增 `PurgeBrokenInstanceDirs`：装加载器前清掉「没有版本 JSON 且没有其它文件」的空壳目录，
  让历史失败残留不再拖垮后续安装。
- **安装器抛出的异常会掩盖真实原因**。Quilt 安装器内部把失败咽进 `Completed` 事件后，
  还会在 `return entry ?? throw new ArgumentNullException("entry")` 处抛一个
  与真实原因无关的 `ArgumentNullException`；Forge 安装器则是 `throw;` 原样再抛。
  现在两边都收口：`Completed` 里记一份、`InstallAsync` 抛出的也接住，
  **优先报 `Completed` 里的原始原因**。
- **Installer 版本号比较的两个坑**（内联进上面的实现，一并记着防回归）：
  Quilt 的 `DisplayVersion` 是 `"1.20.1-0.27.1"`（**带 MC 前缀**），
  纯加载器版本在 `BuildVersion`；`ForgeInstallEntry.DisplayVersion == ForgeVersion` 才是纯版本。
- **慢速链路下大文件（Forge/NeoForge 安装器 8~9 MB）会反复从 0 重下、永远装不上**。
  `FabricDownloader.DownloadFileAsync` 原来的 `timeout` 是**整次尝试的墙钟上限**（`CancelAfter`），
  对几十 KB 的小清单合适；但安装器有几 MB，在限速镜像 / 代理下整体耗时可能远超上限，
  而数据其实一直在流 —— 到点被判失败后重试**又从 0 开始**，于是永远下不完（实测复现）。
  新增可选参数 `idleTimeout`（**空闲超时**）：只要还在持续收到字节就把 deadline 往后顺延，
  只有「彻底卡死」才超时。`LoaderInstaller` 下安装器时传 90 秒空闲超时。

### 说明

- **Fabric 分支不动**：它本来就走 1.0.6.1 起的自实现 `FabricDownloader`，是四家里唯一没问题的。
- **验证方式**：① 反射验证台直接调**编译好的产品程序集**里的
  `NormalizeLoaderVersion` / `BuildInstallerUrl` 与 `DownloadSources.ToMirror`，
  17 项断言全 PASS（含 `1.20.1-forge-47.1.105` 这类双前缀形态、以及三条镜像改写）；
  ② 端到端用最小 `.mrpack` 真实装 Quilt / Forge / NeoForge 三个实例。
- 排查过程确认了一个上层事实：库的 `HttpUtil.FlurlClient` 必须在启动时由
  `InitializeHelper.Initialize()` 注入（应用里由 `App.axaml.cs` 的
  `SettingsService.ApplyDownloadSettings()` 完成）—— 库内部多处直接
  `HttpUtil.FlurlClient.Request(...)`，绕过了它自己的 null 检查，未初始化时就是 NRE。


## [1.0.6.8] - 2026-10-11

修掉整合包安装里两个互相叠加的「必现」问题：**本地没有该基础版本时安装直接崩掉**，
以及**失败一次就永久占掉一个实例名**。

### 修复

- **拖入 / 下载整合包报「目标目录不存在或已被删除，请检查游戏目录设置」**
  （拖入 Modrinth 上的「Zombie Invade 100 Days」，1.20.1 / Forge 47.4.10，稳定复现）。
  根因在 `ModpackInstaller.EnsureVanillaAsync` 里判断「这个基础版本装没装」的那一句：
  `new MinecraftParser(mcFolder).GetMinecraft(version)` 在**实例目录不存在时会抛
  `DirectoryNotFoundException`**（实测 `…\versions\1.20.1 not found`），**而不是返回 null**。
  1.0.6.5 起一直如此，只是之前测的都是本地已装好的版本，这条分支从未被走到。
  于是「装一个本地还没有的版本」这一步会把整个安装带崩，异常再被
  `Helpers/DownloadErrors` 统一翻译成了跟真实原因无关的「目标目录不存在」。
  现在把解析器调用包在 try/catch 里：**「解析不出来」和「解析抛异常」一律视作「还没装」**，
  继续往下走下载流程 —— 该装的照样装，只是不再崩。
- **一次失败的安装会永久占掉一个实例名**。`BuildInstanceId` 判断重名用的是
  「`versions/<id>` 目录在不在」，而 `ResolveGameRoot` 在安装一开始就先把该目录建出来了，
  所以**失败一次就留下一个空目录**，下次重试被当成重名 → 名字一路 `(1) (2) (3)…` 加下去。
  现场实测留下了三个空目录（`Zombie Invade 100 Days_forge_47.4.10` 及其 `(1)`、`(2)`）。
  现在判据改成**「有没有版本 JSON」**（`versions/<id>/<id>.json`），与
  `GameInstanceHelper.GetInstanceIds()` 完全一致 —— 只有真正可用的实例才占名字。

### 说明

- 这两个问题互相放大：**崩 → 留下空目录 → 下次重试名字变了 → 再崩**，用户看到的就是
  「怎么试都装不上，而且实例列表越来越乱」。修好之后，缺基础版本的整合包会正常进入
  「下载 Minecraft 版本 → 安装加载器 → 下载整合包文件 → 铺开覆盖层」的完整流程。
- 同时确认了**「基础版本已装好」的快路径没有被破坏**：仍然直接复用、不会重新下载。
- 验证方式：新增反射验证台，直接调用**编译好的产品程序集**里的私有方法（测的就是会发布出去的那份 IL），
  16 项断言全部通过（另 1 项因需要已初始化的 `SettingsService` 而跳过），
  其中包含「旧判据在同一现场会给出 `(3)`」的反向对照，确认用例能区分修复前后。
- 已清理测试目录里那三个空残留目录。


## [1.0.6.7] - 2026-10-11

给整合包安装补上**进度详情页**，并让下载页的数据**自动保持最新**。

### 新增

- **整合包安装进度详情页**（`Views/Windows/ModpackProgressWindow`）。
  以前开始装整合包后只有一个「45%」的进度条，用户看不到「在装哪个文件、卡在哪一步、哪些文件失败了」。
  现在会自动弹出一个独立窗口（可随时关掉、安装照常继续），展示：
  - **包信息**：整合包名、格式（Modrinth `.mrpack` / CurseForge `.zip`）、所需 Minecraft 版本、
    加载器与版本、即将创建的实例 id；
  - **阶段条**：准备安装 → 安装游戏版本 → 安装加载器 → 下载整合包文件 → 写入随包文件 → 校验实例，
    每步有「等待 / 进行中 / 完成 / 失败」四种状态（原版包会自动省掉加载器那一步）；
  - **文件清单**：清单里每个待下载文件逐行显示状态与失败原因，支持滚动（虚拟化，几百个文件也不卡）；
  - **实时读数**：总进度、当前步骤、下载速度、已用时间、按平均速度估算的剩余时间；
  - **取消安装**按钮（二次确认 —— 取消后已下载的文件会保留，但实例可能不完整）；
  - 完成后显示结果摘要（新建的实例、下载数、随包文件数、未成功的文件），并提供
    **打开实例目录**按钮。
  - 数据源是新增的 `Services/ModpackInstallSession`：把安装器的进度回调累积成
    「阶段走到哪、每个文件什么状态、一共花了多久」。安装器那边为此扩展了进度载荷
    （`ModpackProgress` 增加 `Phase` 与文件级事件），原来只有百分比和一句文案。
  - 关闭窗口不会中断安装；「下载任务」列表里整合包任务的「查看详情」按钮现在直接打开这个窗口
    （安装进行中可随时把详情页调出来）。
- **下载页数据自动刷新**。两个时机各刷一次：
  - **程序运行后 1.3 秒**：主窗口广播一次刷新（社区资源列表 + Minecraft 版本列表），
    并顺手把版本清单催熟，这样用户走到下载中心时数据往往已经就绪；
  - **每次切入页面**：进入下载中心、或在左侧分类之间切换时，都会重新拉一次当前分类的数据。
  - 两处都带 800 毫秒节流：这两个时机可能几乎同时发生（启动后立刻点开下载中心），
    合并成一次就不会同一秒里发两遍请求；而「页面刚建好时自己那次初始化加载」也不会被重复触发。

### 改进

- 下载资源页的**版本列表与资源列表改为并行拉取**。原先串行：版本清单是一条可能重试好几轮的
  HTTPS（弱网下实测能拖十几秒），会把资源列表一起卡住。
- 进度详情页的「已用时间」计时器改为可选：拿不到 UI 调度器时只是不显示时间，不再影响安装。

### 说明

- 启动刷新**先广播、再催熟版本清单**。反过来写会把广播一起拖住 —— 实测弱网下
  `EnumerableMinecraftAsync` 重试能把这一句推到十几秒之后，刷新就失去了意义。
- 详情页会话的状态机做过 56 项复现台校验（阶段推进、文件级事件、成功 / 失败 / 取消三种收尾、
  原版包与带加载器两种阶段组合），全部通过。
- 已用时间 / 剩余时间为估算值：并行下载时进度是「已完成文件数」的线性插值，不追求精确。


## [1.0.6.6] - 2026-10-11

整合包安装的**端到端实测修复**。1.0.6.5 的实现通过了清单解析测试台，但拿真实的
本地整合包跑完整安装链路时暴露出三个只有「真下真铺」才会显形的问题。

### 修复

- **整合包里的模组会下到 `.minecraft` 根目录，而不是实例目录**（本次最严重）。
  安装时统一用 `mcFolder` 当落点，但启动时游戏目录是
  `VersionIsolation ? versions/<实例> : .minecraft`（见 `RunPageViewModel`）。
  开着版本隔离（默认开）时，模组被写进 `.minecraft/mods/`，游戏却去
  `versions/<实例>/mods/` 找 —— 玩家看到的现象是「整合包装完了但没有任何模组生效」。
  现在新增 `ModpackInstaller.ResolveGameRoot()`，用**与启动完全相同的条件**决定落点，
  模组下载与 `overrides` 铺开都走它。这是本项目反复强调过的约定：
  **下载落点必须与启动时的 gameDir 同源，改一处必须改另一处。**
- **实例版本 JSON 丢失，装完的整合包在启动页看不到**。
  `ResolveGameRoot()` 会先把 `versions/<实例>` 建出来放 mods/config，而原版整合包复制
  版本目录的老实现拿「目标目录已存在」当跳过条件 → 版本 JSON 永远不被复制，
  解析器报 `client.json not found`。现改为**逐文件补齐缺失项**（幂等），
  并只在目标 JSON 尚不存在时才改写 `id`。
- **清单里 `http://` 的下载地址被全部丢弃**。`FirstDownloadUrl()` 写死了 `https://` 前缀，
  而整合包规范只要求「可直接下载的 URL」，并没有限定协议；自建镜像、局域网分发、
  内网测试包因此会被判成「没有下载地址」而跳过。现在接受 http / https，
  只挡 `file://` 之类其它协议。

### 说明

- 三个问题都由**真实整合包端到端安装**（本地 HTTP 服务 + 自建 `.mrpack`）发现：
  清单解析测试台全 PASS 仍然漏掉了它们，因为它们全部发生在「写文件」阶段。
  **解析对了不等于装对了**，涉及落点的改动必须跑一遍真安装。
- 顺带确认 `Task.ContinueWith` 只判 `IsFaulted` 不算「异常已观察」——
  弱网下版本清单读取失败会重试（日志 `获取 Minecraft 版本清单失败（第 N/3 次）`），
  属已知瞬时故障，不影响本次改动。


## [1.0.6.5] - 2026-10-11

**新增整合包安装**，并修复了**下载中心所有社区资源都提示「没有匹配的资源」**的问题。

### 新增

- **整合包安装（新建实例）**：支持 Modrinth 的 `.mrpack` 与 CurseForge 的 `.zip`。
  安装语义是**直接新建一个 Minecraft 实例** —— 自动装好整合包声明的基础版本与加载器
  （Fabric / Quilt / Forge / NeoForge / 原版），下载包内全部模组，并把随包携带的
  `overrides`（客户端覆盖层）铺进实例目录。
  - **两个入口**：① 把整合包文件**拖入主窗口**任意位置，出现整窗提示后松手即安装；
    ② 在**下载中心 → 整合包**里选一个版本，点列表项上的「安装整合包」。
    两条入口共用同一套下载 / 解包 / 进度上报流程（`Services/ModpackInstallCoordinator.cs`）。
  - 清单解析（`Services/ModpackInstaller.cs`）：Modrinth 读 `modrinth.index.json`
    （`dependencies` 给出 `minecraft` / `fabric-loader` / `quilt-loader` / `forge` / `neoforge`；
    按 `env.client == "unsupported"` 跳过服务端专用模组）；CurseForge 读 `manifest.json`
    （`minecraft.version` + `modLoaders[]`）。
  - **防目录穿越**：整合包是外部输入，清单里的每个文件路径与 ZIP 内每个条目都会校验
    「解析后仍在实例目录内」，拒绝 `..` 上跳、绝对路径与 UNC 路径。
  - 实例 id 由「整合包名 + 加载器 + 加载器版本」生成，重名自动追加序号，**绝不覆盖已有实例**。
- **整合包详情页**：下载中心打开整合包时，标题与提示文案会跟着资源类型变化
  （整合包 / 资源包 / 光影 / 数据包各有自己的抬头与说明），整合包行提供「安装整合包」按钮。
- **装完立刻可见**：安装完成后自动通知「启动」页刷新实例列表，不需要重启启动器。

### 修复

- **下载中心所有资源都提示「没有匹配的资源」**（用户报：社区资源五个分类 + 原版版本列表
  「一直如此，必现」）。根因是资源搜索失败时会把列表清空，而空列表正好触发空状态文案 ——
  用户看到的是「没有匹配的资源」，实际上根本没搜成功。Modrinth 服务器在境外，
  原实现既无重试、也读不到代理设置、还有 20 秒单发超时，国内网络下必然失败。三处修掉：
  - **网络层**（`Services/ModrinthSearchService.cs`）：补**三轮重试**（带退避，
    4xx 非 429 直接放弃不白重试）、显式读取 `HTTPS_PROXY` / `HTTP_PROXY` 环境变量配置代理
    （`SocketsHttpHandler` 原生不支持，必须显式装配）、连接超时 10 秒、
    单请求独立超时（关掉 HttpClient 默认的 100 秒总超时），失败时给出可操作的中文原因。
  - **状态分离**（`ViewModels/Pages/DownloadResourcesViewModel.cs`）：新增「加载失败」状态，
    失败时**不再清空已有列表**，并按异常类型给出可读文案（超时 / 无法连接 / 429 限流 / 服务端故障 / 被拒绝）。
  - **空状态区分**（`Views/Pages/DownloadResourcesPage.axaml[.cs]`）：失败态显示断网图标 + 原因 +
    「重试」按钮；真的没有结果时才显示「换个关键词或筛选条件试试」。以前两者长得一模一样。
- **版本清单加载失败不再静默**：下载中心初始化时的版本列表加载原先是一个空 `catch {}`，
  出问题时日志里完全无线索，现在会记一条警告（且不影响资源搜索）。

### 说明

- 整合包安装是重操作（下载一整个 Minecraft + 加载器 + 模组），带全局防重入：
  正在安装时再次拖入会提示等待，不会并发出两个安装互相抢带宽。
- CurseForge 整合包里的模组需要登录 CurseForge 才能自动下载，本实现会先建好实例与加载器，
  并在提示里说明这些模组需要手动补齐（不静默跳过）。


## [1.0.6.4] - 2026-10-10

下载环节补上**完整的错误捕捉与翻译**。用户报的原文是：

> 依赖库下载失败（The process cannot access the file '…sponge-mixin-0.17.4+mixin.0.8.7.jar.vbltmp'
> because it is being used by another process.）：sponge-mixin-0.17.4+mixin.0.8.7.jar

两个问题：① 临时文件被别的句柄占住时，重试仍然用**同一个**临时文件名，于是每一轮都撞在同一把锁上，
把「重试次数」白跑完才失败；② 抛出来的是英文原文，用户看不出是网络问题还是本地文件冲突。

### 修复

- **临时文件重名导致的「文件被另一进程占用」**（`Services/FabricDownloader.cs`）：下载库文件时，
  临时文件从固定的 `xxx.jar.vbltmp` 改为**每次尝试独立命名**（`xxx.jar.<随机>.vbltmp`）。
  只要出现两条并发下载落到同一目标（Fabric 清单里重复列出的库、或同时开了两个启动器），
  固定名就会互相抢占；随机后缀后任何两次尝试都不再可能撞车。
- **依赖库清单去重**：`ExtractLibraries` 现在按落点路径去重，避免同一个 artifact 被下发两次。
- **单个库失败不再拖垮整批**：并行下载改为收集失败清单（最多列 5 个文件名 + 各自原因），
  其余文件照常下完再一次性报出。以前 `Parallel.ForEachAsync` 的第一个异常直接上抛，
  外层还裹着 `AggregateException`，真实原因经常在这一层被吞掉。
- **致命错误快速放弃**：磁盘满 / 无权限 / 路径过长这几类重试也没用，现在立即停止，
  不再把「重试次数」白跑完。
- **写盘步骤也纳入保护**：写版本 JSON、创建 libraries 目录失败时同样给出中文可操作的提示。

### 新增

- **`Helpers/DownloadErrors.cs`**：统一的「本地文件系统异常」分类与翻译层。按异常类型 /
  Win32 HRESULT / 关键字识别**文件被占用、磁盘已满、权限不足、路径过长、目录不存在**五类，
  每类给出「原因 + 怎么办」，并提供 `IsFatal`（重试无意义）供下载器决定是否快速放弃。
- **原版下载器同样的错误捕捉**（`Services/VanillaDownloader.cs`）：每一步（版本 JSON、写盘、解析、
  依赖下载）的异常都翻成中文；版本 JSON 补上「官方 ↔ BMCLAPI 镜像」双源兜底与重试，
  单次请求独立超时（不再受 HttpClient 默认 100 秒总超时影响）。
- **模组下载（另存为 / 添加到实例）**：改为「先写临时文件 → 整包写完改名」的原子落盘，
  mods 目录里不会再出现半截 `.jar`；错误提示同样接上统一翻译。
- **版本清单预热不再抛「未观察到的任务异常」**（`Services/MinecraftVersionCache.cs`）：
  `Preload()` 原先只判 `IsFaulted`、没读 `Task.Exception`，任务被 GC 时终结器会把它当
  「未观察到的任务异常」抛出，日志里多一条带堆栈的 ERROR，看着像程序崩了（本机冒烟测试实测到）。
  同时给清单拉取补上**重试 3 次**与「失败任务不留在缓存」（否则一次抖动会让整轮会话都拿同一个失败结果），
  并把手截断响应体导致的 `JsonReaderException` 翻成「服务器返回的数据不完整」。
- **首页新闻拉取加了重试与空数据保护**（`Helpers/MojangNewsHelper.cs`）：
  原来是裸 `GetStringAsync` + `Deserialize`，无重试、超时用默认 100 秒，
  且 `root` 为 null 时会在 `root.Entries` 上直接空引用。

### 说明

- 报错文案里现在会明确区分「是网络问题」还是「是本地文件冲突」：
  后者会提示关闭正在运行的游戏 / 杀毒软件，前者才引导去「设置 → 下载」切换下载源。
  以前两种都会指向「切换下载源」，对文件占用类问题完全是误导。


## [1.0.6.3] - 2026-10-10

「添加到实例」补上**加载器校验**：以前只要本地有实例就能点，导致 Forge 模组能被塞进 Fabric 实例、
模组装进纯原版实例，进游戏就是崩溃或直接不加载。现在按加载器判定能否添加，对不上就只留「另存为」。

### 新增

- **实例加载器识别**（`Helpers/GameInstanceHelper.cs`）：扫描版本 JSON 的 `libraries` Maven 坐标
  （`net.fabricmc:*` / `org.quiltmc:*` / `net.neoforged*` / `net.minecraftforge:*` / `com.mumfrey:*`），
  取不到线索再看 `mainClass`，都没有判定为**原版**；没线索时沿 `inheritsFrom` 往上找（最多 4 层，防环形继承）。
  下拉框条目直接标注加载器，如 `1.21.4_fabric_0.16.5（Fabric）`、`26.4-snapshot-3（原版）`。
- **加载器兼容判定**：Fabric 模组 → Fabric / Quilt 实例（Quilt Loader 向下兼容 Fabric 模组，反之不成立）；
  Quilt / Forge / NeoForge / LiteLoader 模组 → 各自的实例；**原版实例（没有加载器）一律只能「另存为」**。
- **每行显示「需要什么加载器」**（如 `Fabric / Quilt`、`未标注加载器`）：按钮不出现时用户能立刻看懂原因，
  不用去猜「为什么这行没有添加按钮」。
- **默认选中有用的实例**：列表加载完会自动选「能装上至少一个文件」的实例，
  不会再默认停在原版实例上导致整页按钮全灭。

### 修复

- **模组加载器筛选框从来没生效过**：`comboBox1` 的条目是 `ComboBoxItem`，原代码用
  `SelectedItem as string` 取文字恒为 `null`，等于永远「全部」。现改为取 `ComboBoxItem.Content`。
- **游戏版本筛选改用真实支持列表**：优先用 Modrinth 标注的 `game_versions` 判定，
  拿不到时才退回「文件名里含版本号」的旧做法（旧做法选 `1.21.4` 时，文件名不带该串的兼容文件会被漏掉）。
- 两个筛选下拉的首项由空白改为「全部」，不再是看不懂的空条目。
- 筛选不再重建列表项：原实现在每次筛选时 `new` 一批行对象，切筛选会丢掉按钮的显隐状态。

### 说明

- **NeoForge 必须先于 Forge 判断**：NeoForge 1.20.1 发布在 `net.neoforged:forge` 下，与 Forge 自己的
  `net.minecraftforge:forge` 只差 groupId；且两者都用 `cpw.mods` 的启动器类，只看 `mainClass` 分不开。
- **OptiFine 单装版不算加载器**：它也挂在 `launchwrapper` 上启动，若不特判，会被 `mainClass` 规则误判成 Forge，
  进而放行 Forge 模组进一个其实装不了 Forge 模组的实例。
- **加载器标签为空时不拦截**（Modrinth 未标注 ≠ 不能装），拦下来只会变成「功能看起来坏了」。
- 资源包 / 光影仍不校验加载器：它们由游戏或 Iris/OptiFine 读取，与模组加载器无关，加了拦截只会误伤。


## [1.0.6.2] - 2026-10-10

资源详情页（模组 / 资源包 / 光影）新增**「添加到实例」**：不用再「另存为」到别处再手动拷进 `mods`，
选中目标实例即可一键下到位。

### 新增

- **「添加到实例」按钮**：列表每行在「另存为」旁多出一个按钮，直接把文件下进所选实例的对应目录。
  本地**一个实例都没有时不出现该按钮**，整行仍只有「另存为」—— 不给会落空的入口。
- **目标实例下拉框**：页面顶部列出本地已安装实例（口径与启动页一致：排除被 `inheritsFrom` 继承的基础版本）。
  只有一个实例时默认选中它，多个时手动切换。
- **落点随「版本隔离」自动判定**：开启 → `versions/<版本>/<子目录>`；关闭 → `.minecraft/<子目录>`。
  与启动时 `gameDir` 用的是同一个条件，保证「下进去的模组游戏一定读得到」。
  下拉框右侧实时显示判定结果（如 `版本隔离：开 → .minecraft/versions/1.21.4/mods`）。
- 子目录按项目类型区分：模组 → `mods`、资源包 → `resourcepacks`、光影 → `shaderpacks`。
  整合包要解包安装、数据包要放进具体存档，两者**不提供**「添加到实例」，仍只有「另存为」。

### 改进

- **同名文件不覆盖**：目标已存在同名文件时自动追加 ` (1)`、` (2)`，不会把玩家已有的模组冲掉。
- **失败清理半截文件**：下载中断时删掉没写完的文件 —— 在 `mods` 里留一个损坏的 `.jar` 会让游戏直接崩，
  比没下载更糟。
- **文件名规范化**：从下载链接取文件名时去掉查询串 / 片段并清洗非法字符，没带扩展名时自动补上推断的扩展名。

### 说明

- 新增 `Helpers/GameInstanceHelper.cs`：实例枚举与落点解析的统一入口（实例口径、版本隔离条件都只此一处）。
- `ModPage.LoadForProject` 增加 `projectType` 参数，由资源搜索页在跳转时传入，用于决定子目录与入口显隐。

## [1.0.6.1] - 2026-10-07

修复**安装 Fabric 时 HTTP 超时**（用户实测报错：

```
The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.
  ---> System.IO.IOException: Unable to read data from the transport connection
       at System.Net.Http.HttpConnection.ChunkedEncodingReadStream.CopyToAsyncCore(...)
       at VibrantbitLauncher.Services.FabricDownloader.InstallAsync(...) FabricDownloader.cs:line 64
```

）的问题。定位到是**网络层三个缺陷叠加**，与 Fabric 本身无关：

1. **超时是 HttpClient 默认的 100 秒，且罩不住响应体读取**。`meta.fabricmc.net` 的 profile JSON
   走 chunked 传输，在部分线路上会「响应头回来了、body 读到一半就再无数据」，
   于是用户干等 100 秒，最后只看到一句英文异常。
2. **设置页的「BMCLAPI 镜像」对 Fabric 完全无效**。`FabricDownloader` 里的 URL 是写死的官方域名，
   没有任何镜像改写 —— 实测日志显示用户已经切到镜像，依旧超时。
3. **没有重试，库文件还是一个一个串行下载的**。

### 修复

- **每个请求独立超时**：元数据 12 秒、库文件 90 秒、建连 8 秒。
  HttpClient 自身的 100 秒总超时被关掉（`Timeout.InfiniteTimeSpan`），改由每次请求的
  `CancellationTokenSource` 精确控制 —— 留着它，老问题会以原样回来。
- **官方源与镜像互为兜底**。新增 `Helpers/DownloadSources.cs` 维护一份**已实测**的映射表
  （`meta.fabricmc.net`、`maven.fabricmc.net`、`maven.minecraftforge.net`、
  `files.minecraftforge.net/maven`、`libraries.minecraft.net`），
  用户设置只决定「谁先试」，两个源都败才算失败。
  未实测过的路径一律不猜（改出个不存在的路径只会白耗一次超时，比不改更糟），
  所以 Quilt 暂不在表内，行为与改动前一致。
- **源健康度**：某个主机被实测为「慢」（单次请求 > 15 秒）或「坏」后，10 分钟内排到候选末尾。
  这一层很关键 —— 官方 `maven.fabricmc.net` 在国内常见的情况是**能通但极慢**
  （实测 8 个库磨了 145 秒，平均 18 秒一个），它不报错所以不会触发换源；
  只有把「慢」也记成失败，后面的库才会自动绕开它。
- **库文件改为并行下载**（并发数取 `下载线程数`，但夹在 1~4 —— 实测按默认值 10 并发打
  BMCLAPI 会触发瞬时限流 403，4 路已经比原来串行快好几倍）。
  进度按版本 JSON 里声明的大小计算，支持失败重试时的字节回滚。
- **落盘改为 `.vbltmp` + 改名**。原来直接写目标路径，中途失败会留下半截 jar，
  而下次 `File.Exists` 会把它当成「已下载」跳过 —— 之后要等启动游戏时才以
  「找不到类」这种莫名其妙的方式炸出来，排查成本极高。
- **限流（403 / 429）不再当致命错误**：多给 4 轮重试 + 最长 6 秒退避。
- **报错文案中文化且可操作**。原来是一句
  `The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.`，
  现在会说清是超时还是 404/400/限流、试过哪几个源、以及下一步该做什么
  （「设置 → 下载」切镜像 / 检查代理）。
  顺带把 `EnsureSuccessStatusCode()` 换成自己判状态码 —— 前者不填 `StatusCode`，
  导致 400 和超时在用户眼里都是 "Unknown"。

### 改进

- **安装页的四个加载器版本列表改为并行查询**（原来顺序 await，一家卡住后面三家排队干等），
  并给单家查询加了 25 秒上限。弱网下「安装页一直转圈」由此解决。
- **Fabric 版本列表改走自实现版本**（享受镜像 / 精准超时 / 重试），不再用库自带的裸请求。
- **删掉安装时那次重复的联网查询**。原来为了拿一个 entry 对象，会再调一次
  `FabricInstaller.EnumerableFabricAsync` + `FabricInstaller.Create` ——
  而实际安装走的是自实现下载器，那次请求纯属多余，且正是超时高发点之一。

### 说明

- 实测（`Helpers`+`Services` 源码直接编进独立探针工程）：
  - 用本地「先回响应头、body 卡住 35 秒」的服务器复现用户日志里的失败形态，
    **12.0 秒**准时放弃慢源并改用官方源，随后拿到全部 253 个版本 —— 修复前会干等 100 秒。
  - 官方源不可达时自动改走镜像可用；镜像不可达时自动回官方，两个方向都验证过。
  - 全新安装 8 个库 → `libraries` 下 8 个 jar、无 `.vbltmp` 残留、`MinecraftParser` 解析成功。
  - 重复安装（文件已齐）1.9 秒完成；loader 版本不存在时 1.2 秒抛出中文可操作错误。

## [1.0.6] - 2026-10-07

新增**性能模式**，解决低配电脑（尤其是 **Windows 7 x86**）打开就**白屏、加载不出来、非常卡顿**的问题。

### 新增

- **性能模式（设置 → 个性化 → 性能模式）**：一个总开关，专为低配电脑设计，开启后关闭
  云母（Mica）材质、壁纸背景、页面切换与控件悬停 / 按下的动效、卡片阴影、平滑滚动，
  并把位图缩放从「高质量重采样」降为低开销插值，同时改用**软件渲染**。
  绝大多数改动即时生效，渲染方式在下次启动时切换。
- **运行环境自动探测**（`Helpers/AppEnvironment.cs`）：启动时读注册表
  `Windows NT\CurrentVersion` 判定真实系统版本（不再依赖会谎报的 `Environment.OSVersion`），
  并用 `GlobalMemoryStatusEx` 取物理内存，连同 CPU 核数、是否 32 位进程一起写进启动日志。
- **低配自动判定 + 一次性询问**：满足「32 位进程 / Windows 7 及更早 / 内存 < 6GB /
  CPU ≤ 3 核」任一条件且用户此前没被问过时，主窗口出来后弹一次确认框，
  询问是否开启性能模式；无论选什么都不再问第二次（记录 `PerformanceModeAsked`）。
- 新增两个设置项：`PerformanceMode`（是否开启）与 `PerformanceModeAsked`（是否已询问）。
- 支持两个调试 / 兜底入口：命令行 `--software-render`、环境变量 `VBL_SOFTWARE_RENDER=1`，
  用于在无法进入设置界面时强制软件渲染排查问题。

### 修复

- **修复 Windows 7 等非 Windows 11 系统上的启动白屏**。
  根因有两处：① `TryEnableMicaEffect()` 会把窗口背景设成 `Transparent` 并请求 Mica，
  而 Mica 只在 Windows 11 上存在，老系统上既拿不到材质、背景又透明 —— 整窗不着色；
  ② 窗口底色资源 `AppWindowBackgroundBrush` 是 `#26FFFFFF`（15% 白 + 透明），
  在没有窗口合成能力的系统上没有可合成的底，结果就是接近纯白。
  现在 Mica 严格只在 Windows 11 且未开性能模式时启用，且该资源会兜底成**不透明纯色**
  （亮色 `#F3F3F3` / 暗色 `#202020`）。
- **修复 Mica 判定不可靠**：原先用的是 `Window.IsWindows11`，它基于会谎报系统版本的
  `Environment.OSVersion`（未声明 supportedOS 时 Win8.1 以上一律报 6.2），
  现改为读取注册表 `CurrentBuildNumber` 自行判定。
- **修复高性能与低端机共用的一个观感问题**：性能模式下跳过窗口入场动画并把 `AppRoot`
  显式摆到终态（透明度 1、缩放 1、位移 0），避免动画在低配机器上一卡就长时间停在
  「淡 + 缩」的半路，看上去像糊了一层。

### 改进

- **启动链路重排：把重活推到主窗口显示之后**。下载参数初始化、版本清单预热、
  版本 JSON 规范化这三步原先都在 `OnFrameworkInitializationCompleted` 里同步跑完才返回，
  而窗口要到它返回后才由平台显示 —— 在低配机器上这段时间正是用户看到的白屏。
  现在统一移到 `MainWindow.Opened` 之后（`RunPostShowStartupWork`），
  观感变成「界面先出来、内容随后填充」。页面导航本来就在 `OnOpened` 里，顺序不受影响。
- 性能模式下**跳过版本清单预热**：这一步要起线程 + 拉 HTTPS + 反序列化上万个条目，
  在单核 / 32 位进程（用户态地址空间仅 2GB）上代价明显，而它只是为了让「下载中心」
  首次打开快一点。
- 性能模式下把**版本 JSON 规范化**再延后 3 秒，避开窗口首帧的资源竞争。
- 样式层面用**根类 `performanceMode`** 整体关动效（见 `Styles/Animations.axaml` 末尾）：
  Avalonia 的选择器读不到资源值，给窗口根节点挂一个类是唯一零运行时开销的做法。
- 启动日志新增三行，便于远程排障：运行环境摘要、性能模式来源、实际使用的渲染后端。

### 说明

- .NET 10 **官方不支持 Windows 7**（Win7 上最后一个受支持的版本是 .NET 6，
  且已于 2024-11-12 结束支持）。若目标机确实装上了可运行的 .NET 10 运行时，
  本版本的所有优化都在此前提下生效；如果连运行时都起不来，需要先解决框架本身。
- 32 位（x86）进程的用户态地址空间上限是 2GB，与机器装了多少物理内存无关，
  大图 / 长列表会更快触顶 —— 这是性能模式默认关闭壁纸和清单预热的主要原因。

## [1.0.5.21] - 2026-10-07

按需求调整打包格式：**Windows 架构只出 `.zip`，不再附带一份 `.tar.gz`**。

### 改进

- **`win-*`（`win-x64` / `win-x86` / `win-arm64`）现在只生成 `.zip`**，不再生成 `.tar.gz`。
  Windows 上 zip 双击解压更顺手，同一份内容打两种格式既浪费空间、也白占校验和的行数。
- `linux-*` 仍然只出 `.tar.gz`（要保留可执行位）；`osx-*` 仍然只出 `.tar.gz`
  （内含 `.app` bundle，外加 `install.command` 与 `README-macOS.txt`）。
- `publish-all.sh` 在**没有 `zip` 命令**的环境里会退回 `tar.gz` 并打印提示，避免该平台一件产物都没有。
- 两个脚本头部的产物结构说明同步更新。

### 说明

- 旧格式残留（`dist/*win*.tar.gz`）不在脚本的清理职责内：需要时用 `-Clean` / `--clean`
  重跑一遍（会清空 `publish/` 与 `dist/`），本次已据此重发全部 7 个平台。

## [1.0.5.20] - 2026-10-07

修复**发布包在 macOS / Linux 上无法直接使用**的问题，并给 macOS 包补上开箱即用的安装脚本。

### 修复

- **打包丢了可执行位**：Windows 文件系统没有「可执行位」，在 Windows 上打出来的 `.tar.gz`
  里所有文件权限都是 0666、目录 0777 → 解压到 macOS 后 `.app` 的主可执行文件不可执行
  （双击毫无反应），Linux 上 `./VibrantbitLauncher.Avalonia`、`./install.sh` 也都跑不起来。
  修法：打包统一走 `make_tar` / `New-TarGz`，**检测到 GNU tar 就加 `--mode=u+rwx,go+rx` 强制 0755**；
  只有 bsdtar（Windows 自带）时退化为原行为并明确警告。
  - `.sh` 侧直接用 `tar`（Git Bash / Linux / macOS 上都是 GNU tar）；
  - `.ps1` 侧优先用 Git 自带的 `C:\Program Files\Git\usr\bin\tar.exe`（GNU tar 1.35），
    系统自带的 `System32\tar.exe` 是 bsdtar 3.8、不支持 `--mode`，仅在没有 Git 时兜底。
  - 实测归档内权限由 `-rw-rw-rw-` 变为 `-rwxr-xr-x`（目录 0755）。
- **`Info.plist` 带 UTF-8 BOM**：PS 5.1 的 `Set-Content -Encoding UTF8` 会写 BOM，plist 以 `EF BB BF`
  开头，macOS 的 `plutil` 会判为非法格式。改为 UTF-8 **无 BOM** 落盘。
- **`SHA256SUMS.txt` 带 UTF-8 BOM**：同样来自 PS 5.1 的 `-Encoding UTF8`。带 BOM 时
  `sha256sum -c SHA256SUMS.txt` 在 Linux / macOS 上会直接失败（首行文件名被拼上了 BOM）。改为无 BOM。
- **`install.sh` 的中文被写成问号**：PS 5.1 用 `-Encoding ASCII` 落盘，中文注释全成 `?`。改为 UTF-8 无 BOM。
- **`SHA256SUMS.txt` 用了 CRLF 换行**：写文件时取了 `[Environment]::NewLine`，在 Windows 上是 `\r\n`，
  于是 Linux / macOS 上 `sha256sum -c` 读到的文件名尾部多一个 `\r`，报
  `No such file or directory`（十个包全报 FAILED）。改为 `\n`。
- **Windows 的 zip 覆盖被环境的删除钩子打断**：`Compress-Archive` 覆盖同名文件时内部会先删一次，
  而某些环境把「删除」改写成回收站操作并抛错（`SAFE_DELETE_FAIL_CLOSED`），
  会让整个平台被标记为失败。改为**先用 `Remove-FileSafe` 清掉旧 zip**（删完复查是否真的没了），
  并把 zip 生成失败降级为警告（tar.gz 已经出好，不受影响）。

### 新增

- **macOS 包内附 `install.command`（双击即执行）**，一次做完三件事：
  ① `chmod +x` 补可执行位；② `xattr -dr com.apple.quarantine` 去掉下载隔离属性（绕过 Gatekeeper）；
  ③ `codesign --force --deep --sign -` 做 **ad-hoc 自签名**。
  > 第 ③ 步是硬性的：Apple Silicon 内核**拒绝执行未签名的 arm64 二进制**，
  > 而在 Windows 上交叉发布的 osx-arm64 可执行文件没有签名，直接双击会闪退或提示「已损坏」。
  > ad-hoc 签名属本地自签，不需要开发者证书、不需要联网。
- **macOS 包内附 `README-macOS.txt`**：架构选择（M 系列 → osx-arm64 / Intel → osx-x64）、
  装 .NET 10 Runtime（**macOS 上没有 "Desktop Runtime"，就是 Runtime**；Homebrew 装的可能需
  `export DOTNET_ROOT=$(brew --prefix)/opt/dotnet/libexec`）、安装三步、四类常见故障的排查命令、卸载方式。
- 两个脚本的头部注释与 `-h` 帮助补上 macOS 首次运行说明。

### 说明

- `publish-all.ps1` 侧改用 Git 自带的 GNU tar，并把打包拆成两步：`tar --mode=... --force-local -cf`
  打成裸 tar，再用 .NET 的 `GZipStream` 压成 `.tar.gz`。绕开两个坑：
  GNU tar 会把 `C:\…` 里的冒号当成「远程主机」语法（`Cannot connect to C: resolve failed`，用 `--force-local` 关掉）；
  而 `-z` 会 fork 外部 `gzip.exe`，PowerShell 这边的 PATH 里没有 Git 的 `usr\bin`（`gzip: command not found`）。
  `publish-all.sh` 侧不需要这层，GNU tar + `-z` 直接可用。
- **本机仍只能实跑 win-x64**；osx / linux 六个平台只做了归档结构校验（权限位、BOM、`.app` 布局、
  包内脚本存在）。macOS 上的实际启动需要真机验证。
- 验证方式：`tar -tvzf` 看归档内权限位、`xxd` 看首字节（BOM / CR）、`sha256sum -c` 验全套哈希，
  本次三个平台 7 个包全部通过；发布全量 7 平台一次跑通（退出码 0）。

## [1.0.5.19] - 2026-10-07

新增**多平台一键发布**能力：一条命令产出 7 个平台的可分发包，并把 CI 也统一到同一套脚本上。

### 新增

- **`publish-all.ps1`（Windows）与 `publish-all.sh`（Linux / macOS / Git Bash）**，两者功能等价：
  一次产出 `win-x64` / `win-x86` / `win-arm64` / `linux-x64` / `linux-arm64` / `osx-x64` / `osx-arm64`。
  逐 RID 发布 → 按平台打包 → 算 SHA256 → 打一张汇总表；单个平台失败不影响其它平台，退出码反映最终结果。
  - 原始发布目录落在 `publish/<rid>/`，可分发包落在 `dist/`；
  - `win-*` 出 `.tar.gz` + `.zip`（Windows 上双击解压更顺手）；
  - `linux-*` 出 `.tar.gz`，包内附 `install.sh`（解压后 `sh install.sh` 赋可执行权限）；
  - `osx-*` 额外组装 `VibrantbitLauncher.Avalonia.app`（含 `Info.plist`，版本号从 csproj 读）再打包；
  - `dist/SHA256SUMS.txt` 汇总全部包的校验和。
  - 可选参数：`-Rids`（只发部分平台）、`-SelfContained`（自带运行时）、`-Clean`、`-NoPackage`、`-NoTrim`。

### 改进

- **csproj 的 `<RuntimeIdentifier>` 改为条件默认**：`Condition="'$(RuntimeIdentifier)' == ''"` 时才回落 `win-x64`。
  这样 `dotnet publish -r <rid>` 可以自由指定平台，而本地 `dotnet build` / F5 的输出目录仍固定为
  `bin/<cfg>/net10.0/win-x64/`（避免出现「编译成功但跑到的是旧 exe」这种假象）。
- **`TrimPlatformPayload` 从「仅 win-x64」泛化为「按 RID 剥掉其它平台的后端程序集」**：
  - `win-*` → 去掉 X11 / FreeDesktop / DBus / Native / Metal
  - `linux-*` → 去掉 Win32 / Win32.Automation / Native / Metal
  - `osx-*` → 去掉 Win32 / Win32.Automation / X11 / FreeDesktop / DBus
  - 设计器相关（`DesignerSupport` / `Remote.Protocol`）所有平台都去掉

  实测每个平台再省 1.6 ~ 2.8 MB。逃生阀：`-p:SkipPlatformTrim=true`。
- csproj 显式写出 `PublishReadyToRun=false` / `EnableCompressionInSingleFile=false`，
  与 IDE 的发布配置文件（pubxml）行为对齐（此前只有 pubxml 里有，命令行靠默认值）。
- **CI（`.github/workflows/build.yml`）改为直接调用 `publish-all.sh`**，不再自己维护第二套打包逻辑，
  避免两套逻辑长期漂移；同时补上此前缺失的 **`win-x86`**，矩阵从 6 个 RID 扩到 7 个。
- `.gitignore` 增加 `/publish/` 与 `/dist/`（都是几十 MB 的二进制产物）。

### 说明

- 发布模式仍是**框架依赖**（不含 .NET 运行时）。目标机器需要自备：
  Windows → .NET 10 **Desktop** Runtime；Linux / macOS → .NET 10 Runtime。
  想免装运行时，用 `-SelfContained` 重发（体积约 +20 MB）。
- **实测 7 个平台（单文件 / 压缩包 / 平台剥离省下的体积）**：

  | RID | 单文件 | 压缩包 | 剥离省下 |
  |---|---|---|---|
  | win-x64 | 35.8 MB | 15.4 MB | —（早已剥离） |
  | win-x86 | 33.5 MB | 14.7 MB | 2.8 MB |
  | win-arm64 | 34.5 MB | 14.8 MB | 2.8 MB |
  | linux-x64 | 31.2 MB | 13.5 MB | 1.7 MB |
  | linux-arm64 | 30.9 MB | 13.2 MB | 1.6 MB |
  | osx-x64 | 35.9 MB | 16.2 MB | 2.7 MB |
  | osx-arm64 | 36.0 MB | 16.2 MB | 2.8 MB |

  全 7 个平台串行发布总耗时约 **72 秒**（本机增量实测）。
- **只有 win-x64 能在本机实机验证**（实跑结果：日志 `VibrantbitLauncher 1.0.5.19 启动`，异常数 0，配置正常加载）。
  其余 6 个平台做了结构校验（`.app` bundle 布局、`install.sh` 存在、载荷剥离清单），
  但**没有在对应系统上真正启动过**。万一某个平台起不来，先用 `-NoTrim` 重发以排除平台剥离这个因素。
- 写 `.ps1` 时踩到的两个环境坑（都已在脚本里处理）：
  1. **Windows PowerShell 5.1 会按系统 ANSI 代码页解析「无 BOM 的 .ps1」**，中文注释会整体乱码并诱发语法错误
     → 脚本必须存成 **UTF-8 with BOM**；
  2. 某些执行环境把 `Remove-Item` 改写成「移到回收站」，可能**删成功了却仍然抛错**
     → 删除后复查目录是否真的还在，只有还在才算失败。

## [1.0.5.18] - 2026-10-07

继续精简发布体积：**37.9 MB → 35.8 MB**（单文件，−2.1 MB）。这次是**重写 `SkinService`、把
SixLabors.ImageSharp 整块摘掉**，功能与像素结果完全不变。

### 改进

- **新增 `Helpers/RgbaBitmap.cs`**：基于 **SkiaSharp**（Avalonia 渲染后端本来就依赖它）的最小 RGBA 位图，
  提供 PNG 解码 / 编码、像素读写、最近邻缩放。
- **`SkinService` 改用 `RgbaBitmap` 自己实现头像裁剪与正面立绘拼合**，不再依赖 `MinecraftLaunch.Skin` 的
  `CropSkinHeadBitmap()`；新增公开入口 `SkinService.WriteHeadBitmap(path, skinBytes)` 与
  `SkinService.TryReadImageSize(...)`（只读尺寸、不解码像素）。
- `SkinService.ValidateSkinFile` 改为只读图片头拿尺寸，不再为校验而整图解码。
- `AccountPageViewModel` 的三处头像裁剪（微软登录 / 外置登录 / 离线默认皮肤）统一改走 `SkinService.WriteHeadBitmap`。

### 移除

- **`SixLabors.ImageSharp`（2.0 MB）**：全项目仅皮肤裁剪用到，已由 SkiaSharp 顶替。
- **`MinecraftLaunch.Skin`（0.02 MB）**：唯一被用到的是 `CropSkinHeadBitmap()`，其实现正是 ImageSharp。
- 新增显式 `SkiaSharp` 引用（版本与 Avalonia 12.1.0 解析出的 3.119.4 一致，不改变发布载荷，
  只是把原先的隐式传递引用写明白，避免将来 Avalonia 换版本时编译静默失效）。

### 说明

- **踩到的坑：`SKBitmap.Decode` 返回的是预乘（Premul）位图。** 半透明像素在预乘 / 反预乘往返时会有 ±1 取整误差，
  `A=1` 的像素 RGB 甚至会被抹成 0。改用 `SKCodec` 并显式要求 `SKAlphaType.Unpremul` 后，
  实测可与 PNG 原值**逐字节一致**。
- 头像裁剪的算法与参数（底图 (8,8)–(16,16)、叠加层 (40,8)–(48,16)、叠加层仅 `A=255` 时覆盖、
  最近邻放大到 60×60）**逐字照搬原库行为**，刻意连它「叠加层必须完全不透明才生效」这个略显粗糙的规则一起保留，
  就是为了让换库前后生成的头像逐像素一致。
- **对照验证 18 项全过**：新实现 vs 旧库 `CropSkinHeadBitmap`、新实现 vs 旧 `SaveFrontView`（ImageSharp 复现），
  以及**新实现 vs 改动前旧代码真实产出的存量头像 / 立绘 PNG**，逐像素一致；
  另覆盖 64×32 旧皮肤的镜像分支、半透明 / `A=1` 合成皮肤、以及文件校验的 6 种输入。
- 发布产物确认：`SixLabors.ImageSharp` 与 `MinecraftLaunch.Skin` 已不在载荷中；实跑 0 异常。


## [1.0.5.17] - 2026-10-07

继续精简发布体积：**42.6 MB → 37.9 MB**（单文件，−4.7 MB / −11%）。

### 改进

- **剥掉打进发布目录、但 Windows 上永远不会加载的平台程序集**（新增 `TrimPlatformPayload` 目标，
  带 `RuntimeIdentifier == win-x64` 条件，其它平台发布不受影响）：
  `Avalonia.X11`、`Avalonia.FreeDesktop`、`Avalonia.FreeDesktop.AtSpi`、`Tmds.DBus.Protocol`（Linux）、
  `Avalonia.Native`、`Avalonia.Metal`（macOS）、`Avalonia.DesignerSupport`、`Avalonia.Remote.Protocol`（设计器）、
  `System.Diagnostics.EventLog.Messages`（事件日志资源）。
  根因是 `Avalonia.Desktop` 会把所有平台后端一起带进发布载荷，而 win-x64 上 `UsePlatformDetect()`
  只会走 Win32 分支。

### 说明

- 实测排除的几条路：**框架依赖 + 单文件 + 压缩**被 SDK 直接拒绝（`NETSDK1176`：压缩仅支持独立部署）；
  **自包含 + 单文件 + 压缩**反而更大（55.3 MB）；**剪裁**（`PublishTrimmed`）因绑定走反射不能开。
- `Avalonia.Controls.DataGrid` / `Avalonia.Controls.ColorPicker`（共 0.8 MB）虽然本工程没直接用，
  但 FluentAvalonia 有引用，删除有风险，保持不动。



大幅完善设置页面：左栏由「主页 / 个性化 / 其他」三个分类扩成
**主页 / 启动 / 个性化 / 网络 / 日志 / 关于** 六个分类，并把新增的启动与下载设置真正接进游戏启动链路。

### 新增

- **启动 · Java 虚拟机与内存**：Java 版本选择（带「重新检测」）、最大内存（-Xmx）/ 初始内存（-Xms）
  下拉（默认 2G/1G，可「自动推荐」按本机内存一半挑档）、附加 JVM 参数多行输入（一行一条，`#` 开头忽略）。
- **启动 · 游戏目录**：Minecraft 文件夹可选可打开；**版本隔离**开关（每个版本用独立 saves/mods/config）。
- **启动 · 高级**：禁用 IPv6（`-Djava.net.preferIPv4Stack=true`）、GC 模式（自动/G1GC/ZGC/Parallel/Serial）、
  全屏启动、Minecraft 窗体大小（宽×高，改后同时用于 `${resolution_width/height}` 与 `--width/--height`）。
- **个性化 · 主界面**：启动游戏后对启动器自身的行为（不处理 / 最小化 / 关闭）——只在游戏进程确实起来后才执行。
- **网络 · 下载**：BMCLAPI 镜像开关（改动立即影响后续所有下载的 URL 改写）、下载线程数、失败重试次数。
- **日志分类**独立成页：记录等级、保留天数、是否输出到调试器、日志目录与打开/导出/清空、最近日志列表。
- **关于分类**：版本号、简介与致谢。

### 改进

- **启动链路全部接入用户设置**：内存、GC、IPv6、自定义 JVM 参数、窗体大小 / 全屏、版本隔离
  原先都写死在代码里，现在一律读配置（启动日志会打印实际生效的参数）。
- 设置项改动即落盘（`settings.json`），初始化回填控件时用抑制标志挡住无意义的重复保存。
- 新增 `Models/SettingOption.cs`（通用「键-显示名」选项）与内存档位模型，供各下拉复用。
- 下载并发数改为读配置（原版安装器的并发也从 10 固定值改为跟随设置）。

### 修复

- **设置页「关于」图标用了不存在的 `FASymbol` 成员 `Info`**，导致 XAML 编译报
  `AVLN3000: Unable to find suitable setter ... for property Symbol`。已改为合法成员 `Help`。
  注：增量构建会跳过 XAML 校验、看不到该错误，需完整重建才会暴露。



修复安装 1.13 以前的老版本（1.12.2 等）时立刻报
「Operation is not valid due to the current state of the object.」的问题。

### 修复

- **老版本（1.13 以前）装不上、也列不出来**。根因在 MinecraftLaunch 4.0.7 的解析器：
  `MinecraftParser.IsVanilla()` 拿版本 JSON 里的 `arguments` 直接调
  `JsonElement.TryGetProperty("game")`，而 1.13 以前的版本用的是老式的
  `minecraftArguments` 字符串、**根本没有 `arguments` 字段**；此时该 JsonElement 的
  ValueKind 是 Undefined，System.Text.Json 会抛 `InvalidOperationException`，
  文案正是上面那句。同一个 bug 还存在于 `GameArgumentParser` / `JvmArgumentParser`
  （它们也**无条件**对 `Arguments` 调 TryGetProperty），所以老版本连启动都会炸。

- **修法：给缺 `arguments` 的版本 JSON 补一个 `"arguments": {"game": []}`**
  （见新增的 `Helpers/VersionJsonNormalizer.cs`）。库没有留出「JSON 落盘后、解析前」的钩子，
  所以新写了 `Services/VanillaDownloader.cs`——按 `VanillaInstaller` 同样的步骤走一遍：
  下载版本 JSON → 规范化 → 落盘 → 交给库的 `MinecraftParser` 解析 → 交给库的
  `MinecraftResourceDownloader` 下载 client jar / 库 / 资源索引与资源对象。
  重活仍在库里，新增的只有「下载 + 修补 + 解析」这三步。

- **补的 `arguments` 里只放 `game`、不放 `jvm`**。这是踩出来的：`JvmArgumentParser`
  靠「有没有 `jvm` 键」区分新旧两套 JVM 参数，一旦写了 `"jvm": []` 就会走现代分支，
  只吐一个 `-cp`、**丢掉 `-Djava.library.path=${natives_directory}`**，
  老版本进游戏会直接找不到 LWJGL 原生库。留空键即回到老式分支，参数与官方启动器一致
  （实机核对：1.12.2 生成 23 条参数，含 `-Djava.library.path`、`--username`、
  `--version 1.12.2`、`--assetIndex 1.12`、`--accessToken`）。

- **已经装好的老版本会在启动时被自动修复**。App 启动的第 ⑨ 步原本只修 JSON 的
  `+HHMM` 时区格式，现在改成统一的 `NormalizeVersionJsons()`：顺带补 `arguments`。
  这样别家启动器留下的老版本 JSON 也能被正常枚举与启动（否则运行页 /
  版本管理页的 `MinecraftParser.GetMinecrafts()` 会整个崩掉）。

### 改进

- 原版安装的进度文案改为「下载版本信息 / 解析版本 / 下载文件 n/m / 安装完成」，
  并恢复了速度显示（自实现安装器同样回调 `Speed`）。

### 说明

- 改动文件：`Helpers/VersionJsonNormalizer.cs`（新增）、`Services/VanillaDownloader.cs`（新增）、
  `ViewModels/Pages/InstallPageViewModel.cs`、`App.axaml.cs`。
- 实测：1.12.2 原版安装 8 秒完成、26.4-snapshot-2 原版安装 3 秒完成（均已缓存），
  启动日志无任何异常；版本列表能解析出 3 个已装版本。


## [1.0.5.14] - 2026-10-06

本版修掉安装页「安装」按钮会被误判禁用的问题，并让四张加载器卡片在加载期间
有正确的占位文案与勾选状态。

### 修复

- **「安装」按钮不再被误判禁用**。原判断是 `CanInstall => !IsLoading`，而 `IsLoading` 是个
  「什么都算」的总开关，进页面拉取加载器版本列表时它也是 `true`，于是刚进页面那几秒按钮就是灰的。
  现在把忙碌状态拆成两个：

  - `IsLoadingVersions`：正在拉取 Forge / Fabric / NeoForge / Quilt 的版本列表；
  - `IsInstalling`：正在执行安装。

  按钮只看后者：`CanInstall => !IsInstalling`——**只在安装中禁用，其它时候一律可用**。
  「刷新」按钮两者都看（`CanRefresh => !IsLoadingVersions && !IsInstalling`）。
  另保留只读的 `IsLoading = IsLoadingVersions || IsInstalling` 供「有没有在忙」的语义使用。

- **`IsInstalling` 永远会被复位**。原先整个安装流程只有开头一次 `IsLoading = true`、结尾一次
  `IsLoading = false`，中间好几处网络调用（Java 探测、版本清单、加载器枚举）都在 `try` 之外；
  只要其中任一抛异常，标志位就从 `async void` 命令里逃逸出去、永远停在 `true`，按钮就**永久**灰掉。
  现在安装入口拆成 `InstallAsync`（只负责占用 / 释放标志，`try / catch / finally` 兜死）
  与 `InstallCoreAsync`（真正的流程），中途的每个 `IsLoading = false; return;` 都交给 `finally`。

- **连续切换版本时不再互相串台**。`SetMcVersion` 是 `async void`，`await` 期间用户可能又点了
  另一个版本；旧那次加载回来后会拿过期数据去改界面（勾选框被错误禁用、占位文案显示成别的版本的状态）。
  现在加了版本令牌 `_loadToken`，回来时令牌变了就直接丢弃结果；同时换 VM 时**先退订旧的
  `PropertyChanged`**，避免被替换掉的 VM 继续触发刷新。

### 改进

- **加载期间列表占位文案改为「正在加载中」**。原先只要列表为空就显示「该版本暂无可用版本」，
  而列表在拉取完成前本来就是空的，所以加载中看到的是「没有可用版本」——
  更糟的是首次进入页面时代码还没刷过占位文案，显示的可能是**上一个版本残留的状态**。
  现在：正在拉取 → 「正在加载中」；拉取结束仍然为空 → 「该版本暂无可用版本」。
  改版本时也会先把加载跑起来（`vm.Load()` 同步执行到内部第一个 `await`，`IsLoadingVersions` 已置 `true`）
  再刷占位文案，所以不会先闪一下上一个版本的残留文案。

- **加载器勾选框与列表状态对齐**。四个 `CheckBox` 的可用性统一由 `UpdateLoaderAvailability()` 计算，
  优先级从高到低：

  1. 版本列表还没拉完 → **四个框全部禁用**（加载好之前不给勾）；
  2. 该加载器在这个版本下没有可选版本（404 或列表为空）→ 禁用
     （例如 1.20.1 的 NeoForge 列表为空，勾选框直接灰掉，不会让用户勾了却选不出东西）；
  3. 共存互斥规则（沿用原 WPF 版语义）：Forge 挡住 Fabric / Quilt；Fabric 挡住其余全部；
     NeoForge 挡住 Fabric / Quilt；Quilt 挡住其余全部。已被勾中的框始终保持可用，否则没法取消。

  顺带把原来「勾谁就手工关谁」的 8 个 `Checked/Unchecked` 处理器合并成一个 `OnLoaderToggled()`；
  点「刷新」后若某个已勾选的加载器变成没有版本，会自动取消勾选。

### 说明

- 实机验证（`VBL_SMOKE` 跳安装页 + `VBL_PROBE` 每 120ms 采样，日志 `logs/vibrantbit-*.log`）：

  - 加载中：`cb=FFFF btn=true/true`，四张卡片全是「正在加载中」，**安装按钮全程可用**；
  - 加载完成：`cb=TFFF`（Forge 已勾选 → Fabric/Quilt 被互斥挡住，NeoForge 因 0 个版本禁用），
    NeoForge 卡片显示「该版本暂无可用版本」，其余卡片占位隐藏；
  - 点「刷新」：立刻回到 `cb=FFFF` + 「正在加载中」，加载完自动恢复；
  - 模拟 `IsInstalling = true`：`btn=false/false`；置回 `false` 后立刻 `btn=true/true`。

- 探针代码与 `VBL_SMOKE` 冒烟钩子验证完已全部删除（全局 grep `PROBE` = 0）。


## [1.0.5.13] - 2026-10-06

本版给「下载资源」页加上平滑滚动，并修回一个会让构建输出跑到别处、从而「跑的其实是旧 exe」的 csproj 隐患。

### 新增

- **下载资源页平滑滚动**：`DownloadResourcesPage` 的资源列表加上 `Classes="smoothScroll"`，
  滚轮 / 键盘翻页时的位移由硬跳改为 260ms `CubicEaseOut` 补间。

  - 实现方式：`Styles/Animations.axaml` 里给 `Offset` 挂 `VectorTransition`。
    注意 `ListBox` 的滚动条在**控件模板内部**，挂在 `ListBox` 上的类名要用
    `ListBox.smoothScroll /template/ ScrollViewer` 才选得到里面那层 `ScrollViewer`。
  - 故意**不做成全局**：拖动滚动条时补间会带来「跟不上手」的拖影感，
    只有内容较长、以浏览为主的列表才适合开。其他页面想开只需加一个 `Classes="smoothScroll"`。
  - 实测数据（给 300 项列表设置 `Offset = 600` 后每 45ms 采样）：
    `0 → 262.5 → 448.2 → 548.7 → 590.6 → 599 → 600`，确认是补间而不是跳变。
  - 与页内「滚到底自动加载下一页」不冲突：那里的 `_loadMoreRequested` 标记本来就防连发。

### 修复

- **`FolderProfile.pubxml` 关掉 `PublishReadyToRun`**：这才是「用发布档案发布出来还是 70MB」的真正原因。

  - `PublishReadyToRun` 会预编译 IL 到本地代码塞进单文件，冷启动略快，但体积暴涨：
    **开 73.6MB · 关 42.6MB**（同一份代码实测，差额约 31MB）。
  - 1.0.5.12 里对 csproj 做的那些体积优化（去测试框架、去原生 PDB、去未用依赖）
    在发布档案下**同样生效**，只是被 R2R 的 31MB 盖住了，看起来「没变化」。
  - 一并把 `EnableCompressionInSingleFile` 显式写 false 并写清原因，避免以后被误开。

- **`<RuntimeIdentifier>win-x64</RuntimeIdentifier>` 补回**（1.0.5.12 整理 csproj 时被误删）。

  - 影响远不止「发布时挑哪套原生库」：RID 一旦缺失，**构建输出目录会从
    `bin/<配置>/net10.0/win-x64/` 变成 `bin/<配置>/net10.0/`**。
    结果是编译一路显示成功，但实际运行的仍是旧目录里的旧 exe —— 表现成
    「改的代码完全没生效、日志里也没有新增的调试输出」，极难排查（本轮真的踩了一次）。
  - 已在 csproj 里就地写了注释说明这一点，避免以后再被顺手删掉。

## [1.0.5.12] - 2026-10-06

本版做两件事：**把发布体积砍下来**，以及**给整个界面补上动效**（页面切换过渡、按钮点击缩放、列表与卡片反馈）。

### 新增

- **页面切换过渡动画**：新增 `Helpers/FluidPageTransition.cs`（自定义 `IPageTransition`），
  旧页淡出并轻微收缩（200ms），新页自下方浮起 + 淡入 + 从 98.2% 推到 100%（300ms），
  交叉的那几帧有明确的层次感，不是单纯叠两张半透明页面。

  - 承载方式：主窗口里把 `FANavigationView.Content` 固定成一个
    `TransitioningContentControl`（`_pageHost`），之后所有页面都写进它的 `Content`；
    `NavigationService` 相应改为只认一个 `ContentControl` 宿主，
    不再直接操作 `FANavigationView`。「换哪个页面」与「怎么演」因此彻底解耦。
  - 没用自带 `PageSlide`（左右平移会让侧边栏切页产生「可以左右返回」的误导）
    也没用自带 `CrossFade`（只有透明度，交叉帧会糊作一团）。

- **窗口入场动画**：主窗口首次显示时整体淡入 + 从 98.5% 推到 100%（360ms）。
  窗口底色是 Mica（透明），观感上像内容从系统材质里浮出来。

- **`Helpers/AnimationHelper.cs`**：动效底层工具，把「透明度 + 缩放 + 纵向位移」的
  组合动画收敛成一份实现，页面切换、分栏切换、窗口入场、点击反馈共用同一套节奏。

- **`Helpers/InteractionFeedback.cs`**：点击反馈（按下缩到 0.97，松开用 `BackEaseOut` 弹回）。

### 改进

- **新增 `Styles/Animations.axaml`（交互动效规范）**，在 `App.axaml` 里置于 `Controls.axaml` 之后：

  | 控件 | 效果 |
  | --- | --- |
  | `Button`（含 `ToggleButton` / `CheckBox` / `RadioButton`） | 悬停放大到 1.035，按下缩到 0.94（强调按钮 0.925），`BackEaseOut` 收尾带轻微过冲 |
  | `ListBoxItem`（含 `ComboBoxItem`） | 按下缩到 0.985 |
  | `Border.card` | 悬停升起阴影，只加高度不换颜色（卡片多数不可点击，改边框色会误导） |
  | `Border.hoverCard` | 原有的悬停放大 1.06 之外，补上阴影抬升 |
  | `RepeatButton` | 显式还原（`ScrollBar` 的行按钮只有十几像素，放大 3% 会糊） |
  | `Button:disabled` | 显式还原，禁用态不参与缩放 |

  - `TextBox` / `ComboBox` / `ToggleSwitch` / `Slider` / `ProgressBar` / `ProgressRing` /
    `Expander` / `InfoBar` / `ContentDialog` / `SnackBar` / `ToolTip` / `ScrollBar`
    由 FluentAvalonia 自带过渡，未重复添加。
  - 侧边栏导航项（`FANavigationViewItem`）单独走代码挂载：它在控件模板里把
    `RenderTransform` / `Transitions` 写死，Avalonia 的应用级样式优先级（3）压不过
    控件模板（2），样式形同虚设；改成 `Animation` 优先级 + 局部 `RenderTransform` 才生效。

- **`PageTransitionHelper`** 内部改用 `AnimationHelper`，分栏切换（下载中心 / 设置页）
  的观感与新页面切换统一，并由「纯上浮 + 淡入」升级为「上浮 + 淡入 + 轻微推近」。

### 修复

- **发布目录体积**：`bin/Release/net10.0/publish` 从 **147 MB 降到 41 MB**。

  - **剔除上游包误带的测试框架**：`MinecraftLaunch.Base` 的 nuspec 把
    `xunit` / `Microsoft.NET.Test.Sdk` / `Microsoft.CodeCoverage` 声明成了运行时依赖，
    于是 `xunit.v3.*`、`Microsoft.TestPlatform.*`、`Microsoft.VisualStudio.TestPlatform.*`、
    `testhost.dll` 全都会被复制进发布目录（约 2.1 MB），对本应用毫无用处。
    新增 `TrimPublishPayload` 目标按文件名前缀摘除，不锁版本号。
  - **剔除原生库符号文件**：`libSkiaSharp.pdb`（80 MB）+ `libHarfBuzzSharp.pdb`（20 MB）
    是通过 `runtimes/*/native` 运行时资产带进来的，
    `AllowedReferenceRelatedFileExtensions` 管不到（那只作用于 RAR 任务），同样在
    `TrimPublishPayload` 里按扩展名摘除。
  - **移除两个从未用到的依赖**：`Avalonia.Fonts.Inter`（内置 Inter 字体约 1.9 MB，
    界面字体实际是 Segoe UI Variable / 微软雅黑 / HarmonyOS Sans，
    任何 `FontFamily` 都没引用过 Inter，`Program.cs` 里的 `.WithInterFont()` 一并去掉）、
    `System.Management`（全项目没有任何 WMI 调用）。
  - 单文件 exe：**48.6 MB → 40.6 MB**。
  - **未开启剪裁**：本工程绑定走反射（`AvaloniaUseCompiledBindingsByDefault=false`），
    剪裁会摘掉运行期才用到的属性元数据，症状是页面绑不上去而不是编译报错，风险远大于收益；
    实测「独立部署 + 单文件压缩」反而更大（53 MB），因此保持框架依赖发布。
  - Release 改为 `DebugType=none`（原来 `embedded` 会把 PDB 塞回 exe）。

### 说明

- **动效实现要点**（踩过的坑，写下来备用）：

  1. `Animation.RunAsync` 的宿主**必须是 `Visual`**。Avalonia 的 `TransformAnimator` 会去
     `visual.RenderTransform` 里按类型找对应的 `Scale` / `Translate` 子变换；
     直接对 `ScaleTransform` 对象跑动画会抛
     `InvalidCastException: Unable to cast object of type 'ScaleTransform' to type 'Visual'`。
  2. 属性优先级是 `Animation(-1) > LocalValue(0) > StyleTrigger(1) > 控件模板(2) > 应用样式(3)`。
     控件模板里写死的属性，应用级样式（含 `:pressed` 这类伪类）**永远压不过**，
     只能靠代码动画或局部值。
  3. 同优先级下按样式出现顺序生效，**后出现者胜出** ——
     「禁用态 / 例外控件」的重置样式必须排在文件最后。
  4. 动画基准值直接写成**终态** + `FillMode.None`，播完自动回落，不需要收尾赋值，
     也不会把元素「钉」在动画值上。

## [1.0.5.11] - 2026-10-06

本版修复「下载任务」页整片空白、向导里添加账户报错，以及账户弹窗的两个逻辑缺陷。

### 修复

- **「下载任务」页任务列表整片空白（核心）**：`DownloadCenterPage.axaml` 自定义了 `ListBoxItem` 模板，
  里面只写了一个裸 `<ContentPresenter />`。Avalonia 的 `ContentPresenter` 在自定义 `ControlTemplate` 中
  **不会**自动取 `TemplatedParent.Content`（与 WPF 不同），必须显式 `TemplateBinding`，
  于是项容器内容恒为空 —— 列表有数据、有项容器，却什么都画不出来。
  - 补上 `Content` / `ContentTemplate` / `Padding` / 对齐方式的 `TemplateBinding`。
  - `SettingsPage.axaml` 的日志列表模板有**完全相同的缺陷**，一并修复（日志列表此前同样是空白）。

- **点击项内按钮直接崩溃 / 命令失效**：`DownloadCenterPage`、`MultiplayerPage`、`VersionManagePage`
  共 11 处绑定写成 `$parent[UserControl].((vm:XxxViewModel)DataContext).SomeCommand`。
  本工程 `<AvaloniaUseCompiledBindingsByDefault>false</AvaloniaUseCompiledBindingsByDefault>`，
  走反射绑定，解析不了 `vm:` 这种类型转换写法，实测抛
  `ArgumentException: Unable to resolve type vm:XxxViewModel` 并**直接终止进程**。
  - 统一改为 `$parent[UserControl].DataContext.SomeCommand`。
  - 注：这个缺陷此前被上面的 `ContentPresenter` 问题**掩盖**了 —— DataTemplate 从未实例化，
    绑定也就从未求值；修好列表渲染后它立刻暴露成崩溃。

- **首次运行向导里「添加账户」报 `PlatformImpl is null, couldn't handle input`**：
  账户相关的对话框一律用 `ShowDialog(AppLifetime.MainWindow!)` 硬编码主窗口当 owner，
  而向导显示期间主窗口是 `Hide` 状态（没有平台实现），拿它当 owner 就会报这个错。
  - `AppLifetime` 新增 `DialogOwner`（按「当前激活窗口 → 第一个可见窗口 → 主窗口」回退）
    与 `ShowDialogAsync(Window)`；`DialogService` 及各调用点全部改用它。

- **离线 / 外置账户第一次添加无效**：`AuthenticateOffline()` 与 `AuthenticateYggdrasilAsync()` 里
  `ShowDialog(...)` 返回的 `Task` **没有 await**，方法在弹窗还开着的时候就继续往下跑并检查
  用户名 / 账户资料（此时还是 null 或上次的残留值），导致第一次点击什么都不发生、第二次却用旧值创建。
  - 改为 `await AppLifetime.ShowDialogAsync(window)`，把创建逻辑放到弹窗真正关闭之后。

### 说明

- `LaunchPage` 的新闻 `ItemsControl` 改为在 code-behind 直接赋 `ItemsSource`：
  写成 `{Binding News}` 时，`InitializeComponent()` 阶段会先按页面的「启动」VM 求值一次，
  而该 VM 没有 `News` 属性，运行时会刷一条绑定告警。

## [1.0.5.10] - 2026-10-06

本版修复启动页「官方新闻」完全不显示，并让开机向导显示时隐藏主窗口。

### 修复

- **官方新闻不显示**：`Views/Pages/LaunchPage.axaml` 的新闻 `ItemsControl x:Name="NewsHost"`
  **漏了 `ItemsSource`**（上一版把账户面板改回新闻时重写该段 XAML 时遗漏）。
  数据上下文在 code-behind 里挂好了（`NewsHost.DataContext = DashboardPageViewModel`），
  新闻也确实抓到了 100 条，但 ItemsControl 没有数据源，所以一片空白。
  - 补上 `ItemsSource="{Binding News}"`（与 `DashboardPage.axaml` 的写法一致）。

- **开机向导显示时隐藏主窗口**：`ShowWelcomeWizard` 里先 `mainWindow.Hide()`，
  向导关闭后在 `Closed` 中 `mainWindow.Show() + Activate()` 恢复；
  并挂 `desktop.ShutdownRequested` 兜底，防止隐藏期间被判定为退出。

- **向导重复创建导致崩溃（连带修复）**：主窗口 `Hide → Show` 会**再次触发 `Opened`**，
  原本的一次性订阅于是又弹一次向导；向导页面是 **DI 单例**，同一实例被第二个
  `PageHost` 二次挂载会抛 `InvalidOperationException: already has a visual parent`。
  - `App.axaml.cs`：改用命名 handler，触发一次后立即 `mainWindow.Opened -= OnMainWindowOpenedOnce`。
  - `WelcomeWindow.axaml.cs`：`Closed` 里先 `PageHost.Content = null` 再还原导航入口，
    断开单例页面的父引用，即便再次打开也不会冲突。

### 说明

- 向导改为**不设 owner** 显示（`welcome.Show()`）：owner 处于隐藏状态时，
  部分平台会把子窗口一起藏掉。

## [1.0.5.9] - 2026-10-06

本版解决「主题色改不干净」和「首次运行不弹开机向导」两个问题。

### 修复

- **主题色只改到一半**：之前只在 `Application.Current.Resources` 上硬写
  `SystemAccentColor` / `AccentFillColorDefault` 等**少数几个键**，而 FluentAvalonia 会按主色派生出
  一整套资源（`AccentDark1~3` / `AccentLight1~3` / NavigationView 系列 / 文本与描边派生色 …），
  这些键硬写覆盖不到，于是只有部分控件跟着变。
  - 改为走 FluentAvalonia 官方入口：`faTheme.CustomAccentColor = primary`，由它内部
    `UpdateAccentColors` 重算全部强调色派生资源；`Application.Resources` 上的键保留作为兜底
    （我们自定义样式要用 `AppAccentGradientBrush`、`SystemAccentColorBrush` 等）。
  - `PreferUserAccentColor` 仍强制为 `false`，避免操作系统强调色插一手。

- **首次运行不显示开机向导**：`WelcomeWindow` 在整个代码里**从未被实例化**——
  `App.axaml.cs` 只建了 `MainWindow`，`IsFirstRun` 也只在启动日志里被读了一次、从来没人置 false，
  向导窗口和四个向导页面等于死代码。
  - `App.OnFrameworkInitializationCompleted` 中判断 `IsFirstRun`，为真则在主窗口 `Opened` 后弹出向导，
    并以**主窗口为 owner** 显示（关掉子窗口不会连带退出应用，主窗口始终是 `desktop.MainWindow`）。
  - 向导关闭（正常走完或直接关掉）时写回 `IsFirstRun = false` 并保存，下次启动不再弹。

### 说明

- 未采用 `mainWindow.Hide()` 先藏主窗口的方案：Avalonia 的 `Hide()` 会真正关闭平台窗口并触发
  生命周期的窗口关闭处理，有提前退出进程的风险，因此改为向导叠加在主窗口之上。

## [1.0.5.8] - 2026-10-06

本版修复侧边栏的两个问题：改主题色时侧边栏不跟随、页面切换后侧边栏不高亮当前项。

### 修复

- **改主题色侧边栏不变**：`App.axaml` 里 `FluentAvaloniaTheme` 带 `PreferUserAccentColor="True"`，
  FluentAvalonia 会把**操作系统**的强调色写进 NavigationView 的专属资源
  （`NavigationViewSelectionIndicatorForeground` 实测恒为系统蓝 `#ff0067c0`），
  而侧边栏的选中指示条读的是这些键，**不是** `SystemAccentColor` / `AccentFillColorDefault`，
  所以之前只覆盖后者时侧边栏纹丝不动。
  - 修复一：`ApplyAccentCore` 里把 `faTheme.PreferUserAccentColor` 置为 `false`，不再让系统强调色插一手。
  - 修复二：显式覆盖侧边栏专属键 —— `NavigationViewSelectionIndicatorForeground`、
    `NavigationViewItemBackgroundSelected`（含 PointerOver / Pressed）、`NavigationViewItemBorderBrushSelected`。
  - 选中项的图标 / 文字（`NavigationViewItemForegroundSelected*`）同样写为主色，但为可读性做了明暗自适应：
    暗色主题向白色提亮 35%，亮色主题向黑色压暗 18%（新增 `Lighten` / `Darken` 辅助）。
- **页面切换后侧边栏不高亮**：`NavigationService` 只把页面塞进 `_navView.Content`，从不碰 `SelectedItem`；
  FANavigationView 仅在**用户点击菜单项**时自己更新选中态，因此所有代码路径跳转
  （ViewModel 发 `NavigateTo`、返回栈 `GoBack`）都会出现「页面变了但左侧不高亮」。
  - 修复：MainWindow 订阅 `INavigationService.Navigated`，按页面类型名匹配菜单项 `Tag` 并同步
    `RootNavigation.SelectedItem`（同时查 `MenuItems` 与 `FooterMenuItems`）。
  - 无对应菜单项的页面（版本管理 / 安装 / 资源详情 / 新闻详情等）保持当前选中，
    从「启动」进「版本管理」时侧边栏仍高亮「启动」，符合层级直觉。

### 说明

- 编译 0 错误。
- 运行时验证（冒烟脚本）：主题色改红后 `NavigationViewSelectionIndicatorForeground` / `NavigationViewItemBackgroundSelected`
  均由 `#ff0067c0`（系统蓝）变为红色 ✔；代码路径跳「下载中心」→ 侧边栏选中「下载中心」✔，再跳「启动」→ 选中「启动」✔；日志 0 错误 / 0 致命。


## [1.0.5.7] - 2026-10-05

本版修复「下载资源」界面项目图标不显示的问题，并同步修复新闻配图（启动页 / 仪表盘 / 新闻详情）同样不显示的隐患。

### 修复

- **下载资源页图标不显示**：`Image.Source` 被绑到了**字符串** `ImagePath`（形如 `https://cdn.modrinth.com/...`）。
  Avalonia 的 `Image.Source` 需要 `IImage`，字符串会被默认转换器当作**本地路径 / avares 资源**去解析，
  http(s) 网络地址根本走不到网络那一层，解析失败后静默返回 null —— 表现就是图标永远空白。
  - 修复：新增 `Helpers/RemoteImageLoader.cs`，在后台线程用 `HttpClient` 拉字节流、在 UI 线程解码成 `Bitmap`，
    并按 URL 缓存（同一地址只下一次、失败记 null 不反复重试）。
  - `ModrinthMod` 改继承 `ObservableObject`，新增 `IImage? Icon`；`ImagePath` 赋值时触发异步加载，
    地址被列表复用改写时会丢弃过期结果，避免串图。XAML 改绑 `Source="{Binding Icon}"`。
- **新闻配图同源缺陷**：`DashboardPage` / `LaunchPage` / `NewsDetailPage` 的 `Image.Source` 同样直绑字符串 `ImagePath`
  （http 地址），属同一问题，一并修复。
  - `NewsItem` 实现 `INotifyPropertyChanged` 并新增 `IImage? Image`，`ImagePath` 赋值时异步加载；三处 XAML 改绑 `Source="{Binding Image}"`。

### 说明

- 编译 0 错误。
- 运行时验证（冒烟脚本）：启动页新闻 **100/100** 条图片加载成功；下载资源页模组 **20 条 / 有 URL 20 条 / 已加载图标 20 条**，判定 PASS；日志 0 错误 / 0 致命。


## [1.0.5.6] - 2026-10-05

本版修复「版本管理」页点返回时错误地回到旧运行页（RunPage）而非当前的「启动」页（LaunchPage）的问题。

### 修复

- **版本管理返回落错页面**：`VersionManageViewModel.GoBack()` 原先写死发送
  `WeakReferenceMessenger.Send<Type,string>(typeof(RunPage), "NavigateTo")`，硬编码回到已从导航菜单移除的旧独立运行页 `RunPage`。
  - 修复：返回改为**优先走导航服务返回栈** —— `AppNavigation.Current is { } nav && nav.CanGoBack && nav.GoBack()`，
    即「从哪进来的就回哪去」（从「启动」页进入就回 `LaunchPage`）；仅当返回栈为空（如深链直达）时才回退到 `AppNavigation.Navigate(typeof(LaunchPage))`。
  - 背景：菜单只保留「启动」→`LaunchPage`、「账户」→`AccountPage` 等，「版本管理」无独立菜单项，只能由启动页/运行页内的代码路径经
    `Navigation.Navigate`（压栈）进入，因此返回栈方案能正确还原来源页。

### 说明

- 编译 0 错误（仅既存可空性等警告，与本次改动无关）。
- 运行时验证（冒烟脚本）：起点 `LaunchPage` → 进入版本管理 `VersionManagePage`（`CanGoBack = True`）→ 点返回后 `CurrentPage = LaunchPage`，判定 PASS；日志 0 错误 / 0 致命 / 0 绑定失败。


## [1.0.5.5] - 2026-10-05

本版把「启动」页右侧从账户面板改回官方新闻图墙。

### 改进

- **启动页右侧改回「官方新闻」**：此前面板被替换为「账户（选择启动身份）」，现按需求回退为官方新闻图墙。
  - 右侧栏恢复为「官方新闻」标题 + 可滚动新闻卡片列表：图片铺满卡片、底部渐变承托白色标题、附带日期，
    悬停有弹性放大效果（沿用 `Border.hoverCard`）。
  - 单击卡片打开新闻详情（`NewsDetailPage.ShowNews` 后导航过去）；Avalonia 无 WPF 的 `MouseLeftButtonUp`
    元素事件，改用 `PointerReleased` 并只认鼠标左键。
  - 新闻数据源由 `DashboardPageViewModel.News` 提供，首次进入时调用 `LoadNewsAsync()` 拉取。
  - 账户的增删改仍可从左侧导航「账户」页进入，功能不受影响；启动页只保留「选版本 → 启动」的主流程。

### 说明

- 编译 0 错误；导航到启动页日志显示右侧新闻条数 100、0 绑定错误、0 异常。
- 与 WPF 版的差异：WPF 版的启动页右侧同样是账户面板；本分支按需求改为新闻，属有意为之的差异化。


## [1.0.5.4] - 2026-10-05

本版完善设置页「游戏设置」区块：进入页面自动重检 Java、Minecraft 文件夹显示绝对路径、输入框拉满并左对齐。

### 修复

- **进入设置页不刷新 Java**：`NavigationService` 把「页面控件」设为导航内容，因此 `INavigationAware` 回调落在
  `SettingsPage`（UserControl）上；而实现该接口的是 `SettingsPageViewModel`，页面控件并没有实现它 ——
  于是 `OnNavigatedToAsync` **从未被调用**，Java 检测只在首帧初始化时跑过一次。
  - 修复：让 `SettingsPage` 实现 `INavigationAware` 并转发给 ViewModel；ViewModel 的 `OnNavigatedToAsync`
    改为 public，且**每次进入都重新检测 Java**（首次进入仍走整体初始化），装了新 JDK / 改了路径后切回来即可看到。
- **Minecraft 文件夹显示相对路径**：配置里默认存的是可移植的 `./.minecraft`，界面直接展示让人摸不着头脑。
  - 修复：`SettingsService.ResolveMinecraftFolder()` 统一返回**绝对路径**（内部 `Path.GetFullPath`，路径非法时
    回退到默认目录的绝对路径）；设置页概览与「游戏设置」均显示绝对路径。配置持久化仍保留原相对/绝对值不变。

### 改进

- **Minecraft 文件夹输入框拉长并左对齐**：去掉 `MaxWidth="560"` 限制，改为占满整行（`HorizontalAlignment=Stretch`），
  文本明确左对齐（`HorizontalContentAlignment=Left`）；Java 版本下拉同步去掉宽度上限，两者与右侧「选择 / 刷新」
  按钮的排版保持一致。

### 说明

- 编译 0 错误；启动与进入设置页日志均无异常。
- 验证：模拟「进入设置页 → 切到别的页 → 再进设置页」，日志显示 Java 检测执行 **2 次**（每次进入各一次），
  `MinecraftFolder` 显示为绝对路径（本机为 `<程序目录>\.minecraft`），Java 候选 5 条且默认选中 jdk-27。


## [1.0.5.3] - 2026-10-05

本版修复设置页整页绑定落空（表现为「设置页一片空白」）与内置壁纸缩略图不显示的问题。

### 修复

- **设置页绑定全部失效（核心问题）**：进入设置页后，页面文字、开关、色板全部为空，日志刷满
  `Could not find a matching property accessor for 'Xxx' on 'VibrantbitLauncher.ViewModels.Windows.MainWindowViewModel'`。
  - 原因：`SettingsPage` 是 DI 单例，但它的构造函数**没有** `DataContext = _viewModel`，于是继承了主窗口的
    `MainWindowViewModel`；而页面里绑定的 `CurrentThemeText` / `SelectedJava` / `LogSummary` / `AccentPresets`
    等属性都只在 `SettingsPageViewModel` 上。其他页面（多人联机 / 下载中心 / 版本管理…）都在构造里显式赋值，
    唯独设置页漏了。
  - 修复：构造函数里解析 `SettingsPageViewModel` 并赋给 `DataContext`，与其余页面保持一致。
- **设置页右侧色板 / 壁纸命令抛 `Unable to resolve type vm:SettingsPageViewModel`**：主题色、渐变终点色、
  内置壁纸三处 `Command` 用了 `{Binding $parent[UserControl].((vm:SettingsPageViewModel)DataContext).Xxx}` 写法。
  本工程 `<AvaloniaUseCompiledBindingsByDefault>false</AvaloniaUseCompiledBindingsByDefault>`，绑定走**反射**实现，
  反射绑定无法解析 XAML 的 `vm:` 命名空间前缀 → 模板一实例化就抛 `ArgumentException`。
  - 修复：改用反射绑定可解析的形式 `{Binding $parent[ItemsControl].DataContext.XxxCommand}`。
- **内置壁纸缩略图空白**：`WallpaperOption.ImagePath` 存的是应用内相对路径 `/Assets/bg1.png`，直接绑到
  `Image.Source` 时 Avalonia 解析不了这种相对资源 URI，壁纸画廊四格全空。
  - 修复：`WallpaperOption` 新增 `Preview`（在构造时用 `AssetLoader` + `avares://` 显式加载成 `Bitmap`），
    XAML 改绑 `Preview`；`ImagePath` 仍保留原相对路径用于持久化与实际应用背景。
- **主题色 / 渐变终点色选中环画不出**：原先用 `Style Selector="Button[Tag=True]"` 表达选中态 —— Avalonia 的
  属性选择器拿 Boxed `bool` 做字符串比较并不可靠。
  - 修复：在 `AccentPreset` 上新增 `AccentRingBrush` / `GradientRingBrush`（选中为主前景色、未选中透明），
    模板改为一次直接绑定，行为确定。
- **`EnumToBooleanConverter` 为 `internal`**：`SettingsPage` / `WelcomeSetupPage` 通过 `helpers:` 命名空间在
  资源字典里实例化它，Avalonia 编译期 XAML 只能解析可公开访问的类型。已改为 `public`，并让其 `Convert` /
  `ConvertBack` 在参数缺失或值非枚举时返回安全值而不是抛异常（避免日志刷绑定错误）。

### 改进

- **日志列表启用虚拟化**：设置页「最近日志」的 `ListBox` 加回 `VirtualizingStackPanel`，日志上千条时滚动不再卡顿。
- Java 版本下拉项补回 `body` / `secondary` / `caption` 文本样式类，与 WPF 版视觉对齐。
- 内置壁纸缩略图加圆角裁剪，观感更整齐。
- `OnChangeTheme` 参数改为可空 `string?`，消除一处空引用警告。

### 说明

- 编译 0 错误；启动日志无异常。
- 已用独立冒烟脚本逐页导航（启动参数注入）核对绑定：`DashboardPage` / `LaunchPage` / `AccountPage` /
  `RunPage` / `DownloadHubPage` / `DownloadPage` / `ModPage` / `DownloadCenterPage` / `MultiplayerPage` /
  `VersionManagePage` / `SettingsPage` 全部 **0 绑定错误、0 异常**。
  （`InstallPage` 需由下载页先发 `McVersion` 消息再进入，单独直接导航属预期内未就绪，非缺陷。）
- 排查提示：Avalonia 项目若关闭编译绑定（`AvaloniaUseCompiledBindingsByDefault=false`），
  **不要**在 `{Binding ...}` 里写 `((vm:XxxViewModel)DataContext)` 这类类型转换 —— 那是编译绑定语法，
  反射绑定解析不了命名空间前缀，运行时会直接抛异常。跨模板取父级 ViewModel 请用
  `$parent[ItemsControl].DataContext.Xxx`。
- 排查提示：每个 `UserControl` 页面若由 DI 提供 ViewModel，**务必在构造函数里赋值 `DataContext`**，
  否则会静默继承父级 DataContext，整页绑定落空且只在打开该页时于日志暴露。


## [1.0.5.2] - 2026-10-05

本版修复下载中心切换「光影 / 数据包 / 资源包」等社区资源分类时的崩溃。

### 修复

- **切换社区资源分类崩溃（核心问题）**：下载中心左侧点开「光影」「数据包」「资源包」等分类，第二次切换即抛
  `InvalidOperationException: The control DownloadResourcesPage already has a visual parent Grid (Name = HostGrid) while trying to add it as a child of Grid (Name = HostGrid)`。
  - 原因：`DownloadResourcesPage` 与其 ViewModel 都注册成了 DI **单例**，而下载中心右侧的五个社区资源分类（模组 / 整合包 / 数据包 / 资源包 / 光影）各自需要**独立页面**并排放在 `HostGrid` 里。首次进入某分类时 `Add` 成功，切到下一个分类时拿到的是**同一个控件对象**，再次 `Add` 就会因「已有可视父级」而抛异常。
  - 现在改为**每个分类各 new 一个页面实例、各 new 一个 ViewModel**（`ShowResourcePage` 内直接 `new DownloadResourcesPage()`），并移除 `DownloadResourcesPage` / `DownloadResourcesViewModel` 的单例注册。
  - 该问题还暗含**数据串台**：五个分类若共用一个 ViewModel，筛选条件、分页游标（`_nextOffset`）与搜索结果会互相覆盖，表现为「切到光影却看到模组的列表」。一分类一 ViewModel 后彻底隔离。
  - 另加 `HostGrid.Children.Contains` 判重作为双保险，避免将来别的调用路径重复添加同一实例。

### 说明

- 编译 0 警告 / 0 错误；启动与分类切换日志均无异常。
- 排查提示：Avalonia（及 WPF）中**同一个控件对象只能有一个可视父级**，任何「动态把一个控件加进容器」的代码都必须保证该控件是当前容器独占的、且未被加过。共享控件的场景应改为共享数据（ViewModel / 数据模板），而不是共享控件本身。


## [1.0.5.1] - 2026-10-05

本版收尾页面迁移：把最后 3 个页面（版本管理 / 多人联机 / 新闻）与 3 个窗口补齐，
至此全部 19 个页面 + 9 个窗口均已在 Avalonia 上实现，全功能对齐 WPF 版。

### 新增

- **版本管理页（`VersionManagePage`）**：模组 / 存档双标签管理。支持导入模组（多选）、全部启用 / 全部禁用、勾选框即改磁盘文件名（`foo.jar` ↔ `foo.jar.disabled`）、模组搜索过滤；存档导入（文件夹或 `.zip`）、备份导出、内联重命名、删除、打开目录。三个空状态与计数文案由集合变更事件驱动实时刷新。
  - 标签高亮改为代码后置切换 `accent` 类（替代 WPF 的 `Appearance="Primary/Secondary"`）。
  - WPF 里 `IsRenaming` 的 `DataTrigger` 双态模板 → 两个 `Grid` 分别绑定 `IsRenaming` / `!IsRenaming`。
- **多人联机页（`MultiplayerPage`）**：EasyTier 组网全流程 —— 中继服务器配置、启动 / 停止服务、房主生成 / 复制密钥、房客粘贴密钥、虚拟 IP 展示与复制、网络成员列表（每 3 秒自动刷新）。缺核心 / 缺管理员权限时给出引导卡片。
  - 状态圆点（绿 / 黄 / 红）改由代码后置按 `StatusLevel` 查主题画刷赋值（替代 WPF 的三段 `DataTrigger`）。
- **新闻页（`DashboardPage`）**：接入 `DashboardPageViewModel.LoadNewsAsync()`，首次进入自动拉取 Mojang 新闻并按卡片瀑布流展示；页面实例走 DI 单例，`IsLoaded` 保证只加载一次。
- **安装联机模块窗口（`InstallEasyTierWindow`）**：从 GitHub 镜像下载 EasyTier 核心（约 10 MB）并解压到 VBL 目录，带进度条与状态文案；下载进度经 `Dispatcher.UIThread` 回到 UI 线程更新。
- **开机向导宿主窗口（`WelcomeWindow`）**：用 `ContentControl` 承载 `WelcomePage` 等向导页面，并新增 `ContentControlNavigationHost` —— 窗口打开时临时接管 `AppNavigation.Current`，使向导页面里统一的 `AppNavigation.Navigate(...)` 落到本窗口，关闭时还原给主窗口。
- **外置登录窗口（`YggdrasilAuthenticatorWindow`）**：收集服务器地址 / 邮箱 / 密码，经 `WeakReferenceMessenger` 发回 `AccountPageViewModel` 触发 Yggdrasil 认证。

### 改进

- **安装版本页（`InstallPage`）复选框事件**：Avalonia 的 `ToggleButton` 没有 XAML `Checked` / `Unchecked` 事件属性，改为订阅 `IsCheckedChanged` 并统一转发到对应的勾选 / 取消勾选处理器，行为与 WPF 完全一致。
- **`UserControl.Resources` 里误放 `Style` 的两处页面**（`VersionManagePage` / `InstallPage`）改为 `UserControl.Styles` —— Avalonia 的 `Resources` 不接受 `Style` 节点。
- 新增 `Border` 类样式 `AppSuccessSurfaceBrush`（成功提示底色），补齐 `AppTheme.axaml` 的语义色。
- 新增 `Button.danger` 样式（删除类操作红色前景），补齐 `Controls.axaml`。

### 修复

- **`InstallPage` 编译错误**：`CheckBox` 的 `Checked` / `Unchecked` 属性在 Avalonia 不存在，导致 `AVLN2000`，现改为代码订阅。
- **图标名不匹配**：`FASymbol` 枚举中没有 `Warning` / `Info` / `ReportHacked`，多人联机页改用 `Alert` / `ContactInfo`。
- **`VersionManagePage` 编译错误**：`UserControl.Resources` 内放 `Style` 触发 `AVLN3000`，改用 `UserControl.Styles`。

### 说明

- 本工程编译为 **0 警告 / 0 错误**；启动日志确认主窗口、依赖注入容器与全部页面均可正常解析。
- 页面迁移清单（19/19）：LaunchPage、AccountPage、DownloadHubPage、DownloadCenterPage、MultiplayerPage、SettingsPage、DashboardPage、InstallPage、RunPage、VersionManagePage、DownloadPage、DownloadResourcesPage、ModPage、NewsDetailPage、WelcomePage、WelcomeSetupPage、HelloEffectPage、WelcomeAccountPage、WelcomeCompletePage。
- 窗口迁移清单（9/9）：MainWindow、CrashAnalysisWindow、MicrosoftAuthWindow、OfflineAuthenticatorWindow、SkinManagerWindow、SkinUrlInputWindow、InstallEasyTierWindow、WelcomeWindow、YggdrasilAuthenticatorWindow。

### 与本分支约定一致的 Avalonia API 差异（供后续维护参考）

- FluentAvalonia 3.x 所有控件 XAML 别名带 `FA` 前缀；内联图标控件是 `FASymbolIcon`，`IconSource` 属性用 `FASymbolIconSource`；窗口基类是 `FAAppWindow`。
- `FASymbol` 枚举 442 个值，**没有** `Warning` / `Info` / `OK`，对应概念用 `Alert` / `ContactInfo`。
- `ComboBox.DisplayMemberPath` 不存在 → 用 `ItemTemplate` + `DataTemplate`。
- `ListBox.ItemContainerStyle` 不存在 → 用 `ListBox.Styles` + `<Style Selector="ListBoxItem">`。
- 无 `Style.Triggers` / `DataTrigger` → 用 `IsVisible` 直接绑定布尔（含 `!` 取反）、代码后置切 `Classes`，或 `Button[Tag=True]` 选择器。
- `TextBox.Watermark` 已废弃 → `PlaceholderText`。
- 无 `ListCollectionView` / `Frame` / `NavigationUIVisibility` → 手动过滤集合、缓存页面实例切换 `IsVisible`。


## [1.0.5.0] - 2026-10-05

Avalonia 重构分支初始版本：以 FluentAvalonia 3.1.0 为控件库，从 WPF 版（WPF-UI）整体重建。
