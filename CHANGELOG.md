# 更新日志（Avalonia 版）

本文件记录 VibrantbitLauncher.Avalonia 的所有重要变更。

> 本工程是 WPF 版 VibrantbitLauncher 的 Avalonia 重构分支，与之并存、互不影响。
> 版本号唯一来源是 `VibrantbitLauncher.Avalonia.csproj` 的 `<Version>`（四段式 `主.次.修订.补丁`）。


## [1.0.5.16] - 2026-10-06

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
