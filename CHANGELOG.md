# 更新日志（Avalonia 版）

本文件记录 VibrantbitLauncher.Avalonia 的所有重要变更。

> 本工程是 WPF 版 VibrantbitLauncher 的 Avalonia 重构分支，与之并存、互不影响。
> 版本号唯一来源是 `VibrantbitLauncher.Avalonia.csproj` 的 `<Version>`（四段式 `主.次.修订.补丁`）。


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
